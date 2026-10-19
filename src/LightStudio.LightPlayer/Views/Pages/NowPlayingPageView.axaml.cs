using LightMediaRenderer.Avalonia.Controls;
using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Controls;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class NowPlayingPageView : UserControl
{
    private const double MetadataWidth = 270d;
    private const double UpcomingWidth = 360d;
    private const double TwoColumnBreakpoint = 750d;
    private const double ThreeColumnBreakpoint = 1100d;
    private const double DragThreshold = 12d;
    private const double CommitFraction = 0.25d;
    private const double FlickSpeed = 700d;
    private const double SlideDurationMs = 240d;

    private readonly TranslateTransform pagerTransform = new();
    private readonly Glide pagerGlide;
    private readonly TouchpadSwipe touchpadSwipe;
    private readonly Stopwatch dragClock = new();
    private int visibleColumns;
    private int firstCard;
    private double viewportWidth;
    private double pagerPosition;
    private double dragStartOffset;
    private Point dragOrigin;
    private IPointer? dragPointer;
    private bool dragging;
    private bool sliding;
    private bool touchpadDragging;
    private bool pagerVisible;
    private Visual[] visibilityAncestors = [];

    public NowPlayingPageView()
    {
        InitializeComponent();
        RootGrid.RenderTransform = pagerTransform;
        pagerGlide = new Glide(this, ApplyPagerPosition) { Completed = () => sliding = false };
        touchpadSwipe = new TouchpadSwipe(PagerHost,
            () => pagerVisible && visibleColumns < 3 && !sliding && dragPointer is null,
            movement =>
            {
                if (!touchpadDragging)
                {
                    dragStartOffset = PositionToOffset(pagerPosition);
                    touchpadDragging = true;
                }

                MovePager(movement);
            },
            (commit, elapsed) =>
            {
                if (touchpadDragging)
                {
                    touchpadDragging = false;
                    SettleSwipe(commit, elapsed);
                }
            });

        PagerHost.AddHandler(PointerPressedEvent, OnPagerPointerPressed, RoutingStrategies.Tunnel);
        PagerHost.AddHandler(PointerMovedEvent, OnPagerPointerMoved, RoutingStrategies.Tunnel);
        PagerHost.AddHandler(PointerReleasedEvent, OnPagerPointerReleased, RoutingStrategies.Tunnel);
        PagerHost.PointerCaptureLost += (_, e) =>
        {
            if (dragPointer?.Id == e.Pointer.Id)
            {
                EndPointerDrag(commit: false);
            }
        };
        PagerHost.SizeChanged += (_, _) =>
        {
            ApplyAdaptiveLayout();
            UpdateLyricsPageInsets();
        };
        LyricsControl.SizeChanged += (_, _) => UpdateLyricsPageInsets();
        PagerHost.EffectiveViewportChanged += (_, e) =>
        {
            var viewport = e.EffectiveViewport.Intersect(new Rect(PagerHost.Bounds.Size));
            SetPagerVisible(IsEffectivelyVisible && viewport.Width > 0 && viewport.Height > 0);
        };
        Loaded += (_, _) =>
        {
            SetPagerVisible(IsEffectivelyVisible);
            ApplyAdaptiveLayout();
            UpdateLyricsPageInsets();
        };
        Unloaded += (_, _) => SetPagerVisible(false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && pagerGlide is not null)
        {
            ApplyAdaptiveLayout();
            UpdateLyricsPageInsets();
        }
        else if (change.Property == IsVisibleProperty && pagerGlide is not null)
        {
            SetPagerVisible(IsEffectivelyVisible);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        visibilityAncestors = this.GetVisualAncestors().ToArray();
        foreach (var ancestor in visibilityAncestors)
        {
            ancestor.PropertyChanged += OnAncestorPropertyChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        foreach (var ancestor in visibilityAncestors)
        {
            ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        }

        visibilityAncestors = [];
        SetPagerVisible(false);
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
        {
            SetPagerVisible(IsEffectivelyVisible);
        }
    }

    private void ApplyAdaptiveLayout()
    {
        var width = PagerHost.Bounds.Width;
        if (width <= 0)
        {
            return;
        }

        var columns = Bounds.Width > ThreeColumnBreakpoint ? 3 : Bounds.Width > TwoColumnBreakpoint ? 2 : 1;
        if (visibleColumns == columns && viewportWidth.Equals(width))
        {
            return;
        }

        ResetInteraction();
        viewportWidth = width;
        if (visibleColumns != columns)
        {
            visibleColumns = columns;
            firstCard = columns == 1 ? 1 : 0;
        }

        PageDots.IsVisible = columns < 3;
        ApplyPagerPosition(firstCard);
    }

    private void UpdateLyricsPageInsets()
    {
        if (Bounds.Height <= 0 || LyricsControl.Bounds.Height <= 0 ||
            LyricsControl.TranslatePoint(new Point(0, 0), this) is not { } position)
        {
            return;
        }

        LyricsControl.PageTopInset = Math.Max(0d, position.Y);
        LyricsControl.PageBottomInset = Math.Max(0d, Bounds.Height - position.Y - LyricsControl.Bounds.Height);
    }

    private void OnPageDotClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var index))
        {
            return;
        }

        ResetInteraction();
        SetCard(visibleColumns switch
        {
            1 => index,
            2 => index == 0 ? 0 : index == 2 ? 1 : firstCard,
            _ => 0,
        });
    }

    private void SetCard(int index)
    {
        firstCard = Math.Clamp(index, 0, Math.Max(0, 3 - visibleColumns));
        pagerGlide.Stop();
        sliding = pagerVisible && Math.Abs(pagerPosition - firstCard) > 0.0005d;
        if (sliding)
        {
            pagerGlide.Start(pagerPosition, firstCard, SlideDurationMs);
        }
        else
        {
            ApplyPagerPosition(firstCard);
        }
    }

    private double PositionToOffset(double position) => visibleColumns == 1 && position > 1
        ? MetadataWidth + UpcomingWidth * (position - 1)
        : MetadataWidth * position;

    private double OffsetToPosition(double offset) => visibleColumns == 1 && offset > MetadataWidth
        ? 1 + (offset - MetadataWidth) / UpcomingWidth
        : offset / MetadataWidth;

    private void MovePager(double movement)
    {
        var offset = Math.Clamp(dragStartOffset - movement, 0, PositionToOffset(3 - visibleColumns));
        ApplyPagerPosition(OffsetToPosition(offset));
    }

    private void ApplyPagerPosition(double position)
    {
        pagerPosition = position;
        var lyricsWidth = visibleColumns switch
        {
            1 => viewportWidth,
            2 => viewportWidth - MetadataWidth + (MetadataWidth - UpcomingWidth) * position,
            _ => viewportWidth - MetadataWidth - UpcomingWidth,
        };
        var offset = PositionToOffset(position);

        RootGrid.ColumnDefinitions[1].Width = new GridLength(Math.Max(0, lyricsWidth));
        RootGrid.Width = MetadataWidth + Math.Max(0, lyricsWidth) + UpcomingWidth;
        pagerTransform.X = -offset;
        RootGrid.InvalidateArrange();

        MetadataPanel.IsVisible = offset < MetadataWidth - 0.5d;
        LyricsPanel.IsVisible = offset + viewportWidth > MetadataWidth + 0.5d &&
            offset < MetadataWidth + lyricsWidth - 0.5d;
        UpcomingPanel.IsVisible = offset + viewportWidth > MetadataWidth + lyricsWidth + 0.5d;
        MetadataDot.Classes.Set("current", MetadataPanel.IsVisible);
        LyricsDot.Classes.Set("current", LyricsPanel.IsVisible);
        UpcomingDot.Classes.Set("current", UpcomingPanel.IsVisible);
    }

    private void OnPagerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!pagerVisible || visibleColumns >= 3 || dragPointer is not null ||
            e.Pointer.Type != PointerType.Touch)
        {
            return;
        }

        var interrupted = sliding;
        pagerGlide.Stop();
        sliding = false;
        dragPointer = e.Pointer;
        dragOrigin = e.GetPosition(PagerHost);
        dragStartOffset = PositionToOffset(pagerPosition);
        dragClock.Restart();
        dragging = false;
        if (interrupted)
        {
            e.Pointer.Capture(PagerHost);
            e.Handled = true;
        }
    }

    private void OnPagerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragPointer?.Id != e.Pointer.Id)
        {
            return;
        }

        var movement = e.GetPosition(PagerHost) - dragOrigin;
        if (!dragging)
        {
            if (Math.Abs(movement.Y) >= DragThreshold && Math.Abs(movement.Y) >= Math.Abs(movement.X))
            {
                EndPointerDrag(commit: true);
                return;
            }

            if (Math.Abs(movement.X) < DragThreshold)
            {
                return;
            }

            dragging = true;
            e.Pointer.Capture(PagerHost);
        }

        MovePager(movement.X);
        e.PreventGestureRecognition();
        e.Handled = true;
    }

    private void OnPagerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (dragPointer?.Id == e.Pointer.Id)
        {
            e.Handled = dragging || e.Pointer.Captured == PagerHost;
            EndPointerDrag(commit: true);
        }
    }

    private void EndPointerDrag(bool commit)
    {
        var wasDragging = dragging;
        var elapsed = dragClock.Elapsed;
        ReleasePointer();
        if (wasDragging)
        {
            SettleSwipe(commit, elapsed);
        }
        else
        {
            SetCard(firstCard);
        }
    }

    private void SettleSwipe(bool commit, TimeSpan elapsed)
    {
        var movement = dragStartOffset - PositionToOffset(pagerPosition);
        var origin = Math.Round(OffsetToPosition(dragStartOffset), 6);
        var forward = movement < 0;
        var span = visibleColumns == 1 && (forward ? origin >= 1 : origin > 1)
            ? UpcomingWidth : MetadataWidth;
        var speed = Math.Abs(movement) / Math.Max(0.016d, elapsed.TotalSeconds);
        var directed = Math.Abs(movement) >= span * CommitFraction ||
            (speed >= FlickSpeed && Math.Abs(movement) >= DragThreshold * 2);
        var target = directed
            ? forward ? (int)Math.Floor(origin) + 1 : (int)Math.Ceiling(origin) - 1
            : (int)Math.Round(pagerPosition);
        SetCard(commit ? target : firstCard);
    }

    private void ReleasePointer()
    {
        var pointer = dragPointer;
        dragPointer = null;
        dragging = false;
        dragClock.Reset();
        if (pointer?.Captured == PagerHost)
        {
            pointer.Capture(null);
        }
    }

    private void ResetInteraction()
    {
        pagerGlide.Stop();
        sliding = false;
        touchpadSwipe.Reset();
        touchpadDragging = false;
        ReleasePointer();
    }

    private void SetPagerVisible(bool visible)
    {
        pagerVisible = visible;
        if (!visible)
        {
            ResetInteraction();
            ApplyPagerPosition(firstCard);
        }
    }
}
