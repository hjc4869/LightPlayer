using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Controls;

public partial class PageAddressBar : UserControl
{
    private readonly ObservableCollection<PageAddressBarEntry> breadcrumbs = [];
    private PageAddressBarModel? observed;
    private INotifyCollectionChanged? observedBreadcrumb;

    public PageAddressBar()
    {
        InitializeComponent();
        BreadcrumbItems.ItemsSource = breadcrumbs;
        PathBar.SizeChanged += (_, _) => UpdateHeight();
        BreadcrumbViewport.SizeChanged += (_, _) => UpdateBreadcrumbLayout();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        Observe(DataContext as PageAddressBarModel);
        UpdateBreadcrumbs();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        Observe(null);
        ObserveBreadcrumb(null);
        base.OnDetachedFromVisualTree(args);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataContextProperty)
        {
            if (VisualRoot is not null)
            {
                Observe(DataContext as PageAddressBarModel);
            }

            UpdateBreadcrumbs();
            UpdateHeight();
        }
        else if (change.Property == FontFamilyProperty || change.Property == FontWeightProperty ||
                 change.Property == FontStyleProperty)
        {
            UpdateBreadcrumbLayout();
        }
    }

    private void Observe(PageAddressBarModel? model)
    {
        if (observed is not null)
        {
            observed.PropertyChanged -= OnModelChanged;
        }

        observed = model;
        if (observed is not null)
        {
            observed.PropertyChanged += OnModelChanged;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(PageAddressBarModel.Breadcrumb))
        {
            UpdateBreadcrumbs();
        }
    }

    private void UpdateBreadcrumbs()
    {
        var items = (DataContext as PageAddressBarModel)?.Breadcrumb ?? [];
        ObserveBreadcrumb(VisualRoot is null ? null : items as INotifyCollectionChanged);
        // Keep text and button visuals in place while switching their page-owned commands.
        for (int i = 0; i < items.Count; i++)
        {
            if (i < breadcrumbs.Count)
            {
                breadcrumbs[i].Item = items[i];
            }
            else
            {
                breadcrumbs.Add(new PageAddressBarEntry(items[i]));
            }
        }

        while (breadcrumbs.Count > items.Count)
        {
            breadcrumbs.RemoveAt(breadcrumbs.Count - 1);
        }

        UpdateBreadcrumbLayout();
    }

    private void UpdateBreadcrumbLayout()
    {
        if (BreadcrumbViewport is not { Bounds.Width: > 0 } viewport || breadcrumbs.Count == 0)
        {
            if (OverflowMarker is not null)
                OverflowMarker.IsVisible = false;
            return;
        }

        var measure = new TextBlock
        {
            FontFamily = FontFamily,
            FontWeight = FontWeight,
            FontStyle = FontStyle,
            FontSize = 13,
        };
        double MeasureText(string text)
        {
            measure.Text = text;
            measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Ceiling(measure.DesiredSize.Width);
        }

        var entryChrome = MeasureText("\u203a") + 16;
        var markerWidth = MeasureText("...") + 4;
        var minimumCount = Math.Min(3, breadcrumbs.Count);
        var maxNameWidth = Math.Max(0, Math.Min(180, viewport.Bounds.Width / minimumCount - entryChrome));
        var nameWidths = new double[breadcrumbs.Count];
        var totalWidth = 0d;
        for (var index = 0; index < breadcrumbs.Count; index++)
        {
            nameWidths[index] = MeasureText(breadcrumbs[index].Item.Name);
            totalWidth += Math.Min(nameWidths[index], maxNameWidth) + entryChrome;
        }

        var firstVisible = 0;
        if (totalWidth > viewport.Bounds.Width)
        {
            maxNameWidth = Math.Max(0, Math.Min(180,
                (viewport.Bounds.Width - markerWidth) / minimumCount - entryChrome));
            var usedWidth = markerWidth;
            firstVisible = breadcrumbs.Count;
            while (firstVisible > 0)
            {
                var width = Math.Min(nameWidths[firstVisible - 1], maxNameWidth) + entryChrome;
                if (usedWidth + width > viewport.Bounds.Width + 0.01)
                    break;
                usedWidth += width;
                firstVisible--;
            }
        }

        for (var index = 0; index < breadcrumbs.Count; index++)
        {
            breadcrumbs[index].MaxNameWidth = maxNameWidth;
            breadcrumbs[index].IsVisible = index >= firstVisible;
        }
        OverflowMarker.IsVisible = firstVisible > 0;
    }

    private void ObserveBreadcrumb(INotifyCollectionChanged? items)
    {
        if (ReferenceEquals(observedBreadcrumb, items))
        {
            return;
        }

        if (observedBreadcrumb is not null)
        {
            observedBreadcrumb.CollectionChanged -= OnBreadcrumbChanged;
        }

        observedBreadcrumb = items;
        if (observedBreadcrumb is not null)
        {
            observedBreadcrumb.CollectionChanged += OnBreadcrumbChanged;
        }
    }

    private void OnBreadcrumbChanged(object? sender, NotifyCollectionChangedEventArgs args) => UpdateBreadcrumbs();

    private void UpdateHeight()
    {
        if (DataContext is PageAddressBarModel model && PathBar is { Bounds.Height: > 0 } bar)
        {
            model.Height = bar.Bounds.Height;
        }
    }
}

internal sealed class PageAddressBarEntry(PageAddressBarItem item) : ObservableObject
{
    private bool isVisible = true;
    private double maxNameWidth = 180;

    public bool IsVisible
    {
        get => isVisible;
        set => SetProperty(ref isVisible, value);
    }

    public double MaxNameWidth
    {
        get => maxNameWidth;
        set => SetProperty(ref maxNameWidth, value);
    }

    public PageAddressBarItem Item
    {
        get => item;
        set => SetProperty(ref item, value);
    }
}