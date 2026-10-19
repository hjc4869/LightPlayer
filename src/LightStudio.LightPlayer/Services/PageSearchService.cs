using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Controls;

namespace LightStudio.LightPlayer.Services;

public sealed class PageSearchService
{
    private static readonly ConditionalWeakTable<TopLevel, PageSearchService> Services = new();
    private readonly TopLevel topLevel;
    private readonly List<PageSearchHost> hosts = [];
    private PageSearchHost? lastScope;
    private PageSearchHost? opened;
    private IInputElement? previousFocus;

    private PageSearchService(TopLevel topLevel)
    {
        this.topLevel = topLevel;
        topLevel.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        topLevel.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
    }

    internal static PageSearchService? Register(PageSearchHost host)
    {
        if (TopLevel.GetTopLevel(host) is not { } topLevel)
        {
            return null;
        }

        var service = Services.GetValue(topLevel, root => new PageSearchService(root));
        service.hosts.Add(host);
        return service;
    }

    internal void Unregister(PageSearchHost host)
    {
        hosts.Remove(host);
        if (opened == host)
        {
            opened = null;
            previousFocus = null;
        }

        if (lastScope == host)
        {
            lastScope = null;
        }

        if (hosts.Count == 0)
        {
            topLevel.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            topLevel.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            Services.Remove(topLevel);
        }
    }

    public static bool Open(Control context)
    {
        if (TopLevel.GetTopLevel(context) is { } topLevel && Services.TryGetValue(topLevel, out var service) &&
            service.FindHost(topLevel.FocusManager?.GetFocusedElement() as Visual) is { } host)
        {
            service.Open(host);
            return true;
        }

        return false;
    }

    private void Open(PageSearchHost host)
    {
        if (opened != host)
        {
            var previous = opened;
            opened = host;
            previous?.Search?.Close();
            previousFocus = topLevel.FocusManager?.GetFocusedElement();
        }

        lastScope = host;
        host.Search?.Open();
        Dispatcher.UIThread.Post(() =>
        {
            if (host.IsAvailable && host.Search?.IsOpen == true)
            {
                host.FocusQuery();
            }
        }, DispatcherPriority.Loaded);
    }

    internal void RestoreFocus(PageSearchHost host)
    {
        if (opened != host)
        {
            return;
        }

        opened = null;
        if (host.IsAvailable && host.IsKeyboardFocusWithin && previousFocus is Control { IsEffectivelyVisible: true } control &&
            TopLevel.GetTopLevel(control) == topLevel)
        {
            control.Focus();
        }

        previousFocus = null;
    }

    private PageSearchHost? FindHost(Visual? origin)
    {
        var focused = origin?.GetVisualAncestors().Prepend(origin).OfType<PageSearchHost>()
            .FirstOrDefault(host => host.IsAvailable);
        return focused ?? (lastScope?.IsAvailable == true ? lastScope : hosts.LastOrDefault(host => host.IsAvailable));
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (args.Source is Visual visual)
        {
            lastScope = FindHost(visual);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Handled)
        {
            return;
        }

        var command = args.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta;
        if (args.Key == Key.F && command && FindHost(args.Source as Visual) is { } host)
        {
            Open(host);
            args.Handled = true;
            return;
        }

        if (opened is not { IsAvailable: true, Search.IsOpen: true } active)
        {
            return;
        }

        var modifiers = args.KeyModifiers & ~KeyModifiers.Shift;
        var repeat = args.Key == Key.F3 && modifiers == KeyModifiers.None ||
                     args.Key == Key.G && modifiers is KeyModifiers.Control or KeyModifiers.Meta;
        if (repeat || active.IsQueryFocused && modifiers == KeyModifiers.None &&
            args.Key is Key.Enter or Key.Up or Key.Down)
        {
            var previous = args.Key == Key.Up || args.KeyModifiers.HasFlag(KeyModifiers.Shift);
            (previous ? active.Search!.PreviousCommand : active.Search!.NextCommand).Execute(null);
            args.Handled = true;
        }
        else if (args.Key == Key.Escape && args.KeyModifiers == KeyModifiers.None)
        {
            active.Search!.Close();
            args.Handled = true;
        }
        else if (active.IsQueryFocused && command && args.Key == Key.A)
        {
            active.FocusQuery();
            args.Handled = true;
        }
    }
}