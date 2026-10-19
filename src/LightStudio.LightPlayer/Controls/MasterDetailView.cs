using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Animations;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.ViewModels;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Adaptive master-detail container shared by the pages that host their own child navigation
/// (playlists, settings).
///
/// On wide layouts the master and detail sit side by side and the detail swaps with the light
/// <see cref="EntrancePageTransition"/> used elsewhere in the shell. On narrow layouts it collapses
/// to a single pane: the master fills the width and the selected detail slides in from the right over
/// it (and back out to the right on return), phone style.
///
/// <see cref="Detail"/> is nullable and is the single source of truth for "is a sub-page open": a
/// null detail means the master is showing. Clearing it (via the platform back gesture routed through
/// the page view model, or the built-in back button) animates the detail away.
/// </summary>
public class MasterDetailView : Decorator
{
    public static readonly StyledProperty<object?> MasterProperty =
        AvaloniaProperty.Register<MasterDetailView, object?>(nameof(Master));

    public static readonly StyledProperty<object?> DetailProperty =
        AvaloniaProperty.Register<MasterDetailView, object?>(nameof(Detail));

    public static readonly StyledProperty<object?> DetailPlaceholderProperty =
        AvaloniaProperty.Register<MasterDetailView, object?>(nameof(DetailPlaceholder));

    public static readonly StyledProperty<GridLength> MasterLengthProperty =
        AvaloniaProperty.Register<MasterDetailView, GridLength>(nameof(MasterLength), new GridLength(300));

    public static readonly StyledProperty<double> CompactThresholdProperty =
        AvaloniaProperty.Register<MasterDetailView, double>(nameof(CompactThreshold), 640d);

    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<MasterDetailView, bool>(nameof(IsCompact));

    public static readonly StyledProperty<bool> ShowDetailBackButtonProperty =
        AvaloniaProperty.Register<MasterDetailView, bool>(nameof(ShowDetailBackButton));

    public static readonly StyledProperty<ICommand?> DetailBackCommandProperty =
        AvaloniaProperty.Register<MasterDetailView, ICommand?>(nameof(DetailBackCommand));

    // Width of the left column reserved for the built-in back button on narrow layouts, so the
    // button sits beside the detail content (in its own column) rather than above it in a row.
    private const double BackButtonColumnWidth = 40d;

    // Left inset of the back button within its reserved column.
    private const double BackButtonLeftInset = 12d;

    // Top inset of the back button, matching the wide-layout top margin detail views use for their
    // own content so the button lines up with the detail's heading.
    private const double BackButtonTopInset = 32d;

    // Where the host has its own back gesture or toolbar (Android, the browser) it replaces the
    // in-app back affordance, so the built-in back column is skipped entirely (no button, no
    // reserved gutter).
    private static readonly bool HostHandlesBack = PlatformServices.HostProvidesBackNavigation;

    private readonly Grid root;
    private readonly ContentPresenter masterHost;
    private readonly ContentPresenter placeholderHost;
    private readonly TransitioningContentControl detailHost;
    private readonly Button backButton;
    private readonly Rectangle masterDetailSeparator;
    // Narrow layouts reuse the shell's hierarchical slide so drilling into a sub-page here feels the
    // same as drilling into any other detail page (eased motion rather than the linear built-in slide).
    private readonly EntrancePageTransition wideTransition = new();
    private readonly SlidePageTransition narrowTransition = new();

    private Border? currentDetailBorder;
    private ListBox? pendingMasterList;
    private ListBoxItem? pendingMasterItem;
    private IPointer? pendingMasterPointer;
    // Pointer whose press-and-hold opened an item's context menu. Tracked separately from the pending
    // selection state (which a capture loss clears) so the matching release is always swallowed.
    private IPointer? contextMenuPointer;
    private bool ready;
    private BackPreview? backPreview;
    private bool completingBackPreview;

