using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.ViewModels;

/// <summary>
/// Backs the narrow (mobile) now-playing bottom sheet: a compact card docked above the bottom
/// navigation that the user can drag (or tap) up into a full-screen now-playing surface without
/// leaving the page underneath. The shell uses a docked playback bar and Now Playing page when
/// the viewport is wide (see
/// <c>ShellViewModel.SetViewportWidth</c>).
/// </summary>
public sealed partial class NowPlayingSheetViewModel : ObservableObject, IDisposable
{
    /// <summary>Pager index of the cover + metadata page.</summary>
    public const int TrackTab = 0;

    /// <summary>Pager index of the lyrics page.</summary>
    public const int LyricsTab = 1;

    /// <summary>Pager index of the up-next (queue) page.</summary>
    public const int UpNextTab = 2;

    private readonly Func<NowPlayingPageViewModel> contentFactory;
    private bool disposed;

    // Whether the sheet is open full screen. This is the sheet's logical state; the view animates
    // its card between the compact and full presentations to match, and writes the state back when
    // a drag settles.
    [ObservableProperty]
    private bool isExpanded;

    // True while the shell is narrow and something is playing, i.e. the sheet is shown at all.
    [ObservableProperty]
    private bool isAvailable;

    // How far the card is between compact (0) and full screen (1). Written continuously by the view
    // while dragging or animating, so chrome outside the sheet (the shell's bottom navigation) can
    // cross-fade with it instead of popping at the end of the gesture.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BottomNavOpacity))]
    [NotifyPropertyChangedFor(nameof(IsBottomNavInteractive))]
    private double progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrackTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsLyricsTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsUpNextTabSelected))]
    private int selectedTab;

    // Created on first expand: building it starts a lyric lookup for the current track, which is
    // wasted work while the sheet has never been opened.
    [ObservableProperty]
    private NowPlayingPageViewModel? content;

    public NowPlayingSheetViewModel(
        PlaybackBarViewModel playback,
        QueuePaneViewModel queue,
        Func<NowPlayingPageViewModel> contentFactory)
    {
        Playback = playback;
        Queue = queue;
        this.contentFactory = contentFactory;

        ExpandCommand = new RelayCommand(() => IsExpanded = true);
        CollapseCommand = new RelayCommand(() => IsExpanded = false);
        SelectTabCommand = new RelayCommand<int>(SelectTab);
        OpenArtistCommand = new RelayCommand(() => LeaveFor(Content?.OpenArtistCommand));
        OpenAlbumCommand = new RelayCommand(() => LeaveFor(Content?.OpenAlbumCommand));
    }

    /// <summary>Transport, metadata and artwork shared with the wide layout's playback bar.</summary>
    public PlaybackBarViewModel Playback { get; }

    /// <summary>The shared now-playing queue, shown by the sheet's "Up Next" page.</summary>
    public QueuePaneViewModel Queue { get; }

    public IRelayCommand ExpandCommand { get; }

    public IRelayCommand CollapseCommand { get; }

    public IRelayCommand<int> SelectTabCommand { get; }

    /// <summary>Navigates to the current track's artist, closing the sheet first.</summary>
    public IRelayCommand OpenArtistCommand { get; }

    /// <summary>Navigates to the current track's album, closing the sheet first.</summary>
    public IRelayCommand OpenAlbumCommand { get; }

    public bool IsTrackTabSelected => SelectedTab == TrackTab;

    public bool IsLyricsTabSelected => SelectedTab == LyricsTab;

    public bool IsUpNextTabSelected => SelectedTab == UpNextTab;

    /// <summary>
    /// Opacity of the shell's bottom navigation, which the expanding card covers. Fading it with the
    /// drag keeps the destination buttons legible until the full page has actually taken over.
    /// </summary>
    public double BottomNavOpacity => Math.Clamp(1d - (Progress * 1.6d), 0d, 1d);

    /// <summary>The bottom navigation only takes taps while the sheet is (essentially) compact.</summary>
    public bool IsBottomNavInteractive => Progress < 0.02d;

    /// <summary>
    /// Builds the lyrics / metadata content on first use. Called by the view model when the sheet
    /// opens and by the view before it starts an opening drag, so the page is populated by the time
    /// it fades in.
    /// </summary>
    public void EnsureContent()
    {
        if (Content is null && !disposed)
        {
            Content = contentFactory();
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            EnsureContent();
        }
    }

    // Tapping a tab shows its page. Tapping "Up next" additionally re-centers the queue on the
    // current track, mirroring the wide layout's "Now Playing" header -- including when the page is
    // already showing, which is exactly when the user has scrolled away and wants to get back.
    private void SelectTab(int tab)
    {
        SelectedTab = tab;

        if (tab == UpNextTab)
        {
            Queue.RequestScrollToCurrent();
        }
    }

    partial void OnIsAvailableChanged(bool value)
    {
        if (!value)
        {
            // Nothing is playing (or the shell went wide): the sheet is gone, so it must not stay
            // logically open and re-appear expanded the next time it is shown.
            IsExpanded = false;
        }
    }

    private void LeaveFor(IRelayCommand? command)
    {
        if (command is null || !command.CanExecute(null))
        {
            return;
        }

        IsExpanded = false;
        command.Execute(null);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Content?.Dispose();
        Content = null;
    }
}
