using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace LightMediaRenderer.Avalonia.Controls;

/// <summary>
/// Cubic ease-out tween paced by the render loop.
/// </summary>
/// <remarks>
/// <para>
/// Updates are asked for one composed frame at a time rather than driven by a timer of their own.
/// A timer keeps its own cadence, which beats against the rate the window is actually drawn at and
/// shows up as a short animation stuttering; an animation frame arrives exactly once per frame the
/// compositor was going to produce anyway.
/// </para>
/// <para>
/// Progress is still taken from wall-clock time, so an uneven cadence changes how many steps the
/// tween takes but never how long it lasts. Starting a new tween retargets the one in flight
/// rather than stacking callbacks.
/// </para>
/// </remarks>
public sealed class Glide
{
    /// <summary>Used only while the owner has no window to pace against.</summary>
    private static readonly TimeSpan FallbackFrameInterval = TimeSpan.FromMilliseconds(15);

    private readonly Visual owner;
    private readonly Action<double> apply;
    private readonly Stopwatch clock = new();
    private DispatcherTimer? fallbackTimer;
    private double from;
    private double to;
    private double durationMs;
    private bool running;

    /// <summary>Tells the tween in flight apart from callbacks a previous one left queued.</summary>
    private int generation;

    public Glide(Visual owner, Action<double> apply)
    {
        this.owner = owner;
        this.apply = apply;
    }

    /// <summary>Raised once the tween has applied its target value; not raised when it is stopped.</summary>
    public Action? Completed { get; set; }

    public void Start(double start, double target, double duration)
    {
        from = start;
        to = target;
        durationMs = Math.Max(1d, duration);
        running = true;
        generation++;
        clock.Restart();
        Schedule();
    }

    public void Stop()
    {
        running = false;
        generation++;
        fallbackTimer?.Stop();
        clock.Stop();
    }

    private void Schedule()
    {
        if (TopLevel.GetTopLevel(owner) is { } topLevel)
        {
            int token = generation;
            topLevel.RequestAnimationFrame(_ =>
            {
                if (token == generation)
                {
                    Advance();
                }
            });

            return;
        }

        fallbackTimer ??= new DispatcherTimer { Interval = FallbackFrameInterval };
        fallbackTimer.Tick -= OnFallbackTick;
        fallbackTimer.Tick += OnFallbackTick;
        fallbackTimer.Start();
    }

    private void OnFallbackTick(object? sender, EventArgs e) => Advance();

    private void Advance()
    {
        if (!running)
        {
            return;
        }

        double t = Math.Clamp(clock.Elapsed.TotalMilliseconds / durationMs, 0d, 1d);

        // Cubic ease-out: covers most of the distance immediately, then eases into the target.
        double eased = 1d - Math.Pow(1d - t, 3d);
        apply(from + ((to - from) * eased));

        if (t < 1d)
        {
            Schedule();
            return;
        }

        Stop();
        Completed?.Invoke();
    }
}