    public MasterDetailView()
    {
        masterHost = new ContentPresenter
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        placeholderHost = new ContentPresenter { IsVisible = false };
        detailHost = new DispatcherTransitioningContentControl
        {
            ClipToBounds = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        backButton = BuildBackButton();

        // Thin vertical rule marking the master/detail boundary on wide layouts. It sits above the
        // detail host so it stays visible over the detail's opaque background, and is hidden when
        // compact (single-pane), where the detail overlays the master and there is no visible seam.
        masterDetailSeparator = new Rectangle
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
        };
        masterDetailSeparator.Bind(Shape.FillProperty, this.GetResourceObservable("AppDividerBrush"));

        Grid.SetColumn(placeholderHost, 1);
        Grid.SetColumn(detailHost, 1);
        Grid.SetColumn(masterDetailSeparator, 1);

        root = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        root.Children.Add(masterHost);
        root.Children.Add(placeholderHost);
        root.Children.Add(detailHost);
        root.Children.Add(masterDetailSeparator);
        root.Children.Add(backButton);

        masterHost.Bind(ContentPresenter.ContentProperty, this.GetObservable(MasterProperty));
        placeholderHost.Bind(ContentPresenter.ContentProperty, this.GetObservable(DetailPlaceholderProperty));

        // ListBox selects on pointer press. Defer that selection until a matching left-button
        // release so right-clicks and touch context requests cannot open a detail page.
        masterHost.AddHandler(PointerPressedEvent, OnMasterPointerPressed, RoutingStrategies.Tunnel);
        masterHost.AddHandler(PointerReleasedEvent, OnMasterPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        masterHost.AddHandler(PointerCaptureLostEvent, OnMasterPointerCaptureLost, handledEventsToo: true);
        // Bubble (and handled events too) so the item's own ContextFlyout handler has already run:
        // a handled request is how we know a menu actually opened.
        masterHost.AddHandler(ContextRequestedEvent, OnMasterContextRequested, RoutingStrategies.Bubble, handledEventsToo: true);

        Child = root;
    }

    /// <summary>The persistent master pane (typically the list). Always shown.</summary>
    public object? Master
    {
        get => GetValue(MasterProperty);
        set => SetValue(MasterProperty, value);
    }

    /// <summary>The selected detail, or <see langword="null"/> when nothing is selected.</summary>
    public object? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <summary>Shown in the detail area on wide layouts when <see cref="Detail"/> is null.</summary>
    public object? DetailPlaceholder
    {
        get => GetValue(DetailPlaceholderProperty);
        set => SetValue(DetailPlaceholderProperty, value);
    }

    /// <summary>Width of the master column on wide layouts.</summary>
    public GridLength MasterLength
    {
        get => GetValue(MasterLengthProperty);
        set => SetValue(MasterLengthProperty, value);
    }

    /// <summary>Width below which the container collapses to the single-pane (narrow) layout.</summary>
    public double CompactThreshold
    {
        get => GetValue(CompactThresholdProperty);
        set => SetValue(CompactThresholdProperty, value);
    }

    /// <summary>True while the single-pane (narrow) layout is active. Set by the control.</summary>
    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    /// <summary>Whether to show the built-in back button over the detail on narrow layouts.</summary>
    public bool ShowDetailBackButton
    {
        get => GetValue(ShowDetailBackButtonProperty);
        set => SetValue(ShowDetailBackButtonProperty, value);
    }

    /// <summary>Command run by the built-in back button (should clear the selection).</summary>
    public ICommand? DetailBackCommand
    {
        get => GetValue(DetailBackCommandProperty);
        set => SetValue(DetailBackCommandProperty, value);
    }

    /// <summary>
    /// The pane the user is currently working in: the detail host when a sub-page is open, otherwise
    /// the master host. On narrow layouts the master stays visible behind the detail overlay, so this
    /// (rather than raw visibility) is how callers such as the shell's tap-to-scroll-top target the
    /// active pane and leave the inactive master's scroll position untouched.
    /// </summary>
    public Visual ActiveContentRoot => Detail is not null ? detailHost : masterHost;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundsProperty ||
            change.Property == CompactThresholdProperty ||
            change.Property == MasterLengthProperty)
        {
            CancelBackPreview();
            Relayout();
        }
        else if (change.Property == DetailProperty)
        {
            CancelBackPreview();
            ApplyDetail(change.GetNewValue<object?>());
        }
        else if (change.Property == DetailBackCommandProperty)
        {
            CancelBackPreview();
        }
        else if (change.Property == ShowDetailBackButtonProperty)
        {
            UpdateChrome();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelBackPreview();
        base.OnDetachedFromVisualTree(e);
    }

