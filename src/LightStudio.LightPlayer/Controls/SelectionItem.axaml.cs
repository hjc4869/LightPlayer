using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Controls;

public partial class SelectionItem : ContentControl
{
    public SelectionItem()
    {
        InitializeComponent();
        AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble);
        AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble);
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnSelectionPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnTapped(object? sender, TappedEventArgs args)
    {
        if (DataContext is SelectionItemModel { IsSelectionMode: true } item)
        {
            item.Selection!.Toggle(item);
            args.Handled = true;
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (DataContext is SelectionItemModel { IsSelectionMode: true })
        {
            args.Handled = true;
        }
    }

    private void OnSelectionPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (DataContext is SelectionItemModel { IsSelectionMode: true })
        {
            Focus();
            args.Handled = true;
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if (DataContext is not SelectionItemModel { IsSelectionMode: true } item)
        {
            return;
        }

        var selection = item.Selection!;
        if (!item.IsSelected)
        {
            selection.SelectNone();
            selection.Enter(item);
        }

        MultiSelectionMenu.Show(selection, this, includeSelectionCommands: true);
        args.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        if (DataContext is SelectionItemModel { IsSelectionMode: true } item)
        {
            if (args.Key == Key.Space)
            {
                item.Selection!.Toggle(item);
                args.Handled = true;
            }
            else
            {
                MultiSelectionMenu.HandleKey(item.Selection!, args);
            }
        }

        base.OnKeyDown(args);
    }
}