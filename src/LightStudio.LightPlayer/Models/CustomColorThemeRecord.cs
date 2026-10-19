using System.Collections.Generic;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// Serializable form of a user-defined color theme persisted in the app settings.
/// Colors are stored keyed by <see cref="AppColorRole"/> name mapped to an
/// <c>#AARRGGBB</c> hex string so the on-disk format stays human readable.
/// </summary>
public sealed class CustomColorThemeRecord
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The theme variant, stored as the <see cref="ColorThemeVariant"/> name.</summary>
    public string Variant { get; set; } = nameof(ColorThemeVariant.Light);

    /// <summary>Role name (<see cref="AppColorRole"/>) to <c>#AARRGGBB</c> hex color.</summary>
    public Dictionary<string, string> Colors { get; set; } = new();
}
