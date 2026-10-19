using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Icon button that appends track(s) to the play queue and briefly confirms the action by
/// swapping its glyph from a plus to a check mark for a couple of seconds. While the check is
/// showing, further clicks are swallowed so the same items can't be queued again by accident.
/// It renders its own icon, so call sites only supply the button <c>Classes</c>
/// (<c>icon</c>/<c>miniIcon</c>), the <see cref="IconSize"/>, the command and the tooltip.
/// </summary>
public class AddToQueueButton : Button
{
    private static readonly TimeSpan ConfirmationDuration = TimeSpan.FromSeconds(2);

    /// <summary>Size of the icon Viewbox.</summary>
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<AddToQueueButton, double>(nameof(IconSize), 18d);

    private readonly Path _icon;
    private Geometry? _addGeometry;
    private Geometry? _checkGeometry;
    private IDisposable? _resetTimer;
    private bool _confirming;

    public AddToQueueButton()
    {
        _icon = new Path();
        _icon.Bind(Shape.FillProperty, this.GetObservable(TemplatedControl.ForegroundProperty));

        // The global "Viewbox > Path" style normalizes and centers the glyph.
        var iconHost = new Viewbox { Child = _icon };
        iconHost.Bind(Layoutable.WidthProperty, this.GetObservable(IconSizeProperty));
        iconHost.Bind(Layoutable.HeightProperty, this.GetObservable(IconSizeProperty));

        Content = iconHost;
    }

    // Be themed exactly like the plain icon buttons authored in XAML (Button.icon / .miniIcon
    // selectors match on the style key rather than the runtime type).
    protected override Type StyleKeyOverride => typeof(Button);

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    protected override void OnClick()
    {
        if (_confirming)
        {
            return;
        }

        base.OnClick();
        StartConfirmation();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Icon resources only resolve once attached; recycled tiles/rows also re-attach here.
        UpdateGlyph();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ResetConfirmation();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // Virtualized tiles/rows recycle this control for another item; drop any pending
        // confirmation so a stale check doesn't carry over to the new content.
        ResetConfirmation();
    }

    private void StartConfirmation()
    {
        _confirming = true;
        UpdateGlyph();

        _resetTimer?.Dispose();
        _resetTimer = DispatcherTimer.RunOnce(ResetConfirmation, ConfirmationDuration);
    }

    private void ResetConfirmation()
    {
        _resetTimer?.Dispose();
        _resetTimer = null;

        if (!_confirming)
        {
            return;
        }

        _confirming = false;
        UpdateGlyph();
    }

    private void UpdateGlyph()
    {
        _addGeometry ??= FindGeometry("IconAdd");
        _checkGeometry ??= FindGeometry("IconCheck");
        _icon.Data = _confirming ? _checkGeometry : _addGeometry;
    }

    private Geometry? FindGeometry(string key) =>
        this.TryFindResource(key, out var resource) ? resource as Geometry : null;
}
