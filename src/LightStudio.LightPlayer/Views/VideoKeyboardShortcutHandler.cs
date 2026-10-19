using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Reactive;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.ViewModels;
using LightStudio.LightPlayer.ViewModels.Pages;
using LightStudio.LightPlayer.Views.Pages;

namespace LightStudio.LightPlayer.Views;

internal sealed class VideoKeyboardShortcutHandler : IDisposable
{
    private readonly Control host;
    private readonly bool useGlobalHandlers;
    private readonly VideoPlayerPageViewModel? standalonePlayer;
    private VideoSheetViewModel? sheet;
    private bool isRegistered;
    private IDisposable? previewSubscription;
    private IDisposable? routeFinishedSubscription;

    public VideoKeyboardShortcutHandler(Control host, bool useGlobalHandlers = false,
        VideoPlayerPageViewModel? standalonePlayer = null)
    {
        this.host = host;
        this.useGlobalHandlers = useGlobalHandlers;
        this.standalonePlayer = standalonePlayer;
        UpdateRegistration();
    }

    public void Attach(VideoSheetViewModel? value)
    {
        if (ReferenceEquals(sheet, value))
        {
            UpdateRegistration();
            return;
        }

        Detach();
        sheet = value;

        if (sheet is not null)
        {
            sheet.PropertyChanged += OnSheetPropertyChanged;
        }

        UpdateRegistration();
    }

    public void Dispose() => Detach();

    private void Detach()
    {
        SetRegistered(false);

        if (sheet is not null)
        {
            sheet.PropertyChanged -= OnSheetPropertyChanged;
            sheet = null;
        }
    }

