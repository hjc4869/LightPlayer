using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// The seek bar of the video deck: rail, buffered stretch, chapter breaks and thumb drawn as one
/// control, so all of them share a single time-to-pixel mapping and the thumb is always on top.
/// Scrubbing is deferred: the thumb follows the pointer and <see cref="SeekRequested"/> is raised
/// once, on release.
/// </summary>
public sealed class MediaSeekBar : Control
{
    /// <summary>Gap punched into the rail at a chapter boundary, in device-independent pixels.</summary>
    private const double ChapterGap = 3;

    /// <summary>How much the thumb grows while it is held or under the pointer.</summary>
    private const double ThumbGrowth = 2;

    /// <summary>Height of the strip that answers a pointer, beyond the rail and thumb themselves.</summary>
    private const double HitPadding = 8;

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<MediaSeekBar, double>(nameof(Position));

    /// <summary>How far the source has fetched, in seconds.</summary>
    public static readonly StyledProperty<double> BufferedProperty =
        AvaloniaProperty.Register<MediaSeekBar, double>(nameof(Buffered));

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<MediaSeekBar, double>(nameof(Minimum));

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<MediaSeekBar, double>(nameof(Duration));

    public static readonly StyledProperty<IReadOnlyList<MediaChapter>?> ChaptersProperty =
        AvaloniaProperty.Register<MediaSeekBar, IReadOnlyList<MediaChapter>?>(nameof(Chapters));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<MediaSeekBar, IBrush?>(
            nameof(TrackBrush), new ImmutableSolidColorBrush(Color.Parse("#59FFFFFF")));

    public static readonly StyledProperty<IBrush?> BufferedBrushProperty =
        AvaloniaProperty.Register<MediaSeekBar, IBrush?>(
            nameof(BufferedBrush), new ImmutableSolidColorBrush(Color.Parse("#A6D6D6D6")));

    public static readonly StyledProperty<IBrush?> ProgressBrushProperty =
        AvaloniaProperty.Register<MediaSeekBar, IBrush?>(nameof(ProgressBrush), Brushes.White);

    public static readonly StyledProperty<IBrush?> ThumbBrushProperty =
        AvaloniaProperty.Register<MediaSeekBar, IBrush?>(nameof(ThumbBrush), Brushes.White);

    public static readonly StyledProperty<double> TrackHeightProperty =
        AvaloniaProperty.Register<MediaSeekBar, double>(nameof(TrackHeight), 4);

    /// <summary>Radius of the thumb at rest. Kept small so a press reads as an exact time.</summary>
    public static readonly StyledProperty<double> ThumbRadiusProperty =
        AvaloniaProperty.Register<MediaSeekBar, double>(nameof(ThumbRadius), 5);

    public static readonly DirectProperty<MediaSeekBar, bool> IsDraggingProperty =
        AvaloniaProperty.RegisterDirect<MediaSeekBar, bool>(nameof(IsDragging), o => o.IsDragging);

    /// <summary>Where the thumb sits: the held target while seeking, the played position otherwise.</summary>
    public static readonly DirectProperty<MediaSeekBar, double> DisplayPositionProperty =
        AvaloniaProperty.RegisterDirect<MediaSeekBar, double>(nameof(DisplayPosition), o => o.DisplayPosition);

    private bool isDragging;
    private double displayPosition;

    static MediaSeekBar() =>
        AffectsRender<MediaSeekBar>(
            PositionProperty,
            BufferedProperty,
            MinimumProperty,
            DurationProperty,
            ChaptersProperty,
            TrackBrushProperty,
            BufferedBrushProperty,
            ProgressBrushProperty,
            ThumbBrushProperty,
            TrackHeightProperty,
            ThumbRadiusProperty,
            DisplayPositionProperty,
            IsDraggingProperty,
            IsPointerOverProperty);

