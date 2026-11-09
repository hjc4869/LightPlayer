using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>
/// AppKit's Liquid Glass, which composites the window content beneath and beside a view, including the
/// light that nearby content spills onto it.
/// </summary>
/// <remarks>
/// Sidebars and inspectors take the glass AppKit gives split view items of those kinds, from a split view
/// controller whose other item stays empty, so they match the system's own sidebars and inspectors.
/// Floating panes use <c>NSGlassEffectView</c>. Each view is a plain container that the materials fill.
/// </remarks>
[SupportedOSPlatform("macos26.0")]
internal sealed class AppleGlassEffect : INativeGlass
{
    private const nint LeftMouseDownEvent = 1;
    private const nint WidthAndHeightSizable = 2 | 16;

    private readonly Dictionary<IntPtr, Pane> panes = [];

    public IntPtr Create(IntPtr contentView, NativeGlassMaterial material, double cornerRadius)
    {
        foreach (var other in panes.Values)
        {
            if (other.Content == contentView)
                Empty(other);
        }

        var container = New("NSView");
        var pane = new Pane { Content = contentView };
        IntPtr fill;
        if (material == NativeGlassMaterial.Floating)
        {
            pane.Glass = New("NSGlassEffectView");
            ObjC.Send(pane.Glass, ObjC.Sel("setContentView:"), contentView);
            ObjC.Send(pane.Glass, ObjC.Sel("setCornerRadius:"), cornerRadius);
            fill = pane.Glass;
        }
        else
        {
            pane.Split = CreateSplit(contentView, material);
            fill = ObjC.Send(pane.Split, ObjC.Sel("view"));
        }

        // The container starts empty and sized zero, so the fill follows it as the host sizes it.
        ObjC.Send(fill, ObjC.Sel("setFrameSize:"), 0d, 0d);
        ObjC.Send(fill, ObjC.Sel("setAutoresizingMask:"), WidthAndHeightSizable);
        ObjC.Send(container, ObjC.Sel("addSubview:"), fill);
        panes[container] = pane;
        return container;
    }

    public void SetCornerRadius(IntPtr view, double radius)
    {
        if (panes.TryGetValue(view, out var pane) && pane.Glass != IntPtr.Zero)
            ObjC.Send(pane.Glass, ObjC.Sel("setCornerRadius:"), radius);
    }

    public void Destroy(IntPtr view)
    {
        if (!panes.Remove(view, out var pane))
            return;

        Empty(pane);
        ObjC.Send(view, ObjC.Sel("removeFromSuperview"));
        ObjC.Send(view, ObjC.Sel("release"));
    }

    public void BeginWindowDrag(IntPtr view)
    {
        var window = ObjC.Send(view, ObjC.Sel("window"));
        var application = ObjC.Send(ObjC.GetClass("NSApplication"), ObjC.Sel("sharedApplication"));
        var mouseDown = ObjC.Send(application, ObjC.Sel("currentEvent"));
        if (window == IntPtr.Zero || mouseDown == IntPtr.Zero || ObjC.Send(mouseDown, ObjC.Sel("type")) != LeftMouseDownEvent)
            return;
        ObjC.Send(window, ObjC.Sel("performWindowDragWithEvent:"), mouseDown);
    }

    private static IntPtr CreateSplit(IntPtr contentView, NativeGlassMaterial material)
    {
        var split = New("NSSplitViewController");
        var panelController = New("NSViewController");
        ObjC.Send(panelController, ObjC.Sel("setView:"), contentView);
        var emptyController = New("NSViewController");
        var empty = New("NSView");
        ObjC.Send(emptyController, ObjC.Sel("setView:"), empty);
        ObjC.Send(empty, ObjC.Sel("release"));

        var itemClass = ObjC.GetClass("NSSplitViewItem");
        var panel = ObjC.Send(itemClass, ObjC.Sel(material == NativeGlassMaterial.Sidebar
            ? "sidebarWithViewController:"
            : "inspectorWithViewController:"), panelController);
        ObjC.Send(panel, ObjC.Sel("setCanCollapse:"), (byte)0);
        ObjC.Send(panel, ObjC.Sel("setMinimumThickness:"), 0d);

        // The empty item takes no width, leaving the whole view to the panel item.
        var rest = ObjC.Send(itemClass, ObjC.Sel("splitViewItemWithViewController:"), emptyController);
        ObjC.Send(rest, ObjC.Sel("setMinimumThickness:"), 0d);
        ObjC.Send(rest, ObjC.Sel("setMaximumThickness:"), 0d);

        var add = ObjC.Sel("addSplitViewItem:");
        ObjC.Send(split, add, material == NativeGlassMaterial.Sidebar ? panel : rest);
        ObjC.Send(split, add, material == NativeGlassMaterial.Sidebar ? rest : panel);
        ObjC.Send(panelController, ObjC.Sel("release"));
        ObjC.Send(emptyController, ObjC.Sel("release"));
        return split;
    }

    // Takes the content view out and releases the materials, keeping the container.
    private static void Empty(Pane pane)
    {
        if (pane.Content != IntPtr.Zero)
            ObjC.Send(pane.Content, ObjC.Sel("removeFromSuperview"));
        pane.Content = IntPtr.Zero;

        if (pane.Glass != IntPtr.Zero)
        {
            ObjC.Send(pane.Glass, ObjC.Sel("setContentView:"), IntPtr.Zero);
            ObjC.Send(pane.Glass, ObjC.Sel("removeFromSuperview"));
            ObjC.Send(pane.Glass, ObjC.Sel("release"));
            pane.Glass = IntPtr.Zero;
        }

        if (pane.Split != IntPtr.Zero)
        {
            ObjC.Send(ObjC.Send(pane.Split, ObjC.Sel("view")), ObjC.Sel("removeFromSuperview"));
            ObjC.Send(pane.Split, ObjC.Sel("release"));
            pane.Split = IntPtr.Zero;
        }
    }

    private static IntPtr New(string className) =>
        ObjC.Send(ObjC.Send(ObjC.GetClass(className), ObjC.Sel("alloc")), ObjC.Sel("init"));

    private sealed class Pane
    {
        public IntPtr Content;
        public IntPtr Glass;
        public IntPtr Split;
    }
}
