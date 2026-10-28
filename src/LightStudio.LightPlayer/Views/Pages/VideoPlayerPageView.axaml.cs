using LightMediaRenderer.Avalonia.Controls;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Controls;
using LightStudio.LightPlayer.ViewModels.Pages;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class VideoPlayerPageView : UserControl
{
    /// <summary>How long the controls stay up after the last pointer movement, as on a TV or phone.</summary>
    private static readonly TimeSpan ControlsIdleTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Two taps of the picture closer together than this are one double tap.</summary>
    private static readonly TimeSpan DoubleTapWindow = TimeSpan.FromMilliseconds(150);

    /// <summary>How long a finger must rest on the picture before it runs fast.</summary>
    private static readonly TimeSpan HoldToBoostDelay = TimeSpan.FromMilliseconds(450);

    private const double CompactDeckThreshold = 550d;

    /// <summary>Finger travel that turns a press into a gesture rather than a tap.</summary>
    private const double SwipeThresholdPx = 24d;

    /// <summary>Seconds of video a swipe across the full width of the picture covers.</summary>
    private const double SwipeSecondsPerWidth = 120d;

    /// <summary>A swipe landing this close to where playback already is is not worth a seek.</summary>
    private const double SwipeMinimumSeconds = 1d;

    private const double PlaylistSlideMs = 220d;

    /// <summary>Every backend maps this to a hidden pointer; on Android and iOS it is a no-op.</summary>
    private static readonly Cursor HiddenCursor = new(StandardCursorType.None);

    private readonly DispatcherTimer uiTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer hideControlsTimer = new() { Interval = ControlsIdleTimeout };

    /// <summary>Holds back a tap's hide until the double-tap window has passed; see <see cref="HandleTap"/>.</summary>
    private readonly DispatcherTimer tapHideTimer = new() { Interval = DoubleTapWindow };

    /// <summary>Holds back a click's play/pause; see <see cref="QueueClickPlayPause"/>.</summary>
    private readonly DispatcherTimer clickPlayPauseTimer = new();

    private readonly DispatcherTimer holdTimer = new() { Interval = HoldToBoostDelay };

    /// <summary>Takes the toast away again; see <see cref="ShowToast"/>.</summary>
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };

    // Placing the bubble by transform rather than by margin: a margin would feed its own offset
    // back into the layout it is measured from.
    private readonly TranslateTransform seekPreviewOffset = new();
    private readonly TranslateTransform playlistOffset = new();
    private readonly Glide playlistGlide;
    private readonly Stopwatch tapClock = Stopwatch.StartNew();

    private bool draggingSeekBar;

    private VideoPlayerPageViewModel? subscribedModel;
    private bool deckInitialized;
    private bool deckLayoutPending;
    private (Size Left, Size Right, Size Playback) deckButtonSizes;
    private double playlistTravel = 300d;
    private bool playlistClosing;

    // The touch gesture in flight, if any: at most one finger drives the picture.
    private int? touchPointerId;
    private Point touchOrigin;
    private double touchOriginSeconds;
    private bool touchMoved;
    private bool touchBoosted;
    private bool touchSwiping;
    private bool touchDismissesPlaylist;
    private double swipeTargetSeconds;
    private TimeSpan lastTapAt;
    private TimeSpan mousePressAt;
    private PointerType? contextPointerType;

    public VideoPlayerPageView()
    {
        InitializeComponent();

        Stage.AddHandler(PointerPressedEvent, (_, e) => contextPointerType = e.Pointer.Type,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
        Stage.ContextFlyout!.Closed += (_, _) => ShowControls();

        deckInitialized = true;
        LayoutUpdated += (_, _) => UpdateDeckNavigation();
        UpdateDeckLayout();

        SeekPreview.RenderTransform = seekPreviewOffset;
        PlaylistPanel.RenderTransform = playlistOffset;
        playlistGlide = new Glide(this, ApplyPlaylistOffset);

        uiTimer.Tick += (_, _) => ViewModel?.Tick(draggingSeekBar);
        hideControlsTimer.Tick += (_, _) => HideControls();
        tapHideTimer.Tick += (_, _) => HideControlsNow();
        clickPlayPauseTimer.Tick += (_, _) => CommitClickPlayPause();
        holdTimer.Tick += (_, _) => BeginSpeedBoost();
        toastTimer.Tick += (_, _) => HideToast();

        // Scrubbing is deferred by the bar itself: while the thumb is held the picture stays put,
        // only the bubble over the thumb moves, and the seek is issued once on release.
        SeekBar.SeekRequested += (_, e) => ViewModel?.Seek(e.Position);
        SeekBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == MediaSeekBar.IsDraggingProperty)
            {
                draggingSeekBar = SeekBar.IsDragging;
                SeekPreview.IsVisible = draggingSeekBar;
                UpdateSeekPreview();
            }
            else if (e.Property == MediaSeekBar.DisplayPositionProperty && draggingSeekBar)
            {
                UpdateSeekPreview();
            }
        };
        ToolTip.SetShowDelay(SeekBar, 150);
        SeekBar.PointerMoved += OnSeekPointerMoved;

        Video.PropertyChanged += (_, args) =>
        {
            if (args.Property == VideoView.AutomaticToneMappingProperty || args.Property == VideoView.DisplayToneTargetProperty)
                UpdateToneMappingControls();
        };

        DataContextChanged += (_, _) => AttachViewModel();

        AttachedToVisualTree += async (_, _) =>
        {
            uiTimer.Start();
            ShowControls();
            if (OperatingSystem.IsBrowser() && TopLevel.GetTopLevel(this)?.Screens is { } screens)
            {
                try
                {
                    await screens.RequestScreenDetails();
                }
                catch (Exception exception)
                {
                    AppLog.Warning("Video", "Could not request browser screen details.", exception);
                }
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Stage.ContextFlyout?.Hide();
            uiTimer.Stop();
            hideControlsTimer.Stop();
            tapHideTimer.Stop();
            clickPlayPauseTimer.Stop();
            playlistGlide.Stop();
            EndTouchGesture(commit: false);
            HideToast();
        };

    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
            RequestDeckLayout();
    }

    private void RequestDeckLayout()
    {
        if (!deckInitialized || deckLayoutPending)
            return;

        deckLayoutPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            deckLayoutPending = false;
            UpdateDeckLayout();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateDeckLayout()
    {
        if (!deckInitialized)
            return;

        var compact = Bounds.Width < CompactDeckThreshold;
        Classes.Set("compactDeck", compact);
        Classes.Set("tightDeck", Bounds.Width < 360d);
        var playButtonSize = compact ? 44d : 56d;
        var playGlyphSize = compact ? 18d : 30d;
        PlayPauseButton.MinWidth = playButtonSize;
        PlayPauseButton.MinHeight = playButtonSize;
        PlayPauseGlyph.Width = playGlyphSize;
        PlayPauseGlyph.Height = playGlyphSize;
        UpdateSettingsPopupSize();
        UpdateDeckNavigation();
    }

    private void OnSettingsFlyoutOpening(object? sender, EventArgs args)
    {
        UpdateSettingsPopupSize();
        UpdateToneMappingControls();
    }

    private void UpdateSettingsPopupSize()
    {
        SettingsContent.Width = Math.Max(160, Math.Min(280, Bounds.Width - 64));
        SettingsScroll.MaxHeight = Math.Max(80, Math.Min(460, Bounds.Height - 100));
    }

    private void UpdateToneMappingControls()
    {
        if (ViewModel is { } model)
        {
            model.ToneProfiles.Editor.AutomaticOptions = Video.AutomaticToneMapping;
            model.ToneProfiles.Editor.DisplayTarget = Video.DisplayToneTarget;
        }
    }

    private void UpdateDeckNavigation()
    {
        var sizes = (LeftButtons.DesiredSize, RightButtons.DesiredSize, PlaybackButtons.DesiredSize);
        if (deckButtonSizes != sizes)
        {
            deckButtonSizes = sizes;
            LeftButtons.InvalidateArrange();
            RightButtons.InvalidateArrange();
            PlaybackButtons.InvalidateArrange();
        }

        var pairWidth = 2d * ((Bounds.Width < 360d ? 32d : Bounds.Width < CompactDeckThreshold ? 36d : 44d) + 2d);
        var sideWidth = Math.Max(LeftButtons.DesiredSize.Width, RightButtons.DesiredSize.Width);
        var navigationPairs = (ViewModel?.CanSeek == true ? 1 : 0) +
            (ViewModel?.HasChapters == true ? 1 : 0) +
            (ViewModel?.IsPlaylistAvailable == true ? 1 : 0);
        var maximumWidth = PlayPauseButton.MinWidth + navigationPairs * pairWidth +
            2d * (sideWidth + 100d) + DeckLayout.Margin.Left + DeckLayout.Margin.Right;
        ControlDeck.Width = Math.Max(0, Math.Min(maximumWidth, Bounds.Width - 24d));

        var remaining = DeckControlsRow.Bounds.Width - 2d * (sideWidth + 8d) - PlayPauseButton.MinWidth;
        SetNavigationVisibility("hideSeekSteps", ViewModel?.CanSeek == true);
        SetNavigationVisibility("hideChapterSteps", ViewModel?.HasChapters == true);
        SetNavigationVisibility("hideVideoSteps", ViewModel?.IsPlaylistAvailable == true);

        void SetNavigationVisibility(string className, bool available)
        {
            var show = available && remaining >= pairWidth;
            PlaybackButtons.Classes.Set(className, !show);
            if (show && available)
                remaining -= pairWidth;
        }
    }

    private VideoPlayerPageViewModel? ViewModel => DataContext as VideoPlayerPageViewModel;

    /// <summary>Puts the timestamp bubble over the thumb, kept inside the bar's own width.</summary>
    private void UpdateSeekPreview()
    {
        if (!draggingSeekBar || ViewModel is null)
        {
            return;
        }

        SeekPreviewText.Text = ViewModel.IsLiveStream
            ? SeekBar.DisplayPosition >= ViewModel.LiveWindowEndSeconds -
                Math.Max(1, ViewModel.LiveTargetDurationSeconds * 1.5)
                ? Strings.Resources.VideoPlayer_Live
                : "-" + FormatClock(ViewModel.LiveWindowEndSeconds - SeekBar.DisplayPosition)
            : FormatClock(SeekBar.DisplayPosition);

        var size = SeekPreview.Bounds.Size;
        var track = SeekBar.Bounds.Width;
        if (size.Width <= 0 || track <= 0)
        {
            // Shown but not arranged yet; place it once the layout pass has run.
            Dispatcher.UIThread.Post(UpdateSeekPreview, DispatcherPriority.Loaded);
            return;
        }

        seekPreviewOffset.X = Math.Clamp(SeekBar.ThumbOffset - (size.Width / 2), 0, Math.Max(0, track - size.Width));
        seekPreviewOffset.Y = -(size.Height + 6);
    }

    private void OnStagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Touch)
        {
            UpdateTouchGesture(e);
            return;
        }

        if (e.Pointer.Type == PointerType.Mouse && IsPointerAtDisplayEdge(e))
        {
            HideControlsNow();
            return;
        }

        ShowControls();
    }

    private void OnStagePointerExited(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse)
        {
            HideControlsNow();
        }
    }

    private bool IsPointerAtDisplayEdge(PointerEventArgs e)
    {
        if (ViewModel is not { IsFullScreen: true } ||
            TopLevel.GetTopLevel(Stage) is not { Screens: { } screens } topLevel)
        {
            return false;
        }

        var position = Stage.PointToScreen(e.GetPosition(Stage));
        if (screens.ScreenFromPoint(position) is not { } screen)
        {
            return false;
        }

        var bounds = screen.Bounds;
        return position.X <= bounds.X || position.X >= bounds.Right - 1 ||
               position.Y <= bounds.Y || position.Y >= bounds.Bottom - 1;
    }

    /// <summary>
    /// Starts the touch gestures the picture answers: a hold that runs it fast, and a sideways
    /// swipe that seeks. A press that lands on the deck, the title bar or any control is left
    /// alone, and one that lands on a control never reaches here at all.
    /// </summary>
    private void OnStagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // A pending hide belongs to the tap before this one; this press decides its fate.
        tapHideTimer.Stop();

        if (e.Pointer.Type != PointerType.Touch)
        {
            // Where the double-click window this press opened runs from; see QueueClickPlayPause.
            mousePressAt = tapClock.Elapsed;
            return;
        }

        if (ViewModel is not { IsSubtitleBrowserOpen: false } model ||
            !IsPictureHit(e.Source))
        {
            return;
        }

        EndTouchGesture(commit: false);

        touchPointerId = e.Pointer.Id;
        touchOrigin = e.GetPosition(Stage);
        touchOriginSeconds = model.PositionSeconds;

        // With the list up the picture only dismisses it: nothing this finger does reaches playback.
        touchDismissesPlaylist = model.IsPlaylistOpen;
        if (!touchDismissesPlaylist)
        {
            holdTimer.Start();
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (contextPointerType != PointerType.Mouse || !IsPictureHit(e.Source) ||
            ViewModel is not { IsSubtitleBrowserOpen: false, AreShortcutsVisible: false, IsMinimized: false } model)
        {
            e.Handled = true;
            return;
        }

        ChapterMenu.Items.Clear();
        foreach (var chapter in model.Chapters)
        {
            ChapterMenu.Items.Add(new MenuItem
            {
                Header = chapter.DisplayName,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = ReferenceEquals(chapter, model.SelectedChapter),
                Command = new RelayCommand(() => model.Seek(chapter.Start.TotalSeconds)),
            });
        }
        ChapterMenu.IsVisible = ChapterMenu.Items.Count > 0;
        clickPlayPauseTimer.Stop();
        ShowControls();
    }

    private void OnStagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (touchPointerId == e.Pointer.Id)
        {
            EndTouchGesture(commit: true);
        }
    }

    private void OnStagePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (touchPointerId == e.Pointer.Id)
        {
            EndTouchGesture(commit: false);
        }
    }

    private void UpdateTouchGesture(PointerEventArgs e)
    {
        if (touchPointerId != e.Pointer.Id || touchDismissesPlaylist || ViewModel is not { } model)
        {
            return;
        }

        var point = e.GetPosition(Stage);
        var dx = point.X - touchOrigin.X;
        var dy = point.Y - touchOrigin.Y;

        if (!touchSwiping)
        {
            if (touchBoosted || (Math.Abs(dx) < SwipeThresholdPx && Math.Abs(dy) < SwipeThresholdPx))
            {
                return;
            }

            // Traveled: this is no longer a tap, whatever it turns into.
            touchMoved = true;
            holdTimer.Stop();

            if (Math.Abs(dx) <= Math.Abs(dy) || !model.CanSeek)
            {
                return;
            }

            touchSwiping = true;
            e.Pointer.Capture(Stage);
            SwipeSeekIndicator.IsVisible = true;
        }

        var width = Math.Max(1d, Stage.Bounds.Width);
        swipeTargetSeconds = Math.Clamp(
            touchOriginSeconds + (dx / width * SwipeSecondsPerWidth),
            model.SeekMinimumSeconds,
            model.SeekMaximumSeconds);

        // Measured from where the finger went down, not from the running position, so the figure
        // holds still while the finger does.
        var delta = swipeTargetSeconds - touchOriginSeconds;
        SwipeSeekTargetText.Text = FormatClock(swipeTargetSeconds);
        SwipeSeekDeltaText.Text = (delta < 0 ? "-" : "+") + FormatClock(Math.Abs(delta));
    }

    /// <summary>
    /// Ends the gesture in flight and carries out what it asked for, if anything: a seek to where
    /// the swipe left off, a dismissal of the video list, or the tap that a still finger amounts to.
    /// </summary>
    private void EndTouchGesture(bool commit)
    {
        if (touchPointerId is null)
        {
            return;
        }

        var swiped = touchSwiping;
        var tapped = !touchMoved && !touchBoosted;
        var dismiss = touchDismissesPlaylist;
        var target = swipeTargetSeconds;

        touchPointerId = null;
        touchMoved = false;
        touchSwiping = false;
        touchDismissesPlaylist = false;
        holdTimer.Stop();
        SwipeSeekIndicator.IsVisible = false;

        if (touchBoosted)
        {
            touchBoosted = false;
            ViewModel?.SetSpeedBoost(false);
        }

        if (!commit || ViewModel is not { } model)
        {
            return;
        }

        if (dismiss)
        {
            model.IsPlaylistOpen = false;
            return;
        }

        if (swiped)
        {
            // A swipe that lands where the picture already is was a wobble, not a seek.
            if (Math.Abs(target - model.PositionSeconds) >= SwipeMinimumSeconds)
            {
                model.Seek(target);
                ShowControls();
            }

            return;
        }

        if (tapped)
        {
            HandleTap(model);
        }
    }

    private void BeginSpeedBoost()
    {
        holdTimer.Stop();
        if (touchPointerId is null || touchSwiping || ViewModel is not { } model)
        {
            return;
        }

        touchBoosted = true;
        model.SetSpeedBoost(true);
    }

    /// <summary>
    /// A tap of the picture shows or hides the deck; two in quick succession play or pause instead.
    /// The hide waits out the double-tap window so the deck does not blink on the way to a pause.
    /// </summary>
    private void HandleTap(VideoPlayerPageViewModel model)
    {
        var now = tapClock.Elapsed;
        var isDoubleTap = lastTapAt > TimeSpan.Zero && now - lastTapAt <= DoubleTapWindow;
        lastTapAt = isDoubleTap ? TimeSpan.Zero : now;

        if (isDoubleTap)
        {
            model.PlayPauseCommand.Execute(null);
            ShowControls();
        }
        else if (model.AreControlsVisible)
        {
            hideControlsTimer.Stop();
            tapHideTimer.Start();
        }
        else
        {
            ShowControls();
        }
    }

    private static string FormatClock(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss");

    /// <summary>
    /// Closes the subtitle menu once one of its sources is chosen: the picker it opens is a window
    /// of its own, and the browser is drawn over the picture the flyout sits on. Deferred by a
    /// dispatcher turn because hiding a flyout unparents its content, which drops the command
    /// binding the click has not run yet.
    /// </summary>
    private void OnSubtitleSourceClick(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => SubtitleMenuButton.Flyout?.Hide());

    /// <summary>The shortcut list is drawn over the picture the settings menu sits on.</summary>
    private void OnShortcutsClick(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => SettingsMenuButton.Flyout?.Hide());

    /// <summary>A click on the dim around the shortcut card puts it away; one on the card does not.</summary>
    private void OnShortcutsBackgroundTapped(object? sender, TappedEventArgs e)
    {
        if (ReferenceEquals(e.Source, ShortcutsPanel) && ViewModel is { } model)
        {
            model.AreShortcutsVisible = false;
        }
    }

    /// <summary>
    /// Names the chapter the pointer is over, so the breaks along the seek bar read as a chapter
    /// list rather than as unlabeled gaps.
    /// </summary>
    private void OnSeekPointerMoved(object? sender, PointerEventArgs e)
    {
        if (ViewModel is not { Chapters.Count: > 1, DurationSeconds: > 0 } model)
        {
            ToolTip.SetTip(SeekBar, null);
            return;
        }

        var seconds = SeekBar.PositionAt(e.GetPosition(SeekBar).X);
        var title = model.Chapters.LastOrDefault(chapter => chapter.Start.TotalSeconds <= seconds)?.DisplayName;
        if (!Equals(ToolTip.GetTip(SeekBar), title))
        {
            ToolTip.SetTip(SeekBar, title);
        }
    }

    private void OnStageTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is not { } model)
        {
            return;
        }

        // The subtitle browser covers the picture, so nothing behind it answers a click.
        if (model.IsSubtitleBrowserOpen)
        {
            return;
        }

        var onPicture = IsPictureHit(e.Source);
        if (!onPicture)
        {
            // Working the deck or a menu keeps them up: the idle timer is about a picture left alone.
            ShowControls();
        }

        // Touch is driven by the gesture handlers above, which tell a tap from a hold or a swipe.
        if (TryDismissPlaylist(e.Source) || e.Pointer.Type == PointerType.Touch || !onPicture)
        {
            return;
        }

        // With the deck down the click is spent bringing it back up.
        if (model.AreControlsVisible)
        {
            QueueClickPlayPause(e.Pointer.Type);
        }

        ShowControls();
    }

    /// <summary>
    /// Holds a click's play/pause back until a second press could no longer arrive, because a
    /// double click of the picture goes full screen instead and the toolkit only says so on that
    /// second press. The wait is what is left of the platform's double-click window, measured from
    /// the press rather than the release, so a click held for a moment is answered that much sooner.
    /// </summary>
    private void QueueClickPlayPause(PointerType pointerType)
    {
        var window = this.GetPlatformSettings()?.GetDoubleTapTime(pointerType) ?? DoubleTapWindow;
        var remaining = window - (tapClock.Elapsed - mousePressAt);

        clickPlayPauseTimer.Stop();
        if (remaining <= TimeSpan.Zero)
        {
            ViewModel?.PlayPauseCommand.Execute(null);
            return;
        }

        clickPlayPauseTimer.Interval = remaining;
        clickPlayPauseTimer.Start();
    }

    private void CommitClickPlayPause()
    {
        clickPlayPauseTimer.Stop();
        ViewModel?.PlayPauseCommand.Execute(null);
    }

    /// <summary>
    /// Whether a click reached the picture itself rather than the deck, the title bar or one of the
    /// panels drawn over it. Anything in a flyout leaves the page's own tree, and counts as neither.
    /// </summary>
    private bool IsPictureHit(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, Stage))
            {
                return true;
            }

            if (ReferenceEquals(current, TitleBar) ||
                ReferenceEquals(current, ControlDeck) ||
                ReferenceEquals(current, PlaylistPanel) ||
                ReferenceEquals(current, SubtitleBrowser) ||
                ReferenceEquals(current, ShortcutsPanel) ||
                ReferenceEquals(current, StatisticsPanel) ||
                ReferenceEquals(current, PictureInPicturePanel) ||
                ReferenceEquals(current, ErrorPanel))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Puts the video list away when the click landed anywhere else, and answers whether it did:
    /// that click is spent on closing the list and must not also toggle playback.
    /// </summary>
    private bool TryDismissPlaylist(object? source)
    {
        if (ViewModel is not { IsPlaylistOpen: true } model || IsInteractive(source) || IsWithin(source, PlaylistPanel))
        {
            return false;
        }

        model.IsPlaylistOpen = false;
        return true;
    }

    private static bool IsWithin(object? source, Visual container) =>
        source is Visual visual &&
        (ReferenceEquals(visual, container) || visual.GetVisualAncestors().Contains(container));

    /// <summary>Whether a click landed on something that answers it itself rather than the picture.</summary>
    private static bool IsInteractive(object? source) =>
        source is not Control control ||
        control.FindAncestorOfType<Button>(includeSelf: true) is not null ||
        control.FindAncestorOfType<Slider>(includeSelf: true) is not null ||
        control.FindAncestorOfType<MediaSeekBar>(includeSelf: true) is not null ||
        control.FindAncestorOfType<ComboBox>(includeSelf: true) is not null ||
        control.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is not null;

    private void OnStageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch && IsPictureHit(e.Source))
        {
            // The first click of the pair queued a play/pause; full screen takes its place.
            clickPlayPauseTimer.Stop();
            ViewModel?.ToggleFullScreenCommand.Execute(null);
        }
    }

    private void AttachViewModel()
    {
        if (subscribedModel is not null)
        {
            subscribedModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        subscribedModel = ViewModel;
        if (subscribedModel is not null)
        {
            subscribedModel.PropertyChanged += OnViewModelPropertyChanged;
            SetPlaylistOpen(subscribedModel.IsPlaylistOpen, animate: false);
        }

        UpdateToneMappingControls();
        ApplyStageCursor();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoPlayerPageViewModel.IsPlaylistOpen) && ViewModel is { } model)
        {
            SetPlaylistOpen(model.IsPlaylistOpen, animate: true);
        }
        else if (e.PropertyName == nameof(VideoPlayerPageViewModel.AreControlsVisible))
        {
            ApplyStageCursor();
        }
        else if (e.PropertyName == nameof(VideoPlayerPageViewModel.IsMinimized) && ViewModel is { } minimized)
        {
            // The corner card runs its own idle countdown, so this one must not also be pending
            // against a deck that is no longer on screen.
            if (minimized.IsMinimized)
            {
                hideControlsTimer.Stop();
                tapHideTimer.Stop();
            }
            else
            {
                ShowControls();
            }
        }
        else if (e.PropertyName == nameof(VideoPlayerPageViewModel.IsPictureInPicture) &&
                 ViewModel is { IsPictureInPicture: false })
        {
            // The detached view had the pipeline's only sink; frames come back here on the asking.
            Video.ClaimVideo();
        }
    }

    /// <summary>
    /// The pointer goes away with the deck. Set on the stage because the property inherits, so it
    /// reaches the picture and the scrims alike; the transport buttons name their own cursor and
    /// so keep it while they are up.
    /// </summary>
    private void ApplyStageCursor() =>
        Stage.Cursor = ViewModel is { AreControlsVisible: false } ? HiddenCursor : null;

    /// <summary>Slides the video list in from the right edge, and back out over it when it closes.</summary>
    private void SetPlaylistOpen(bool open, bool animate)
    {
        playlistTravel = (double.IsNaN(PlaylistPanel.Width)
            ? Math.Max(1d, PlaylistPanel.Bounds.Width)
            : PlaylistPanel.Width) + PlaylistPanel.Margin.Right;

        if (open)
        {
            playlistClosing = false;
            if (!PlaylistPanel.IsVisible)
            {
                playlistOffset.X = animate ? playlistTravel : 0d;
                PlaylistPanel.IsVisible = true;
            }

            if (animate)
            {
                playlistGlide.Start(playlistOffset.X, 0d, PlaylistSlideMs);
            }
            else
            {
                playlistGlide.Stop();
                playlistOffset.X = 0d;
            }

            ScrollPlaylistToCurrent();
            return;
        }

        if (animate && PlaylistPanel.IsVisible)
        {
            playlistClosing = true;
            playlistGlide.Start(playlistOffset.X, playlistTravel, PlaylistSlideMs);
            return;
        }

        playlistGlide.Stop();
        playlistClosing = false;
        playlistOffset.X = 0d;
        PlaylistPanel.IsVisible = false;
    }

    private void ApplyPlaylistOffset(double offset)
    {
        playlistOffset.X = offset;

        if (playlistClosing && offset >= playlistTravel - 0.5)
        {
            playlistClosing = false;
            PlaylistPanel.IsVisible = false;
        }
    }

    /// <summary>Opens the list on the entry playing now rather than at the top of the folder.</summary>
    private void ScrollPlaylistToCurrent()
    {
        if (ViewModel is not { } model)
        {
            return;
        }

        var index = -1;
        for (var i = 0; i < model.Playlist.Count; i++)
        {
            if (model.Playlist[i].IsCurrent)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        // The rows are only built once the panel has been laid out, which this waits for.
        Dispatcher.UIThread.Post(
            () =>
            {
                // Aligned to the top rather than merely brought into view: the entries below the
                // one playing are the ones the list is there to show.
                if (PlaylistItems.ContainerFromIndex(index) is { } container &&
                    container.TranslatePoint(default, PlaylistItems) is { } origin)
                {
                    PlaylistScroll.Offset = new Vector(PlaylistScroll.Offset.X, origin.Y);
                }
            },
            DispatcherPriority.Loaded);
    }

    internal void ShowControls()
    {
        tapHideTimer.Stop();

        if (ViewModel is { } model)
        {
            model.AreControlsVisible = true;
        }

        hideControlsTimer.Stop();
        hideControlsTimer.Start();
    }

    private void HideControls()
    {
        hideControlsTimer.Stop();

        // Keep the deck up while paused: there is nothing to watch behind it.
        if (ViewModel is { IsPlaying: true } model && !model.IsPlaylistOpen && Stage.ContextFlyout?.IsOpen != true)
        {
            model.AreControlsVisible = false;
        }
    }

    /// <summary>Puts the deck away because it was asked for, whatever the picture is doing.</summary>
    private void HideControlsNow()
    {
        tapHideTimer.Stop();
        hideControlsTimer.Stop();

        if (ViewModel is { } model && Stage.ContextFlyout?.IsOpen != true)
        {
            model.AreControlsVisible = false;
        }
    }

    /// <summary>
    /// Flashes a word over the picture. Volume, mute, speed and the tracks are all worked by keys
    /// that leave no other sign of what they did when the deck is down or the menu holding them shut.
    /// </summary>
    internal void ShowToast(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        StatusToastText.Text = text;
        StatusToast.IsVisible = true;
        toastTimer.Stop();
        toastTimer.Start();
    }

    private void HideToast()
    {
        toastTimer.Stop();
        StatusToast.IsVisible = false;
    }
}
