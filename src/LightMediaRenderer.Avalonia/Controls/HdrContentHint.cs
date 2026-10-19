using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.VisualTree;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;

namespace LightMediaRenderer.Avalonia.Controls;

internal sealed class HdrContentHint
{
    internal readonly record struct Metadata(
        double? HeadroomRatio = null,
        double? ReferenceWhiteNits = null,
        VideoLuminance? MasteringLuminance = null);

    private static readonly ConditionalWeakTable<TopLevel, SurfaceHints> requests = new();
    private readonly Control control;
    private readonly List<Visual> visibilityAncestors = new();
    private TopLevel? requestedTopLevel;
    private bool hasHdrContent;
    private Metadata? metadata;
    private bool inViewport = true;

    public HdrContentHint(Control control)
    {
        this.control = control;
        control.AttachedToVisualTree += OnAttached;
        control.DetachedFromVisualTree += OnDetached;
        control.EffectiveViewportChanged += OnViewportChanged;
    }

    public static bool IsHdr(VideoFrame frame) =>
        frame.GainMap is not null || frame.DolbyVision.IsPresent ||
        frame.ColorTransfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg;

    public static Metadata Describe(VideoFrame frame)
    {
        double white = VideoOutputColorSpaceExtensions.HdrReferenceWhiteNits;
        double? peak;
        VideoLuminance? mastering = frame.HdrMasteringLuminance is { } range
            ? new VideoLuminance(range.MinimumNits, range.MaximumNits)
            : null;
        if (frame.PhotoPeakLuminance is { } measured && double.IsFinite(measured) && measured >= 0)
            return new Metadata(Math.Max(1, measured / white), white, mastering);
        if (frame.GainMap is { } gainMap)
        {
            double headroom = Math.Pow(2, gainMap.CapacityMax);
            return new Metadata(double.IsFinite(headroom) ? headroom : null, white);
        }
        if (frame.DolbyVision.IsPresent)
        {
            DolbyVisionMetadata dolbyVision = frame.DolbyVision;
            peak = Positive(dolbyVision.EffectivePeakLuminance);
            if (dolbyVision.SourceMaximumPq > dolbyVision.SourceMinimumPq &&
                dolbyVision.SourceMinimumPq >= 0 && dolbyVision.SourceMaximumPq <= 1)
                mastering = new VideoLuminance(PqToNits(dolbyVision.SourceMinimumPq),
                    PqToNits(dolbyVision.SourceMaximumPq));
            peak ??= mastering?.MaximumNits;
        }
        else if (frame.ColorTransfer == VideoColorTransfer.Hlg)
        {
            peak = 1000;
        }
        else
        {
            peak = frame.Hdr10Plus.IsPresent ? Positive(frame.Hdr10Plus.EffectivePeakLuminance) : null;
            peak ??= Positive(frame.HdrPeakLuminance) ?? mastering?.MaximumNits;
        }
        return new Metadata(peak is { } maximum ? Math.Max(1, maximum / white) : null,
            white, mastering);
    }

    public static Metadata Combine(Metadata? first,
        Metadata second)
    {
        if (first is not { } previous)
            return second;
        double white = VideoOutputColorSpaceExtensions.HdrReferenceWhiteNits;
        double? firstScale = previous.ReferenceWhiteNits is > 0 and var firstWhite ? white / firstWhite : null;
        double? secondScale = second.ReferenceWhiteNits is > 0 and var secondWhite ? white / secondWhite : null;
        double? minimum = Minimum(previous.MasteringLuminance?.MinimumNits * firstScale,
            second.MasteringLuminance?.MinimumNits * secondScale);
        double? maximum = Maximum(previous.MasteringLuminance?.MaximumNits * firstScale,
            second.MasteringLuminance?.MaximumNits * secondScale);
        return new Metadata(Maximum(previous.HeadroomRatio, second.HeadroomRatio), white,
            minimum is not null || maximum is not null ? new VideoLuminance(minimum, maximum) : null);
    }

