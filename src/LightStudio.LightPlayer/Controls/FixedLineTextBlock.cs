using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Text block that draws every line at the baseline its primary font would have within
/// <see cref="TextBlock.LineHeight"/>. Fallback fonts with taller metrics (e.g. CJK) keep the
/// fixed line slot and the same baseline as Latin text, and their glyphs are not clipped.
/// </summary>
public class FixedLineTextBlock : TextBlock
{
    private (Typeface Typeface, double FontSize, double LineHeight) _baselineKey;
    private double _baseline = double.NaN;

    static FixedLineTextBlock()
    {
        ClipToBoundsProperty.OverrideDefaultValue<FixedLineTextBlock>(false);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        var baseline = GetPrimaryBaseline();
        var top = origin.Y;

        foreach (var line in TextLayout.TextLines)
        {
            line.Draw(context, new Point(origin.X, top + baseline - line.Baseline));
            top += line.Height;
        }
    }

    // An empty layout only measures the primary font, using the same line-height rules as the text.
    private double GetPrimaryBaseline()
    {
        (Typeface Typeface, double FontSize, double LineHeight) key =
            (new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, LineHeight);

        if (double.IsNaN(_baseline) || !key.Equals(_baselineKey))
        {
            using var layout = new TextLayout(string.Empty, key.Typeface, key.FontSize, lineHeight: key.LineHeight);
            _baseline = layout.Baseline;
            _baselineKey = key;
        }

        return _baseline;
    }
}
