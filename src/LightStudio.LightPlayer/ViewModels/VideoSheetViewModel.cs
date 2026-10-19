using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.ViewModels;

/// <summary>
/// Backs the video player sheet: the running video session layered over the shell rather than
/// navigated to. Maximized it covers the whole shell; minimized it shrinks to a small card in the
/// bottom-right corner that keeps playing while the user browses the library behind it.
///
/// Uses the same logical state, drag, and cross-fade as <see cref="NowPlayingSheetViewModel"/>,
/// but the two are separate surfaces: music is stood down for as long as a video
/// session exists, so they are never both live.
/// </summary>
public sealed partial class VideoSheetViewModel : ObservableObject
{
    public const double DefaultMinimizedWidth = 300d;
    public const double MinimumMinimizedWidth = 180d;

    private readonly Action close;
    private readonly IShellStateStore stateStore;
    private VideoPlayerPageViewModel? subscribed;

    [ObservableProperty]
    private double minimizedWidth;

    // Set while the card plays its exit. The session is only torn down once the view reports that
    // the card has left, so closing is not a surface vanishing mid-frame.
    [ObservableProperty]
    private bool isClosing;

    // The live video session, or null when there is none. Owned by the shell, which builds it,
    // disposes it, and stands music down around it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    [NotifyPropertyChangedFor(nameof(IsShellChromeHidden))]
    [NotifyPropertyChangedFor(nameof(IsFullScreen))]
    private VideoPlayerPageViewModel? content;

    // Whether the sheet is open full screen. This is the sheet's logical state; the view animates
    // its card between the corner and full presentations to match, and writes the state back when
    // a drag settles.
    [ObservableProperty]
    private bool isExpanded;

    // How far the card is between the corner (0) and the whole shell (1). Written continuously by
    // the view while dragging or animating, so the shell's chrome only stands down once the card
    // has actually covered it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShellChromeHidden))]
    private double progress;

    // True while the picture is showing in a window of its own, which is when the sheet has nothing
    // left to draw and gets out of the way. A host that shrinks the app instead keeps showing this
    // very card, so that mode is not covered here.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    [NotifyPropertyChangedFor(nameof(IsShellChromeHidden))]
    private bool isPictureDetached;

    /// <param name="close">Ends the video session and tears the sheet down; supplied by the shell.</param>
    public VideoSheetViewModel(Action close, IShellStateStore stateStore)
    {
        this.close = close;
        this.stateStore = stateStore;
        minimizedWidth = NormalizeMinimizedWidth(stateStore.MinimizedVideoWidth);

        ExpandCommand = new RelayCommand(() =>
        {
            IsClosing = false;
            IsExpanded = true;
        });
        MinimizeCommand = new RelayCommand(() =>
        {
            IsClosing = false;
            IsExpanded = false;
        });
        CloseCommand = new RelayCommand(() => IsClosing = Content is not null);
    }

    /// <summary>Maximizes the card from its corner presentation.</summary>
    public IRelayCommand ExpandCommand { get; }

    /// <summary>Drops the card back into its corner without ending playback.</summary>
    public IRelayCommand MinimizeCommand { get; }

    /// <summary>Asks for the session to end; the card plays its exit before it actually does.</summary>
    public IRelayCommand CloseCommand { get; }

    /// <summary>Updates the preferred corner-card width while it is being resized.</summary>
    public void Resize(double width) => MinimizedWidth = NormalizeMinimizedWidth(width);

    /// <summary>Persists the width once a resize gesture has finished.</summary>
    public void CompleteResize() => stateStore.MinimizedVideoWidth = MinimizedWidth;

    /// <summary>Ends the session for real. Called by the view once the card has left the screen.</summary>
    public void CompleteClose() => close();

    /// <summary>Whether a video session exists at all, whichever state its card is in.</summary>
    public bool IsActive => Content is not null;

    /// <summary>Whether the card is on screen, i.e. active and not showing its picture elsewhere.</summary>
    public bool IsVisible => IsActive && !IsPictureDetached;

    /// <summary>Whether the video has asked its platform host to occupy the whole display.</summary>
    public bool IsFullScreen => Content?.IsFullScreen == true;

    /// <summary>
    /// Whether the card has covered the shell's own chrome. Driven by the travel rather than by
    /// <see cref="IsExpanded"/> so the navigation and playback chrome stays put until the card that
    /// replaces it has actually arrived.
    /// </summary>
    public bool IsShellChromeHidden => IsVisible && Progress > 0.98d;

    /// <summary>Puts a freshly opened session on the sheet, maximized.</summary>
    public void Attach(VideoPlayerPageViewModel player)
    {
        if (ReferenceEquals(Content, player))
        {
            IsClosing = false;
            IsExpanded = true;
            return;
        }

        IsExpanded = true;
        IsClosing = false;
        Subscribe(player);
        IsPictureDetached = player.IsPictureDetached;
        player.IsMinimized = false;

        // Assigned last: the view drives the card's entrance off this, so everything else about the
        // session must already be in place.
        Content = player;
    }

    /// <summary>Takes the session off the sheet and hands it back for disposal.</summary>
    public VideoPlayerPageViewModel? Detach()
    {
        var player = Content;
        IsExpanded = false;
        IsClosing = false;
        Progress = 0d;
        IsPictureDetached = false;
        Subscribe(null);

        // Cleared last, for the same reason it is assigned last in Attach: the view places the card
        // outright on this, and it must not be left mid-throw.
        Content = null;
        return player;
    }

    partial void OnProgressChanged(double value)
    {
        // The deck and the panels drawn over the picture are unreadable on the corner card, so they
        // leave as soon as the card starts shrinking and only come back once it has filled the shell.
        if (Content is { } player)
        {
            player.IsMinimized = value < 0.98d;
        }
    }

    private void Subscribe(VideoPlayerPageViewModel? player)
    {
        if (subscribed is not null)
        {
            subscribed.PropertyChanged -= OnContentPropertyChanged;
        }

        subscribed = player;

        if (subscribed is not null)
        {
            subscribed.PropertyChanged += OnContentPropertyChanged;
        }
    }

    private void OnContentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoPlayerPageViewModel.IsFullScreen))
        {
            OnPropertyChanged(nameof(IsFullScreen));
            return;
        }

        if (e.PropertyName != nameof(VideoPlayerPageViewModel.IsPictureInPicture) || Content is not { } player)
        {
            return;
        }

        IsPictureDetached = player.IsPictureDetached;

        // A host that shrinks the whole app around the picture is showing this card, so it has to be
        // the maximized one rather than a thumbnail in the corner of a thumbnail.
        if (player.IsPictureInPicture && !player.IsPictureDetached)
        {
            IsExpanded = true;
        }
    }

    private static double NormalizeMinimizedWidth(double width) =>
        double.IsFinite(width) && width > 0d
            ? Math.Max(MinimumMinimizedWidth, width)
            : DefaultMinimizedWidth;
}
