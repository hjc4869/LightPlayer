using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CoreGraphics;
using LightStudio.LightPlayer.Services;
using ObjCRuntime;
using UIKit;

namespace LightStudio.LightPlayer.iOS.Services;

/// <summary>
/// Reports the regions iOS reserves around a window's content: the window controls iPadOS places over
/// the top-left corner, the bands beside the status bar where the system lays out vertical and
/// horizontal bars, and the active occlusions and divisions, such as cameras and a fold.
/// </summary>
/// <remarks>
/// The regions can move after the window has settled, without a resize or safe area change following,
/// so hidden probe views pinned to the layout guides and a hinge interaction report every change to them.
/// Vertical bars and reserved regions need the iOS 27.1 SDK (<c>IOS_RESERVED_REGIONS</c>).
/// </remarks>
internal static class IosDeviceRegions
{
    // The extent UIKit gives navigation bar content; vertical bars center their buttons in it.
    private const double BarExtent = 44;

    public static IDisposable? Observe(TopLevel topLevel, Action<DeviceRegions> changed)
    {
        if (OperatingSystem.IsIOSVersionAtLeast(26) &&
            topLevel.TryGetPlatformHandle() is { HandleDescriptor: "UIView" } handle &&
            Runtime.GetNSObject<UIView>(handle.Handle) is { } view)
        {
            return new Observer(view, changed);
        }

        return null;
    }

    [SupportedOSPlatform("ios26.0")]
    [SupportedOSPlatform("maccatalyst26.0")]
    private static UIViewLayoutRegion Region(UIViewLayoutRegionAdaptivityAxis axis) =>
        UIViewLayoutRegion.CreateSafeAreaLayoutRegion(axis);

#if IOS_RESERVED_REGIONS
    [SupportedOSPlatform("ios27.1")]
    [SupportedOSPlatform("maccatalyst27.1")]
#pragma warning disable CA1416 // The head targets iOS only; the bindings leave Mac Catalyst out.
    private static UIViewLayoutRegion BarRegion(UIRectEdge edge) =>
        UIViewLayoutRegion.CreateBarLayoutRegion(edge, (NFloat)BarExtent);
#pragma warning restore CA1416
#endif

