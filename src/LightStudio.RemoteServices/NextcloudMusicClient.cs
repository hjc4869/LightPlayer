using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;

namespace LightStudio.RemoteServices;

internal sealed record NextcloudMusicServerInfo(
    string Type,
    string ApiVersion,
    string ServerVersion,
    IReadOnlySet<string> Extensions);

internal sealed record NextcloudMusicSong(
    string Id,
    string AlbumId,
    string ArtistId,
    string Title,
    string Album,
    string Artist,
    string Genre,
    string Path,
    string Suffix,
    string ContentType,
    string? CoverArt,
    int DurationSeconds,
    int? DiscNumber,
    int? TrackNumber,
    int? Year,
    long Size,
    DateTimeOffset Created);

internal sealed record NextcloudMusicAlbum(
    string Id,
    string ArtistId,
    string Name,
    string Artist,
    string Genre,
    string? CoverArt,
    int SongCount,
    int? Year,
    DateTimeOffset Created);

internal sealed class NextcloudMusicClient : IDisposable, IRemoteMediaRangeClient
{
    private sealed record ResourceMetadata(string Path, long Length, DateTimeOffset Timestamp);

    private const string ApiVersion = "1.16.1";
    private const string ClientName = "LightPlayer";
    private static readonly HttpRequestOptionsKey<bool> BrowserResponseStreaming =
        new("WebAssemblyEnableStreamingResponse");

    private readonly HttpClient apiClient;
    private readonly HttpClient streamingClient;
    private readonly RemoteRangeRequests rangeRequests = new();
    private readonly Uri apiBaseAddress;
    private readonly string? apiKey;
    private readonly ConcurrentDictionary<string, ResourceMetadata> resourceMetadata = new(StringComparer.Ordinal);

    public NextcloudMusicClient(string serverAddress, string? apiKey)
    {
        var address = serverAddress.TrimEnd('/');
        var apiSuffix = address.EndsWith("/index.php", StringComparison.OrdinalIgnoreCase)
            ? "/apps/music/subsonic/rest/"
            : "/index.php/apps/music/subsonic/rest/";
        apiBaseAddress = new Uri(address + apiSuffix, UriKind.Absolute);
        this.apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        apiClient = CreateHttpClient(TimeSpan.FromSeconds(90));
        streamingClient = CreateHttpClient(Timeout.InfiniteTimeSpan);
    }

    public event EventHandler? Unauthorized;

    public string EndpointIdentity => apiBaseAddress.AbsoluteUri;

    public async Task<NextcloudMusicServerInfo> ProbeAsync(CancellationToken cancellationToken = default)
    {
        using var document = await CallAsync(
            "getOpenSubsonicExtensions",
            new Dictionary<string, string?>(),
            includeAuthentication: false,
            cancellationToken).ConfigureAwait(false);
        var envelope = Envelope(document);
        var type = String(envelope, "type");
        var version = String(envelope, "version");
        var serverVersion = String(envelope, "serverVersion");
        var extensions = new HashSet<string>(StringComparer.Ordinal);
        if (envelope.TryGetProperty("openSubsonicExtensions", out var values))
        {
            foreach (var value in Elements(values))
            {
                if (String(value, "name") is { Length: > 0 } name)
                {
                    extensions.Add(name);
                }
            }
        }

        return new NextcloudMusicServerInfo(type, version, serverVersion, extensions);
    }

    public async Task<string> GetUserNameAsync(CancellationToken cancellationToken = default)
    {
        using var document = await CallAsync(
            "tokenInfo",
            new Dictionary<string, string?>(),
            true,
            cancellationToken).ConfigureAwait(false);
        var envelope = Envelope(document);
        return envelope.TryGetProperty("tokenInfo", out var tokenInfo)
            ? RequiredString(tokenInfo, "username")
            : throw new InvalidDataException("Nextcloud Music omitted tokenInfo from its response.");
    }

