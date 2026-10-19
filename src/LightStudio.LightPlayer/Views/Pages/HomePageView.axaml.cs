using LightMediaRenderer.Avalonia.Controls;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Reactive;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Controls;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Views.Pages;

/// <summary>
/// Hosts the home page, and owns the parts of it that are geometry and gesture rather than state:
/// the spotlight card's shape, the swipe that turns it over, and the sideways stepping of the
/// tile rows.
/// </summary>
public partial class HomePageView : UserControl
{
    // Alpha ramp of the spotlight scrim, top to bottom: the upper part of the artwork is left
    // untouched, then the page background ramps in to back the title and metadata.
    private static readonly (double Offset, double Alpha)[] ScrimStops =
    [
        (0.00, 0.00),
        (0.40, 0.05),
        (0.56, 0.30),
        (0.75, 0.68),
        (1.00, 0.94),
    ];

    // Alpha ramp of the side scrim, left to right. Backs the cover and the text without touching the
    // far side of the card, which keeps showing the artwork or preview frame as it is.
    private static readonly (double Offset, double Alpha)[] SideScrimStops =
    [
        (0.00, 0.64),
        (0.38, 0.42),
        (0.74, 0.08),
        (1.00, 0.00),
    ];

    /// <summary>Shape the card keeps on the wide layout, and the floor its content still fits in.</summary>
    private const double CardAspect = 4.0d;
    private const double MinCardHeight = 200d;

    /// <summary>The narrow layout is a fraction of the width, so the card is far closer to square.</summary>
    private const double NarrowCardAspect = 1.75d;
    private const double NarrowMinCardHeight = 150d;

    private const double SpotlightBreakpoint = 500d;

    /// <summary>Gap between the card and the one waiting either side of it.</summary>
    private const double CardGap = 16d;

    private const double CardSlideMs = 300d;

    /// <summary>How far a pointer travels before it is a swipe rather than a click.</summary>
    private const double DragSlopPx = 12d;

    /// <summary>Fraction of the card a drag has to cross for it to turn over on release.</summary>
    private const double CommitFraction = 0.25d;

    /// <summary>A flick this fast turns the card over however short it was.</summary>
    private const double FlickPxPerSecond = 700d;

    /// <summary>How much of a row one press of its step arrows covers, leaving a tile of overlap.</summary>
    private const double RowStepFraction = 0.8d;

    private const double RowStepMs = 260d;

    private readonly Glide cardGlide;
    private readonly Glide rowGlide;
    private readonly Glide artworkGlide;
    private readonly TouchpadSwipe spotlightTouchpadSwipe;
    private readonly Stopwatch dragClock = Stopwatch.StartNew();
    private readonly LinearGradientBrush scrimBrush = CreateFade(ScrimStops, horizontal: false);
    private readonly LinearGradientBrush sideScrimBrush = CreateFade(SideScrimStops, horizontal: true);

    private IDisposable? scrimSubscription;
    private HomePageViewModel? subscribed;
    private SpotlightSlideModel? artworkSlide;
    private ScrollViewer? steppingRow;
    private int? dragPointerId;
    private Point dragOrigin;
    private double dragStartOffset;
    private TimeSpan dragStartedAt;
    private double cardOffset;
    private int pendingStep;
    private bool dragging;
    private bool sliding;
    private bool attached;
    private bool spotlightVisible;

