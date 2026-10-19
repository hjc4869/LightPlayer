using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Behaviors;

/// <summary>
/// The presentation contract every view of a <see cref="VideoTileModel"/> shares: exactly one
/// preview hold while the view is bound and on screen, the folder/video placeholder glyph, the
/// context menu, and activation. Shared by the grid tile and the narrow list row so both recycle
/// previews the same way.
/// </summary>
internal sealed class VideoTilePresenter
{
    // Touch and pen use a longer hold threshold than Avalonia's context gesture so deliberate taps
    // still activate the tile. Mouse keeps the framework's click and right-click behavior.
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(700);

    // How far the contact may drift and still count as a tap or a hold. A scroll starts with a
    // press just like a tap does, and fingers are never perfectly still.
    private const double HoldSlop = 12;

    private readonly Control view;
    private readonly Path? placeholderGlyph;
    private readonly Path? typeGlyph;

    private VideoTileModel? bound;
    private VideoTileModel? previewHolder;
    private bool attached;

    private IDisposable? holdTimer;
    private IPointer? holdPointer;
    private Point holdOrigin;
    private bool holdAbandoned;
    private bool holdMenuShown;
    private VideoTilePresenter(Control view, Path? placeholderGlyph, Path? typeGlyph)
    {
        this.view = view;
        this.placeholderGlyph = placeholderGlyph;
        this.typeGlyph = typeGlyph;

        view.DataContextChanged += (_, _) =>
        {
            bound = view.DataContext as VideoTileModel;
            Refresh();
        };
        view.AttachedToVisualTree += (_, _) =>
        {
            attached = true;
            Refresh();
        };
        view.DetachedFromVisualTree += (_, _) =>
        {
            attached = false;
            EndTouchGesture();
            UpdatePreviewHold();
        };
        view.Tapped += OnTapped;

        // Tunnel, and handled events too: the press lands on whichever element of the template was
        // hit, and the panels this tile sits in mark pointer events handled as they scroll.
        view.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        view.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        view.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        view.AddHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost, handledEventsToo: true);
        view.AddHandler(InputElement.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
        view.AddHandler(InputElement.ContextCanceledEvent, OnContextCanceled, RoutingStrategies.Tunnel);
    }

    public static void Track(Control view, Path? placeholderGlyph = null, Path? typeGlyph = null) =>
        _ = new VideoTilePresenter(view, placeholderGlyph, typeGlyph);

    private void Refresh()
    {
        UpdateGlyphs();
        UpdateContextFlyout();
        UpdatePreviewHold();
    }

    // Exactly one preview hold per view, taken while it is bound and on screen. The virtualizing
    // panels recycle these controls, so dropping the hold here is what keeps decoded preview
    // bitmaps from piling up as the user scrolls.
    private void UpdatePreviewHold()
    {
        var target = attached ? bound : null;
        if (ReferenceEquals(previewHolder, target))
        {
            return;
        }

        previewHolder?.ReleasePreview();
        previewHolder = target;
        previewHolder?.RequestPreview();
    }

    private void UpdateGlyphs()
    {
        if (placeholderGlyph is null && typeGlyph is null)
        {
            return;
        }

        var key = bound?.GlyphKey ?? "IconVideo";
        if (view.TryFindResource(key, out var resource) && resource is Geometry geometry)
        {
            if (placeholderGlyph is not null)
            {
                placeholderGlyph.Data = geometry;
            }

            if (typeGlyph is not null)
            {
                typeGlyph.Data = geometry;
            }
        }
    }

    // Folder tiles have only the recursive action, and a tile inside a playlist has actions the
    // library grid does not, so each tile names the menu it opens.
    private void UpdateContextFlyout()
    {
        var key = bound?.ContextFlyoutKey;

        view.ContextFlyout = !string.IsNullOrEmpty(key) &&
            view.TryFindResource(key, out var resource) && resource is FlyoutBase flyout
                ? flyout
                : null;
    }

