using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using LightStudio.LightPlayer.Services;
using LightMediaRenderer.Avalonia.Pipeline;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.RemoteServices;

internal sealed record WebDavEntry(
    string Path,
    string Name,
    bool IsCollection,
    long Length,
    DateTime LastModifiedUtc,
    string? EntityTag,
    string? ContentType);

internal sealed class WebDavClient : IDisposable, IRemoteMediaRangeClient
{
    private const int MaximumRangeProbeFolders = 32;
    private const long RangeProbeEnd = 1;

    private const string PropFindBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <d:propfind xmlns:d="DAV:">
          <d:prop>
            <d:displayname />
            <d:resourcetype />
            <d:getcontentlength />
            <d:getlastmodified />
            <d:getetag />
            <d:getcontenttype />
          </d:prop>
        </d:propfind>
        """;

    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly XNamespace Dav = "DAV:";
    private static readonly HttpRequestOptionsKey<bool> BrowserResponseStreaming =
        new("WebAssemblyEnableStreamingResponse");

    private readonly HttpClient metadataClient;
    private readonly HttpClient streamingClient;
    private readonly RemoteRangeRequests rangeRequests = new();
    private readonly Uri rootUri;
    private readonly string rootPath;

    public WebDavClient(string rootAddress, string? username, string? password)
    {
        rootUri = CollectionUri(new Uri(rootAddress, UriKind.Absolute));
        rootPath = Uri.UnescapeDataString(rootUri.AbsolutePath);
        metadataClient = CreateClient(TimeSpan.FromSeconds(90), username, password);
        streamingClient = CreateClient(Timeout.InfiniteTimeSpan, username, password);
    }

    public event EventHandler? Unauthorized;

    public string RootPath => rootUri.AbsoluteUri;

    public string EndpointIdentity => rootUri.AbsoluteUri;

    public async Task<Stream> OpenMediaStreamAsync(
        string resourceId,
        CancellationToken cancellationToken = default,
        bool readAheadEnabled = true)
    {
        var resource = GetResourceUri(resourceId, collection: false);
        var stream = await HttpMediaStream.OpenAsync(
            streamingClient,
            resource,
            cancellationToken,
            response => EnsureSuccess(response, resource),
            readAheadEnabled).ConfigureAwait(false);
        if (stream is HttpRangeStream)
            return stream;

        stream.Dispose();
        throw new RemoteLibraryIncompatibleException(Strings.Resources.RemoteLibraries_WebDavRangeRequired);
    }

    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        var entries = await GetPropertiesAsync(rootUri, depth: 1, cancellationToken).ConfigureAwait(false);
        var root = entries.FirstOrDefault(entry => SameResource(entry.Path, rootUri));
        if (root is null)
        {
            throw new InvalidDataException("The WebDAV server did not describe the configured path.");
        }

        if (!root.IsCollection)
        {
            throw new RemoteLibraryIncompatibleException("The configured WebDAV path is not a folder.");
        }

        await ProbeRangeSupportAsync(entries, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProbeRangeSupportAsync(
        IReadOnlyList<WebDavEntry> rootEntries,
        CancellationToken cancellationToken)
    {
        var pending = new Queue<WebDavEntry>();
        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            rootUri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
        };
        var entries = rootEntries;
        for (var folderCount = 0; folderCount < MaximumRangeProbeFolders; folderCount++)
        {
            var file = entries.FirstOrDefault(entry => !entry.IsCollection && entry.Length > 0)
                ?? entries.FirstOrDefault(entry => !entry.IsCollection);
            if (file is not null)
            {
                var info = await GetResourceInfoAsync(file.Path, cancellationToken).ConfigureAwait(false);
                if (info.Length > 0)
                {
                    if (!info.SupportsRangeRequests)
                    {
                        throw new RemoteLibraryIncompatibleException(
                            Strings.Resources.RemoteLibraries_WebDavRangeRequired);
                    }

                    return;
                }
            }

            foreach (var folder in entries.Where(entry => entry.IsCollection))
            {
                if (visited.Add(folder.Path.TrimEnd('/')))
                {
                    pending.Enqueue(folder);
                }
            }

            if (!pending.TryDequeue(out var nextFolder))
            {
                return;
            }

            entries = await ListAsync(nextFolder.Path, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<IReadOnlyList<WebDavEntry>> ListAsync(
        string folderPath,
        CancellationToken cancellationToken = default) =>
        GetPropertiesAsync(GetResourceUri(folderPath, collection: true), depth: 1, cancellationToken);

    public async Task<WebDavEntry?> GetEntryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var uri = GetResourceUri(path, collection: path.EndsWith("/", StringComparison.Ordinal));
        var entries = await GetPropertiesAsync(uri, depth: 0, cancellationToken).ConfigureAwait(false);
        return entries.FirstOrDefault(entry => SameResource(entry.Path, uri));
    }

    public async Task<RemoteResourceInfo> GetResourceInfoAsync(
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        var resource = GetResourceUri(resourceId, collection: false);
        using var request = CreateGetRequest(resource, new RangeHeaderValue(0, RangeProbeEnd));
        using var response = await streamingClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
            && response.Content.Headers.ContentRange?.Length == 0)
        {
            return new RemoteResourceInfo(0, StrongEntityTag(response), false, CacheRevision(response, 0));
        }

        EnsureSuccess(response, resource);
        var contentRange = response.Content.Headers.ContentRange;
        var length = contentRange?.Length ?? response.Content.Headers.ContentLength ?? -1;
        var supportsRanges = response.StatusCode == HttpStatusCode.PartialContent
            && contentRange is not null
            && string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            && contentRange.From == 0
            && contentRange.Length > 0
            && contentRange.To == Math.Min(RangeProbeEnd, contentRange.Length.Value - 1);
        if (supportsRanges)
        {
            var probe = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var expectedLength = checked((int)(contentRange!.To!.Value + 1));
            if (probe.Length != expectedLength)
            {
                throw new IOException(
                    $"WebDAV returned {probe.Length} bytes for a {expectedLength}-byte range probe.");
            }
        }

        return new RemoteResourceInfo(
            length,
            StrongEntityTag(response),
            supportsRanges,
            CacheRevision(response, length));
    }

    public async Task<byte[]> GetRangeAsync(
        string resourceId,
        long offset,
        int count,
        string? entityTag = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var resource = GetResourceUri(resourceId, collection: false);
        using var request = CreateGetRequest(
            resource,
            new RangeHeaderValue(offset, checked(offset + count - 1)));
        if (entityTag is { Length: > 0 }
            && EntityTagHeaderValue.TryParse(entityTag, out var parsedEntityTag))
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(parsedEntityTag);
        }

        using var response = await streamingClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, resource);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new InvalidDataException($"WebDAV ignored the byte range request for '{resource}'.");
        }

        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange is null
            || !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || contentRange.From != offset
            || contentRange.To != offset + count - 1)
        {
            throw new InvalidDataException($"WebDAV returned the wrong byte range for '{resource}'.");
        }

        if (entityTag is { Length: > 0 }
            && !string.Equals(StrongEntityTag(response), entityTag, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The WebDAV file '{resource}' changed while it was being read.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length != count)
        {
            throw new IOException($"WebDAV returned {bytes.Length} bytes when {count} were requested.");
        }

        return bytes;
    }

    public Task<RemoteRangeResponse> OpenRangesAsync(
        string resourceId,
        IReadOnlyList<RemoteByteRange> ranges,
        long length,
        string? entityTag,
        CancellationToken cancellationToken)
    {
        var resource = GetResourceUri(resourceId, collection: false);
        return rangeRequests.OpenAsync(
            ranges,
            length,
            (range, token) => SendRangesAsync(resource, range, entityTag, token),
            cancellationToken);
    }

    private async Task<HttpResponseMessage> SendRangesAsync(
        Uri resource,
        RangeHeaderValue range,
        string? entityTag,
        CancellationToken cancellationToken)
    {
        using var request = CreateGetRequest(resource, range);
        if (entityTag is { Length: > 0 }
            && EntityTagHeaderValue.TryParse(entityTag, out var parsedEntityTag))
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(parsedEntityTag);
        }

        var response = await streamingClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSuccess(response, resource);
            if (entityTag is { Length: > 0 }
                && !string.Equals(StrongEntityTag(response), entityTag, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The WebDAV file '{resource}' changed while it was being read.");
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task<byte[]> GetBytesAsync(
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        var resource = GetResourceUri(resourceId, collection: false);
        using var request = CreateGetRequest(resource, range: null);
        using var response = await streamingClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, resource);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<WebDavEntry>> GetPropertiesAsync(
        Uri resource,
        int depth,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(PropFindMethod, resource)
        {
            Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml"),
        };
        request.Headers.TryAddWithoutValidation("Depth", depth.ToString(CultureInfo.InvariantCulture));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        request.Options.Set(BrowserResponseStreaming, true);

        using var response = await metadataClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, resource);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        XDocument document;
        try
        {
            document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Xml.XmlException)
        {
            throw new InvalidDataException("The WebDAV server returned an unreadable multistatus response.", exception);
        }

        var result = new List<WebDavEntry>();
        foreach (var item in document.Descendants(Dav + "response"))
        {
            var href = item.Element(Dav + "href")?.Value;
            var propertySet = item.Elements(Dav + "propstat")
                .FirstOrDefault(propstat => IsSuccessfulStatus(propstat.Element(Dav + "status")?.Value))
                ?.Element(Dav + "prop");
            if (string.IsNullOrWhiteSpace(href) || propertySet is null)
            {
                continue;
            }

            var isCollection = propertySet
                .Element(Dav + "resourcetype")?
                .Element(Dav + "collection") is not null;
            var responseUri = ResolveResponseUri(href, isCollection);
            if (responseUri is null)
            {
                continue;
            }

            var lengthText = propertySet.Element(Dav + "getcontentlength")?.Value;
            var length = long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLength)
                ? parsedLength
                : 0;
            var modifiedText = propertySet.Element(Dav + "getlastmodified")?.Value;
            var lastModified = DateTimeOffset.TryParse(
                modifiedText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsedModified)
                    ? parsedModified.UtcDateTime
                    : DateTime.UnixEpoch;
            var displayName = propertySet.Element(Dav + "displayname")?.Value.Trim();
            result.Add(new WebDavEntry(
                responseUri.AbsoluteUri,
                string.IsNullOrEmpty(displayName) ? LeafName(responseUri) : displayName,
                isCollection,
                Math.Max(0, length),
                lastModified,
                NullIfEmpty(propertySet.Element(Dav + "getetag")?.Value),
                NullIfEmpty(propertySet.Element(Dav + "getcontenttype")?.Value)));
        }

        return result;
    }

    private HttpRequestMessage CreateGetRequest(Uri resource, RangeHeaderValue? range)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, resource);
        request.Headers.Range = range;
        request.Options.Set(BrowserResponseStreaming, true);
        return request;
    }

    private static HttpClient CreateClient(TimeSpan timeout, string? username, string? password)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LightPlayer/1.0");
        if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password))
        {
            var value = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", value);
        }

        return client;
    }

    private void EnsureSuccess(HttpResponseMessage response, Uri resource)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Unauthorized?.Invoke(this, EventArgs.Empty);
        }

        throw new HttpRequestException(
            $"WebDAV returned {(int)response.StatusCode} {response.ReasonPhrase} for '{resource}'.",
            null,
            response.StatusCode);
    }

    private Uri GetResourceUri(string value, bool collection)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var resource))
        {
            resource = new Uri(rootUri, value);
        }

        resource = collection ? CollectionUri(resource) : FileUri(resource);
        if (!IsInsideRoot(resource))
        {
            throw new UnauthorizedAccessException("The WebDAV resource is outside the configured root.");
        }

        return resource;
    }

    private Uri? ResolveResponseUri(string href, bool collection)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var resource)
            || (!string.Equals(resource.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(resource.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            resource = new Uri(rootUri, href);
        }

        resource = collection ? CollectionUri(resource) : FileUri(resource);
        return IsInsideRoot(resource) ? resource : null;
    }

    private bool IsInsideRoot(Uri resource)
    {
        if (!string.Equals(resource.Scheme, rootUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(resource.IdnHost, rootUri.IdnHost, StringComparison.OrdinalIgnoreCase)
            || resource.Port != rootUri.Port)
        {
            return false;
        }

        var path = Uri.UnescapeDataString(resource.AbsolutePath);
        return string.Equals(path.TrimEnd('/'), rootPath.TrimEnd('/'), StringComparison.Ordinal)
            || path.StartsWith(rootPath, StringComparison.Ordinal);
    }

    private static Uri CollectionUri(Uri uri)
    {
        var path = uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        return new Uri(path, UriKind.Absolute);
    }

    private static Uri FileUri(Uri uri) => new(uri.GetLeftPart(UriPartial.Path), UriKind.Absolute);

    private static bool SameResource(string path, Uri resource) =>
        Uri.TryCreate(path, UriKind.Absolute, out var candidate)
        && string.Equals(
            candidate.GetLeftPart(UriPartial.Path).TrimEnd('/'),
            resource.GetLeftPart(UriPartial.Path).TrimEnd('/'),
            StringComparison.Ordinal);

    private static bool IsSuccessfulStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        var parts = status.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            && code is >= 200 and < 300;
    }

    private static string LeafName(Uri resource)
    {
        var path = Uri.UnescapeDataString(resource.AbsolutePath).TrimEnd('/');
        var separator = path.LastIndexOf('/');
        return separator >= 0 ? path[(separator + 1)..] : path;
    }

    private static string? StrongEntityTag(HttpResponseMessage response) =>
        response.Headers.ETag is { IsWeak: false } entityTag ? entityTag.ToString() : null;

    private static string? CacheRevision(HttpResponseMessage response, long length)
    {
        var modified = response.Content.Headers.LastModified;
        return length < 0 && modified is null
            ? null
            : $"{length.ToString(CultureInfo.InvariantCulture)}\n{modified?.UtcTicks.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        metadataClient.Dispose();
        streamingClient.Dispose();
    }
}