    public HomePageView()
    {
        InitializeComponent();

        // The card template reaches these through DynamicResource. They are published once and then
        // only recolored: replacing the entries would raise a resource change that feeds straight
        // back into the observable driving the recolor.
        Resources["SpotlightScrimBrush"] = scrimBrush;
        Resources["SpotlightSideScrimBrush"] = sideScrimBrush;

        cardGlide = new Glide(this, ApplyCardOffset) { Completed = OnCardSlideLanded };
        spotlightTouchpadSwipe = new TouchpadSwipe(SpotlightHost,
            () => spotlightVisible && dragPointerId is null &&
                ViewModel is { HasSpotlightSteps: true },
            offset =>
            {
                dragging = true;
                UpdateCardSwipe(offset);
            }, EndCardSwipe, BeginCardSwipe);
        artworkGlide = new Glide(this, opacity =>
        {
            if (artworkSlide is { } slide)
            {
                slide.PreviousBackdropOpacity = opacity;
            }
        }) { Completed = FinishArtworkTransition };
        rowGlide = new Glide(this, offset =>
        {
            if (steppingRow is { } row)
            {
                row.Offset = row.Offset.WithX(offset);
            }
        });

        RouteRowWheelToPage(UpcomingRow);
        RouteRowWheelToPage(RecentlyPlayedRow);
        RouteRowWheelToPage(ContinueWatchingRow);
        RouteRowWheelToPage(RecentlyAddedRow);

        // Seen on the way down: the card is a button, which handles the press itself and would
        // otherwise never let these through.
        SpotlightViewport.AddHandler(PointerPressedEvent, OnCardPointerPressed, RoutingStrategies.Tunnel);
        SpotlightViewport.AddHandler(PointerMovedEvent, OnCardPointerMoved, RoutingStrategies.Tunnel);
        SpotlightViewport.AddHandler(PointerReleasedEvent, OnCardPointerReleased, RoutingStrategies.Tunnel);

        // A swipe holds the pointer on the viewport, so the loss of that hold is reported there
        // rather than here: capture-lost does not travel up the tree the way the pointer events do.
        SpotlightViewport.PointerCaptureLost += (_, e) =>
        {
            if (dragPointerId == e.Pointer.Id)
            {
                EndCardDrag(commit: false);
            }
        };

        SpotlightHost.SizeChanged += (_, e) => ApplyCardHeight(e.NewSize.Width);
        SpotlightHost.EffectiveViewportChanged += (_, e) =>
        {
            var viewport = e.EffectiveViewport.Intersect(new Rect(SpotlightHost.Bounds.Size));
            SetSpotlightActive(attached && IsEffectivelyVisible &&
                viewport.Width > 0 && viewport.Height > 0);
        };

        // The cards either side are placed a viewport apart, so where they sit depends on how wide
        // that viewport currently is.
        SpotlightViewport.SizeChanged += (_, _) => ApplyCardOffset(cardOffset);
    }

    private HomePageViewModel? ViewModel => DataContext as HomePageViewModel;

