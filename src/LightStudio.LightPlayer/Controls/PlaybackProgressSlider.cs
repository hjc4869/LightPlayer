using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LightStudio.LightPlayer.Controls;

public sealed class PlaybackProgressSlider : Slider
{
    private IPointer? scrubPointer;

    public PlaybackProgressSlider()
    {
        AddHandler(PointerPressedEvent, OnScrubPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnScrubMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnScrubReleased, RoutingStrategies.Tunnel);
    }

    protected override Type StyleKeyOverride => typeof(Slider);

    public event EventHandler? ScrubStarted;

    public event EventHandler? ScrubCompleted;

    private void OnScrubPressed(object? sender, PointerPressedEventArgs args)
    {
        if (scrubPointer is not null || Track is null || Maximum <= Minimum ||
            !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        scrubPointer = args.Pointer;
        args.Pointer.Capture(this);
        ScrubStarted?.Invoke(this, EventArgs.Empty);
        UpdatePosition(args);
        args.Handled = true;
    }

    private void OnScrubMoved(object? sender, PointerEventArgs args)
    {
        if (scrubPointer != args.Pointer)
        {
            return;
        }

        UpdatePosition(args);
        args.Handled = true;
    }

    private void OnScrubReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (scrubPointer != args.Pointer || args.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        UpdatePosition(args);
        scrubPointer = null;
        args.Pointer.Capture(null);
        ScrubCompleted?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs args)
    {
        base.OnPointerCaptureLost(args);

        if (scrubPointer == args.Pointer)
        {
            scrubPointer = null;
            ScrubCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdatePosition(PointerEventArgs args)
    {
        if (Track is not { } track)
        {
            return;
        }

        var thumbWidth = track.Thumb?.Bounds.Width ?? 0;
        var travel = track.Bounds.Width - thumbWidth;
        if (travel <= 0)
        {
            return;
        }

        var fraction = Math.Clamp((args.GetPosition(track).X - (thumbWidth / 2)) / travel, 0, 1);
        if (IsDirectionReversed)
        {
            fraction = 1 - fraction;
        }

        SetCurrentValue(ValueProperty, Minimum + (fraction * (Maximum - Minimum)));
    }
}