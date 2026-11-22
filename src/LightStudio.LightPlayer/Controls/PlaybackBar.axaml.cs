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

    // The width a divided bar's controls need to show every control: eight touch buttons and padding.
    private const double DividedAllControlsWidth = 420d;

    // The space the track details keep before a division, besides their own margin.
    private const double DividedDetailsEndPadding = 16d;

    /// <summary>
    /// The area, in the bar's coordinates, where a fold divides the bar: the track details keep before it
    /// and the controls center after it. Empty when the bar is whole.
    /// </summary>
    public static readonly StyledProperty<Rect> DivisionProperty =
        AvaloniaProperty.Register<PlaybackBar, Rect>(nameof(Division));

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

    public Rect Division
    {
        get => GetValue(DivisionProperty);
        set => SetValue(DivisionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DivisionProperty)
            UpdateContentLayout(Bounds.Width);
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

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateContentLayout(e.NewSize.Width);

    private void UpdateContentLayout(double width)
    {
        var division = Division;
        bool divided = division.Width > 0;
        double controlsWidth = Math.Max(0, width - division.Right);
        BarContent.Margin = new Thickness(24, 0, divided ? 0 : 24, 0);
        BarContent.ColumnDefinitions[3].Width = new GridLength(divided ? division.Width + DividedDetailsEndPadding : 0);
        BarContent.ColumnDefinitions[4].Width = divided ? new GridLength(controlsWidth) : GridLength.Auto;
        ShowAllControls = divided ? controlsWidth >= DividedAllControlsWidth : width > CompactWidthThreshold;
        ShowRemainingTime = !PlatformServices.UsesTouchChrome || (divided ? division.X : width) > TouchTimeWidthThreshold;
    }
}