    private void OnHomeScrollViewerGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional &&
            e.Source is Control target)
        {
            target.BringIntoView();
        }
    }

    private void OnSpotlightContextItemChanged(object? sender, EventArgs e)
    {
        if (sender is not Control target)
        {
            return;
        }

        var key = target.DataContext switch
        {
            LibraryTileModel => "LibraryTileContextFlyout",
            VideoTileModel tile => tile.ContextFlyoutKey,
            _ => null,
        };
        target.ContextFlyout = key is not null && this.TryFindResource(key, out var resource)
            ? resource as MenuFlyout
            : null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && SpotlightHost is not null)
        {
            ApplySpotlightLayout();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;

        // The page background is both theme-variant and user-palette driven, so track the resource
        // rather than baking a color into the gradient.
        scrimSubscription?.Dispose();
        scrimSubscription = this.GetResourceObservable("AppPageBackgroundBrush")
            .Subscribe(new AnonymousObserver<object?>(value => ApplyScrim(value as ISolidColorBrush)));

        SetSpotlightActive(IsEffectivelyVisible);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        attached = false;
        scrimSubscription?.Dispose();
        scrimSubscription = null;

        rowGlide.Stop();
        steppingRow = null;

        SetSpotlightActive(false);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Attach();
        SetSpotlightActive(attached && IsEffectivelyVisible);
    }

    // The card only rotates while the page is the one on screen; a home page still held on the
    // navigation stack has no reason to keep swapping artwork.
    private void SetSpotlightActive(bool value)
    {
        spotlightVisible = value;
        if (!value)
        {
            OnSpotlightResetRequested();
            FinishArtworkTransition();
        }

        ViewModel?.SetSpotlightActive(value);
    }

    private void Attach()
    {
        if (spotlightTouchpadSwipe is not null)
            OnSpotlightResetRequested();
        if (subscribed is not null)
        {
            subscribed.SpotlightAdvanceRequested -= OnSpotlightAdvanceRequested;
            subscribed.SpotlightResetRequested -= OnSpotlightResetRequested;
            subscribed.PropertyChanged -= OnViewModelPropertyChanged;
        }

        subscribed = ViewModel;
        if (subscribed is not null)
        {
            subscribed.SpotlightAdvanceRequested += OnSpotlightAdvanceRequested;
            subscribed.SpotlightResetRequested += OnSpotlightResetRequested;
            subscribed.PropertyChanged += OnViewModelPropertyChanged;
        }

        AttachArtwork();
        ApplySpotlightLayout();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomePageViewModel.SpotlightCurrent))
        {
            AttachArtwork();
        }
    }

    private void AttachArtwork()
    {
        FinishArtworkTransition();
        if (artworkSlide is not null)
        {
            artworkSlide.PropertyChanged -= OnArtworkPropertyChanged;
        }

        artworkSlide = ViewModel?.SpotlightCurrent;
        if (artworkSlide is not null)
        {
            artworkSlide.ReleasePreviousBackdrop();
            artworkSlide.PropertyChanged += OnArtworkPropertyChanged;
        }
    }

    private void OnArtworkPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SpotlightSlideModel.Backdrop))
        {
            return;
        }

        if (spotlightVisible && IsEffectivelyVisible && artworkSlide is { ShowCover: true, PreviousBackdrop: not null })
        {
            artworkGlide.Start(1, 0, 300);
        }
        else
        {
            FinishArtworkTransition();
        }
    }

    private void FinishArtworkTransition()
    {
        artworkGlide?.Stop();
        artworkSlide?.ReleasePreviousBackdrop();
    }

    private void ApplySpotlightLayout()
    {
        ViewModel?.SetSpotlightNarrow(Bounds.Width < SpotlightBreakpoint);
        ApplyCardHeight(SpotlightHost.Bounds.Width);
    }

    private void OnAlbumPicksSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not ItemsControl { ItemsPanelRoot: UniformTileGrid grid } itemsControl ||
            e.NewSize.Width <= 0)
        {
            return;
        }

        var minWidth = Math.Max(1d, grid.MinItemWidth);
        var minHeight = Math.Max(1d, grid.MinItemHeight);
        var columns = Math.Max(1, (int)(e.NewSize.Width / (minWidth + grid.MinColumnSpacing)));
        var used = columns * (minWidth + grid.MinColumnSpacing) - grid.MinColumnSpacing;
        var extra = Math.Max(0d, Math.Truncate((e.NewSize.Width - used) / columns));
        var itemHeight = minHeight + minHeight * (extra / minWidth);
        var rows = columns <= 4 ? 4 : 2;
        itemsControl.MaxHeight = rows * itemHeight + (rows - 1) * grid.MinRowSpacing;
    }

    // ---- Spotlight geometry --------------------------------------------------

    /// <summary>
    /// Holds the card to one shape as the page resizes: its width comes from the column, so the
    /// height has to follow rather than stay where a fixed value put it.
    /// </summary>
    private void ApplyCardHeight(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var narrow = Bounds.Width < SpotlightBreakpoint;
        var height = Math.Max(
            narrow ? NarrowMinCardHeight : MinCardHeight,
            width / (narrow ? NarrowCardAspect : CardAspect));
        if (double.IsNaN(SpotlightHost.Height) || Math.Abs(SpotlightHost.Height - height) > 0.5)
        {
            SpotlightHost.Height = height;
        }
    }

    /// <summary>Places the three drawn cards, with the middle one carried to <paramref name="offset"/>.</summary>
    private void ApplyCardOffset(double offset)
    {
        cardOffset = offset;
        var span = SpotlightViewport.Bounds.Width + CardGap;
        SetCardOffset(SpotlightCurrentCard, offset);
        SetCardOffset(SpotlightPreviousCard, offset - span);
        SetCardOffset(SpotlightNextCard, offset + span);
    }

    private static void SetCardOffset(ContentControl card, double x)
    {
        if (card.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            card.RenderTransform = transform;
        }

        transform.X = x;
    }

    // ---- Spotlight gestures --------------------------------------------------

    private void OnSpotlightResetRequested()
    {
        spotlightTouchpadSwipe.Reset();
        cardGlide.Stop();
        sliding = false;
        pendingStep = 0;
        dragPointerId = null;
        dragging = false;
        ApplyCardOffset(0);
    }

    private void OnSpotlightAdvanceRequested(int step)
    {
        // A card the user is already working on keeps its place until they are done with it.
        if (!sliding && dragPointerId is null && !spotlightTouchpadSwipe.IsTracking &&
            !SpotlightViewport.GetVisualDescendants().OfType<Control>().Any(control => control.ContextFlyout?.IsOpen == true))
        {
            StartCardSlide(step);
        }
    }

    private void OnSpotlightPreviousClick(object? sender, RoutedEventArgs e) => StepCard(-1);

    private void OnSpotlightNextClick(object? sender, RoutedEventArgs e) => StepCard(1);

    private void StepCard(int step)
    {
        if (!sliding && dragPointerId is null && !spotlightTouchpadSwipe.IsTracking &&
            ViewModel is { HasSpotlightSteps: true })
        {
            StartCardSlide(step);
        }
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch ||
            dragPointerId is not null ||
            ViewModel is not { HasSpotlightSteps: true } ||
            !e.GetCurrentPoint(SpotlightViewport).Properties.IsLeftButtonPressed)
        {
            return;
        }

        bool interrupted = sliding;
        BeginCardSwipe();
        dragPointerId = e.Pointer.Id;
        dragOrigin = e.GetPosition(SpotlightViewport);
        dragStartedAt = dragClock.Elapsed;
        if (interrupted)
        {
            e.Pointer.Capture(SpotlightViewport);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Follows the pointer once it has traveled far enough sideways to be a swipe. A move that goes
    /// down the page instead belongs to the page's own scrolling, and the press is let go of.
    /// </summary>
    private void OnCardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragPointerId != e.Pointer.Id)
        {
            return;
        }

        var point = e.GetPosition(SpotlightViewport);
        var dx = point.X - dragOrigin.X;
        var dy = point.Y - dragOrigin.Y;

        if (!dragging)
        {
            if (Math.Abs(dx) < DragSlopPx)
            {
                if (Math.Abs(dy) >= DragSlopPx)
                {
                    EndCardDrag(commit: true);
                }

                return;
            }

            dragging = true;

            // Taking the pointer away from the card cancels the press it is holding, so the swipe
            // does not also open whatever the card was showing.
            e.Pointer.Capture(SpotlightViewport);
        }

        UpdateCardSwipe(dx);
        e.PreventGestureRecognition();
        e.Handled = true;
    }

    private void BeginCardSwipe()
    {
        cardGlide.Stop();
        sliding = false;
        dragStartOffset = cardOffset;
        dragging = false;
    }

    private void UpdateCardSwipe(double movement)
    {
        var span = Math.Max(1, SpotlightViewport.Bounds.Width) + CardGap;
        var offset = dragStartOffset + movement;
        while (Math.Abs(offset) > span)
        {
            var step = offset < 0 ? 1 : -1;
            offset += step * span;
            dragStartOffset += step * span;
            pendingStep = 0;
            ApplyCardOffset(offset);
            ViewModel?.StepSpotlight(step);
        }

        ApplyCardOffset(offset);
    }

    private void OnCardPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (dragPointerId == e.Pointer.Id)
        {
            EndCardDrag(commit: true);
        }
    }

    /// <summary>
    /// Settles a swipe: carried far enough across, or thrown fast enough, and the card it was
    /// reaching for slides the rest of the way in; otherwise the one that was there slides back.
    /// </summary>
    private void EndCardDrag(bool commit)
    {
        if (dragPointerId is null)
        {
            return;
        }

        var elapsed = dragClock.Elapsed - dragStartedAt;
        dragPointerId = null;
        EndCardSwipe(commit, elapsed);
    }

    private void EndCardSwipe(bool commit, TimeSpan elapsed)
    {
        var wasDragging = dragging;
        dragging = false;

        if (!wasDragging)
        {
            if (cardOffset != 0 || pendingStep != 0)
                StartCardSlide(commit ? pendingStep : 0);
            return;
        }

        SettleCardSwipe(commit, elapsed, cardOffset - dragStartOffset);
    }

    private void SettleCardSwipe(bool commit, TimeSpan elapsed, double movement)
    {
        var width = Math.Max(1d, SpotlightViewport.Bounds.Width);
        var traveled = Math.Abs(cardOffset);
        var speed = elapsed.TotalSeconds > 0 ? Math.Abs(movement) / elapsed.TotalSeconds : 0d;
        var directed = Math.Abs(movement) >= width * CommitFraction ||
            (speed >= FlickPxPerSecond && Math.Abs(movement) >= DragSlopPx * 2);
        if (directed && Math.Sign(movement) != Math.Sign(cardOffset))
        {
            StartCardSlide(0);
            return;
        }

        var decided = commit && (traveled >= width * CommitFraction || directed);

        StartCardSlide(decided ? cardOffset < 0 ? 1 : -1 : 0);
    }

    private void StartCardSlide(int step)
    {
        pendingStep = step;
        sliding = true;
        cardGlide.Start(cardOffset, -step * (SpotlightViewport.Bounds.Width + CardGap), CardSlideMs);
    }

    private void OnCardSlideLanded()
    {
        sliding = false;
        var step = pendingStep;
        pendingStep = 0;

        // Put the row back before the step so the card that slid into the middle is the one the page
        // then draws there, with no frame in between showing it displaced.
        ApplyCardOffset(0);
        if (step != 0)
        {
            ViewModel?.StepSpotlight(step);
        }
    }

    // ---- Tile rows -----------------------------------------------------------

    private static void RouteRowWheelToPage(ScrollViewer row)
    {
        PointerWheelEventArgs? bypassedWheel = null;
        // Skip the row's presenter, then let the original wheel event reach the page's presenter.
        row.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            bypassedWheel = e;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        row.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (ReferenceEquals(bypassedWheel, e))
            {
                bypassedWheel = null;
                e.Handled = false;
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnRowScrollLeftClick(object? sender, RoutedEventArgs e) => StepRow(sender, -1);

    private void OnRowScrollRightClick(object? sender, RoutedEventArgs e) => StepRow(sender, 1);

    private void StepRow(object? sender, double direction)
    {
        if (sender is not Button { CommandParameter: ScrollViewer row })
        {
            return;
        }

        var step = Math.Max(1d, row.Viewport.Width * RowStepFraction);
        var target = Math.Clamp(row.Offset.X + (step * direction), 0d, row.ScrollBarMaximum.X);

        rowGlide.Stop();
        steppingRow = row;
        rowGlide.Start(row.Offset.X, target, RowStepMs);
    }

    // ---- Scrims --------------------------------------------------------------

    private void ApplyScrim(ISolidColorBrush? pageBackground)
    {
        if (pageBackground is null)
        {
            return;
        }

        Recolor(scrimBrush, ScrimStops, pageBackground.Color);
        Recolor(sideScrimBrush, SideScrimStops, pageBackground.Color);
    }

    private static LinearGradientBrush CreateFade((double Offset, double Alpha)[] stops, bool horizontal)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = horizontal
                ? new RelativePoint(1, 0, RelativeUnit.Relative)
                : new RelativePoint(0, 1, RelativeUnit.Relative),
        };

        foreach (var (offset, _) in stops)
        {
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, offset));
        }

        return brush;
    }

    private static void Recolor(LinearGradientBrush brush, (double Offset, double Alpha)[] stops, Color color)
    {
        for (var i = 0; i < stops.Length; i++)
        {
            // Only the alpha varies across the stops; keeping the RGB constant avoids the muddy
            // interpolation a fade from Colors.Transparent would produce.
            brush.GradientStops[i].Color = Color.FromArgb(
                (byte)Math.Round(stops[i].Alpha * 255),
                color.R,
                color.G,
                color.B);
        }
    }
}
