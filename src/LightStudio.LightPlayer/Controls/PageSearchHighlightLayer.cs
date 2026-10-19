using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace LightStudio.LightPlayer.Controls;

public sealed class PageSearchHighlightLayer : Control
{
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.FromRgb(235, 180, 35));

    internal PageSearchHost? Host { get; set; }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Host?.Search is not { IsOpen: true } search || search.Query.Trim() is not { Length: > 0 } query)
        {
            return;
        }

        var currentBrush = this.TryFindResource("AppAccentBrush", out var resource) && resource is IBrush brush
            ? brush
            : MatchBrush;
        foreach (var (control, item) in Host.GetMatchControls())
        {
            if (TransformToLayer(control) is not { } transform)
            {
                continue;
            }

            var clip = VisibleBounds(control);
            if (clip.Width <= 0 || clip.Height <= 0)
            {
                continue;
            }

            using var clipped = context.PushClip(clip);
            var current = ReferenceEquals(item, search.CurrentMatch?.Item);
            var bounds = new Rect(control.Bounds.Size).TransformToAABB(transform).Deflate(1);
            context.DrawRectangle(null, new Pen(current ? currentBrush : MatchBrush, current ? 2 : 1), bounds, 4, 4);
            foreach (var text in control.GetVisualDescendants().OfType<TextBlock>().Prepend(control as TextBlock).OfType<TextBlock>())
            {
                if (!text.IsEffectivelyVisible || text.Text is not { } value ||
                    TransformToLayer(text) is not { } textTransform)
                {
                    continue;
                }

                var start = 0;
                while (start <= value.Length - query.Length)
                {
                    var index = value.IndexOf(query, start, StringComparison.CurrentCultureIgnoreCase);
                    if (index < 0)
                    {
                        break;
                    }

                    using (context.PushOpacity(current ? 0.4 : 0.25))
                    {
                        foreach (var rectangle in text.TextLayout.HitTestTextRange(index, query.Length))
                        {
                            context.DrawRectangle(MatchBrush, null, rectangle.TransformToAABB(textTransform));
                        }
                    }

                    start = index + query.Length;
                }
            }
        }
    }

    private Matrix? TransformToLayer(Visual visual) =>
        TopLevel.GetTopLevel(this) is { } root &&
        visual.TransformToVisual(root) is { } toRoot && root.TransformToVisual(this) is { } fromRoot
            ? toRoot * fromRoot
            : null;

    private Rect VisibleBounds(Control control)
    {
        var clip = new Rect(Bounds.Size);
        foreach (var ancestor in control.GetVisualAncestors().Prepend(control))
        {
            if (ancestor.ClipToBounds && TransformToLayer(ancestor) is { } transform)
            {
                clip = clip.Intersect(new Rect(ancestor.Bounds.Size).TransformToAABB(transform));
            }
        }

        return clip;
    }
}