    /// <summary>Raised when a scrub is let go, with the second the picture should move to.</summary>
    public event EventHandler<SeekRequestedEventArgs>? SeekRequested;

    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public double Buffered
    {
        get => GetValue(BufferedProperty);
        set => SetValue(BufferedProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public IReadOnlyList<MediaChapter>? Chapters
    {
        get => GetValue(ChaptersProperty);
        set => SetValue(ChaptersProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? BufferedBrush
    {
        get => GetValue(BufferedBrushProperty);
        set => SetValue(BufferedBrushProperty, value);
    }

    public IBrush? ProgressBrush
    {
        get => GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public IBrush? ThumbBrush
    {
        get => GetValue(ThumbBrushProperty);
        set => SetValue(ThumbBrushProperty, value);
    }

    public double TrackHeight
    {
        get => GetValue(TrackHeightProperty);
        set => SetValue(TrackHeightProperty, value);
    }

    public double ThumbRadius
    {
        get => GetValue(ThumbRadiusProperty);
        set => SetValue(ThumbRadiusProperty, value);
    }

    public bool IsDragging
    {
        get => isDragging;
        private set => SetAndRaise(IsDraggingProperty, ref isDragging, value);
    }

    public double DisplayPosition
    {
        get => displayPosition;
        private set => SetAndRaise(DisplayPositionProperty, ref displayPosition, value);
    }

    /// <summary>Center of the thumb, in this control's own coordinates.</summary>
    public double ThumbOffset
    {
        get
        {
            var (left, width) = TrackGeometry();
            return left + (Ratio(DisplayPosition) * width);
        }
    }

    /// <summary>The second an x coordinate of this control stands for.</summary>
    public double PositionAt(double x)
    {
        var (left, width) = TrackGeometry();
        if (width <= 0 || Range <= 0)
        {
            return Minimum;
        }

        return Minimum + (Math.Clamp((x - left) / width, 0, 1) * Range);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(0, Math.Max(TrackHeight, (ThumbRadius + ThumbGrowth) * 2) + HitPadding);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // A scrub in progress owns the thumb; the next player position takes over after release.
        if (change.Property == PositionProperty && !IsDragging)
        {
            DisplayPosition = Position;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetCurrentPoint(this);
        if (Range <= 0 || (point.Pointer.Type == PointerType.Mouse && !point.Properties.IsLeftButtonPressed))
        {
            return;
        }

        e.Pointer.Capture(this);
        IsDragging = true;
        DisplayPosition = PositionAt(point.Position.X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (IsDragging)
        {
            DisplayPosition = PositionAt(e.GetPosition(this).X);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!IsDragging)
        {
            return;
        }

        var target = DisplayPosition;
    EndDrag(restorePosition: false);
        SeekRequested?.Invoke(this, new SeekRequestedEventArgs(target));
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag(restorePosition: true);
    }

    public override void Render(DrawingContext context)
    {
        var height = Bounds.Height;

        // Nothing else here is opaque, and a control with no drawn surface takes no pointer.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var (left, width) = TrackGeometry();
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var thickness = Math.Max(1, TrackHeight);
        var top = (height - thickness) / 2;
        var progressX = left + (Ratio(DisplayPosition) * width);
        var bufferedX = left + (Ratio(Buffered) * width);

        var segments = BuildSegments(left, width);
        var radius = segments.Count == 1 ? thickness / 2 : 0;
        foreach (var (start, end) in segments)
        {
            Fill(context, TrackBrush, start, end, top, thickness, radius);
            Fill(context, BufferedBrush, start, Math.Min(end, bufferedX), top, thickness, radius);
            Fill(context, ProgressBrush, start, Math.Min(end, progressX), top, thickness, radius);
        }

        if (Range > 0 && ThumbBrush is { } thumb)
        {
            var thumbRadius = ThumbRadius + (IsDragging || IsPointerOver ? ThumbGrowth : 0);
            context.DrawEllipse(thumb, null, new Point(progressX, height / 2), thumbRadius, thumbRadius);
        }
    }

    private static void Fill(
        DrawingContext context, IBrush? brush, double start, double end, double top, double thickness, double radius)
    {
        if (brush is null || end <= start)
        {
            return;
        }

        context.DrawRectangle(brush, null, new RoundedRect(new Rect(start, top, end - start, thickness), radius));
    }

    /// <summary>The rail runs inset by the widest the thumb can get, so it never clips at an end.</summary>
    private (double Left, double Width) TrackGeometry()
    {
        var inset = ThumbRadius + ThumbGrowth;
        return (inset, Math.Max(0, Bounds.Width - (inset * 2)));
    }

    private double Range => Math.Max(0, Duration - Minimum);

    private double Ratio(double seconds) =>
        Range <= 0 ? 0 : Math.Clamp((seconds - Minimum) / Range, 0, 1);

    /// <summary>The rail split at every chapter boundary, so the breaks are gaps rather than ticks.</summary>
    private List<(double Start, double End)> BuildSegments(double left, double width)
    {
        var right = left + width;
        var segments = new List<(double, double)>();
        if (Chapters is not { Count: > 1 } chapters || Range <= 0)
        {
            segments.Add((left, right));
            return segments;
        }

        var cursor = left;
        foreach (var chapter in chapters)
        {
            var start = chapter.Start.TotalSeconds;

            // The opening chapter starts at the left edge, where a gap would only shorten the rail.
            if (start <= Minimum || start >= Duration)
            {
                continue;
            }

            var x = left + (width * Ratio(start));
            var end = Math.Clamp(x - (ChapterGap / 2), cursor, right);
            if (end > cursor)
            {
                segments.Add((cursor, end));
            }

            cursor = Math.Clamp(x + (ChapterGap / 2), cursor, right);
        }

        if (right > cursor)
        {
            segments.Add((cursor, right));
        }

        if (segments.Count == 0)
        {
            segments.Add((left, right));
        }

        return segments;
    }

    private void EndDrag(bool restorePosition)
    {
        if (!IsDragging)
        {
            return;
        }

        IsDragging = false;
        if (restorePosition)
        {
            DisplayPosition = Position;
        }
    }
}

public sealed class SeekRequestedEventArgs(double position) : EventArgs
{
    /// <summary>Where the picture should move to, in seconds.</summary>
    public double Position { get; } = position;
}
