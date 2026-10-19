using LightMediaRenderer.Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// The vertical drag shared by the app's bottom sheets (the now-playing card and the video player
/// card). Turns pointer travel into a 0..1 position, decides where an interrupted drag lands, and
/// glides the rest of the way with a cubic ease-out.
///
/// The class owns nothing visual: it hands each new position to the callback the sheet passes in,
/// so a drag frame or an animation frame only touches whatever that callback chooses to change.
/// </summary>
internal sealed class SheetGesture
{
    // Open/close glide. Long enough to read as a sheet rising, short enough to stay responsive.
    private const double SettleDurationMs = 300d;

    // Pointer travel before a press turns into a drag rather than a tap.
    private const double DragThresholdPx = 6d;

    // Velocity (px/ms) over the recent samples above which the release direction decides the
    // outcome, regardless of how far the card actually traveled. Below it the halfway point wins.
    private const double FlingVelocity = 0.25d;

    // Window of pointer samples used to measure the release velocity.
    private const double VelocityWindowMs = 120d;

    private readonly Glide glide;
    private readonly Action<double> apply;
    private readonly List<(double Ms, double Y)> samples = new();
    private readonly Stopwatch clock = new();

    private bool pressed;
    private bool dragging;
    private double startY;
    private double startProgress;

    public SheetGesture(Visual owner, Action<double> apply, double minimum = 0d)
    {
        this.apply = apply;
        Minimum = minimum;
        glide = new Glide(owner, Apply);
    }

    /// <summary>How far the sheet is between compact (0) and full (1).</summary>
    public double Progress { get; private set; }

    /// <summary>
    /// Lowest position the sheet can reach. 0 for one that only opens and closes; a sheet that can
    /// also be pulled off the bottom sets -1, where -1 is "gone".
    /// </summary>
    public double Minimum { get; }

    /// <summary>Where the sheet stood when the current gesture was armed.</summary>
    public double PressProgress { get; private set; }

    /// <summary>True between an armed press and its release, whether or not it became a drag.</summary>
    public bool IsPressed => pressed;

    /// <summary>True once the press has traveled far enough to own the pointer.</summary>
    public bool IsDragging => dragging;

    /// <summary>Arms the gesture; the drag itself only starts once the finger has actually moved.</summary>
    public void Press(double y)
    {
        pressed = true;
        dragging = false;
        startY = y;
        startProgress = Progress;
        PressProgress = Progress;
        clock.Restart();
        samples.Clear();
        samples.Add((0d, y));
    }

    /// <summary>
    /// Feeds a pointer move measured against the sheet (never against the card, which moves with the
    /// gesture and would feed the drag back into itself). <paramref name="travel"/> is the distance
    /// the card covers between compact and full, <paramref name="dismissTravel"/> the (much shorter)
    /// distance from compact to gone. Returns true once the gesture owns the pointer, which is when
    /// the caller must capture it and mark the event handled.
    /// </summary>
    public bool Move(double y, double travel, double dismissTravel = 0d)
    {
        if (!pressed)
        {
            return false;
        }

        var deltaY = y - startY;
        if (!dragging)
        {
            if (Math.Abs(deltaY) < DragThresholdPx)
            {
                return false;
            }

            dragging = true;
            glide.Stop();
        }

        samples.Add((clock.Elapsed.TotalMilliseconds, y));
        if (samples.Count > 8)
        {
            samples.RemoveAt(0);
        }

        if (travel > 0d)
        {
            // Worked in pixels rather than in progress, because the two halves of the range are
            // scaled differently: opening covers the whole sheet, being flung away covers only the
            // card. The position tracks the finger 1:1 either side of the compact state.
            var offset = Math.Clamp(ToOffset(startProgress, travel, dismissTravel) + deltaY, -travel, dismissTravel);
            Apply(ToProgress(offset, travel, dismissTravel));
        }

        return true;
    }

    private static double ToOffset(double progress, double travel, double dismissTravel) =>
        progress >= 0d ? -progress * travel : -progress * dismissTravel;

    private static double ToProgress(double offset, double travel, double dismissTravel) =>
        offset <= 0d || dismissTravel <= 0d ? -offset / travel : -offset / dismissTravel;

    /// <summary>
    /// Ends the gesture. <paramref name="target"/> is where it should settle: a flick wins on
    /// direction alone (matching the user's momentum even from a short pull), a slow drag settles to
    /// whichever end is nearer. Returns false when the finger never moved, i.e. the press was a tap
    /// the caller owns.
    /// </summary>
    public bool Release(out double target)
    {
        var wasDragging = dragging;
        pressed = false;
        dragging = false;
        target = wasDragging ? ResolveTarget() : Nearest;
        return wasDragging;
    }

    /// <summary>Drops the gesture without deciding anything (a lost capture).</summary>
    public void Abandon()
    {
        pressed = false;
        dragging = false;
    }

    /// <summary>Whichever end of the segment the card is in it is currently nearer to.</summary>
    public double Nearest
    {
        get
        {
            var (low, high) = Segment;
            return Progress >= (low + high) / 2d ? high : low;
        }
    }

    // The pair of stops the card is currently between: compact and full above the compact state,
    // compact and gone below it. A release only ever lands on one of the two it sits between.
    private (double Low, double High) Segment => Progress < 0d ? (Minimum, 0d) : (0d, 1d);

    /// <summary>
    /// True when the press landed on a control that owns the gesture itself (a transport button or
    /// a position slider), so the sheet must not steal it.
    /// </summary>
    public static bool IsInteractive(Visual? source, Visual root)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, root); current = current.GetVisualParent())
        {
            if (current is Button or RangeBase or Thumb or TextBox)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Glides to <paramref name="target"/> from wherever the card is now.</summary>
    public void SettleTo(double target, double durationMs = SettleDurationMs)
    {
        if (Math.Abs(target - Progress) < 0.0005d)
        {
            glide.Stop();
            Apply(target);
            return;
        }

        glide.Start(Progress, target, durationMs);
    }

    /// <summary>Places the card at <paramref name="value"/> outright, canceling any glide.</summary>
    public void JumpTo(double value)
    {
        glide.Stop();
        Apply(value);
    }

    /// <summary>Re-applies the current position, e.g. after a resize changed the card's geometry.</summary>
    public void Refresh() => apply(Progress);

    public void Stop() => glide.Stop();

    private void Apply(double value)
    {
        Progress = Math.Clamp(value, Minimum, 1d);
        apply(Progress);
    }

    // Vertical pointer velocity in px/ms over the recent sample window; negative is upwards.
    private double ResolveTarget()
    {
        var (low, high) = Segment;
        var velocity = MeasureVelocity();
        if (velocity < -FlingVelocity)
        {
            return high;
        }

        if (velocity > FlingVelocity)
        {
            return low;
        }

        return Nearest;
    }

    private double MeasureVelocity()
    {
        if (samples.Count < 2)
        {
            return 0d;
        }

        var last = samples[^1];
        var first = samples[0];
        for (var i = samples.Count - 1; i >= 0; i--)
        {
            if (last.Ms - samples[i].Ms > VelocityWindowMs)
            {
                break;
            }

            first = samples[i];
        }

        var elapsed = last.Ms - first.Ms;
        return elapsed <= 0d ? 0d : (last.Y - first.Y) / elapsed;
    }
}
