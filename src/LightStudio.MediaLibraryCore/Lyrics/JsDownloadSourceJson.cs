using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

namespace LightStudio.MediaLibraryCore.Lyrics;

public static class JsDownloadSourceJson
{
    public static IReadOnlyList<LyricSourceInfoItem> DeserializeSourceInfo(string json) =>
        JsonSerializer.Deserialize(json, LyricsJsonContext.Default.LyricSourceInfoItemArray) ?? [];

    public static string SerializeCandidate(ExternalLrcInfo candidate) =>
        JsonSerializer.Serialize(candidate, ExternalLrcInfoJsonContext.Default.ExternalLrcInfo);

    public static ExternalLrcInfo[] DeserializeLookup(string json, string sourceName)
    {
        var results = JsonSerializer.Deserialize(json, ExternalLrcInfoJsonContext.Default.ExternalLrcInfoArray)
            ?? ExternalLrcInfo.EmptyArray;
        foreach (var result in results)
        {
            result.Source = sourceName;
        }

        return results;
    }

    public static LyricDownload DeserializeDownload(string json)
    {
        var download = JsonSerializer.Deserialize(json, LyricsJsonContext.Default.ScriptDownload);
        if (download is null || string.IsNullOrWhiteSpace(download.Content))
        {
            return null;
        }

        var type = string.Equals(download.Type, "synced", StringComparison.OrdinalIgnoreCase)
            ? LyricType.Synced
            : LyricType.Plain;
        return new LyricDownload(download.Content, type);
    }
}

internal sealed class ScriptDownload
{
    public string Content { get; set; }

    public string Type { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScriptDownload))]
[JsonSerializable(typeof(LyricSourceInfoItem[]))]
internal partial class LyricsJsonContext : JsonSerializerContext;