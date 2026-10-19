#nullable enable

using System;

namespace LightStudio.MediaLibraryCore.Database;

/// <summary>Thrown when a lookup names an album, artist, or track the library does not have.</summary>
public sealed class LibraryItemNotFoundException(string message) : ArgumentException(message);
