using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LightStudio.LightPlayer.Models;

public enum SearchResultKind
{
    Song,
    Album,
    Artist,
}

/// <summary>
/// A single search suggestion shown in the shell search box.
/// </summary>
public sealed partial class SearchResultModel : ObservableObject
{
    /// <summary>
    /// Artwork shown for the suggestion. Loaded asynchronously for albums; until it arrives (and for
    /// artists and tracks) the kind-specific placeholder glyph is shown instead.
    /// </summary>
    [ObservableProperty]
    private IImage? image;

    public SearchResultModel(string title, string subtitle, SearchResultKind kind, ThumbnailKind thumbnailKind = ThumbnailKind.Album, bool hasThumbnail = false)
    {
        Title = title;
        Subtitle = subtitle;
        Kind = kind;
        ThumbnailKind = thumbnailKind;
        HasThumbnail = hasThumbnail;
    }

    public string Title { get; }

    public string Subtitle { get; }

    public SearchResultKind Kind { get; }

    public ThumbnailKind ThumbnailKind { get; }

    public bool HasThumbnail { get; }

    /// <summary>
    /// Navigation payload for the suggestion (a <c>MediaLibraryItemIdentifier</c> for album and
    /// artist results). Null for free-text matches.
    /// </summary>
    public object? Identifier { get; init; }

    // AutoCompleteBox filters against ToString().
    public override string ToString() => Title;
}
