using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    public PlaybackBar()
    {
        InitializeComponent();

        // Scrub the progress slider: suppress engine position updates while the
        // user drags, then seek to the released value. handledEventsToo ensures
        // the events still arrive after the slider handles them internally.
        ProgressSlider.AddHandler(
            PointerPressedEvent,
            OnProgressPointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        ProgressSlider.AddHandler(
            PointerReleasedEvent,
            OnProgressPointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    private PlaybackBarViewModel? ViewModel => DataContext as PlaybackBarViewModel;

    public bool ShowAllControls
    {
        get => GetValue(ShowAllControlsProperty);
        set => SetValue(ShowAllControlsProperty, value);
    }

    private void OnProgressPointerPressed(object? sender, PointerPressedEventArgs e) =>
        ViewModel?.BeginScrub();

    private void OnProgressPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        ViewModel?.EndScrub();

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) =>
        ShowAllControls = e.NewSize.Width > CompactWidthThreshold;
}