    // Mouse only: a touch that never became a hold is activated from its release below, and a touch
    // that did become one is reported as a right tap rather than a tap anyway.
    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (IsHoldCapable(e.Pointer) || IsOnButton(e.Source))
        {
            return;
        }

        e.Handled = Activate();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        EndTouchGesture();

        if (!IsHoldCapable(e.Pointer))
        {
            return;
        }

        holdPointer = e.Pointer;
        holdOrigin = e.GetPosition(view);

        // The inline resume and favorite buttons act on their own press; the tile stays out of it.
        holdAbandoned = IsOnButton(e.Source);
        if (holdAbandoned)
        {
            return;
        }

        // Capture makes a takeover by a surrounding scroll or refresh gesture observable.
        e.Pointer.Capture(view);
        holdTimer = DispatcherTimer.RunOnce(OnHoldElapsed, HoldDuration, DispatcherPriority.Input);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (holdAbandoned || !ReferenceEquals(holdPointer, e.Pointer))
        {
            return;
        }

        var offset = e.GetPosition(view) - holdOrigin;
        if (Math.Abs(offset.X) > HoldSlop || Math.Abs(offset.Y) > HoldSlop)
        {
            // The contact is being dragged: a scroll, not a tap, and no longer a hold either.
            AbandonHold();
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(holdPointer, e.Pointer))
        {
            return;
        }

        var activate = !holdAbandoned &&
            !holdMenuShown &&
            new Rect(view.Bounds.Size).Contains(e.GetPosition(view));

        EndTouchGesture();

        if (activate)
        {
            _ = Activate();
        }
    }

    // A pull, scroll, or flyout has claimed the contact. The tap is over, but the contact remains
    // down, so keep its state until release.
    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(holdPointer, e.Pointer))
        {
            AbandonHold();
        }
    }

    // Suppress Avalonia's shorter hold gesture and let OnHoldElapsed decide when to open the menu.
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (holdPointer is not null && !holdMenuShown && e.TryGetPosition(null, out _))
        {
            e.Handled = true;
        }
    }

    // Keep Avalonia from canceling the custom hold while the contact remains within HoldSlop.
    private void OnContextCanceled(object? sender, RoutedEventArgs e)
    {
        if (holdPointer is not null)
        {
            e.Handled = true;
        }
    }

    private void OnHoldElapsed()
    {
        holdTimer = null;

        // Still holding the capture taken on press: the contact is down, on this tile, and no
        // gesture recognizer has claimed it.
        if (holdAbandoned || holdMenuShown || !ReferenceEquals(holdPointer?.Captured, view))
        {
            return;
        }

        if (view.ContextFlyout is PopupFlyoutBase flyout)
        {
            // Marks the gesture as spent: the release that follows must not also open the item, and
            // the capture the flyout takes must not read as a takeover that never happened.
            holdMenuShown = true;
            holdPointer?.Capture(null);
            if (flyout is not MenuFlyout menu || !NativeContextMenu.TryShow(menu, view))
            {
                flyout.ShowAt(view, showAtPointer: true);
            }
        }
    }

    // The contact is no longer a tap or a hold, but it is still down: keep holding on to it so the
    // framework's pending hold stays suppressed until it is released or a new press replaces it.
    private void AbandonHold()
    {
        holdAbandoned = true;
        holdTimer?.Dispose();
        holdTimer = null;
    }

    private void EndTouchGesture()
    {
        holdTimer?.Dispose();
        holdTimer = null;
        holdPointer = null;
        holdAbandoned = false;
        holdMenuShown = false;
    }

    private bool Activate()
    {
        if (bound?.OpenCommand is not { } command || !command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    private static bool IsHoldCapable(IPointer pointer) =>
        pointer.Type is PointerType.Touch or PointerType.Pen;

    private static bool IsOnButton(object? source) =>
        source is Control control && control.FindAncestorOfType<Button>(includeSelf: true) is not null;
}
