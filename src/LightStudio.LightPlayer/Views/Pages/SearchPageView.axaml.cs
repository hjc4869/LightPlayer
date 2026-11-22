using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Behaviors;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class SearchPageView : UserControl
{
    // DIPs the result kinds scroll sideways for one notch of the mouse wheel.
    private const double ScopeWheelStep = 48;

    // DIPs over which the result kinds fade out toward an edge they continue beyond.
    private const double ScopeFadeWidth = 20;

    // The field width below which the library filter shows its glyph without its label.
    private const double FilterLabelMinimumWidth = 560;

    private SearchPageViewModel? observedPage;
    private (bool Left, bool Right, double Width) scopeFade;
    private bool attached;

    public SearchPageView()
    {
        InitializeComponent();
        BrowseScrollMemory.Track(this, ResultsScrollViewer);
        IncrementalLoadingScroll.Track(this, ResultsScrollViewer);
        ScopeScroller.ScrollChanged += (_, _) => UpdateScopeFade();
        SearchFieldHost.SizeChanged += (_, args) =>
            SourceFilterButton.Classes.Set("compact", args.NewSize.Width < FilterLabelMinimumWidth);
        DataContextChanged += (_, _) =>
        {
            ObservePage();
            UpdateContentWidth();
        };
        AttachedToVisualTree += (_, _) =>
        {
            attached = true;
            ObservePage();

            // A page opened without a query is waiting for one.
            if (observedPage is { Query.Length: 0 })
                FocusQuery();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            attached = false;
            ObservePage();
        };
    }

    private void ObservePage()
    {
        var page = attached ? DataContext as SearchPageViewModel : null;
        if (ReferenceEquals(observedPage, page))
            return;

        if (observedPage is not null)
        {
            observedPage.FocusRequested -= OnFocusRequested;
            observedPage.ScrollResetRequested -= OnScrollResetRequested;
            observedPage.PropertyChanged -= OnPagePropertyChanged;
        }

        observedPage = page;
        if (observedPage is not null)
        {
            observedPage.FocusRequested += OnFocusRequested;
            observedPage.ScrollResetRequested += OnScrollResetRequested;
            observedPage.PropertyChanged += OnPagePropertyChanged;
        }
    }

    private void OnFocusRequested(object? sender, EventArgs args) => FocusQuery();

    private void OnScrollResetRequested(object? sender, EventArgs args) => ResultsScrollViewer.Offset = default;

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SearchPageViewModel.Scope))
            Dispatcher.UIThread.Post(BringSelectedScopeIntoView, DispatcherPriority.Loaded);
    }

    // Focuses the query once the field is laid out, with its text selected for replacement.
    private void FocusQuery() => Dispatcher.UIThread.Post(() =>
    {
        if (!attached)
            return;
        QueryBox.Focus();
        QueryBox.SelectAll();
    }, DispatcherPriority.Loaded);

    private void BringSelectedScopeIntoView()
    {
        if (observedPage is { } page &&
            page.ScopeOptions.FirstOrDefault(option => option.IsSelected) is { } selected)
        {
            ScopeItems.ContainerFromItem(selected)?.BringIntoView();
        }
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs args)
    {
        if (DataContext is not SearchPageViewModel page)
            return;

        if (args.Key == Key.Enter)
        {
            page.SubmitCommand.Execute(null);

            // Touch heads put the on-screen keyboard away so the results take its place.
            if (PlatformServices.UsesTouchChrome || OperatingSystem.IsAndroid())
                TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
            args.Handled = true;
        }
        else if (args.Key == Key.Escape && page.Query.Length > 0)
        {
            page.Query = string.Empty;
            args.Handled = true;
        }
    }

    // A mouse wheel has no sideways axis, so its notches scroll the result kinds sideways.
    private void OnScopeScrollerWheel(object? sender, PointerWheelEventArgs args)
    {
        var overflow = ScopeScroller.Extent.Width - ScopeScroller.Viewport.Width;
        if (overflow <= 0 || args.Delta.Y == 0)
            return;

        var x = Math.Clamp(ScopeScroller.Offset.X - (args.Delta.Y * ScopeWheelStep), 0, overflow);
        ScopeScroller.Offset = new Vector(x, 0);
        args.Handled = true;
    }

    private void UpdateScopeFade()
    {
        var viewport = ScopeScroller.Viewport.Width;
        var overflow = ScopeScroller.Extent.Width - viewport;
        var fade = (
            Left: overflow > 0.5 && ScopeScroller.Offset.X > 0.5,
            Right: overflow > 0.5 && ScopeScroller.Offset.X < overflow - 0.5,
            Width: viewport);
        if (fade == scopeFade)
            return;

        scopeFade = fade;
        if (viewport <= 0 || (!fade.Left && !fade.Right))
        {
            ScopeScroller.OpacityMask = null;
            return;
        }

        var edge = Math.Min(0.5, ScopeFadeWidth / viewport);
        ScopeScroller.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(fade.Left ? Colors.Transparent : Colors.Black, 0),
                new GradientStop(Colors.Black, fade.Left ? edge : 0),
                new GradientStop(Colors.Black, fade.Right ? 1 - edge : 1),
                new GradientStop(fade.Right ? Colors.Transparent : Colors.Black, 1),
            },
        };
    }

    private void OnResultsSizeChanged(object? sender, SizeChangedEventArgs args) => UpdateContentWidth();

    // The tile panels are inset 5 DIPs on either side of the results column.
    private void UpdateContentWidth()
    {
        if (DataContext is SearchPageViewModel page)
            page.SetContentWidth(ResultsContent.Bounds.Width - 10);
    }
}