    public async Task<NextcloudMusicSong[]> GetSongsPageAsync(
        int offset,
        int count,
        CancellationToken cancellationToken = default)
    {
        using var document = await CallAsync(
            "search3",
            new Dictionary<string, string?>
            {
                ["query"] = string.Empty,
                ["artistCount"] = "0",
                ["albumCount"] = "0",
                ["songCount"] = count.ToString(CultureInfo.InvariantCulture),
                ["songOffset"] = offset.ToString(CultureInfo.InvariantCulture),
            },
            true,
            cancellationToken).ConfigureAwait(false);
        var envelope = Envelope(document);
        if (!envelope.TryGetProperty("searchResult3", out var result)
            || !result.TryGetProperty("song", out var songs))
        {
            return [];
        }

        var parsedSongs = Elements(songs).Select(ParseSong).ToArray();
        foreach (var song in parsedSongs)
        {
            resourceMetadata[song.Id] = new ResourceMetadata(song.Path, song.Size, song.Created);
        }
        return parsedSongs;
    }

    public async Task<NextcloudMusicAlbum[]> GetAlbumsPageAsync(
        int offset,
        int count,
        CancellationToken cancellationToken = default)
    {
        using var document = await CallAsync(
            "getAlbumList2",
            new Dictionary<string, string?>
            {
                ["type"] = "alphabeticalByName",
                ["size"] = count.ToString(CultureInfo.InvariantCulture),
                ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
            },
            true,
            cancellationToken).ConfigureAwait(false);
        var envelope = Envelope(document);
        if (!envelope.TryGetProperty("albumList2", out var result)
            || !result.TryGetProperty("album", out var albums))
        {
            return [];
        }

        return Elements(albums).Select(ParseAlbum).ToArray();
    }

    public async Task<byte[]> GetCoverArtAsync(
        string coverArtId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendBinaryAsync(
            apiClient,
            "getCoverArt",
            new Dictionary<string, string?>
            {
                ["id"] = coverArtId,
                ["size"] = "512",
            },
            range: null,
            cancellationToken).ConfigureAwait(false);
        await EnsureBinarySuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteResourceInfo> GetResourceInfoAsync(
        string trackId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendBinaryAsync(
            streamingClient,
            "stream",
            new Dictionary<string, string?> { ["id"] = trackId },
            new RangeHeaderValue(0, 0),
            cancellationToken).ConfigureAwait(false);
        await EnsureBinarySuccessAsync(response, cancellationToken).ConfigureAwait(false);

        resourceMetadata.TryGetValue(trackId, out var metadata);
        var contentRange = response.Content.Headers.ContentRange;
        var length = contentRange?.Length ?? metadata?.Length ?? -1;
        var validContentRange = contentRange is null
            || (string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                && contentRange.From == 0
                && contentRange.To == 0
                && contentRange.Length > 0);
        var supportsRanges = response.StatusCode == HttpStatusCode.PartialContent
            && validContentRange
            && length > 0;
        if (supportsRanges)
        {
            var probe = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (probe.Length != 1)
            {
                throw new IOException($"Nextcloud Music returned {probe.Length} bytes for a one-byte range probe.");
            }
        }

        var entityTag = response.Headers.ETag;
        return new RemoteResourceInfo(
            length,
            entityTag is { IsWeak: false } ? entityTag.ToString() : null,
            supportsRanges,
            CreateCacheRevision(metadata, length));
    }

    public async Task<byte[]> GetRangeAsync(
        string trackId,
        long offset,
        int count,
        string? entityTag = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        using var response = await SendBinaryAsync(
            streamingClient,
            "stream",
            new Dictionary<string, string?> { ["id"] = trackId },
            new RangeHeaderValue(offset, offset + count - 1),
            cancellationToken).ConfigureAwait(false);
        await EnsureBinarySuccessAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new InvalidDataException($"Nextcloud Music ignored the byte range request for '{trackId}'.");
        }

        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange is not null
            && (contentRange.From != offset || contentRange.To != offset + count - 1))
        {
            throw new InvalidDataException($"Nextcloud Music returned the wrong byte range for '{trackId}'.");
        }
        if (entityTag is { Length: > 0 }
            && !string.Equals(response.Headers.ETag?.ToString(), entityTag, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The Nextcloud Music track '{trackId}' changed while it was being read.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length != count)
        {
            throw new IOException($"Nextcloud Music returned {bytes.Length} bytes when {count} were requested.");
        }

        return bytes;
    }

