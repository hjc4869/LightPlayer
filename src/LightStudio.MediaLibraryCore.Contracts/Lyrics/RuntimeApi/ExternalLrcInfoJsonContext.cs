using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ExternalLrcInfo[]))]
[JsonSerializable(typeof(ExternalLrcInfo))]
[JsonSerializable(typeof(JsonElement))]
public partial class ExternalLrcInfoJsonContext : JsonSerializerContext;