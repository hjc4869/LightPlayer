using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Controls;

public partial class MultiSelectBar : UserControl
{
    private UserControl? page;
    private TopLevel? topLevel;

    public MultiSelectBar() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
        {
            Dispatcher.UIThread.Post(FocusSelection);
        }
    }

    private void FocusSelection()
    {
        if (IsEffectivelyVisible && TopLevel.GetTopLevel(this) is not null &&
            DataContext is MultiSelectionModel { IsActive: true })
        {
            Focus();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        page = this.FindAncestorOfType<UserControl>();
        page?.AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Tunnel);
        topLevel = TopLevel.GetTopLevel(this);
        topLevel?.AddHandler(KeyDownEvent, OnUnfocusedKeyDown, RoutingStrategies.Tunnel);
        Dispatcher.UIThread.Post(FocusSelection);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        page?.RemoveHandler(KeyDownEvent, OnPageKeyDown);
        page = null;
        topLevel?.RemoveHandler(KeyDownEvent, OnUnfocusedKeyDown);
        topLevel = null;
        base.OnDetachedFromVisualTree(args);
    }

    private void OnUnfocusedKeyDown(object? sender, KeyEventArgs args)
    {
        if (IsEffectivelyVisible && topLevel?.FocusManager?.GetFocusedElement() is null)
        {
            OnPageKeyDown(sender, args);
            if (args.Handled)
            {
                FocusSelection();
            }
        }
    }

    private void OnPageKeyDown(object? sender, KeyEventArgs args)
    {
        if (DataContext is MultiSelectionModel selection)
        {
            MultiSelectionMenu.HandleKey(selection, args);
        }
    }

    private void OnMenuClick(object? sender, RoutedEventArgs args)
    {
        if (sender is Control target && DataContext is MultiSelectionModel selection)
        {
            MultiSelectionMenu.Show(selection, target);
        }
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        if (DataContext is MultiSelectionModel selection)
        {
            MultiSelectionMenu.HandleKey(selection, args);
        }

        base.OnKeyDown(args);
    }
}