    internal BackPreview? BeginBackPreview(IInPageBackHandler handler)
    {
        if (backPreview is not null || completingBackPreview || !IsEffectivelyVisible || !IsCompact ||
            !ReferenceEquals(DataContext, handler) || !handler.CanPreviewBack || Detail is not { } detail ||
            detailHost.Bounds.Width <= 0 ||
            currentDetailBorder is not { Bounds.Width: > 0 } ||
            currentDetailBorder?.GetVisualParent() is not { RenderTransform: null, Opacity: 1, IsVisible: true })
            return null;

        backPreview = new BackPreview(this, detail, handler, narrowTransition.Begin(currentDetailBorder, null, forward: false));
        return backPreview;
    }

    private void CancelBackPreview()
    {
        BackPreview? preview = backPreview;
        backPreview = null;
        preview?.Dispose();
    }

    // Places the panes for the current width: side by side when wide, single pane (master full width
    // with the detail overlaid on top) when narrow. Layout changes here are instantaneous; only detail
    // swaps animate.
    private void Relayout()
    {
        var width = Bounds.Width;
        if (width <= 0)
        {
            return;
        }

        var compact = width < CompactThreshold;
        SetCurrentValue(IsCompactProperty, compact);

        if (compact)
        {
            root.ColumnDefinitions[0].Width = GridLength.Star;
            root.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetColumnSpan(masterHost, 2);
            Grid.SetColumn(detailHost, 0);
            Grid.SetColumnSpan(detailHost, 2);
            Grid.SetColumn(backButton, 0);
            Grid.SetColumnSpan(backButton, 2);
        }
        else
        {
            root.ColumnDefinitions[0].Width = MasterLength;
            root.ColumnDefinitions[1].Width = GridLength.Star;
            Grid.SetColumnSpan(masterHost, 1);
            Grid.SetColumn(detailHost, 1);
            Grid.SetColumnSpan(detailHost, 1);
        }

        ready = true;
        UpdateChrome();
    }

    // Wraps the incoming detail in a border and swaps it into the content host with the transition
    // appropriate to the current layout.
    private void ApplyDetail(object? detail)
    {
        Border? visual = null;
        if (detail is not null)
        {
            var content = new ContentControl
            {
                Content = detail,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            visual = new Border { Child = content, Padding = ComputeDetailPadding(detail) };
        }

        currentDetailBorder = visual;

        // Reverse the transition when clearing the detail so it slides/scales back out the way it came.
        detailHost.IsTransitionReversed = detail is null;
        // Suppress the transition for the first assignment (initial load / auto-selection) so opening
        // the page does not double up with the shell's own page transition.
        detailHost.PageTransition = ready && !completingBackPreview ? (IsCompact ? narrowTransition : wideTransition) : null;
        detailHost.IsHitTestVisible = detail is not null;
        detailHost.Content = visual;

        UpdateChrome();
    }

    private void UpdateChrome()
    {
        var hasDetail = Detail is not null;
        backButton.IsVisible = ShouldShowBackButton(Detail);
        placeholderHost.IsVisible = !IsCompact && !hasDetail && DetailPlaceholder is not null;
        // The boundary rule belongs to the two-pane (wide) layout only.
        masterDetailSeparator.IsVisible = !IsCompact;
        if (currentDetailBorder is not null)
        {
            currentDetailBorder.Padding = ComputeDetailPadding(Detail);
            // Compact details cover the master, so use the opaque page brush instead of a Mica layer.
            currentDetailBorder.Bind(Border.BackgroundProperty,
                this.GetResourceObservable(IsCompact ? "AppPageBackgroundBrush" : "AppContentLayerBrush"));
        }
    }

    // The built-in back button is shown only in the narrow single-pane layout, when a detail is open
    // and the host opted in -- and never where the host supplies back navigation itself.
    private bool ShouldShowBackButton(object? detail) =>
        !HostHandlesBack && IsCompact && ShowDetailBackButton && detail is not null;

    // The detail area itself carries no content padding: each detail view applies its own (adaptive)
    // margins inside its scroll viewer so the scroll bar stays hard against the pane edge. The only
    // inset here is the gutter reserved for the built-in back button.
    private Thickness ComputeDetailPadding(object? detail) =>
        ShouldShowBackButton(detail) ? new Thickness(BackButtonColumnWidth, 0, 0, 0) : default;

    private Button BuildBackButton()
    {
        var path = new Path();
        path.Bind(Path.DataProperty, this.GetResourceObservable("IconArrowLeft"));
        path.Bind(Shape.FillProperty, this.GetResourceObservable("AppPrimaryTextBrush"));

        var button = new Button
        {
            Classes = { "icon" },
            Content = new Viewbox { Width = 20, Height = 20, Child = path },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(BackButtonLeftInset, BackButtonTopInset, 0, 0),
            IsVisible = false,
        };
        ToolTip.SetTip(button, Strings.Resources.Common_Back);
        button.Click += OnBackButtonClick;
        return button;
    }

    private void OnBackButtonClick(object? sender, RoutedEventArgs e)
    {
        var command = DetailBackCommand;
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }

    internal sealed class BackPreview : IDisposable
    {
        private readonly MasterDetailView owner;
        private readonly object detail;
        private readonly IInPageBackHandler handler;
        private bool disposed;

        internal BackPreview(MasterDetailView owner, object detail, IInPageBackHandler handler,
            PageTransitionSession session)
        {
            this.owner = owner;
            this.detail = detail;
            this.handler = handler;
            Session = session;
        }

        internal PageTransitionSession Session { get; }
        internal bool IsCurrent => !disposed && ReferenceEquals(owner.Detail, detail) &&
            ReferenceEquals(owner.DataContext, handler) && owner.IsCompact && handler.CanPreviewBack;

        internal bool Commit()
        {
            if (!IsCurrent)
                return false;
            owner.completingBackPreview = true;
            try
            {
                return handler.TryHandleBack();
            }
            finally
            {
                owner.completingBackPreview = false;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            Session.Dispose();
            if (ReferenceEquals(owner.backPreview, this))
                owner.backPreview = null;
        }
    }

    private void OnMasterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        contextMenuPointer = null;

        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item is null || !IsLeftActivation(e))
        {
            ClearPendingMasterSelection();
            if (item is not null)
            {
                e.Handled = true;
            }

            return;
        }

        pendingMasterItem = item;
        pendingMasterList = item.FindAncestorOfType<ListBox>();
        pendingMasterPointer = e.Pointer;
        e.Handled = true;
    }

