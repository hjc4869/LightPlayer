using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// One card of the home spotlight: the playing track, or an item drawn from the library. The page
/// draws three of these at a time -- the one in the middle and the two either side -- so a swipe has
/// something to pull in.
/// </summary>
public sealed partial class SpotlightSlideModel : ObservableObject
{
    [ObservableProperty]
    private string header = string.Empty;

    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private string subtitle = string.Empty;

    [ObservableProperty]
    private string meta = string.Empty;

    /// <summary>
    /// What fills the card behind its text: an album cover reduced to a wash of its own
    /// colors, or a library item's preview frame.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundImage))]
    private IImage? backdrop;

    [ObservableProperty]
    private IImage? previousBackdrop;

    [ObservableProperty]
    private double previousBackdropOpacity;

    /// <summary>The album cover, drawn over the backdrop.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundImage))]
    private IImage? cover;

    [ObservableProperty]
    private MediaReference? artworkKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundImage))]
    private bool showCover;

    public IImage? BackgroundImage => ShowCover ? Backdrop : Cover ?? Backdrop;

    /// <summary>True while this is the card in the middle, which is what fills its dot in.</summary>
    [ObservableProperty]
    private bool isCurrent;

    [ObservableProperty]
    private object? contextItem;

    public IRelayCommand? OpenCommand { get; init; }

    /// <summary>Drops the artwork of a card that has rotated out of the drawn window.</summary>
    public void ReleaseImages()
    {
        Backdrop = null;
        Cover = null;
        ReleasePreviousBackdrop();
    }

    public void ReleasePreviousBackdrop()
    {
        PreviousBackdropOpacity = 0;
        PreviousBackdrop = null;
    }

    partial void OnBackdropChanged(IImage? oldValue, IImage? newValue)
    {
        PreviousBackdrop = newValue is not null ? oldValue : null;
        PreviousBackdropOpacity = PreviousBackdrop is not null ? 1 : 0;
        if (newValue is null)
        {
            (oldValue as IDisposable)?.Dispose();
        }
    }

    partial void OnPreviousBackdropChanged(IImage? oldValue, IImage? newValue) =>
        (oldValue as IDisposable)?.Dispose();

    partial void OnCoverChanged(IImage? oldValue, IImage? newValue) =>
        (oldValue as IDisposable)?.Dispose();
}