    public Task<RemoteRangeResponse> OpenRangesAsync(
        string trackId,
        IReadOnlyList<RemoteByteRange> ranges,
        long length,
        string? entityTag,
        CancellationToken cancellationToken) =>
        rangeRequests.OpenAsync(
            ranges,
            length,
            (range, token) => SendRangesAsync(trackId, range, entityTag, token),
            cancellationToken);

    private async Task<HttpResponseMessage> SendRangesAsync(
        string trackId,
        RangeHeaderValue range,
        string? entityTag,
        CancellationToken cancellationToken)
    {
        var response = await SendBinaryAsync(
            streamingClient,
            "stream",
            new Dictionary<string, string?> { ["id"] = trackId },
            range,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureBinarySuccessAsync(response, cancellationToken).ConfigureAwait(false);
            if (entityTag is { Length: > 0 }
                && !string.Equals(response.Headers.ETag?.ToString(), entityTag, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The Nextcloud Music track '{trackId}' changed while it was being read.");
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task<byte[]> GetBytesAsync(string trackId, CancellationToken cancellationToken = default)
    {
        using var response = await SendBinaryAsync(
            streamingClient,
            "stream",
            new Dictionary<string, string?> { ["id"] = trackId },
            range: null,
            cancellationToken).ConfigureAwait(false);
        await EnsureBinarySuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private HttpClient CreateHttpClient(TimeSpan timeout) => OperatingSystem.IsBrowser() ? new()
    {
        BaseAddress = apiBaseAddress,
        Timeout = timeout
    } : new ()
    {
        BaseAddress = apiBaseAddress,
        Timeout = timeout,
        DefaultRequestHeaders = 
        {
            UserAgent = { new ProductInfoHeaderValue(ClientName, "1.0") },
        }
    };

    private static string? CreateCacheRevision(ResourceMetadata? metadata, long length) =>
        metadata is null || length <= 0
            ? null
            : $"{metadata.Path}\n{length.ToString(CultureInfo.InvariantCulture)}\n{metadata.Timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture)}";

    private async Task<JsonDocument> CallAsync(
        string method,
        IReadOnlyDictionary<string, string?> parameters,
        bool includeAuthentication,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, parameters, includeAuthentication);
        using var response = await apiClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            RaiseUnauthorized(response.StatusCode);
            throw new HttpRequestException(
                $"Nextcloud Music returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException(
                $"Nextcloud Music returned an unreadable response for '{method}'.",
                exception,
                response.StatusCode);
        }

        try
        {
            EnsureEnvelopeSuccess(document);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendBinaryAsync(
        HttpClient client,
        string method,
        IReadOnlyDictionary<string, string?> parameters,
        RangeHeaderValue? range,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, parameters, includeAuthentication: true);
        request.Headers.Range = range;
        request.Options.Set(BrowserResponseStreaming, true);
        return await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(
        string method,
        IReadOnlyDictionary<string, string?> parameters,
        bool includeAuthentication)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("c", ClientName),
            new("v", ApiVersion),
            new("f", "json"),
        };
        if (includeAuthentication)
        {
            if (apiKey is null)
            {
                throw new RemoteAuthenticationRequiredException(apiBaseAddress.GetLeftPart(UriPartial.Authority));
            }
            values.Add(new("apiKey", apiKey));
        }

        values.AddRange(parameters
            .Where(value => value.Value is not null)
            .Select(value => new KeyValuePair<string, string>(value.Key, value.Value!)));
        return new HttpRequestMessage(HttpMethod.Post, method)
        {
            Content = new FormUrlEncodedContent(values),
        };
    }

    private async Task EnsureBinarySuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            RaiseUnauthorized(response.StatusCode);
            throw new HttpRequestException(
                $"Nextcloud Music returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                null,
                response.StatusCode);
        }

        if (!string.Equals(
            response.Content.Headers.ContentType?.MediaType,
            "application/json",
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureEnvelopeSuccess(document);
        throw new InvalidDataException("Nextcloud Music returned JSON where media bytes were expected.");
    }

    private void EnsureEnvelopeSuccess(JsonDocument document)
    {
        var envelope = Envelope(document);
        if (string.Equals(String(envelope, "status"), "ok", StringComparison.Ordinal))
        {
            return;
        }

        var error = envelope.TryGetProperty("error", out var value) ? value : default;
        var code = error.ValueKind == JsonValueKind.Object ? Int32(error, "code") : null;
        var message = error.ValueKind == JsonValueKind.Object ? String(error, "message") : null;
        var unauthorized = code is 40 or 41 or 42 or 43 or 44;
        if (unauthorized)
        {
            Unauthorized?.Invoke(this, EventArgs.Empty);
        }

        throw new HttpRequestException(
            message ?? "Nextcloud Music rejected the request.",
            null,
            unauthorized ? HttpStatusCode.Unauthorized : HttpStatusCode.BadRequest);
    }

    private void RaiseUnauthorized(HttpStatusCode statusCode)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Unauthorized?.Invoke(this, EventArgs.Empty);
        }
    }

    private static NextcloudMusicSong ParseSong(JsonElement value) => new(
        RequiredString(value, "id"),
        String(value, "albumId"),
        String(value, "artistId"),
        String(value, "title"),
        String(value, "album"),
        String(value, "artist"),
        String(value, "genre"),
        String(value, "path"),
        String(value, "suffix"),
        String(value, "contentType"),
        NullableString(value, "coverArt"),
        Int32(value, "duration") ?? 0,
        Int32(value, "discNumber"),
        Int32(value, "track"),
        Int32(value, "year"),
        Int64(value, "size") ?? 0,
        DateTime(value, "created"));

    private static NextcloudMusicAlbum ParseAlbum(JsonElement value) => new(
        RequiredString(value, "id"),
        String(value, "artistId"),
        String(value, "name"),
        String(value, "artist"),
        String(value, "genre"),
        NullableString(value, "coverArt"),
        Int32(value, "songCount") ?? 0,
        Int32(value, "year"),
        DateTime(value, "created"));

    private static JsonElement Envelope(JsonDocument document) =>
        document.RootElement.TryGetProperty("subsonic-response", out var envelope)
            ? envelope
            : throw new InvalidDataException("Nextcloud Music returned no Subsonic response envelope.");

    private static IEnumerable<JsonElement> Elements(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                yield return item;
            }
        }
        else if (value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any())
        {
            yield return value;
        }
    }

    private static string RequiredString(JsonElement value, string propertyName) =>
        NullableString(value, propertyName) is { Length: > 0 } result
            ? result
            : throw new InvalidDataException($"Nextcloud Music omitted '{propertyName}'.");

    private static string String(JsonElement value, string propertyName) =>
        NullableString(value, propertyName) ?? string.Empty;

    private static string? NullableString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.GetRawText();
    }

    private static int? Int32(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) && TryInt64(property, out var result)
            ? checked((int)result)
            : null;

    private static long? Int64(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) && TryInt64(property, out var result)
            ? result
            : null;

    private static bool TryInt64(JsonElement value, out long result) =>
        value.ValueKind == JsonValueKind.Number
            ? value.TryGetInt64(out result)
            : long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static DateTimeOffset DateTime(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.TryGetDateTimeOffset(out var result)
                ? result
                : DateTimeOffset.UnixEpoch;

    public void Dispose()
    {
        apiClient.Dispose();
        streamingClient.Dispose();
    }
}