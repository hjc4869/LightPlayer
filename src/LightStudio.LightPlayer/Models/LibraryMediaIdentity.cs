using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightStudio.LightPlayer.Models;

[JsonConverter(typeof(LibrarySourceIdJsonConverter))]
public readonly record struct LibrarySourceId(string Value)
{
    public static LibrarySourceId Local { get; } = new("local");

    public static LibrarySourceId System { get; } = new("system");

    public bool IsLocal => this == Local || this == System;

    public static LibrarySourceId FromPersisted(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Local : new(value);

    public override string ToString() => Value ?? string.Empty;
}

public sealed class LibrarySourceIdJsonConverter : JsonConverter<LibrarySourceId>
{
    public override LibrarySourceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? LibrarySourceId.FromPersisted(reader.GetString())
            : throw new JsonException("A library source id must be a string.");

    public override void Write(Utf8JsonWriter writer, LibrarySourceId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

public readonly record struct MediaReference(LibrarySourceId SourceId, string Path)
{
    public static MediaReference Local(string path) => new(LibrarySourceId.Local, path);
}

public readonly record struct MediaItemKey(MediaReference Media, int? StartTimeMs);

public sealed record Sourced<T>(LibrarySourceId SourceId, T Value);

public static class MediaReferenceUri
{
    private const string Scheme = "lightplayer";
    private const string Host = "library";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Format(MediaReference media, int? startTimeMs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(media.SourceId.Value);
        ArgumentNullException.ThrowIfNull(media.Path);

        var sourceId = Uri.EscapeDataString(media.SourceId.Value);
        var encodedPath = Convert.ToBase64String(StrictUtf8.GetBytes(media.Path))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var query = startTimeMs is { } value ? $"?start_ms={value}" : string.Empty;
        return $"{Scheme}://{Host}/{sourceId}/media/{encodedPath}{query}";
    }

    public static bool TryParse(string? value, out MediaItemKey mediaItem)
    {
        mediaItem = default;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Port != -1)
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3 ||
            !string.Equals(segments[1], "media", StringComparison.Ordinal) ||
            !TryDecodePath(segments[2], out var path))
        {
            return false;
        }

        var sourceId = Uri.UnescapeDataString(segments[0]);
        if (string.IsNullOrWhiteSpace(sourceId) || !TryParseStartTime(uri.Query, out var startTimeMs))
        {
            return false;
        }

        mediaItem = new MediaItemKey(new MediaReference(new LibrarySourceId(sourceId), path), startTimeMs);
        return true;
    }

    private static bool TryDecodePath(string encodedPath, out string path)
    {
        path = string.Empty;
        try
        {
            var base64 = encodedPath.Replace('-', '+').Replace('_', '/');
            var padding = base64.Length % 4;
            if (padding == 1)
            {
                return false;
            }

            if (padding > 1)
            {
                base64 = base64.PadRight(base64.Length + 4 - padding, '=');
            }

            path = StrictUtf8.GetString(Convert.FromBase64String(base64));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryParseStartTime(string query, out int? startTimeMs)
    {
        startTimeMs = null;
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        var parts = query.AsSpan(1);
        const string prefix = "start_ms=";
        if (!parts.StartsWith(prefix, StringComparison.Ordinal) ||
            parts[prefix.Length..].Contains('&') ||
            !int.TryParse(parts[prefix.Length..], out var parsed) ||
            parsed < 0)
        {
            return false;
        }

        startTimeMs = parsed;
        return true;
    }
}