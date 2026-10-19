#nullable enable
using System;

namespace LightStudio.MediaLibraryCore.IO;

/// <summary>
/// Filesystem-like properties resolved by the platform that owns a media identifier.
/// The path and directory may be local paths, remote paths, content URIs, or other opaque
/// identifiers; callers must not reinterpret or normalize them for the current operating system.
/// </summary>
public sealed record MediaFileProperties(
    string Name,
    string Path,
    string? DirectoryPath,
    long Length,
    DateTime LastModifiedUtc);
