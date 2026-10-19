using System;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ObjCRuntime;
using UIKit;

namespace LightStudio.LightPlayer.iOS.Services;

/// <summary>
/// Reports the window controls iPadOS places over the top-left corner of a window, from the safe
/// area adapted around them in each direction.
/// </summary>
/// <remarks>
/// The controls can move after the window has settled, without a resize or safe area change
/// following, so hidden probe views pinned to the layout guides report every change to them.
/// </remarks>
internal static class IosWindowControls
{
    public static IDisposable? Observe(TopLevel topLevel, Action<Thickness> changed)
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

    [SupportedOSPlatform("ios26.0")]
    [SupportedOSPlatform("maccatalyst26.0")]
    private sealed class Observer : IDisposable
    {
        private readonly UIView view;
        private readonly Action<Thickness> changed;
        private readonly GuideProbe[] probes;
        private Thickness? reported;
        private bool reportPending;

        public Observer(UIView view, Action<Thickness> changed)
        {
            this.view = view;
            this.changed = changed;
            probes =
            [
                new GuideProbe(this, view, view.SafeAreaLayoutGuide),
                new GuideProbe(this, view, view.GetLayoutGuide(Region(UIViewLayoutRegionAdaptivityAxis.Horizontal))),
                new GuideProbe(this, view, view.GetLayoutGuide(Region(UIViewLayoutRegionAdaptivityAxis.Vertical))),
            ];
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
            if (reported != value)
            {
                reported = value;
                changed(value);
            }
        }

        private Thickness Measure()
        {
            var safe = view.SafeAreaInsets;
            var beside = view.GetEdgeInsets(Region(UIViewLayoutRegionAdaptivityAxis.Horizontal));
            var below = view.GetEdgeInsets(Region(UIViewLayoutRegionAdaptivityAxis.Vertical));
            return new Thickness(Math.Max(0, beside.Left - safe.Left), Math.Max(0, below.Top - safe.Top), 0, 0);
        }

        public void Dispose()
        {
            foreach (var probe in probes)
            {
                probe.RemoveFromSuperview();
                probe.Dispose();
            }
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
