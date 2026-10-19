#nullable enable
using System;
using System.Collections.Generic;

namespace LightStudio.MediaLibraryCore.Database;

public sealed record PhotoMetadata
{
    public string Path { get; init; } = "";
    public string Root { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Format { get; init; } = "";
    public long SizeInBytes { get; init; }
    public DateTime LastModifiedUtc { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? DateTaken { get; init; }
    public string? CameraMake { get; init; }
    public string? CameraModel { get; init; }
    public string? Lens { get; init; }
    public double? ExposureSeconds { get; init; }
    public double? Aperture { get; init; }
    public double? FocalLength { get; init; }
    public int? Iso { get; init; }
    public int? Orientation { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? Altitude { get; init; }
    public string? Description { get; init; }
    public string? Artist { get; init; }
    public string? Copyright { get; init; }
}

public sealed record PhotoSearchMatch(
    string Path,
    string FileName,
    long SizeInBytes,
    int Width,
    int Height,
    string? DateTaken,
    double Score);

public sealed record PhotoSearchPage(
    IReadOnlyList<PhotoSearchMatch> Items,
    string? NextCursor,
    bool IsAvailable,
    bool RestartRequired = false);