    public void Update(bool value, Metadata? sourceMetadata = null)
    {
        sourceMetadata = value ? sourceMetadata : null;
        if (hasHdrContent == value && metadata == sourceMetadata)
            return;
        hasHdrContent = value;
        metadata = sourceMetadata;
        Refresh();
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        inViewport = true;
        for (Visual? visual = control; visual is not null; visual = visual.GetVisualParent())
        {
            visibilityAncestors.Add(visual);
            visual.PropertyChanged += OnVisibilityChanged;
        }
        Refresh();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        foreach (Visual visual in visibilityAncestors)
            visual.PropertyChanged -= OnVisibilityChanged;
        visibilityAncestors.Clear();
        hasHdrContent = false;
        metadata = null;
        SetTopLevel(null);
    }

    private void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Visual.IsVisibleProperty || args.Property == Visual.OpacityProperty ||
            args.Property == Visual.BoundsProperty)
            Refresh();
    }

    private void OnViewportChanged(object? sender, EffectiveViewportChangedEventArgs args)
    {
        Rect visible = args.EffectiveViewport.Intersect(new Rect(control.Bounds.Size));
        inViewport = visible.Width > 0 && visible.Height > 0;
        Refresh();
    }

    private void Refresh()
    {
        TopLevel? target = null;
        if (hasHdrContent && inViewport && visibilityAncestors.Count > 0 &&
            control.Bounds.Width > 0 && control.Bounds.Height > 0)
        {
            target = TopLevel.GetTopLevel(control);
            foreach (Visual visual in visibilityAncestors)
            {
                if (!visual.IsVisible || visual.Opacity <= 0)
                {
                    target = null;
                    break;
                }
            }
        }
        SetTopLevel(target);
    }

    private void SetTopLevel(TopLevel? target)
    {
        if (ReferenceEquals(requestedTopLevel, target))
        {
            if (target is not null && requests.TryGetValue(target, out SurfaceHints? current))
                current.Publish(target);
            return;
        }
        if (requestedTopLevel is { } previous && requests.TryGetValue(previous, out var previousRequests))
        {
            previousRequests.Active.Remove(this);
            previousRequests.Publish(previous);
            if (previousRequests.Active.Count == 0)
                requests.Remove(previous);
        }
        requestedTopLevel = target;
        if (target is not null)
        {
            var active = requests.GetValue(target, static _ => new SurfaceHints());
            active.Active.Add(this);
            active.Publish(target);
        }
    }

    private static double? Positive(double value) => value > 0 && double.IsFinite(value) ? value : null;

    private static double? Minimum(double? first, double? second) =>
        first is { } firstValue && second is { } secondValue ? Math.Min(firstValue, secondValue) : null;

    private static double? Maximum(double? first, double? second) =>
        first is { } firstValue && second is { } secondValue ? Math.Max(firstValue, secondValue) : null;

    private static double PqToNits(double signal)
    {
        double power = Math.Pow(signal, 32.0 / 2523);
        return 10000 * Math.Pow(Math.Max(power - 3424.0 / 4096, 0) /
            (2413.0 / 128 - 2392.0 / 128 * power), 16384.0 / 2610);
    }

    private sealed class SurfaceHints
    {
        public HashSet<HdrContentHint> Active { get; } = new();
        private Metadata? published;
        private bool enabled;

        public void Publish(TopLevel topLevel)
        {
            bool hasContent = Active.Count > 0;
            Metadata? combined = null;
            foreach (HdrContentHint hint in Active)
                combined = Combine(combined, hint.metadata ?? new Metadata());
            if (enabled == hasContent && published == combined)
                return;
#if LMR_AVALONIA_FORK
            if (topLevel.PlatformImpl?.TryGetFeature<IPlatformHdrContentFeature>() is not { } feature)
                return;
            feature.SetHdrContent(hasContent, combined is { } value
                ? new PlatformHdrContentMetadata(value.HeadroomRatio, value.ReferenceWhiteNits,
                    value.MasteringLuminance is { } luminance
                        ? new PlatformLuminanceRange(luminance.MinimumNits, luminance.MaximumNits)
                        : null)
                : null);
#endif
            enabled = hasContent;
            published = combined;
        }
    }
}