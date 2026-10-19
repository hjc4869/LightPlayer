using System.Text.Json.Serialization;

namespace LightStudio.VideoValidation;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(RenderedFrameMetrics))]
[JsonSerializable(typeof(ValidationAssetManifest))]
[JsonSerializable(typeof(ValidationRunReport))]
public sealed partial class ValidationJsonContext : JsonSerializerContext;