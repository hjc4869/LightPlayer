using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Controls;

public partial class PageSearchHost : ContentControl
{
    public static readonly StyledProperty<PageSearchModel?> SearchProperty =
        AvaloniaProperty.Register<PageSearchHost, PageSearchModel?>(nameof(Search));

    public static readonly AttachedProperty<object?> TargetProperty =
        AvaloniaProperty.RegisterAttached<PageSearchHost, Control, object?>("Target");

    public static readonly AttachedProperty<bool> IsSearchEnabledProperty =
        AvaloniaProperty.RegisterAttached<PageSearchHost, Control, bool>("IsSearchEnabled", true, inherits: true);

    private PageSearchBar? searchBar;
    private PageSearchHighlightLayer? highlights;
    private PageSearchService? service;
    private PageSearchModel? observedSearch;
    private bool inViewport = true;
    private int navigationVersion;

    public PageSearchHost()
    {
        InitializeComponent();
        LayoutUpdated += OnLayoutUpdated;
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    public PageSearchModel? Search
    {
        get => GetValue(SearchProperty);
        set => SetValue(SearchProperty, value);
    }

    public static object? GetTarget(Control control) => control.GetValue(TargetProperty);

    public static void SetTarget(Control control, object? value) => control.SetValue(TargetProperty, value);

    public static bool GetIsSearchEnabled(Control control) => control.GetValue(IsSearchEnabledProperty);

    public static void SetIsSearchEnabled(Control control, bool value) => control.SetValue(IsSearchEnabledProperty, value);

    internal bool IsAvailable => GetIsSearchEnabled(this) && Search is { IsSupported: true } && IsEffectivelyVisible && inViewport &&
        Bounds.Width > 0 && Bounds.Height > 0 &&
        !this.GetVisualAncestors().OfType<MasterDetailView>().Any(detail =>
            (detail.IsCompact || detail.Detail is null) &&
            !this.GetVisualAncestors().Contains(detail.ActiveContentRoot));

    internal bool IsQueryFocused => searchBar?.IsQueryFocused == true;

    internal void FocusQuery() => searchBar?.FocusQuery();

    protected override void OnApplyTemplate(TemplateAppliedEventArgs args)
    {
        base.OnApplyTemplate(args);
        searchBar = args.NameScope.Find<PageSearchBar>("PART_SearchBar");
        highlights = args.NameScope.Find<PageSearchHighlightLayer>("PART_Highlights");
        if (highlights is not null)
        {
            highlights.Host = this;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        ObserveSearch();
        service = PageSearchService.Register(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        service?.Unregister(this);
        service = null;
        Search?.Close();
        ObserveSearch(detach: true);
        base.OnDetachedFromVisualTree(args);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SearchProperty && service is not null)
        {
            observedSearch?.Close();
            ObserveSearch();
        }
        else if (change.Property == IsVisibleProperty && !IsVisible)
        {
            Search?.Close();
        }
        else if (change.Property == IsSearchEnabledProperty && !GetIsSearchEnabled(this))
        {
            Search?.Close();
        }
    }

    private void ObserveSearch(bool detach = false)
    {
        if (observedSearch is not null)
        {
            observedSearch.ResultsChanged -= OnResultsChanged;
            observedSearch.NavigateRequested -= OnNavigateRequested;
            observedSearch.PropertyChanged -= OnSearchPropertyChanged;
        }

        observedSearch = detach ? null : Search;
        if (observedSearch is not null)
        {
            observedSearch.ResultsChanged += OnResultsChanged;
            observedSearch.NavigateRequested += OnNavigateRequested;
            observedSearch.PropertyChanged += OnSearchPropertyChanged;
        }
    }

    private void OnSearchPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PageSearchModel.IsOpen) && Search?.IsOpen == false)
        {
            navigationVersion++;
            service?.RestoreFocus(this);
        }
    }

    private void OnResultsChanged(object? sender, EventArgs args) => highlights?.InvalidateVisual();

    private void OnLayoutUpdated(object? sender, EventArgs args)
    {
        if (Search?.IsOpen == true)
        {
            if (!IsAvailable)
            {
                Search.Close();
            }

            highlights?.InvalidateVisual();
        }
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs args)
    {
        var visible = args.EffectiveViewport.Intersect(new Rect(Bounds.Size));
        inViewport = visible.Width > 0 && visible.Height > 0;
        if (!IsAvailable)
        {
            Search?.Close();
        }
    }

    private void OnNavigateRequested(PageSearchEntry entry) =>
        _ = AppErrorReporter.GuardAsync(() => RevealAsync(entry));

    private async Task RevealAsync(PageSearchEntry entry)
    {
        var version = ++navigationVersion;
        entry.Prepare?.Invoke();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (version != navigationVersion || Search?.IsOpen != true || !IsAvailable ||
                !ReferenceEquals(Search.CurrentMatch?.Item, entry.Item))
            {
                return;
            }

            var scope = Content as Control;
            foreach (var ancestor in entry.Ancestors ?? [])
            {
                scope = scope is null ? null : RevealItem(scope, ancestor);
            }

            if (scope is not null && RevealItem(scope, entry.Item) is { } target)
            {
                target.BringIntoView();
                highlights?.InvalidateVisual();
                return;
            }
        }
    }

    private static Control? RevealItem(Control scope, object item)
    {
        var controls = scope.GetVisualDescendants().OfType<Control>().Prepend(scope).ToArray();
        if (controls.FirstOrDefault(control => control.IsEffectivelyVisible &&
                ReferenceEquals(GetTarget(control), item)) is { } target)
        {
            target.BringIntoView();
            return target;
        }

        foreach (var owner in controls.OfType<ItemsControl>().Where(control => control.IsEffectivelyVisible))
        {
            var index = owner.Items.IndexOf(item);
            if (index >= 0)
            {
                owner.ScrollIntoView(index);
                owner.UpdateLayout();
                return owner.ContainerFromIndex(index);
            }
        }

        return controls.OfType<ContentControl>().FirstOrDefault(control =>
            control.IsEffectivelyVisible && ReferenceEquals(control.Content, item));
    }

    internal IEnumerable<(Control Control, object Item)> GetMatchControls()
    {
        if (Search is not { IsOpen: true } search || Content is not Control content)
        {
            yield break;
        }

        foreach (var control in content.GetVisualDescendants().OfType<Control>().Prepend(content))
        {
            if (!control.IsEffectivelyVisible)
            {
                continue;
            }

            if (GetTarget(control) is { } target && search.IsMatch(target))
            {
                yield return (control, target);
            }
            else if (control is ItemsControl owner)
            {
                foreach (var container in owner.GetRealizedContainers())
                {
                    if (owner.ItemFromContainer(container) is { } item && search.IsMatch(item))
                    {
                        yield return (container, item);
                    }
                }
            }
        }
    }
}