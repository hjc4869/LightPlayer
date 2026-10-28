using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using LightMediaRenderer.Avalonia.Rendering;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Shaders;
using LightStudio.Logging;
using SkiaSharp;

namespace LightMediaRenderer.Avalonia.Controls;

/// <summary>
/// Plots the luminance curve the renderer applies to static-metadata PQ content on the current
/// output. Input is on a PQ axis in nits; output is relative to SDR white, and each point of the
/// curve is drawn at the light it maps to.
/// </summary>
public sealed class ToneMappingChart : Control
{
    private const float SourcePeakNits = 10000;
    private const int SampleCount = 192;
    private const double LeftMargin = 48;
    private const double TopMargin = 8;
    private const double RightMargin = 8;
    private const double BottomMargin = 36;
    private const double LabelFontSize = 11;

    private static readonly double[] InputTicks = [0, 1, 10, 100, 1000, 10000];
    private static readonly IBrush PlotBackground = new ImmutableSolidColorBrush(Colors.Black);
    private static readonly IBrush PlotLabelBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
    private static readonly IPen GridPen = new ImmutablePen(
        new ImmutableSolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), 1);

    public static readonly StyledProperty<VideoSdrToneMappingOptions?> ToneMappingProperty =
        AvaloniaProperty.Register<ToneMappingChart, VideoSdrToneMappingOptions?>(nameof(ToneMapping));

    public static readonly StyledProperty<bool> EnableHdrPassthroughProperty =
        AvaloniaProperty.Register<ToneMappingChart, bool>(nameof(EnableHdrPassthrough), true);

    public static readonly StyledProperty<bool> IsHdrHintEnabledProperty =
        AvaloniaProperty.Register<ToneMappingChart, bool>(nameof(IsHdrHintEnabled));

    public static readonly StyledProperty<double> HdrHintHeadroomProperty =
        AvaloniaProperty.Register<ToneMappingChart, double>(nameof(HdrHintHeadroom), 4,
            coerce: (_, value) => double.IsFinite(value) ? Math.Max(value, 1) : 1);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<ToneMappingChart>();

    public static readonly StyledProperty<string?> InputAxisTitleProperty =
        AvaloniaProperty.Register<ToneMappingChart, string?>(nameof(InputAxisTitle));

    public static readonly StyledProperty<string?> OutputAxisTitleProperty =
        AvaloniaProperty.Register<ToneMappingChart, string?>(nameof(OutputAxisTitle));

    public static readonly StyledProperty<string?> CurrentHeadroomFormatProperty =
        AvaloniaProperty.Register<ToneMappingChart, string?>(nameof(CurrentHeadroomFormat));

    public static readonly StyledProperty<string?> PotentialHeadroomFormatProperty =
        AvaloniaProperty.Register<ToneMappingChart, string?>(nameof(PotentialHeadroomFormat));

    public static readonly DirectProperty<ToneMappingChart, VideoDisplayToneTarget> DisplayToneTargetProperty =
        AvaloniaProperty.RegisterDirect<ToneMappingChart, VideoDisplayToneTarget>(
            nameof(DisplayToneTarget), chart => chart.DisplayToneTarget);

    private readonly HdrContentHint hdrContentHint;
    private readonly object reportSync = new();
    private ChartOutput? reported;
    private ChartOutput output = new(null, null, new VideoDisplayToneTarget(1, VideoOutputColorSpaceExtensions.HdrReferenceWhiteNits));
    private VideoDisplayToneTarget displayToneTarget = new(1, VideoOutputColorSpaceExtensions.HdrReferenceWhiteNits);

    static ToneMappingChart()
    {
        AffectsRender<ToneMappingChart>(
            ToneMappingProperty,
            EnableHdrPassthroughProperty,
            IsHdrHintEnabledProperty,
            HdrHintHeadroomProperty,
            ForegroundProperty,
            InputAxisTitleProperty,
            OutputAxisTitleProperty,
            CurrentHeadroomFormatProperty,
            PotentialHeadroomFormatProperty);
    }

    public ToneMappingChart()
    {
        hdrContentHint = new HdrContentHint(this);
        DetachedFromVisualTree += (_, _) =>
        {
            lock (reportSync)
                reported = null;
        };
    }

    /// <summary>Tone curve and targets to plot, or null for the renderer's automatic choice.</summary>
    public VideoSdrToneMappingOptions? ToneMapping
    {
        get => GetValue(ToneMappingProperty);
        set => SetValue(ToneMappingProperty, value);
    }

    public bool EnableHdrPassthrough
    {
        get => GetValue(EnableHdrPassthroughProperty);
        set => SetValue(EnableHdrPassthroughProperty, value);
    }

    /// <summary>Whether the chart asks the platform for HDR output while it is visible.</summary>
    public bool IsHdrHintEnabled
    {
        get => GetValue(IsHdrHintEnabledProperty);
        set => SetValue(IsHdrHintEnabledProperty, value);
    }

    /// <summary>Content headroom published with the HDR hint, relative to SDR white.</summary>
    public double HdrHintHeadroom
    {
        get => GetValue(HdrHintHeadroomProperty);
        set => SetValue(HdrHintHeadroomProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public string? InputAxisTitle
    {
        get => GetValue(InputAxisTitleProperty);
        set => SetValue(InputAxisTitleProperty, value);
    }

    public string? OutputAxisTitle
    {
        get => GetValue(OutputAxisTitleProperty);
        set => SetValue(OutputAxisTitleProperty, value);
    }

    /// <summary>Label for the current headroom line; <c>{0}</c> receives the ratio.</summary>
    public string? CurrentHeadroomFormat
    {
        get => GetValue(CurrentHeadroomFormatProperty);
        set => SetValue(CurrentHeadroomFormatProperty, value);
    }

    /// <summary>Label for the potential headroom line; <c>{0}</c> receives the ratio.</summary>
    public string? PotentialHeadroomFormat
    {
        get => GetValue(PotentialHeadroomFormatProperty);
        set => SetValue(PotentialHeadroomFormatProperty, value);
    }

    /// <summary>What the tone mapping targets left to the display currently resolve to.</summary>
    public VideoDisplayToneTarget DisplayToneTarget
    {
        get => displayToneTarget;
        private set => SetAndRaise(DisplayToneTargetProperty, ref displayToneTarget, value);
    }

    public override void Render(DrawingContext context)
    {
        hdrContentHint.Update(IsHdrHintEnabled,
            new HdrContentHint.Metadata(HdrHintHeadroom, VideoOutputColorSpaceExtensions.HdrReferenceWhiteNits));

        var bounds = new Rect(Bounds.Size);
        var plot = new Rect(LeftMargin, TopMargin,
            Math.Max(0, bounds.Width - LeftMargin - RightMargin),
            Math.Max(0, bounds.Height - TopMargin - BottomMargin));
        if (plot.Width < 1 || plot.Height < 1)
            return;

        double outputMaximum = OutputRange(output, out double step);
        IBrush foreground = Foreground ?? Brushes.Gray;
        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty));
        context.DrawRectangle(PlotBackground, null, plot, 4, 4);

        foreach (double nits in InputTicks)
        {
            double x = plot.Left + PqSignal(nits) * plot.Width;
            context.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            FormattedText label = Text(nits.ToString("0", CultureInfo.CurrentCulture), foreground, typeface);
            context.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, 0, Math.Max(0, bounds.Width - label.Width)),
                plot.Bottom + 3));
        }

        string valueFormat = step < 1 ? "0.##" : "0";
        for (int index = 0; index * step <= outputMaximum + 1e-9; index++)
        {
            double value = index * step;
            double y = plot.Bottom - value / outputMaximum * plot.Height;
            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            FormattedText label = Text(value.ToString(valueFormat, CultureInfo.CurrentCulture), foreground, typeface);
            context.DrawText(label, new Point(plot.Left - 4 - label.Width,
                Math.Clamp(y - label.Height / 2, 0, Math.Max(0, bounds.Height - label.Height))));
        }

        if (InputAxisTitle is { Length: > 0 } inputTitle)
        {
            FormattedText title = Text(inputTitle, foreground, typeface);
            context.DrawText(title, new Point(plot.Left + (plot.Width - title.Width) / 2, bounds.Height - title.Height));
        }

        if (OutputAxisTitle is { Length: > 0 } outputTitle)
        {
            FormattedText title = Text(outputTitle, foreground, typeface);
            using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2) *
                Matrix.CreateTranslation(0, plot.Top + (plot.Height + title.Width) / 2)))
                context.DrawText(title, default);
        }

        context.Custom(new CurveOperation(bounds, plot, outputMaximum, ToneMapping, EnableHdrPassthrough, Report));

        double? labelTop = null;
        if (output.PotentialHeadroom is { } potential)
            labelTop = DrawLevelLabel(context, plot, outputMaximum, potential, PotentialHeadroomFormat, typeface, null);
        if (output.CurrentHeadroom is { } current)
            DrawLevelLabel(context, plot, outputMaximum, current, CurrentHeadroomFormat, typeface, labelTop);
    }

    private static double? DrawLevelLabel(DrawingContext context, Rect plot, double outputMaximum, double level,
        string? format, Typeface typeface, double? occupiedTop)
    {
        if (string.IsNullOrEmpty(format) || level > outputMaximum)
            return null;

        string ratio = level.ToString("0.0", CultureInfo.CurrentCulture) + "x";
        FormattedText label = Text(string.Format(CultureInfo.CurrentCulture, format, ratio), PlotLabelBrush, typeface);
        double y = plot.Bottom - level / outputMaximum * plot.Height;
        double top = y - label.Height - 1;
        // Labels that would overlap the one above move below their own line.
        if (top < plot.Top || occupiedTop is { } other && Math.Abs(top - other) < label.Height)
            top = y + 1;
        context.DrawText(label, new Point(Math.Max(plot.Left + 4, plot.Right - 4 - label.Width), top));
        return top;
    }

    private static FormattedText Text(string value, IBrush brush, Typeface typeface) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, LabelFontSize, brush);

    private static double OutputRange(ChartOutput output, out double step)
    {
        double top = Math.Max(1, Math.Max(output.CurrentHeadroom ?? 1, output.PotentialHeadroom ?? 1)) * 1.08;
        double raw = top / 5;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double normalized = raw / magnitude;
        step = magnitude * (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 2.5 ? 2.5 : normalized <= 5 ? 5 : 10);
        return Math.Ceiling(top / step) * step;
    }

    private void Report(ChartOutput value)
    {
        lock (reportSync)
        {
            if (reported == value)
                return;
            reported = value;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (output == value)
                return;
            output = value;
            DisplayToneTarget = value.Target;
            InvalidateVisual();
        });
    }

    private static double PqSignal(double nits)
    {
        if (nits <= 0)
            return 0;
        double level = Math.Pow(Math.Clamp(nits / 10000, 0, 1), 0.1593017578125);
        return Math.Pow((0.8359375 + 18.8515625 * level) / (1 + 18.6875 * level), 78.84375);
    }

    private static float PqToNits(float signal)
    {
        float power = MathF.Pow(Math.Clamp(signal, 0, 1), 1 / 78.84375f);
        return 10000 * MathF.Pow(MathF.Max(power - 0.8359375f, 0) /
            MathF.Max(18.8515625f - 18.6875f * power, 1e-6f), 1 / 0.1593017578125f);
    }

    private readonly record struct ChartOutput(
        double? CurrentHeadroom,
        double? PotentialHeadroom,
        VideoDisplayToneTarget Target);

    private sealed class CurveOperation(
        Rect bounds,
        Rect plot,
        double outputMaximum,
        VideoSdrToneMappingOptions? toneMapping,
        bool hdrPassthrough,
        Action<ChartOutput> report) : ICustomDrawOperation
    {
        private static readonly Lazy<SKRuntimeEffect?> LightEffect = new(CreateLightEffect);

        public Rect Bounds => bounds;

        public bool HitTest(Point point) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
                return;

            using ISkiaSharpApiLease lease = feature.Lease();
            SKCanvas canvas = lease.SkCanvas;
            int saved = canvas.Save();
            try
            {
                Draw(lease, canvas);
            }
            catch (Exception exception)
            {
                DrawOperationFailures.Report(nameof(ToneMappingChart), exception);
            }
            finally
            {
                canvas.RestoreToCount(saved);
            }
        }

        private void Draw(ISkiaSharpApiLease lease, SKCanvas canvas)
        {
            VideoOutputColorSpace target = lease.GetOutputColorSpace();
            VideoOutputColorVolume? volume = lease.GetOutputColorVolume();
            bool extended = target.IsExtendedRange();
            double? current = extended && volume?.HeadroomRatio is >= 1 and var ratio && double.IsFinite(ratio)
                ? ratio
                : null;
            double? potential = extended && volume?.MaximumHeadroomRatio is >= 1 and var maximum &&
                double.IsFinite(maximum) && maximum > (current ?? 1) + 0.01
                    ? maximum
                    : null;
            report(new ChartOutput(current, potential, target.ResolveDisplayToneTarget(hdrPassthrough, volume)));

            Span<float> nits = stackalloc float[SampleCount];
            Span<float> values = stackalloc float[SampleCount];
            for (int index = 0; index < SampleCount; index++)
                nits[index] = PqToNits(index / (float)(SampleCount - 1));
            VideoFramePipeline.EvaluateToneCurve(target, hdrPassthrough, volume, toneMapping, SourcePeakNits,
                nits, values);

            var area = new SKRect((float)plot.Left, (float)plot.Top, (float)plot.Right, (float)plot.Bottom);
            float scale = area.Height / (float)outputMaximum;
            canvas.ClipRect(area, SKClipOperation.Intersect, true);
            using var builder = new SKPathBuilder();
            for (int index = 0; index < SampleCount; index++)
            {
                float value = float.IsFinite(values[index]) ? Math.Clamp(values[index], 0, 2 * (float)outputMaximum) : 0;
                var point = new SKPoint(area.Left + area.Width * index / (SampleCount - 1), area.Bottom - value * scale);
                if (index == 0)
                    builder.MoveTo(point);
                else
                    builder.LineTo(point);
            }
            using SKPath path = builder.Detach();

            byte alpha = (byte)Math.Clamp(lease.CurrentOpacity * 255, 0, 255);
            using (var halo = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 5,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round,
                Color = new SKColor(0x80, 0x80, 0x80, (byte)(alpha * 0.35f)),
            })
                canvas.DrawPath(path, halo);

            // Each fragment takes the output value of its height on the axis, so the curve shows
            // the light it maps to; the working color space applies the surface's white scale.
            SKRuntimeEffect? effect = LightEffect.Value;
            using SKShader? light = effect?.ToShader(
                new SKRuntimeEffectUniforms(effect) { ["bottom"] = area.Bottom, ["scale"] = scale },
                new SKRuntimeEffectChildren(effect));
            using var line = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round,
                Color = SKColors.White.WithAlpha(alpha),
                Shader = light,
            };
            canvas.DrawPath(path, line);

            using SKPathEffect dash = SKPathEffect.CreateDash([6, 4], 0);
            line.PathEffect = dash;
            line.StrokeWidth = 1.5f;
            line.StrokeCap = SKStrokeCap.Butt;
            foreach (double? level in (ReadOnlySpan<double?>)[current, potential])
            {
                if (level is not { } headroom || headroom > outputMaximum)
                    continue;
                float y = area.Bottom - (float)headroom * scale;
                canvas.DrawLine(area.Left, y, area.Right, y, line);
            }
        }

        private static SKRuntimeEffect? CreateLightEffect()
        {
            SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader("""
                uniform float bottom;
                uniform float scale;
                half4 main(float2 position) {
                    float value = max((bottom - position.y) / scale, 0.0);
                    return half4(fromLinearSrgb(half3(value)), 1.0);
                }
                """, out string errors);
            if (effect is null)
                AppLog.Error("Video.ToneMapping", $"Could not compile the tone mapping chart shader: {errors}");
            return effect;
        }
    }
}