    private void OnSheetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName == nameof(VideoSheetViewModel.Content) ||
            e.PropertyName == nameof(VideoSheetViewModel.IsExpanded) ||
            e.PropertyName == nameof(VideoSheetViewModel.IsClosing) ||
            e.PropertyName == nameof(VideoSheetViewModel.IsPictureDetached))
        {
            UpdateRegistration();
        }
    }

    private VideoPlayerPageViewModel? ActiveModel => standalonePlayer ?? (sheet is
            {
                Content: { } player,
                IsExpanded: true,
                IsClosing: false,
                IsPictureDetached: false,
            } ? player : null);

    private void UpdateRegistration() => SetRegistered(ActiveModel is not null);

    private void SetRegistered(bool registered)
    {
        if (isRegistered == registered)
        {
            return;
        }

        isRegistered = registered;
        if (registered)
        {
            if (useGlobalHandlers)
            {
                // A popup or stale focused element can build a route that never reaches MainWindow.
                // Observe every route, then filter it back to this window and its popup roots.
                previewSubscription = InputElement.KeyDownEvent.AddClassHandler<InputElement>(
                    OnGlobalPreviewKeyDown,
                    RoutingStrategies.Tunnel);
                routeFinishedSubscription = InputElement.KeyDownEvent.RouteFinished.Subscribe(
                    new AnonymousObserver<RoutedEventArgs>(OnGlobalKeyRouteFinished));
            }
            else
            {
                // Tunnel routing prevents stale focus in the covered shell from consuming the key first.
                host.AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
                host.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
            }
        }
        else
        {
            if (useGlobalHandlers)
            {
                previewSubscription?.Dispose();
                previewSubscription = null;
                routeFinishedSubscription?.Dispose();
                routeFinishedSubscription = null;
            }
            else
            {
                host.RemoveHandler(InputElement.KeyDownEvent, OnPreviewKeyDown);
                host.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            }
        }
    }

    private void OnGlobalPreviewKeyDown(InputElement sender, KeyEventArgs e)
    {
        if (!IsFromActiveHost(e.Source) ||
            IsWithinPlayer(e.Source, ActivePlayerView()))
        {
            return;
        }

        OnKeyDown(sender, e);
    }

    private void OnGlobalKeyRouteFinished(RoutedEventArgs e)
    {
        if (e is not KeyEventArgs { Handled: false, Route: RoutingStrategies.Bubble } keyEvent ||
            !IsFromActiveHost(keyEvent.Source))
        {
            return;
        }

        OnKeyDown(null, keyEvent);
    }

    private bool IsFromActiveHost(object? source)
    {
        var topLevel = TopLevel.GetTopLevel(source as Avalonia.Visual);
        while (topLevel is PopupRoot popup)
        {
            topLevel = popup.ParentTopLevel;
        }

        return topLevel is not null
            ? ReferenceEquals(topLevel, host)
            : host is WindowBase { IsActive: true };
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ActiveModel is not { } model ||
            IsWithinPlayer(e.Source, FindActiveVideoPlayerView(model)))
        {
            return;
        }

        OnKeyDown(sender, e);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (TryHandleKeyDown(e.Key, e.KeyModifiers, e.KeySymbol))
        {
            e.Handled = true;
        }
    }

    private bool TryHandleKeyDown(Key key, KeyModifiers keyModifiers, string? keySymbol)
    {
        if (ActiveModel is not { } model ||
            keyModifiers.HasFlag(KeyModifiers.Alt))
        {
            return false;
        }

        var playerView = FindActiveVideoPlayerView(model);

        // Escape is the way out of every panel, including one whose input control has focus.
        if (key == Key.Escape)
        {
            DismissTopVideoPanel(model);
            ShowVideoControls(model, playerView);
            return true;
        }

        if (model.IsSubtitleBrowserOpen)
        {
            return false;
        }

        var shift = keyModifiers.HasFlag(KeyModifiers.Shift);
        var control = keyModifiers.HasFlag(KeyModifiers.Control);

        if (keySymbol == "?")
        {
            model.ToggleShortcutsCommand.Execute(null);
            ShowVideoControls(model, playerView);
            return true;
        }

        var speedDelta = keySymbol switch
        {
            "<" => -1,
            ">" => 1,
            _ => key switch
            {
                Key.OemComma when shift => -1,
                Key.OemPeriod when shift => 1,
                _ => 0,
            },
        };

        if (speedDelta != 0)
        {
            model.StepSpeed(speedDelta);
            playerView?.ShowToast(model.SelectedSpeedOption?.DisplayName ?? string.Empty);
            ShowVideoControls(model, playerView);
            return true;
        }

        switch (key)
        {
            case Key.Space or Key.K or Key.MediaPlayPause:
                model.PlayPauseCommand.Execute(null);
                break;
            case Key.MediaStop:
                model.StopCommand.Execute(null);
                break;
            case Key.Left when control:
                model.PreviousChapterCommand.Execute(null);
                break;
            case Key.Right when control:
                model.NextChapterCommand.Execute(null);
                break;
            case Key.Left:
                model.SeekRelative(shift ? -60 : -5);
                break;
            case Key.Right:
                model.SeekRelative(shift ? 60 : 5);
                break;
            case Key.J:
                model.SkipBackCommand.Execute(null);
                break;
            case Key.L:
                model.SkipForwardCommand.Execute(null);
                break;
            case Key.Home:
                model.Seek(0);
                break;
            case Key.End:
                model.Seek(model.DurationSeconds);
                break;
            case Key.P when shift:
            case Key.MediaPreviousTrack:
                model.PreviousVideoCommand.Execute(null);
                break;
            case Key.N when shift:
            case Key.MediaNextTrack:
                model.NextVideoCommand.Execute(null);
                break;
            case Key.Up:
                AdjustVideoVolume(model, playerView, 0.05);
                break;
            case Key.Down:
                AdjustVideoVolume(model, playerView, -0.05);
                break;
            case Key.M:
                model.ToggleMuteCommand.Execute(null);
                playerView?.ShowToast(model.IsMuted ? Strings.Resources.VideoPlayer_Muted : VideoVolumeText(model));
                break;
            case Key.A:
                model.CycleAudioTrackCommand.Execute(null);
                playerView?.ShowToast(model.SelectedAudioTrack?.DisplayName ?? string.Empty);
                break;
            case Key.C when shift:
                model.CycleSubtitleTrackCommand.Execute(null);
                playerView?.ShowToast(model.SelectedSubtitleOption?.DisplayName ?? string.Empty);
                break;
            case Key.C:
                model.ToggleSubtitlesCommand.Execute(null);
                break;
            case Key.F:
                model.ToggleFullScreenCommand.Execute(null);
                break;
            case Key.P:
                model.TogglePictureInPictureCommand.Execute(null);
                break;
            case Key.Q:
                model.TogglePlaylistCommand.Execute(null);
                break;
            case Key.I:
                model.AreStatisticsVisible = !model.AreStatisticsVisible;
                break;
            default:
                return false;
        }

        ShowVideoControls(model, playerView);
        return true;
    }

    private VideoPlayerPageView? ActivePlayerView() =>
        ActiveModel is { } model ? FindActiveVideoPlayerView(model) : null;

    private VideoPlayerPageView? FindActiveVideoPlayerView(VideoPlayerPageViewModel model)
    {
        foreach (var descendant in host.GetVisualDescendants())
        {
            if (descendant is VideoPlayerPageView view && ReferenceEquals(view.DataContext, model))
            {
                return view;
            }
        }

        return null;
    }

    private static bool IsWithinPlayer(object? source, VideoPlayerPageView? playerView)
    {
        if (playerView is null)
        {
            return false;
        }

        for (var current = source as Avalonia.Visual; current is not null; current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, playerView))
            {
                return true;
            }
        }

        return false;
    }

    private static void DismissTopVideoPanel(VideoPlayerPageViewModel model)
    {
        if (model.AreShortcutsVisible)
        {
            model.AreShortcutsVisible = false;
        }
        else if (model.IsSubtitleBrowserOpen)
        {
            model.CloseSubtitleBrowserCommand.Execute(null);
        }
        else if (model.IsPlaylistOpen)
        {
            model.IsPlaylistOpen = false;
        }
        else if (model.IsFullScreen)
        {
            model.IsFullScreen = false;
        }
        else if (model.CanMinimize)
        {
            model.MinimizeCommand.Execute(null);
        }
        else
        {
            model.CloseCommand.Execute(null);
        }
    }

    private static void AdjustVideoVolume(
        VideoPlayerPageViewModel model,
        VideoPlayerPageView? playerView,
        double delta)
    {
        model.AdjustVolume(delta);
        playerView?.ShowToast(model.IsMuted ? Strings.Resources.VideoPlayer_Muted : VideoVolumeText(model));
    }

    private static string VideoVolumeText(VideoPlayerPageViewModel model) =>
        $"{Strings.Resources.PlaybackBar_Volume} {model.Volume.ToString("P0", CultureInfo.CurrentCulture)}";

    private static void ShowVideoControls(VideoPlayerPageViewModel model, VideoPlayerPageView? playerView)
    {
        if (playerView is not null)
        {
            playerView.ShowControls();
        }
        else
        {
            model.AreControlsVisible = true;
        }
    }
}