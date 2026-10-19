using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Controls;

/// <summary>Reserves non-scrolling page space and positions the shell address bar above it.</summary>
public sealed class PageAddressBarSlot : Control
{
    public static readonly StyledProperty<PageAddressBarModel?> AddressBarProperty =
        AvaloniaProperty.Register<PageAddressBarSlot, PageAddressBarModel?>(nameof(AddressBar));

    private PageAddressBarModel? observed;

    public PageAddressBarSlot() => IsHitTestVisible = false;

    public PageAddressBarModel? AddressBar
    {
        get => GetValue(AddressBarProperty);
        set => SetValue(AddressBarProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        Observe(AddressBar);
        LayoutUpdated += OnLayoutUpdated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        LayoutUpdated -= OnLayoutUpdated;
        Observe(null);
        base.OnDetachedFromVisualTree(args);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AddressBarProperty)
        {
            if (VisualRoot is not null)
            {
                Observe(AddressBar);
            }

            UpdateReservation();
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

        UpdateReservation();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName is
            nameof(PageAddressBarModel.Height) or nameof(PageAddressBarModel.Margin) or nameof(PageAddressBarModel.IsVisible))
        {
            UpdateReservation();
        }
    }

    private void UpdateReservation()
    {
        var model = AddressBar is { IsVisible: true } visible ? visible : null;
        Height = model?.Height ?? 0;
        Margin = model?.Margin ?? default;
        IsVisible = model is not null;
    }

    private void OnLayoutUpdated(object? sender, EventArgs args)
    {
        if (!IsVisible || AddressBar is not { } model)
        {
            return;
        }

        // Accumulate layout coordinates relative to the owning page.
        double top = Bounds.Top - Margin.Top;
        for (var parent = this.GetVisualParent(); parent is not null; parent = parent.GetVisualParent())
        {
            if (parent is UserControl { DataContext: IPageAddressBarSource source } &&
                ReferenceEquals(source.AddressBar, model))
            {
                model.TopOffset = top;
                return;
            }

            top += parent.Bounds.Top;
        }
    }
}