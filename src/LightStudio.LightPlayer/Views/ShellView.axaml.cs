using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Behaviors;
using LightStudio.LightPlayer.Animations;
using LightStudio.LightPlayer.Controls;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightMediaRenderer.Avalonia.Controls;
using LightStudio.LightPlayer.ViewModels;
using LightStudio.LightPlayer.ViewModels.Pages;
using LightStudio.MediaLibraryCore.IO;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Views;

public partial class ShellView : UserControl
{
    /// <summary>
    /// The space the Apple layout's toolbar and playback bar take from panels that float over the page,
    /// within the safe area.
    /// </summary>
    public static readonly StyledProperty<Thickness> FloatingPanelMarginProperty =
        AvaloniaProperty.Register<ShellView, Thickness>(nameof(FloatingPanelMargin));

    public Thickness FloatingPanelMargin
    {
        get => GetValue(FloatingPanelMarginProperty);
        private set => SetValue(FloatingPanelMarginProperty, value);
    }

    private readonly HashSet<OverlayPopupHost> queuePopupHosts = [];

    static ShellView()
    {
        Control.LoadedEvent.AddClassHandler<OverlayPopupHost>((host, _) =>
        {
            if (host.GetLogicalAncestors().OfType<ShellView>().FirstOrDefault() is { } shell
                && shell.IsQueuePopupInteraction(host) && shell.queuePopupHosts.Add(host))
                host.DetachedFromVisualTree += shell.OnQueuePopupDetached;
        });
    }

    // Shared media file-type filter for the "Open file" pickers on both the in-shell More menu and
    // the desktop native File menu (see MainWindow). The extension sets are the ones the queue
    // drop and file activation already recognize, so the picker never hides a file the app can open.
    internal static readonly FilePickerFileType MediaFiles = new("Media files")
    {
        Patterns = QueueDragFormats.AudioExtensions
            .Concat(MediaContainerFormats.Video)
            .Concat(SupportedDiscImageFormats())
            .Concat(MediaContainerFormats.Image)
            .Select(extension => "*" + extension)
            .ToArray(),
        MimeTypes = new[] { "audio/*", "video/*", "image/*" },
    };

    private static IEnumerable<string> SupportedDiscImageFormats() =>
        IMultiMediaPlatformIO.Instance is IVideoDiscPlatformIO platform &&
        (platform.SupportsVideoDisc(VideoMediaKind.Dvd) ||
            platform.SupportsVideoDisc(VideoMediaKind.BluRay))
            ? MediaContainerFormats.DiscImage
            : [];

    // Tracks how deeply the drag is nested in drop targets across the window, so the
    // now-playing pane is only revealed while a queue drag is actually over the window.
    private int dragHintDepth;

    // Status-indicator "peek": clicking a collapsed scan/update status row briefly expands the nav
    // rail so its caption is readable, then auto-collapses. The flag guards the timer so it never
    // closes a pane the user opened themselves (via the rail toggle or search).
    private static readonly TimeSpan StatusPeekDuration = TimeSpan.FromSeconds(5);
    private DispatcherTimer? statusPeekTimer;
    private bool isStatusPeekOpen;
    private ConnectedPageTransition? connectedTransition;
    private ShellViewModel? transitionShell;
    private ShellViewModel? observedShell;

    // Space kept between the floating playback bar and the page content above it.
    private const double FloatingPlaybackBarGap = 8;

    // The floating playback bar's distance from the bottom of the page surface, when no band is below it.
    private const double FloatingPlaybackBarBottom = 14;

    // The page width below which the touch toolbar offers a search button instead of its search field.
    private const double ToolbarSearchFieldMinimumWidth = 720;

    // The narrowest the floating playback bar and the in-shell dialog become to keep beside a division.
    private const double MinimumFloatingPlaybackBarWidth = 320;
    private const double MinimumOverlayCardWidth = 280;

    // How far the vertical toolbar's column reaches past its band on either side, for the capsules
    // and their shadows, which are wider than the band.
    private const double VerticalToolbarOverhang = 12;

    // How far the vertical tab bar's column reaches past its band on either side, for its wider capsule.
    private const double VerticalTabBarOverhang = 24;

    // The space below the vertical tab bar, matching the compact now-playing card's.
    private const double VerticalTabBarBottomMargin = 4;

    // The height of the title row below the toolbar buttons while they share a corner status bar's row.
    private const double StatusBandTitleHeight = 44;

    private bool statusBandLayout;
    private double statusBandTitleRow;
    private Rect appliedStatusBand;

