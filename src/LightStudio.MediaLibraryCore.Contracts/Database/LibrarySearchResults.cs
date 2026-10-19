using System.Collections.Generic;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.MediaLibraryCore.Database;

/// <summary>Result groups returned by a media-library search.</summary>
public sealed class LibrarySearchResults
{
    public List<MediaLibraryAlbum> Albums { get; set; } = [];

    public List<MediaLibraryArtist> Artists { get; set; } = [];

    public List<DbMediaFile> Files { get; set; } = [];
}