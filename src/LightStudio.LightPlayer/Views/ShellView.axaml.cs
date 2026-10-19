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

    public ShellView()
    {
        InitializeComponent();
        AddHandler(ContextRequestedEvent, OnPredictiveBackContextRequested, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnPredictiveBackInput, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, OnPredictiveBackInput, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddressBarHost.AddHandler(KeyDownEvent, OnAddressBarKeyDown, RoutingStrategies.Tunnel);
        AppleTitle.AddHandler(ContextRequestedEvent, OnPathMenuRequested, RoutingStrategies.Tunnel);
        NarrowTitle.AddHandler(ContextRequestedEvent, OnPathMenuRequested, RoutingStrategies.Tunnel);

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
        }

        observedShell = shell;
        if (shell is not null)
        {
            shell.PropertyChanged += OnShellPropertyChanged;
        }

        UpdatePlaybackBarPlacement();
        UpdateContentInsets();
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
            case nameof(ShellViewModel.ShowAppleTopBar):
            case nameof(ShellViewModel.ShowFloatingPlaybackBar):
                UpdateContentInsets();
                break;
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

    private void OnFloatingPlaybackBarSizeChanged(object? sender, SizeChangedEventArgs args) => UpdateContentInsets();

    // The Apple layout's toolbar and playback bar float over the page area. Pages are laid out clear of
    // them, while the scrollers reaching the page edges keep scrolling underneath.
    private void UpdateContentInsets()
    {
        double top = observedShell?.ShowAppleTopBar == true ? AppleTopBar.Height : 0;
        double bottom = observedShell?.ShowFloatingPlaybackBar == true
            ? FloatingPlaybackBarHost.Bounds.Height + FloatingPlaybackBarHost.Margin.Bottom + FloatingPlaybackBarGap
            : 0;
        var insets = new Thickness(0, top, 0, bottom);
        PageArea.Margin = insets;
        ShellChrome.SetScrollUnderInsets(PageArea, insets);
    }

    /// <summary>
    /// The page insets the Apple layout's toolbar and playback bar reserve, measured while they are
    /// hidden for previews of the page behind a full-window view.
    /// </summary>
    internal Thickness MeasureAppleChromeInsets(bool includePlaybackBar)
    {
        double bottom = 0;
        if (includePlaybackBar)
        {
            PlaybackBarView.Measure(Size.Infinity);
            bottom = PlaybackBarView.DesiredSize.Height + FloatingPlaybackBarHost.Margin.Bottom + FloatingPlaybackBarGap;
        }

        return new Thickness(0, AppleTopBar.Height, 0, bottom);
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
    private IDisposable? windowControlsObserver;
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
            Shell?.SetViewportWidth(Bounds.Width);
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
            shell.SetViewportWidth(Bounds.Width);
        }

        // Platform "back" gesture: Android system back button and browser back both surface through
        // TopLevel.BackRequested. Subscribe here because the TopLevel is not known until attached.
        backSubscribedTopLevel = TopLevel.GetTopLevel(this);
        if (backSubscribedTopLevel is not null)
        {
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

            windowControlsObserver = PlatformServices.ObserveWindowControls?.Invoke(backSubscribedTopLevel, OnWindowControlsChanged);
            UpdateInputPanePadding();
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

        windowControlsObserver?.Dispose();
        windowControlsObserver = null;

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

    private void OnWindowControlsChanged(Thickness area)
    {
        if (Shell is { } shell)
        {
            shell.DeviceWindowControls = area;
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
                || source == ToolbarQueueToggleButton || ToolbarQueueToggleButton.IsVisualAncestorOf(source)))
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
                || target == ToolbarQueueToggleButton || ToolbarQueueToggleButton.IsVisualAncestorOf(target))
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