    public ShellView()
    {
        InitializeComponent();
        AddHandler(ContextRequestedEvent, OnPredictiveBackContextRequested, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnPredictiveBackInput, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, OnPredictiveBackInput, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddressBarHost.AddHandler(KeyDownEvent, OnAddressBarKeyDown, RoutingStrategies.Tunnel);
        AppleTitle.AddHandler(ContextRequestedEvent, OnPathMenuRequested, RoutingStrategies.Tunnel);
        NarrowTitle.AddHandler(ContextRequestedEvent, OnPathMenuRequested, RoutingStrategies.Tunnel);
        ToolbarGoUpButton.AddHandler(ContextRequestedEvent, OnPathMenuRequested, RoutingStrategies.Tunnel);
        VerticalGoUpButton.AddHandler(ContextRequestedEvent, OnPathMenuRequested, RoutingStrategies.Tunnel);

        // Enable window-wide drag detection (AllowDrop inherits to every descendant) so the
        // now-playing pane can be revealed the moment a queue drag enters the window, wherever
        // it lands. The pane itself handles the real drop; these handlers only toggle the hint.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnShellDragEnter, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnShellDragLeave, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnShellDrop, handledEventsToo: true);

        // Settle what the graphics backend can do with decoder surfaces while the library is still
        // on screen: a decoder cannot revise that choice once it has opened, and the player page
        // appears at the same moment the first decoder does.
        ShellRoot.Children.Insert(0, new VideoSurfaceProbe());
        SetToolbarRows(statusBand: false);
        if (PlatformServices.UsesTouchChrome)
        {
            LayoutUpdated += OnStatusBandLayoutUpdated;
        }
    }

    // The status band is placed from the toolbar's position in the window, which a safe area change
    // can move after the insets were applied without resizing the page surface.
    private void OnStatusBandLayoutUpdated(object? sender, EventArgs args)
    {
        if (StatusBand != appliedStatusBand)
        {
            UpdateContentInsets();
        }
    }

    // Beside a corner status bar, the height of the row below the buttons, for the title or the title band's search field.
    private double StatusBandTitleRow =>
        observedShell is { ShowToolbarTitle: true } or { ShowToolbarSearchField: true } ? StatusBandTitleHeight : 0;

    // The toolbar's row of buttons, and the title row it gains beside a corner status bar.
    private void SetToolbarRows(bool statusBand)
    {
        statusBandLayout = statusBand;
        statusBandTitleRow = statusBand ? StatusBandTitleRow : 0;
        AppleTopBarContent.RowDefinitions = new RowDefinitions($"{ShellViewModel.AppleToolbarHeight},{statusBandTitleRow}");
        AppleTopBarContent.Classes.Set("statusBand", statusBand);
        AppleTopBarContent.VerticalAlignment = statusBand ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Bottom;
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private IPageTransition PreparePageTransition(PageViewModelBase? previous, PageViewModelBase next, IPageTransition fallback)
    {
        CancelPredictiveBack();
        connectedTransition?.Cancel();
        connectedTransition = (ConnectedPageTransition?)PhotoPageTransition.Create(ShellRoot, previous, next, fallback)
            ?? ArtworkPageTransition.Create(ShellRoot, previous, next, fallback);
        return connectedTransition ?? fallback;
    }

    private void AttachConnectedTransitions(ShellViewModel? model)
    {
        if (ReferenceEquals(transitionShell, model))
            return;
        connectedTransition?.Cancel();
        connectedTransition = null;
        if (transitionShell?.PreparePageTransition == PreparePageTransition)
            transitionShell.PreparePageTransition = null;
        transitionShell = model;
        if (model is not null)
            model.PreparePageTransition = PreparePageTransition;
    }

    private void OnFindInPageClick(object? sender, RoutedEventArgs args) => PageSearchService.Open(this);

    private void ObserveShell(ShellViewModel? shell)
    {
        if (ReferenceEquals(observedShell, shell))
        {
            return;
        }

        if (observedShell is not null)
        {
            observedShell.PropertyChanged -= OnShellPropertyChanged;
            observedShell.Queue.PropertyChanged -= OnQueuePropertyChanged;
            observedShell.NowPlayingSheet.PropertyChanged -= OnNowPlayingSheetPropertyChanged;
        }

        observedShell = shell;
        if (shell is not null)
        {
            shell.PropertyChanged += OnShellPropertyChanged;
            shell.Queue.PropertyChanged += OnQueuePropertyChanged;
            shell.NowPlayingSheet.PropertyChanged += OnNowPlayingSheetPropertyChanged;
        }

        UpdatePlaybackBarPlacement();
        UpdateNarrowBarPlacement();
        UpdateContentInsets();
        UpdateSystemBarVisibility();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case null:
            case nameof(ShellViewModel.IsAppleLayout):
                UpdatePlaybackBarPlacement();
                UpdateContentInsets();
                break;
            case nameof(ShellViewModel.IsChromeVisible):
            case nameof(ShellViewModel.ShowAppleTopBar):
            case nameof(ShellViewModel.ShowToolbarBand):
            case nameof(ShellViewModel.ShowToolbarTitle):
            case nameof(ShellViewModel.ShowToolbarSearchField):
            case nameof(ShellViewModel.ShowFloatingPlaybackBar):
            case nameof(ShellViewModel.ShowFloatingNarrowChrome):
            case nameof(ShellViewModel.UsesVerticalToolbar):
            case nameof(ShellViewModel.SafeAreaPadding):
            case nameof(ShellViewModel.DeviceRegions):
                UpdateContentInsets();
                break;
            case nameof(ShellViewModel.UsesVerticalTabBar):
                UpdateNarrowBarPlacement();
                UpdateContentInsets();
                break;
            case nameof(ShellViewModel.IsSystemBarHidden):
                UpdateSystemBarVisibility();
                break;
        }
    }

    // Touch heads hide the status bar along with the controls of a photo or video covering the window.
    private void UpdateSystemBarVisibility()
    {
        if (PlatformServices.UsesTouchChrome && insetsManager is not null && observedShell is { } shell)
        {
            insetsManager.IsSystemBarVisible = !shell.IsSystemBarHidden;
        }
    }

    // A docked queue takes the band beside the safe area that the page otherwise extends under.
    private void OnQueuePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(QueuePaneViewModel.LayoutWidth) && PlatformServices.UsesTouchChrome)
        {
            UpdateContentInsets();
        }
    }

    // The floating now-playing card takes room from the page it floats over.
    private void OnNowPlayingSheetPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(NowPlayingSheetViewModel.IsAvailable) && PlatformServices.UsesTouchChrome)
        {
            UpdateContentInsets();
        }
    }

    private void OnBottomNavHostSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        if (PlatformServices.UsesTouchChrome)
        {
            UpdateContentInsets();
        }
    }

    // One playback bar serves both presentations: docked below the shell, or floating over the page.
    private void UpdatePlaybackBarPlacement()
    {
        bool floating = observedShell?.IsAppleLayout == true;
        Decorator host = floating ? FloatingPlaybackBarHost : DockedPlaybackBarHost;
        if (ReferenceEquals(PlaybackBarView.Parent, host))
        {
            return;
        }

        if (PlaybackBarView.Parent is Decorator previous)
        {
            previous.Child = null;
        }

        PlaybackBarView.Classes.Set("floating", floating);
        host.Child = PlaybackBarView;
    }

    // The narrow layout's tab bar stands below the page and its back button in the top bar, or both in
    // the band the system offers vertical bars.
    private void UpdateNarrowBarPlacement()
    {
        bool vertical = observedShell?.UsesVerticalTabBar == true;
        Decorator host = vertical ? VerticalTabBar : BottomNavBar;
        if (!ReferenceEquals(BottomNavGlass.Parent, host))
        {
            if (BottomNavGlass.Parent is Decorator previous)
            {
                previous.Child = null;
            }

            BottomNavGlass.Classes.Set("vertical", vertical);
            host.Child = BottomNavGlass;
        }

        if (vertical != ReferenceEquals(NarrowBackGlass.Parent, VerticalBackButton))
        {
            if (vertical)
            {
                NarrowTopBar.Children.Remove(NarrowBackGlass);
                VerticalBackButton.Child = NarrowBackGlass;
            }
            else
            {
                VerticalBackButton.Child = null;
                NarrowTopBar.Children.Insert(0, NarrowBackGlass);
            }
        }
    }

    private void OnFloatingPlaybackBarSizeChanged(object? sender, SizeChangedEventArgs args) => UpdateContentInsets();

    private void OnPageSurfaceSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        // Touch chrome folds the toolbar's search field into a button where the row would crowd the title.
        if (observedShell is { } shell && sender == PageSurface)
        {
            shell.IsToolbarSearchCollapsed = PlatformServices.UsesTouchChrome &&
                PageSurface.Bounds.Width - PageBleed.Right < ToolbarSearchFieldMinimumWidth;
        }

        UpdateContentInsets();
    }

    // The touch Apple layout's page surface extends under the system bands beside the safe area, as
    // UIKit content does: the status bar above, the home indicator below, and a vertical bar's band
    // when no docked queue stands there. Its content keeps to the safe area. A photo or video that
    // owns the surface covers every band, keeping its controls clear of them.
    private Thickness PageBleed
    {
        get
        {
            if (!PlatformServices.UsesTouchChrome || observedShell is not { } shell)
            {
                return default;
            }

            if (!shell.IsChromeVisible)
            {
                return shell.SafeAreaPadding;
            }

            if (shell.ShowAppleTopBar)
            {
                return new Thickness(0, shell.SafeAreaPadding.Top, shell.Queue.LayoutWidth > 0 ? 0 : shell.SafeAreaPadding.Right,
                    shell.SafeAreaPadding.Bottom);
            }

            if (!shell.ShowFloatingNarrowChrome)
            {
                return default;
            }

            // The narrow layout's floating chrome stands over the band below the page, and a vertical
            // tab bar over the band beside it.
            var edge = shell.UsesVerticalTabBar ? shell.DeviceRegions.VerticalBarEdge : null;
            return new Thickness(
                edge == DeviceBarEdge.Left ? shell.SafeAreaPadding.Left : 0,
                0,
                edge == DeviceBarEdge.Right ? shell.SafeAreaPadding.Right : 0,
                shell.SafeAreaPadding.Bottom);
        }
    }

    // The band beside a corner status bar where the toolbar's buttons go, in the toolbar's coordinates,
    // or empty where the toolbar keeps its row below the status bar.
    private Rect StatusBand
    {
        get
        {
            if (!PlatformServices.UsesTouchChrome || observedShell is not { ShowAppleTopBar: true, UsesVerticalToolbar: false } shell ||
                shell.DeviceRegions.HorizontalBar is not { Height: > 0 } band)
            {
                return default;
            }

            var origin = TopLevel.GetTopLevel(this)?.TranslatePoint(band.TopLeft, AppleTopBar) ?? band.TopLeft;
            return new Rect(origin, band.Size);
        }
    }

    // Where the toolbar's row of buttons starts so that it is centered on the band.
    private static double StatusBandTop(Rect band) =>
        Math.Max(0, band.Y + ((band.Height - ShellViewModel.AppleToolbarHeight) / 2));

    // The Apple layout's toolbar and playback bar float over the page area. Pages are laid out clear of
    // them, while the scrollers reaching the page edges keep scrolling underneath.
    private void UpdateContentInsets()
    {
        var bleed = PageBleed;
        bool chromeVisible = observedShell?.IsChromeVisible != false;
        PageSurface.Margin = new Thickness(-bleed.Left, -bleed.Top, -bleed.Right, -bleed.Bottom);
        var band = StatusBand;
        appliedStatusBand = band;
        AppleTopBar.Height = band.Height > 0
            ? Math.Max(StatusBandTop(band) + ShellViewModel.AppleToolbarHeight + StatusBandTitleRow, bleed.Top)
            : ShellViewModel.AppleToolbarHeight + bleed.Top;
        double top = observedShell?.ShowToolbarBand == true ? AppleTopBar.Height : bleed.Top;
        double bottom = observedShell switch
        {
            { ShowFloatingPlaybackBar: true } =>
                FloatingPlaybackBarHost.Bounds.Height + Math.Max(FloatingPlaybackBarBottom, bleed.Bottom) + FloatingPlaybackBarGap,
            // The tab bar's host reaches the safe area's edge; its capsule and the card above it float over the page.
            { ShowFloatingNarrowChrome: true } shell =>
                BottomNavHost.Bounds.Height + bleed.Bottom +
                (shell.NowPlayingSheet.IsAvailable ? NowPlayingSheet.CompactHeight + FloatingPlaybackBarGap : 0),
            _ => chromeVisible ? bleed.Bottom : 0,
        };
        var insets = chromeVisible ? new Thickness(bleed.Left, top, bleed.Right, bottom) : default;
        PageArea.Margin = insets;
        FloatingPanelMargin = new Thickness(0, Math.Max(0, top - bleed.Top), 0, Math.Max(0, bottom - bleed.Bottom));
        ShellChrome.SetScrollUnderInsets(PageArea, new Thickness(0, top, 0, bottom));
        ShellChrome.SetSafeAreaInsets(PageSurface, chromeVisible ? default : bleed);
        VideoSheetView.SafeAreaInsets = observedShell?.MediaSafeAreaInsets ?? default;
        var mediaRegions = PlatformServices.UsesTouchChrome ? observedShell?.DeviceRegions ?? DeviceRegions.Empty : DeviceRegions.Empty;
        ShellChrome.SetDeviceRegions(PageSurface, chromeVisible ? DeviceRegions.Empty : mediaRegions);
        ShellChrome.SetDeviceRegions(VideoSheetView, mediaRegions);

        // The sidebar's material extends under the same bands as the page beside it.
        NavigationBackdrop.Margin = PlatformServices.UsesTouchChrome && observedShell?.IsAppleLayout == true
            ? observedShell.SafeAreaBackgroundMargin
            : default;
        UpdateChromeRegions();
    }

    // The Apple layout lets the queue's shadow reach past its host, but a closed or closing panel
    // stays out of the band beside the safe area.
    private void OnQueuePaneHostSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        if (PlatformServices.UsesTouchChrome)
        {
            const double reach = 10000;
            QueuePaneHost.Clip = new RectangleGeometry(new Rect(-reach, -reach, reach + args.NewSize.Width, args.NewSize.Height + 2 * reach));
        }
    }

    /// <summary>
    /// The page insets the Apple layout's toolbar and playback bar reserve, measured while they are
    /// hidden for previews of the page behind a full-window view.
    /// </summary>
    internal Thickness MeasureAppleChromeInsets(bool includePlaybackBar)
    {
        // On touch heads the preview covers the window, so the chrome is measured from its edges.
        var safe = observedShell?.MediaSafeAreaInsets ?? default;
        double top = observedShell?.OmitsToolbarBand == true ? safe.Top : ShellViewModel.AppleToolbarHeight + safe.Top;
        if (observedShell?.DeviceRegions is { VerticalBarEdge: null, HorizontalBar.Height: > 0 } regions)
        {
            top = Math.Max(StatusBandTop(regions.HorizontalBar) + ShellViewModel.AppleToolbarHeight + StatusBandTitleRow, safe.Top);
        }

        double bottom = safe.Bottom;
        if (includePlaybackBar)
        {
            PlaybackBarView.Measure(Size.Infinity);
            bottom = PlaybackBarView.DesiredSize.Height + Math.Max(FloatingPlaybackBarBottom, safe.Bottom) + FloatingPlaybackBarGap;
        }

        return new Thickness(0, top, 0, bottom);
    }

    // Places the vertical toolbar or tab bar in the band the system offers it, the toolbar's buttons in a corner
    // status bar's row, and keeps the toolbar content, the floating playback bar, and the in-shell
    // dialog clear of the regions the device reserves.
    private void UpdateChromeRegions()
    {
        var regions = observedShell?.DeviceRegions ?? DeviceRegions.Empty;
        if (regions.VerticalBarEdge is not null)
        {
            var bar = regions.VerticalBar;
            VerticalToolbar.Margin = new Thickness(bar.X - VerticalToolbarOverhang, bar.Y, 0, 0);
            VerticalToolbar.Width = bar.Width + (2 * VerticalToolbarOverhang);
            VerticalToolbar.MaxHeight = bar.Height;
        }

        // The back button tops the band, below the status bar, and the tab bar ends level with the compact
        // now-playing card, above the home indicator's band.
        if (observedShell is { UsesVerticalTabBar: true } shell)
        {
            var bar = regions.VerticalBar;
            double bottom = Math.Min(bar.Bottom, Bounds.Height - shell.SafeAreaPadding.Bottom) - VerticalTabBarBottomMargin;
            var column = new Thickness(bar.X - VerticalTabBarOverhang, bar.Y, 0, 0);
            double width = bar.Width + (2 * VerticalTabBarOverhang);
            VerticalBackButton.Margin = column;
            VerticalBackButton.Width = width;
            VerticalTabBar.Margin = column;
            VerticalTabBar.Width = width;
            VerticalTabBar.Height = Math.Max(0, bottom - bar.Y);
        }

        var bleed = PageBleed;
        var band = StatusBand;
        if (statusBandLayout != band.Height > 0 || statusBandTitleRow != (band.Height > 0 ? StatusBandTitleRow : 0))
        {
            SetToolbarRows(band.Height > 0);
        }

        var contentMargin = band.Height > 0
            ? new Thickness(band.X, StatusBandTop(band), Math.Max(0, AppleTopBar.Bounds.Width - band.Right), 0)
            : new Thickness(7, 0, 12 + bleed.Right, 0);
        var barMargin = new Thickness(20, 0, 20 + bleed.Right, Math.Max(FloatingPlaybackBarBottom, bleed.Bottom));
        var cardMargin = new Thickness(20);
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            AppleTopBarContent.Margin = contentMargin;
            FloatingPlaybackBarHost.Margin = barMargin;
            OverlayCard.Margin = cardMargin;
            return;
        }

        // The toolbar content ends before the first reserved region in its band; beside a vertical
        // toolbar it holds only the title, which also keeps to one side of a division.
        if (observedShell?.ShowAppleTopBar == true &&
            TranslateRegions(topLevel, AppleTopBar, regions.Occlusions, observedShell.UsesVerticalToolbar ? regions.Divisions : []) is { Count: > 0 } blocked)
        {
            double start = contentMargin.Left + 120;
            double end = AppleTopBar.Bounds.Width - contentMargin.Right;
            double rowTop = band.Height > 0 ? StatusBandTop(band) : AppleTopBar.Bounds.Height - ShellViewModel.AppleToolbarHeight;
            foreach (var rect in blocked)
            {
                if (rect.Bottom > rowTop && rect.Top < rowTop + ShellViewModel.AppleToolbarHeight &&
                    rect.Right > start && rect.Left < end)
                {
                    end = Math.Max(start, rect.Left - 8);
                }
            }

            contentMargin = new Thickness(contentMargin.Left, contentMargin.Top, AppleTopBar.Bounds.Width - end, contentMargin.Bottom);
        }

        AppleTopBarContent.Margin = contentMargin;
        FloatingPlaybackBarHost.Margin = KeepBesideDivisions(topLevel, PageSurface, regions.Divisions, barMargin, MinimumFloatingPlaybackBarWidth);
        OverlayCard.Margin = KeepBesideDivisions(topLevel, OverlayRoot, regions.Divisions, cardMargin, MinimumOverlayCardWidth);
        UpdateSidebarToggle();
    }

    // A rail's headings show only once the sidebar has fully opened.
    private void OnNavPaneSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        NavPane.Classes.Set("fullyOpen", observedShell is { } shell && args.NewSize.Width >= shell.NavPaneOpenWidth - 0.5);
        UpdateSidebarToggle();
    }

    // The sidebar button tops a rail's column, as the regular layout's menu button does, and stays there
    // as the sidebar opens. Without a rail it keeps to the open sidebar's top-right corner, level with the
    // toolbar's row, and follows the edge as the sidebar slides; the toolbar offers it otherwise.
    private void UpdateSidebarToggle()
    {
        var shell = observedShell;
        bool show = shell is { ShowSidebarToggle: true } && NavPane.IsVisible &&
            NavPane.Bounds.Width >= shell.NavRailWidth - 0.5;
        SidebarToggleHost.IsVisible = show;
        if (!show)
        {
            return;
        }

        // The host covers the bands beside the safe area, whose top-left corner the rail starts at; the
        // button keeps below the window controls or the rounded corner there, as the regular layout's does.
        if (shell!.HasSidebarRail)
        {
            SidebarToggle.Margin = new Thickness(shell.SafeAreaPadding.Left, shell.SafeAreaPadding.Top + shell.DeviceWindowControls.Top, 0, 0);
            return;
        }

        var band = StatusBand;
        double rowTop = band.Height > 0 ? StatusBandTop(band) : AppleTopBar.Height - ShellViewModel.AppleToolbarHeight;
        if (NavPane.TranslatePoint(new Point(NavPane.Bounds.Width, 0), SidebarToggleHost) is { } edge &&
            AppleTopBar.TranslatePoint(new Point(0, rowTop), SidebarToggleHost) is { } row)
        {
            SidebarToggle.Margin = new Thickness(edge.X - SidebarToggle.Width, row.Y, 0, 0);
        }
    }

    private static List<Rect> TranslateRegions(TopLevel topLevel, Visual target, IReadOnlyList<Rect> first, IReadOnlyList<Rect> second)
    {
        var result = new List<Rect>(first.Count + second.Count);
        foreach (var rect in first.Concat(second))
        {
            if (topLevel.TranslatePoint(rect.TopLeft, target) is { } topLeft)
            {
                result.Add(new Rect(topLeft, rect.Size));
            }
        }

        return result;
    }

    // Keeps a horizontally centered element to the wider side of each division that crosses it, as
    // long as that side leaves it the minimum width.
    private static Thickness KeepBesideDivisions(TopLevel topLevel, Control area, IReadOnlyList<Rect> divisions, Thickness margin, double minimumWidth)
    {
        if (divisions.Count == 0 || area.Bounds.Width <= 0)
        {
            return margin;
        }

        double start = margin.Left;
        double end = area.Bounds.Width - margin.Right;
        foreach (var rect in TranslateRegions(topLevel, area, divisions, []))
        {
            // Only divisions running across the element's width, such as a vertical fold, split it.
            if (rect.Height < rect.Width || rect.Right <= start || rect.Left >= end)
            {
                continue;
            }

            double before = rect.Left - start;
            double after = end - rect.Right;
            if (after >= before && after >= minimumWidth)
            {
                start = rect.Right + margin.Left;
            }
            else if (before >= minimumWidth)
            {
                end = rect.Left - margin.Right;
            }
        }

        return new Thickness(start, margin.Top, area.Bounds.Width - end, margin.Bottom);
    }

    // The More menu offers the same choices as a submenu; the toolbar lifts them into a menu of their own.
    private void OnLibrarySortingClick(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button button || this.FindResource("LibrarySortingMenuItem") is not MenuItem sorting)
        {
            return;
        }

        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var item in sorting.Items.OfType<Control>().ToList())
        {
            sorting.Items.Remove(item);
            menu.Items.Add(item);
        }

        KeepOpenOnClick(menu.Items);
        if (!NativeContextMenu.TryShow(menu, button))
        {
            menu.ShowAt(button);
        }
    }

    private static void KeepOpenOnClick(ItemCollection items)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            item.StaysOpenOnClick = true;
            KeepOpenOnClick(item.Items);
        }
    }

    private void OnAddressBarKeyDown(object? sender, KeyEventArgs args)
    {
        if (Shell?.CurrentPage is SelectionPageViewModelBase page)
        {
            MultiSelectionMenu.HandleKey(page.Selection, args);
        }
    }

    // Plain text, since string headers would take underscores in folder names as access keys.
    private static readonly FuncDataTemplate<string> PathMenuHeaderTemplate = new((name, _) => new TextBlock
    {
        Text = name,
        TextTrimming = TextTrimming.CharacterEllipsis,
    });

    // Lists the current folder above the folders containing it, as the Finder window title does.
    private void OnPathMenuRequested(object? sender, ContextRequestedEventArgs args)
    {
        if (sender is not Control { ContextFlyout: MenuFlyout menu } title)
        {
            return;
        }

        menu.Items.Clear();
        if (Shell?.AddressBar is { Page.HasShellPathMenu: true, Current.Breadcrumb: { } path })
        {
            for (var index = path.Count - 1; index >= 0; index--)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = path[index].Name,
                    HeaderTemplate = PathMenuHeaderTemplate,
                    Command = path[index].Command,
                    CommandParameter = path[index].Parameter,
                });
            }
        }

        if (menu.Items.Count == 0)
        {
            args.Handled = true;
        }
        else if (OperatingSystem.IsMacOS() && NativeContextMenu.TryShow(menu, title, positioningItemIndex: 0))
        {
            args.Handled = true;
        }
    }

    private TopLevel? backSubscribedTopLevel;
    private IInsetsManager? insetsManager;
    private IDisposable? deviceRegionsObserver;
    private IInputPane? inputPane;
    private bool bringFocusedControlIntoViewPending;

    protected override void OnPropertyChanged(global::Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Report the shell width so the view model can adapt the queue's pinned, overlay, and sheet
        // presentations as the window resizes.
        if (change.Property == BoundsProperty)
        {
            CancelPredictiveBack();
            Shell?.SetViewportSize(Bounds.Size);
            UpdateInputPanePadding();
        }
        else if (change.Property == DataContextProperty && VisualRoot is not null)
        {
            CancelPredictiveBack();
            AttachConnectedTransitions(Shell);
            ObserveShell(Shell);
        }
    }

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachConnectedTransitions(Shell);
        ObserveShell(Shell);

        // The update download opens the release page in the browser; route it through the platform
        // launcher here since the view owns the TopLevel the view model cannot reach directly.
        if (Shell is { } shell)
        {
            shell.OpenExternalUrl = OpenExternalUrlAsync;

            // Apply the initial presentation immediately so a narrow launch does not briefly dock the
            // side panel before the first bounds change arrives.
            shell.SetViewportSize(Bounds.Size);
        }

        // Platform "back" gesture: Android system back button and browser back both surface through
        // TopLevel.BackRequested. Subscribe here because the TopLevel is not known until attached.
        backSubscribedTopLevel = TopLevel.GetTopLevel(this);
        if (backSubscribedTopLevel is not null)
        {
            AlbumArtService.DisplayScaling = backSubscribedTopLevel.RenderScaling;
            backSubscribedTopLevel.BackRequested += OnBackRequested;
            backSubscribedTopLevel.ScalingChanged += OnTopLevelScalingChanged;
            backSubscribedTopLevel.AddHandler(InputElement.PointerPressedEvent, OnQueueOverlayInteraction, RoutingStrategies.Tunnel, handledEventsToo: true);
            backSubscribedTopLevel.AddHandler(InputElement.PointerWheelChangedEvent, OnQueueOverlayInteraction, RoutingStrategies.Tunnel, handledEventsToo: true);
            backSubscribedTopLevel.AddHandler(InputElement.KeyDownEvent, OnQueueOverlayInteraction, RoutingStrategies.Tunnel, handledEventsToo: true);
            backSubscribedTopLevel.AddHandler(InputElement.GotFocusEvent, OnQueueOverlayInteraction, RoutingStrategies.Bubble, handledEventsToo: true);

            if (backSubscribedTopLevel.InsetsManager is { } manager)
            {
                insetsManager = manager;
                manager.DisplayEdgeToEdgePreference = true;
                manager.SystemBarColor = Colors.Transparent;
                manager.SafeAreaChanged += OnSafeAreaChanged;
                UpdateSafeAreaPadding(manager.SafeAreaPadding);
            }

            inputPane = backSubscribedTopLevel.InputPane;
            if (inputPane is not null)
            {
                inputPane.StateChanged += OnInputPaneStateChanged;
            }

            deviceRegionsObserver = PlatformServices.ObserveDeviceRegions?.Invoke(backSubscribedTopLevel, OnDeviceRegionsChanged);
            UpdateInputPanePadding();
            UpdateSystemBarVisibility();
        }

        AddHandler(InputElement.GotFocusEvent, OnShellGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);

        // Desktop mice with navigation side buttons: XButton1 (back) / XButton2 (forward). Tunnel so
        // the gesture is caught before descendants and works wherever the pointer is over the shell.
        AddHandler(InputElement.PointerPressedEvent, OnShellPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        CancelPredictiveBack();
        AttachConnectedTransitions(null);
        ObserveShell(null);

        if (inputPane is not null)
        {
            inputPane.StateChanged -= OnInputPaneStateChanged;
            inputPane = null;
        }

        if (insetsManager is not null)
        {
            insetsManager.SafeAreaChanged -= OnSafeAreaChanged;
            insetsManager = null;
        }

        deviceRegionsObserver?.Dispose();
        deviceRegionsObserver = null;

        if (backSubscribedTopLevel is not null)
        {
            backSubscribedTopLevel.BackRequested -= OnBackRequested;
            backSubscribedTopLevel.ScalingChanged -= OnTopLevelScalingChanged;
            backSubscribedTopLevel.RemoveHandler(InputElement.PointerPressedEvent, OnQueueOverlayInteraction);
            backSubscribedTopLevel.RemoveHandler(InputElement.PointerWheelChangedEvent, OnQueueOverlayInteraction);
            backSubscribedTopLevel.RemoveHandler(InputElement.KeyDownEvent, OnQueueOverlayInteraction);
            backSubscribedTopLevel.RemoveHandler(InputElement.GotFocusEvent, OnQueueOverlayInteraction);
            backSubscribedTopLevel = null;
        }

        UpdateInputPanePadding();
        RemoveHandler(InputElement.GotFocusEvent, OnShellGotFocus);
        RemoveHandler(InputElement.PointerPressedEvent, OnShellPointerPressed);
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e) =>
        UpdateSafeAreaPadding(e.SafeAreaPadding);

    private void OnTopLevelScalingChanged(object? sender, EventArgs e)
    {
        if (sender is TopLevel topLevel)
        {
            AlbumArtService.DisplayScaling = topLevel.RenderScaling;
        }

        CancelPredictiveBack();
        UpdateSafeAreaPadding(insetsManager?.SafeAreaPadding ?? default);
    }

    private void UpdateSafeAreaPadding(Thickness padding)
    {
        if (Shell is { } shell)
        {
            shell.DeviceSafeAreaPadding = padding;
        }

        UpdateInputPanePadding();
    }

    private void OnDeviceRegionsChanged(DeviceRegions regions)
    {
        if (Shell is { } shell)
        {
            shell.DeviceRegions = regions;
        }
    }

    private void OnInputPaneStateChanged(object? sender, InputPaneStateEventArgs e)
    {
        CancelPredictiveBack();
        UpdateInputPanePadding(e.NewState == InputPaneState.Open ? e.EndRect : default(Rect));
    }

    private void UpdateInputPanePadding(Rect? occludedRect = null)
    {
        var bottomPadding = 0d;
        var keyboardRect = occludedRect
            ?? (inputPane is { State: InputPaneState.Open } ? inputPane.OccludedRect : default);

        if (backSubscribedTopLevel is { } topLevel
            && keyboardRect.Width > 0 && keyboardRect.Height > 0
            && topLevel.TranslatePoint(keyboardRect.TopLeft, this) is { } topLeft
            && topLevel.TranslatePoint(keyboardRect.BottomRight, this) is { } bottomRight)
        {
            var overlap = new Rect(Bounds.Size).Intersect(new Rect(topLeft, bottomRight));
            if (overlap.Width > 0 && overlap.Height > 0)
            {
                bottomPadding = Math.Max(0, Bounds.Height - overlap.Top - (Shell?.SafeAreaPadding.Bottom ?? 0));
            }
        }

        if (Padding.Bottom != bottomPadding)
        {
            Padding = new Thickness(Padding.Left, Padding.Top, Padding.Right, bottomPadding);
        }

        QueueBringFocusedControlIntoView();
    }

    private void OnShellGotFocus(object? sender, RoutedEventArgs e) =>
        QueueBringFocusedControlIntoView();

    private void QueueBringFocusedControlIntoView()
    {
        if (bringFocusedControlIntoViewPending || Padding.Bottom <= 0 || inputPane?.State != InputPaneState.Open)
        {
            return;
        }

        bringFocusedControlIntoViewPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            bringFocusedControlIntoViewPending = false;
            if (Padding.Bottom > 0
                && inputPane?.State == InputPaneState.Open
                && backSubscribedTopLevel?.FocusManager?.GetFocusedElement() is Control focused
                && this.IsVisualAncestorOf(focused))
            {
                focused.BringIntoView();
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        CancelPredictiveBack();
        // A modal overlay (a mobile in-shell dialog) consumes the back gesture first; otherwise
        // fall through to page navigation. Only then leave it unhandled so the platform performs
        // its default (Android closes/backgrounds the app).
        if (Shell?.CloseOverlay() == true)
        {
            e.Handled = true;
            return;
        }

        // A narrow-layout search field consumes back next, collapsing to the page title.
        if (Shell?.CloseSearch() == true)
        {
            e.Handled = true;
            return;
        }

        // Consume the back gesture only when there is a page to return to; otherwise leave it
        // unhandled so the platform performs its default (Android closes/backgrounds the app).
        if (Shell?.TryGoBack() == true)
        {
            e.Handled = true;
        }
    }

    private void OnQueueOverlayInteraction(object? sender, RoutedEventArgs args)
    {
        if (Shell?.Queue is not { IsOverlayMode: true, IsOpen: true } queue)
        {
            return;
        }

        if (args.Source is Visual popupSource && IsQueuePopupInteraction(popupSource))
            return;

        if (args.RoutedEvent == InputElement.GotFocusEvent && backSubscribedTopLevel is { } topLevel
            && topLevel.GetVisualDescendants().OfType<OverlayPopupHost>()
                .Any(host => host.Content is null && IsQueuePopupInteraction(host)))
            return;

        if (args is KeyEventArgs { Key: Key.Escape })
        {
            queue.IsOpen = false;
            args.Handled = true;
            return;
        }

        if (args.Source is Visual source
            && (source == QueuePaneHost || QueuePaneHost.IsVisualAncestorOf(source)
                || source == QueueToggleButton || QueueToggleButton.IsVisualAncestorOf(source)
                || source == ToolbarQueueToggleButton || ToolbarQueueToggleButton.IsVisualAncestorOf(source)
                || source == VerticalQueueToggleButton || VerticalQueueToggleButton.IsVisualAncestorOf(source)))
        {
            return;
        }

        queue.IsOpen = false;
    }

    private bool IsQueuePopupInteraction(Visual source)
    {
        while (source.GetSelfAndVisualAncestors().OfType<OverlayPopupHost>().FirstOrDefault()
               is { Parent: Popup popup } host)
        {
            if (queuePopupHosts.Contains(host))
                return true;

            if ((popup.PlacementTarget ?? popup.TemplatedParent as Control ?? popup.Parent as Control)
                is not { } target)
                break;

            if (target == QueuePaneHost || QueuePaneHost.IsVisualAncestorOf(target)
                || target == QueueToggleButton || QueueToggleButton.IsVisualAncestorOf(target)
                || target == ToolbarQueueToggleButton || ToolbarQueueToggleButton.IsVisualAncestorOf(target)
                || target == VerticalQueueToggleButton || VerticalQueueToggleButton.IsVisualAncestorOf(target))
                return true;

            source = target;
        }

        return false;
    }

    private void OnQueuePopupDetached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        if (sender is OverlayPopupHost host)
        {
            host.DetachedFromVisualTree -= OnQueuePopupDetached;
            queuePopupHosts.Remove(host);
        }
    }

    private void OnShellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CancelPredictiveBack();
        var properties = e.GetCurrentPoint(this).Properties;

        // XButton1 = back. XButton2 (forward) is intentionally not handled: navigation is a strict
        // back stack with no forward history.
        if (properties.IsXButton1Pressed && Shell?.TryGoBack() == true)
        {
            e.Handled = true;
        }
    }

    private void OnShellDragEnter(object? sender, DragEventArgs e)
    {
        DragDropLog.Write($"ShellView.DragEnter isQueueDrag={IsQueueDrag(e)} depth={dragHintDepth} formats=[{DragDropLog.DescribeFormats(e.DataTransfer)}]");

        if (!IsQueueDrag(e))
        {
            return;
        }

        dragHintDepth++;
        Shell?.Queue.BeginDragHint();
    }

    private void OnShellDragLeave(object? sender, DragEventArgs e)
    {
        if (dragHintDepth == 0)
        {
            return;
        }

        dragHintDepth--;
        DragDropLog.Write($"ShellView.DragLeave depth={dragHintDepth}");
        if (dragHintDepth != 0)
        {
            return;
        }

        // A drag moving between two child drop targets raises DragLeave (old) before
        // DragEnter (new), so the depth dips to zero mid-move. Defer the close and re-check:
        // a following DragEnter will have restored the depth, leaving the pane open. The depth
        // only stays at zero once the drag has truly left the window.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (dragHintDepth == 0)
                {
                    Shell?.Queue.EndDragHint();
                }
            },
            DispatcherPriority.Background);
    }

    private void OnShellDrop(object? sender, DragEventArgs e)
    {
        DragDropLog.Write($"ShellView.Drop depth={dragHintDepth} formats=[{DragDropLog.DescribeFormats(e.DataTransfer)}]");
        dragHintDepth = 0;
        Shell?.Queue.EndDragHint();
    }

    private static bool IsQueueDrag(DragEventArgs e) =>
        e.DataTransfer.Contains(QueueDragFormats.QueueReorder)
        || e.DataTransfer.Contains(QueueDragFormats.LibraryInsert)
        || (PlatformServices.SupportsLocalFileAccess && e.DataTransfer.Contains(DataFormat.File));


    private async void OnOpenFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell)
        {
            return;
        }

        // Reached without an intervening await, so the click still counts as the user gesture the
        // browser's own picker refuses to open without.
        var paths = PlatformServices.MediaFilePicker is { } picker
            ? await picker()
            : await PickLocalMediaFilesAsync();

        if (paths.Count > 0)
        {
            // Same routing as a file-manager "Open with": videos take the player, the rest the queue.
            shell.OpenActivationFiles(paths);
        }
    }

    private async Task<IReadOnlyList<string>> PickLocalMediaFilesAsync()
    {
        if (!PlatformServices.SupportsLocalFileAccess ||
            TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return [];
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Resources.Shell_OpenMediaFilesPickerTitle,
            AllowMultiple = true,
            FileTypeFilter = new[] { MediaFiles, FilePickerFileTypes.All },
        });

        var paths = new List<string>();
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    private void OnExpandAndFocusSearch(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.Navigation.IsExpanded = true;
        }

        // Focus after the search box has been made visible and laid out.
        Dispatcher.UIThread.Post(() => RailSearchBox?.FocusInput(), DispatcherPriority.Loaded);
    }

    private void OnStatusPeekClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell)
        {
            return;
        }

        // Only peek when the rail is collapsed. If it is already open -- whether the user pinned it
        // or an earlier peek is still up -- the caption is visible, so leave the pane state alone
        // rather than arming a timer that could collapse a pane the user opened deliberately.
        if (shell.Navigation.IsExpanded)
        {
            return;
        }

        shell.Navigation.IsExpanded = true;
        isStatusPeekOpen = true;

        statusPeekTimer ??= new DispatcherTimer { Interval = StatusPeekDuration };
        statusPeekTimer.Stop();
        statusPeekTimer.Tick -= OnStatusPeekElapsed;
        statusPeekTimer.Tick += OnStatusPeekElapsed;
        statusPeekTimer.Start();
    }

    private void OnStatusPeekElapsed(object? sender, EventArgs e)
    {
        statusPeekTimer?.Stop();

        // Skip the auto-collapse if the peek was already dismissed (e.g. the user navigated, which
        // collapses the rail on its own); collapsing again would be redundant.
        if (!isStatusPeekOpen)
        {
            return;
        }

        isStatusPeekOpen = false;

        if (DataContext is ShellViewModel shell)
        {
            shell.Navigation.CollapseOverlay();
        }
    }

    private void OnNarrowSearchClick(object? sender, RoutedEventArgs e)
    {
        // Reveal the narrow layout's top search field in place of the page title, then focus it once
        // it has been made visible and laid out.
        Shell?.OpenSearch();
        Dispatcher.UIThread.Post(() => NarrowSearchBox?.FocusInput(), DispatcherPriority.Loaded);
    }

    private void OnNarrowSearchCloseClick(object? sender, RoutedEventArgs e)
    {
        Shell?.CloseSearch();
    }

    // The toolbar's search button opens the search field in the toolbar's title band, or closes it.
    private void OnToolbarSearchClick(object? sender, RoutedEventArgs e)
    {
        if (Shell is not { } shell || shell.CloseSearch())
        {
            return;
        }

        shell.OpenSearch();
        Dispatcher.UIThread.Post(() => TitleBandSearchBox.FocusInput(), DispatcherPriority.Loaded);
    }

    // An empty title band search field gives the band back to the page title once focus leaves it.
    private void OnTitleBandSearchLostFocus(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Shell is { ShowToolbarSearchField: true } shell && !TitleBandSearchBox.IsKeyboardFocusWithin &&
                string.IsNullOrEmpty(shell.Search.Text))
            {
                shell.CloseSearch();
            }
        }, DispatcherPriority.Input);
    }

    private void OnNarrowTopBarTapped(object? sender, TappedEventArgs e)
    {
        // Tapping the title / empty area of the narrow top bar scrolls the current page's list back
        // to the top (phone "tap the status bar" gesture). Taps on the interactive chrome -- the
        // search field and the status / search buttons -- keep their own behavior instead.
        if (e.Source is Visual source && IsInteractiveTopBarElement(source))
        {
            return;
        }

        // The narrow layout's library sections live in the pager, which also holds the neighboring
        // pages off screen; scroll the one actually on show rather than the first one found.
        PageScrollToTop.Invoke(
            Shell?.ShowLibraryPager == true ? LibraryPagerHost.CurrentPageVisual : PageHost);
    }

    // Walks up from the tapped visual to the top bar, treating the search field and any button as
    // interactive so their taps are not hijacked by scroll-to-top.
    private bool IsInteractiveTopBarElement(Visual source)
    {
        for (Visual? current = source; current is not null && !ReferenceEquals(current, NarrowTopBar); current = current.GetVisualParent())
        {
            if (current is Button or SearchBox)
            {
                return true;
            }
        }

        return false;
    }

    private async void OnViewWarningsClick(object? sender, RoutedEventArgs e)
    {
        // Presentation lives in the dialog service so it is platform-appropriate: a window on
        // desktop, an in-shell overlay on single-view (mobile). Creating a Window here directly
        // crashed on Android, where windows are unsupported.
        if (Shell is { } shell)
        {
            await shell.ShowWarningDetailsAsync();
        }
    }

    private async void OnDownloadDesktopAppClick(object? sender, RoutedEventArgs e)
    {
        await OpenExternalUrlAsync("https://github.com/hjc4869/LightPlayer/releases");
    }

    private async Task OpenExternalUrlAsync(string url)
    {
        // Launcher works across desktop and Android, unlike Process.Start which is desktop-only.
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher
            && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            await launcher.LaunchUriAsync(uri);
        }
    }
}