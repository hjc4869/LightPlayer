using Avalonia;
using Avalonia.Controls.Primitives;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Slider states for the AppKit slider thumb. A slider's control theme can style its thumb but not
/// the thumb's template, so it sets these on the thumb for the thumb's own theme to draw.
/// </summary>
public static class MacSliderThumbState
{
    /// <summary>Whether the thumb is drawn as a clear lens, as while its slider is pressed.</summary>
    public static readonly AttachedProperty<bool> ShowsLensProperty =
        AvaloniaProperty.RegisterAttached<Thumb, bool>("ShowsLens", typeof(MacSliderThumbState));

    /// <summary>Whether the keyboard focus ring is drawn around the thumb.</summary>
    public static readonly AttachedProperty<bool> ShowsFocusRingProperty =
        AvaloniaProperty.RegisterAttached<Thumb, bool>("ShowsFocusRing", typeof(MacSliderThumbState));

    public static bool GetShowsLens(Thumb thumb) => thumb.GetValue(ShowsLensProperty);

    public static void SetShowsLens(Thumb thumb, bool value) => thumb.SetValue(ShowsLensProperty, value);

    public static bool GetShowsFocusRing(Thumb thumb) => thumb.GetValue(ShowsFocusRingProperty);

    public static void SetShowsFocusRing(Thumb thumb, bool value) => thumb.SetValue(ShowsFocusRingProperty, value);
}
