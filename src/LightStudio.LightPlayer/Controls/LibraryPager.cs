using LightMediaRenderer.Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using LightStudio.LightPlayer.ViewModels;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Narrow (mobile) layout host for the library sections. The music / albums / artists / playlists
/// pages are laid out end to end in one track and the user swipes horizontally between them, the
/// same gesture the now-playing sheet uses for its song / lyrics / up-next pages.
///
/// The track is positioned with a render transform, so a drag follows the finger without
/// re-measuring the pages it carries; on release the swipe either commits to the neighboring page
/// or springs back, and a cubic ease-out glide covers the rest. Which page is showing lives in
/// <see cref="LibraryPagerViewModel.SelectedIndex"/>, so a swipe and a tapped tab drive exactly the
/// same code path.
/// </summary>
public class LibraryPager : Decorator
{
    // Horizontal travel before a press turns into a swipe rather than a tap or a scroll.
    private const double SwipeThresholdPx = 12d;

    // How much more horizontal than vertical the movement must be before the pager claims it, so
    // the lists inside the pages keep their vertical scrolling.
    private const double HorizontalBias = 1.5d;

    // Fraction of a page the swipe must cover before release commits to the neighbor.
    private const double CommitFraction = 0.2d;

    // Resistance applied past the first and last page, instead of exposing empty space.
    private const double EdgeResistance = 0.35d;

    // Page-change glide, matching the now-playing sheet's pager.
    private const double PageDurationMs = 240d;

    private readonly Panel viewport;
    private readonly StackPanel trackPanel;
    private readonly TranslateTransform trackTransform = new();
    private readonly Glide glide;
    private readonly List<ContentPresenter> pagePresenters = new();
    private readonly List<LibraryPagerSlotViewModel> subscribedSlots = new();

    private LibraryPagerViewModel? subscribed;
    private int pageIndex;
    private double offset;
    private bool pressed;
    private bool dragging;
    private double pressX;
    private double pressY;

    public LibraryPager()
    {
        // Every page paints a transparent background so a swipe is picked up over its empty areas
        // too, not only where its content happens to be.
        trackPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            RenderTransform = trackTransform,
        };

        viewport = new Panel { ClipToBounds = true };
        viewport.Children.Add(trackPanel);
        Child = viewport;

        glide = new Glide(this, ApplyOffset);

        // Press is observed in the tunnel pass without handling it, so buttons and list rows in the
        // pages keep working; the gesture only takes over (capturing the pointer, which cancels any
        // armed press) once the finger has moved clearly sideways. Move and release bubble back up,
        // and listen for handled events so a scrolling child cannot swallow the rest of the gesture.
        viewport.AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        viewport.AddHandler(PointerMovedEvent, OnPointerMovedBubble, RoutingStrategies.Bubble, handledEventsToo: true);
        viewport.AddHandler(PointerReleasedEvent, OnPointerReleasedBubble, RoutingStrategies.Bubble, handledEventsToo: true);
        viewport.AddHandler(PointerCaptureLostEvent, OnPointerCaptureLostBubble, handledEventsToo: true);

