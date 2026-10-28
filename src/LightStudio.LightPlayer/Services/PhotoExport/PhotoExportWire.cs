using System.Text.Json.Serialization;
using LightStudio.FfmpegShim.Engine;

namespace LightStudio.LightPlayer.Services.PhotoExport;

/// <summary>
/// A save a page asks its media worker for. <paramref name="Encoder"/> names one of the worker's still-image
/// encoders, and <see cref="Operation"/> identifies the save to cancel it.
/// </summary>
internal sealed record WirePhotoExport(WireSource Source, bool? RenderRaw, string? Encoder, PhotoExportOptions Options)
{
    public int Operation { get; init; }
}

// Fields carry the tile size, a value tuple. Nulls are written, as some options default to a value.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    IncludeFields = true)]
[JsonSerializable(typeof(WirePhotoExport))]
internal sealed partial class PhotoExportWireContext : JsonSerializerContext;