    private void OnMasterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // A touch press-and-hold that opened the item's context menu must not also navigate. Simply
        // skipping our own selection is not enough: the framework selects touch/pen items on RELEASE
        // (ListBoxItem.OnPointerReleased -> UpdateSelectionFromEvent, which only bails out when the
        // event is already handled), so the release has to be swallowed.
        if (ReferenceEquals(contextMenuPointer, e.Pointer))
        {
            contextMenuPointer = null;
            ClearPendingMasterSelection();
            e.Handled = true;
            return;
        }

        if (pendingMasterItem is null || !ReferenceEquals(pendingMasterPointer, e.Pointer))
        {
            ClearPendingMasterSelection();
            return;
        }

        var item = pendingMasterItem;
        var releasePoint = e.GetCurrentPoint(item).Position;
        if (pendingMasterList is not null &&
            IsLeftRelease(e) &&
            new Rect(item.Bounds.Size).Contains(releasePoint))
        {
            pendingMasterList.SetCurrentValue(
                SelectingItemsControl.SelectedItemProperty,
                item.DataContext);
        }

        // Swallow every release we tracked (including drag-away cancels): this control owns master
        // selection, and an unhandled release lets the framework's touch/pen selection run anyway.
        // Right-button releases never get here (the press clears the pending state), so the mouse
        // context menu, which is raised from an unhandled release, still opens.
        e.Handled = true;
        ClearPendingMasterSelection();
    }

    private void OnMasterPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        ClearPendingMasterSelection();

    // Touch hold raises ContextRequested while the finger is still down; the item's ContextFlyout
    // marks it handled when it opens a menu. Only then is the pending selection canceled, so a long
    // press on a list without a context menu still activates the item normally.
    private void OnMasterContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled && pendingMasterItem is not null)
        {
            contextMenuPointer = pendingMasterPointer;
        }
    }

    private static bool IsLeftActivation(PointerPressedEventArgs e) =>
        e.Pointer.Type == PointerType.Touch ||
        e.GetCurrentPoint(null).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;

    private static bool IsLeftRelease(PointerReleasedEventArgs e) =>
        e.Pointer.Type == PointerType.Touch || e.InitialPressMouseButton == MouseButton.Left;

    private void ClearPendingMasterSelection()
    {
        pendingMasterList = null;
        pendingMasterItem = null;
        pendingMasterPointer = null;
    }
}
