using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.ViewModels;

namespace LightStudio.LightPlayer.Controls;

public partial class PlaybackBar : UserControl
{
    // At or below this width the secondary transport controls (favorite, volume,
    // mode) collapse, leaving only previous/play-pause/next.
    private const double CompactWidthThreshold = 750d;

    /// <summary>
    /// True when the bar is wide enough to show every transport control. When
    /// false the favorite, volume, and mode buttons are hidden.
    /// </summary>
    public static readonly StyledProperty<bool> ShowAllControlsProperty =
        AvaloniaProperty.Register<PlaybackBar, bool>(nameof(ShowAllControls), defaultValue: true);

    // Below this width touch chrome leaves out the remaining time, as iOS mini players do, so the
    // title and progress keep their room.
    private const double TouchTimeWidthThreshold = 480d;

    /// <summary>Whether the bar shows the remaining time beside the progress slider.</summary>
    public static readonly StyledProperty<bool> ShowRemainingTimeProperty =
        AvaloniaProperty.Register<PlaybackBar, bool>(nameof(ShowRemainingTime), defaultValue: true);

    public PlaybackBar()
    {
        InitializeComponent();

        ProgressSlider.ScrubStarted += OnProgressScrubStarted;
        ProgressSlider.ScrubCompleted += OnProgressScrubCompleted;
    }

    private PlaybackBarViewModel? ViewModel => DataContext as PlaybackBarViewModel;

    public bool ShowAllControls
    {
        get => GetValue(ShowAllControlsProperty);
        set => SetValue(ShowAllControlsProperty, value);
    }

    public bool ShowRemainingTime
    {
        get => GetValue(ShowRemainingTimeProperty);
        set => SetValue(ShowRemainingTimeProperty, value);
    }

    private void OnProgressScrubStarted(object? sender, EventArgs e) =>
        ViewModel?.BeginScrub();

    private void OnProgressScrubCompleted(object? sender, EventArgs e) =>
        ViewModel?.EndScrub();

    private void OnAudioSeparationClick(object? sender, RoutedEventArgs e)
    {
        if (NativeContextMenu.GetMenu(AudioSeparationButton) is { } menu)
            AudioSeparationMenu.Populate(menu.Items, ViewModel);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ShowAllControls = e.NewSize.Width > CompactWidthThreshold;
        ShowRemainingTime = !PlatformServices.UsesTouchChrome || e.NewSize.Width > TouchTimeWidthThreshold;
    }
}