        viewport.SizeChanged += OnViewportSizeChanged;
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => glide.Stop();
    }

    /// <summary>
    /// Visual of the page currently on screen. The shell's "tap the top bar to scroll to the top"
    /// gesture targets this rather than the whole track, which also holds the neighboring pages.
    /// </summary>
    public Visual? CurrentPageVisual =>
        pageIndex >= 0 && pageIndex < pagePresenters.Count ? pagePresenters[pageIndex] : null;

    internal Visual? PreviewPage(PageViewModelBase page)
    {
        for (int index = 0; index < subscribedSlots.Count; index++)
        {
            if (ReferenceEquals(subscribedSlots[index].Page, page))
            {
                CancelDrag();
                SetPage(index, animate: false);
                return pagePresenters[index];
            }
        }
        return null;
    }

    internal void RestorePreviewPage()
    {
        if (ViewModel is { } model)
            SetPage(model.SelectedIndex, animate: false);
    }

    private LibraryPagerViewModel? ViewModel => DataContext as LibraryPagerViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (subscribed is not null)
        {
            subscribed.PropertyChanged -= OnViewModelPropertyChanged;
        }

        subscribed = ViewModel;
        BuildPages();

        if (subscribed is not null)
        {
            subscribed.PropertyChanged += OnViewModelPropertyChanged;
            SetPage(subscribed.SelectedIndex, animate: false);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (subscribed is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(LibraryPagerViewModel.SelectedIndex):
                // A page picked outside the gesture (a tapped tab, or the shell navigating to a
                // library route): glide there from wherever the track currently is.
                SetPage(subscribed.SelectedIndex, animate: true);
                break;
            case nameof(LibraryPagerViewModel.CanSwipe) when !subscribed.CanSwipe:
                CancelDrag();
                break;
        }
    }

    // One presenter per section, following its slot so a page materialized later simply appears in
    // the position that was already reserved for it.
    //
    // The presenter's Content is driven from the slot's PropertyChanged rather than by binding it.
    // A code-created `new Binding(nameof(...)) { Source = slot }` is a *reflection* binding: it
    // resolves the property by name at runtime, and under NativeAOT the trimmer drops the metadata
    // it needs (nothing references the property reflectively), so the binding silently yields null
    // and every library page renders blank. XAML bindings are unaffected because the project builds
    // them as compiled bindings, so this was the one binding in the app that broke when published.
    private void BuildPages()
    {
        foreach (var slot in subscribedSlots)
        {
            slot.PropertyChanged -= OnSlotPropertyChanged;
        }

        subscribedSlots.Clear();
        trackPanel.Children.Clear();
        pagePresenters.Clear();

        if (subscribed is null)
        {
            return;
        }

        foreach (var slot in subscribed.Slots)
        {
            var presenter = new ContentPresenter
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Background = Brushes.Transparent,
                Content = slot.Page,
            };

            slot.PropertyChanged += OnSlotPropertyChanged;
            subscribedSlots.Add(slot);
            trackPanel.Children.Add(presenter);
            pagePresenters.Add(presenter);
        }

        SyncPageWidths();
    }

    // A slot materialized (or gave up) its page: swap it into the presenter holding that position.
    private void OnSlotPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or nameof(LibraryPagerSlotViewModel.Page))
            || sender is not LibraryPagerSlotViewModel slot)
        {
            return;
        }

        var index = subscribedSlots.IndexOf(slot);
        if (index >= 0 && index < pagePresenters.Count)
        {
            pagePresenters[index].Content = slot.Page;
        }
    }

    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        SyncPageWidths();
        SetPage(pageIndex, animate: false);
    }

    private void SyncPageWidths()
    {
        // Every page carries an explicit width, including before the pager has ever been measured.
        // A horizontal StackPanel measures its children with infinite width, and a library page
        // measured that way lays its virtualizing grid out as one enormous row (tens of thousands
        // of pixels). That bogus arrange is what the page's virtualizing items panel derives its
        // viewport from, so it ends up realizing nothing and the page reads as blank even after a
        // real width arrives. Pages are built as soon as the pager gets its view model -- which
        // happens before the first layout pass when the shell starts on a library route -- so pin
        // them to zero until the viewport reports a size rather than letting that unbounded pass
        // happen.
        var width = Math.Max(0d, viewport.Bounds.Width);

        foreach (var presenter in pagePresenters)
        {
            presenter.Width = width;
        }
    }

    private void SetPage(int index, bool animate)
    {
        pageIndex = Math.Clamp(index, 0, Math.Max(0, pagePresenters.Count - 1));
        var target = -pageIndex * viewport.Bounds.Width;

        // Nothing to animate while the pager is off screen: the shell adopts the section it is
        // navigating to before revealing the pager, so a glide there would play out as the pager
        // fades in, sliding past sections the user never asked for.
        if (!animate || !IsEffectivelyVisible)
        {
            glide.Stop();
            ApplyOffset(target);
            return;
        }

        glide.Start(offset, target, PageDurationMs);
    }

    private void ApplyOffset(double value)
    {
        if (offset.Equals(value))
        {
            return;
        }

        offset = value;
        trackTransform.X = value;

        // Moving the track is a render-level change, but the virtualizing lists inside the pages
        // realize their items from the *effective viewport*, which Avalonia recomputes only at the
        // end of a layout pass -- and which it derives through this very render transform. Without
        // a pass here the page sliding in keeps the (empty) viewport it had while parked off to the
        // side and shows a single item until some unrelated interaction happens to trigger layout.
        // Re-arranging the track is cheap (its children keep their rects and skip re-arranging) and
        // makes the incoming page realize exactly the rows the swipe has actually revealed.
        trackPanel.InvalidateArrange();
    }

    private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        pressed = false;
        dragging = false;

        // Swiping is suppressed while the visible page has drilled into its own detail on a
        // single-pane master-detail layout: that surface owns the width, and the sections it would
        // swipe between are not what the user is looking at.
        if (subscribed is not { CanSwipe: true } || e.Pointer.Type != PointerType.Touch)
        {
            return;
        }

        var point = e.GetPosition(viewport);
        pressed = true;
        pressX = point.X;
        pressY = point.Y;
    }

    private void OnPointerMovedBubble(object? sender, PointerEventArgs e)
    {
        if (!pressed)
        {
            return;
        }

        var point = e.GetPosition(viewport);
        var deltaX = point.X - pressX;
        var deltaY = point.Y - pressY;

        if (!dragging)
        {
            if (Math.Abs(deltaX) < SwipeThresholdPx || Math.Abs(deltaX) < Math.Abs(deltaY) * HorizontalBias)
            {
                return;
            }

            dragging = true;
            glide.Stop();
            e.Pointer.Capture(viewport);
        }

        var width = viewport.Bounds.Width;
        var rest = -pageIndex * width;

        if ((pageIndex == 0 && deltaX > 0) || (pageIndex == pagePresenters.Count - 1 && deltaX < 0))
        {
            deltaX *= EdgeResistance;
        }

        ApplyOffset(rest + deltaX);
        e.Handled = true;
    }

    private void OnPointerReleasedBubble(object? sender, PointerReleasedEventArgs e)
    {
        if (!dragging)
        {
            pressed = false;
            return;
        }

        pressed = false;
        dragging = false;

        var width = viewport.Bounds.Width;
        var traveled = offset + (pageIndex * width);
        var next = pageIndex;
        if (width > 0d && Math.Abs(traveled) > width * CommitFraction)
        {
            next = traveled < 0 ? pageIndex + 1 : pageIndex - 1;
        }

        next = Math.Clamp(next, 0, Math.Max(0, pagePresenters.Count - 1));
        if (subscribed is { } viewModel && viewModel.SelectedIndex != next)
        {
            // Raises SelectedIndex, whose handler glides the track and lets the shell adopt the page.
            viewModel.SelectedIndex = next;
        }
        else
        {
            SetPage(next, animate: true);
        }

        e.Handled = true;
    }

    private void OnPointerCaptureLostBubble(object? sender, PointerCaptureLostEventArgs e) => CancelDrag();

    private void CancelDrag()
    {
        if (!pressed && !dragging)
        {
            return;
        }

        pressed = false;
        dragging = false;
        SetPage(pageIndex, animate: true);
    }
}