    private static Rect ToRect(CGRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    [SupportedOSPlatform("ios26.0")]
    [SupportedOSPlatform("maccatalyst26.0")]
    private sealed class Observer : IDisposable
    {
        private readonly UIView view;
        private readonly Action<DeviceRegions> changed;
        private readonly GuideProbe[] probes;
#if IOS_RESERVED_REGIONS
        private readonly IUIInteraction? hingeInteraction;
#endif
        private DeviceRegions? reported;
        private bool reportPending;

        public Observer(UIView view, Action<DeviceRegions> changed)
        {
            this.view = view;
            this.changed = changed;
            var guides = new[]
            {
                view.SafeAreaLayoutGuide,
                view.GetLayoutGuide(Region(UIViewLayoutRegionAdaptivityAxis.Horizontal)),
                view.GetLayoutGuide(Region(UIViewLayoutRegionAdaptivityAxis.Vertical)),
            };
#if IOS_RESERVED_REGIONS
            if (OperatingSystem.IsIOSVersionAtLeast(27, 1))
            {
                guides =
                [
                    .. guides,
                    view.GetLayoutGuide(BarRegion(UIRectEdge.Left)),
                    view.GetLayoutGuide(BarRegion(UIRectEdge.Right)),
                ];
#pragma warning disable CA1416 // The head targets iOS only; the bindings leave Mac Catalyst out.
                hingeInteraction = new UIHingeInteraction((_, _) => ScheduleReport());
#pragma warning restore CA1416
                view.AddInteraction(hingeInteraction);
            }
#endif

            probes = guides.Select(guide => new GuideProbe(this, view, guide)).ToArray();
            Report();
        }

        public void ScheduleReport()
        {
            if (reportPending)
            {
                return;
            }

            reportPending = true;
            Dispatcher.UIThread.Post(Report);
        }

        private void Report()
        {
            reportPending = false;
            var value = Measure();
            if (!value.Equals(reported))
            {
                reported = value;
                changed(value);
            }
        }

        private DeviceRegions Measure()
        {
            var safe = view.SafeAreaInsets;
            var beside = view.GetEdgeInsets(Region(UIViewLayoutRegionAdaptivityAxis.Horizontal));
            var below = view.GetEdgeInsets(Region(UIViewLayoutRegionAdaptivityAxis.Vertical));
            var windowControls = new Thickness(Math.Max(0, beside.Left - safe.Left), Math.Max(0, below.Top - safe.Top), 0, 0);
#if IOS_RESERVED_REGIONS
            if (OperatingSystem.IsIOSVersionAtLeast(27, 1))
            {
                return MeasureReserved(windowControls, safe);
            }
#endif

            return new DeviceRegions { WindowControls = windowControls };
        }

#if IOS_RESERVED_REGIONS
        [SupportedOSPlatform("ios27.1")]
        [SupportedOSPlatform("maccatalyst27.1")]
        private DeviceRegions MeasureReserved(Thickness windowControls, UIEdgeInsets safe)
        {
            // UIKit offers no vertical bars or reserved regions to Mac Catalyst windows.
            if (OperatingSystem.IsMacCatalyst())
            {
                return new DeviceRegions { WindowControls = windowControls };
            }

#pragma warning disable CA1416 // The head targets iOS only; the bindings leave Mac Catalyst out.
            var bounds = view.Bounds;
            DeviceBarEdge? edge = null;
            var bar = default(Rect);
            var leading = view.EffectiveUserInterfaceLayoutDirection == UIUserInterfaceLayoutDirection.LeftToRight;
            var preferred = view.TraitCollection.VerticalBarEdge switch
            {
                UIVerticalBarEdge.Leading => leading ? DeviceBarEdge.Left : DeviceBarEdge.Right,
                UIVerticalBarEdge.Trailing => leading ? DeviceBarEdge.Right : DeviceBarEdge.Left,
                _ => (DeviceBarEdge?)null,
            };

            // The trait names the preferred edge even where bars stay horizontal, so a vertical bar is
            // used only where its band lies beside the safe area rather than over the content. The trait
            // trails a turn of the interface without a layout to report it, so the band of the other
            // edge is taken when only that one lies beside the safe area.
            if (preferred is { } first)
            {
                foreach (var side in new[] { first, first == DeviceBarEdge.Left ? DeviceBarEdge.Right : DeviceBarEdge.Left })
                {
                    var band = view.GetEdgeInsets(BarRegion(side == DeviceBarEdge.Left ? UIRectEdge.Left : UIRectEdge.Right)).InsetRect(bounds);
                    var outside = side == DeviceBarEdge.Left
                        ? band.Right <= safe.Left + 0.5
                        : band.Left >= bounds.Width - safe.Right - 0.5;
                    if (outside && band.Width > 0 && band.Height >= BarExtent)
                    {
                        edge = side;
                        bar = ToRect(band);
                        break;
                    }
                }
            }

            // Where the status bar keeps to a corner, the top bar band shares its row, above the safe area.
            var horizontal = default(Rect);
            if (edge is null)
            {
                var top = view.GetEdgeInsets(BarRegion(UIRectEdge.Top)).InsetRect(bounds);
                if (top.Y < safe.Top - 0.5 && top.Width > 0 && top.Height >= BarExtent)
                {
                    horizontal = ToRect(top);
                }
            }

            return new DeviceRegions
            {
                WindowControls = windowControls,
                VerticalBarEdge = edge,
                VerticalBar = bar,
                HorizontalBar = horizontal,
                Occlusions = view.GetReservedRegions(UIViewReservedRegionKind.CreateOcclusion()).Select(r => ToRect(r.Frame)).ToArray(),
                Divisions = view.GetReservedRegions(UIViewReservedRegionKind.CreateDivision()).Select(r => ToRect(r.Frame)).ToArray(),
            };
#pragma warning restore CA1416
        }
#endif

        public void Dispose()
        {
            foreach (var probe in probes)
            {
                probe.RemoveFromSuperview();
                probe.Dispose();
            }

#if IOS_RESERVED_REGIONS
            if (hingeInteraction is not null)
            {
                view.RemoveInteraction(hingeInteraction);
                hingeInteraction.Dispose();
            }
#endif
        }
    }

    [SupportedOSPlatform("ios26.0")]
    [SupportedOSPlatform("maccatalyst26.0")]
    private sealed class GuideProbe : UIView
    {
        private readonly Observer observer;

        public GuideProbe(Observer observer, UIView host, UILayoutGuide guide)
        {
            this.observer = observer;
            Hidden = true;
            UserInteractionEnabled = false;
            TranslatesAutoresizingMaskIntoConstraints = false;
            host.AddSubview(this);
            NSLayoutConstraint.ActivateConstraints(
            [
                LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor),
                TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor),
                TopAnchor.ConstraintEqualTo(guide.TopAnchor),
                BottomAnchor.ConstraintEqualTo(guide.BottomAnchor),
            ]);
        }

        public override void LayoutSubviews()
        {
            base.LayoutSubviews();
            observer.ScheduleReport();
        }
    }
}
