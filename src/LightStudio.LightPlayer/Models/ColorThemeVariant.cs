namespace LightStudio.LightPlayer.Models;

/// <summary>
/// Whether a color theme provides the palette for the light or the dark
/// application appearance. A theme belongs to exactly one variant and its color
/// definitions can never be moved to the other variant.
/// </summary>
public enum ColorThemeVariant
{
    Light,
    Dark,
}
