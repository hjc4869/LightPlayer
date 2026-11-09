using System;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// The platform's own glass material, which the compositor draws from the window content beneath a
/// native view and the content beside its edges.
/// </summary>
public interface INativeGlass
{
    /// <summary>Creates a retained view that embeds <paramref name="contentView"/> on the material.</summary>
    /// <remarks>A content view already embedded by another view moves here, leaving that view empty.</remarks>
    IntPtr Create(IntPtr contentView, NativeGlassMaterial material, double cornerRadius);

    /// <summary>Sets the corner radius of a <see cref="NativeGlassMaterial.Floating"/> view.</summary>
    void SetCornerRadius(IntPtr view, double radius);

    /// <summary>Detaches the embedded content view and releases a view from <see cref="Create"/>.</summary>
    void Destroy(IntPtr view);

    /// <summary>
    /// Moves the window containing <paramref name="view"/> with the mouse, for the mouse-down event being
    /// handled.
    /// </summary>
    void BeginWindowDrag(IntPtr view);
}

public enum NativeGlassMaterial
{
    /// <summary>The window's sidebar, along its leading edge.</summary>
    Sidebar,

    /// <summary>An inspector along the window's trailing edge.</summary>
    Inspector,

    /// <summary>A pane floating over the window content, with its own edge and shadow.</summary>
    Floating,
}
