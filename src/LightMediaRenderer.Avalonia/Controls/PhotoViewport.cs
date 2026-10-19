using System;
using Avalonia;

namespace LightMediaRenderer.Avalonia.Controls;

/// <summary>
/// Where a picture sits inside the control showing it: how far it is magnified, and how far it has
/// been moved from the middle.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="PhotoView"/> because it is arithmetic and nothing else. The control
/// owns pointers, gestures and a render pass, none of which this needs, and none of which a test
/// of the arithmetic should have to stand up.
/// </remarks>
public readonly record struct PhotoViewport
{
    /// <summary>Largest magnification beyond the size the picture opens at.</summary>
    public const double MaximumZoom = 64;

    public PhotoViewport(Size natural, Size viewport, double renderScaling)
    {
        Natural = natural;
        Viewport = viewport;
        RenderScaling = renderScaling > 0 ? renderScaling : 1;
    }

    /// <summary>Size the picture is meant to be seen at, with pixel aspect and rotation applied.</summary>
    public Size Natural { get; }

    /// <summary>Size of the control drawing it.</summary>
    public Size Viewport { get; }

    public double RenderScaling { get; }

    public bool IsEmpty =>
        Natural.Width <= 0 || Natural.Height <= 0 || Viewport.Width <= 0 || Viewport.Height <= 0;

    /// <summary>
    /// The scale that fits the entire picture in the viewport without cropping.
    /// </summary>
    public double BaseScale
    {
        get
        {
            if (IsEmpty)
                return 1;

            return Math.Min(Viewport.Width / Natural.Width, Viewport.Height / Natural.Height);
        }
    }

    /// <summary>The scale at which one stored pixel covers one display pixel.</summary>
    public double OneToOneScale => 1 / RenderScaling;

    /// <summary>The zoom that reaches <see cref="OneToOneScale"/>, which is what a double tap goes to.</summary>
    public double ActualSizeZoom => BaseScale > 0 ? OneToOneScale / BaseScale : 1;

    public double MinimumZoom => IsEmpty ? 1 : Math.Min(1, ActualSizeZoom);

    /// <summary>How large the picture is drawn at <paramref name="zoom"/>.</summary>
    public Size ScaledSize(double zoom)
    {
        double scale = BaseScale * zoom;
        return new Size(Natural.Width * scale, Natural.Height * scale);
    }

    /// <summary>
    /// Keeps the picture against the viewport: an axis larger than the viewport may be moved as far
    /// as its own edges, and one smaller than it stays centered.
    /// </summary>
    public Vector Clamp(Vector offset, double zoom)
    {
        Size scaled = ScaledSize(zoom);
        double limitX = Math.Max(0, (scaled.Width - Viewport.Width) / 2);
        double limitY = Math.Max(0, (scaled.Height - Viewport.Height) / 2);
        return new Vector(
            Math.Clamp(offset.X, -limitX, limitX),
            Math.Clamp(offset.Y, -limitY, limitY));
    }

    /// <summary>Where the picture is drawn, in the control's own coordinates.</summary>
    public Rect Destination(double zoom, Vector offset)
    {
        Size scaled = ScaledSize(zoom);
        Vector clamped = Clamp(offset, zoom);
        return new Rect(
            ((Viewport.Width - scaled.Width) / 2) + clamped.X,
            ((Viewport.Height - scaled.Height) / 2) + clamped.Y,
            scaled.Width,
            scaled.Height);
    }

    /// <summary>Whether the picture is larger than the viewport, and so has anywhere to be moved to.</summary>
    public bool CanPan(double zoom)
    {
        Size scaled = ScaledSize(zoom);
        return scaled.Width - Viewport.Width > 1 || scaled.Height - Viewport.Height > 1;
    }

    /// <summary>
    /// Which part of the picture <paramref name="point"/> falls on, as a fraction of the picture's
    /// own width and height. Outside the picture the fraction simply runs past 0 or 1.
    /// </summary>
    /// <remarks>
    /// A gesture that both magnifies and moves has to remember what it took hold of, because the
    /// place it must be put back to moves with the fingers. Naming that place as a fraction of the
    /// picture keeps it meaningful at any magnification.
    /// </remarks>
    public Point Anchor(double zoom, Vector offset, Point point)
    {
        Rect destination = Destination(zoom, offset);
        return new Point(
            destination.Width > 0 ? (point.X - destination.X) / destination.Width : 0.5,
            destination.Height > 0 ? (point.Y - destination.Y) / destination.Height : 0.5);
    }

    /// <summary>
    /// Zooms to <paramref name="requested"/> and puts <paramref name="anchor"/>, as returned by
    /// <see cref="Anchor"/>, under <paramref name="focus"/>.
    /// </summary>
    public (double Zoom, Vector Offset) ZoomToAnchor(double requested, Point anchor, Point focus)
    {
        double target = Math.Clamp(requested, MinimumZoom, MaximumZoom);
        if (IsEmpty)
            return (target, default);

        Size scaled = ScaledSize(target);
        var moved = new Vector(
            focus.X - (anchor.X * scaled.Width) - ((Viewport.Width - scaled.Width) / 2),
            focus.Y - (anchor.Y * scaled.Height) - ((Viewport.Height - scaled.Height) / 2));

        return (target, Clamp(moved, target));
    }

    /// <summary>
    /// Zooms to <paramref name="requested"/> while keeping whatever sits under
    /// <paramref name="focus"/> exactly where it is.
    /// </summary>
    public (double Zoom, Vector Offset) ZoomAbout(
        double zoom,
        Vector offset,
        double requested,
        Point focus)
    {
        if (IsEmpty)
            return (Math.Clamp(requested, MinimumZoom, MaximumZoom), default);

        // The anchor is read before the zoom changes, so the new offset can be solved for directly
        // instead of converged on.
        return ZoomToAnchor(requested, Anchor(zoom, offset, focus), focus);
    }
}
