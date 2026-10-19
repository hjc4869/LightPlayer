using System;
using System.Collections.Generic;
using Avalonia.Media;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// Predefined color themes, each with light and dark variants built from shared neutral
/// surfaces and family-specific accent colors.
/// </summary>
public static class BuiltInColorThemes
{
    /// <summary>Stable id of the light theme selected by default.</summary>
    public const string DefaultLightId = "builtin.ocean.light";

    /// <summary>Stable id of the dark theme selected by default.</summary>
    public const string DefaultDarkId = "builtin.ocean.dark";

    private static readonly Lazy<IReadOnlyList<ColorTheme>> LazyAll = new(Build);

    /// <summary>All built-in themes, ordered by family then by variant.</summary>
    public static IReadOnlyList<ColorTheme> All => LazyAll.Value;

    /// <summary>The default built-in theme for a variant.</summary>
    public static ColorTheme Default(ColorThemeVariant variant) =>
        Find(variant == ColorThemeVariant.Dark ? DefaultDarkId : DefaultLightId)
        ?? throw new InvalidOperationException("Built-in default theme is missing.");

    /// <summary>Finds a built-in theme by id, or null when none matches.</summary>
    public static ColorTheme? Find(string id)
    {
        foreach (var theme in All)
        {
            if (string.Equals(theme.Id, id, StringComparison.Ordinal))
            {
                return theme;
            }
        }

        return null;
    }

    /// <summary>The favorite-glyph color every shipped theme of a variant uses.</summary>
    public static Color Favorite(ColorThemeVariant variant) =>
        C(variant == ColorThemeVariant.Dark ? 0xE2566Au : 0xC72335u);

    private static IReadOnlyList<ColorTheme> Build()
    {
        var themes = new List<ColorTheme>();
        foreach (var family in Families)
        {
            themes.Add(BuildTheme(family, ColorThemeVariant.Light));
            themes.Add(BuildTheme(family, ColorThemeVariant.Dark));
        }

        return themes;
    }

    private static ColorTheme BuildTheme(Family family, ColorThemeVariant variant)
    {
        var isDark = variant == ColorThemeVariant.Dark;
        var seed = isDark ? family.Dark : family.Light;
        var colors = BuildPalette(C(seed.Accent), C(seed.Selection), C(seed.Favorite), variant);
        var id = $"builtin.{family.Key}.{(isDark ? "dark" : "light")}";
        return new ColorTheme(id, string.Empty, variant, isBuiltIn: true, colors, nameKey: family.NameKey);
    }

    /// <summary>
    /// Builds a full palette from the three colors that distinguish one theme from another,
    /// laying them over the variant's shared neutral surfaces and text. Shared with the
    /// system-accent theme so a derived palette sits on exactly the same base as the shipped
    /// families and only its accent-driven colors differ.
    /// </summary>
    /// <param name="accent">The theme's accent, already adjusted for the variant's contrast.</param>
    /// <param name="selection">Fill behind selected rows and tiles.</param>
    /// <param name="favorite">Color of the favorite (heart) glyph.</param>
    /// <param name="variant">Which appearance the palette is for.</param>
    public static IReadOnlyDictionary<AppColorRole, Color> BuildPalette(
        Color accent,
        Color selection,
        Color favorite,
        ColorThemeVariant variant)
    {
        var isDark = variant == ColorThemeVariant.Dark;
        var neutral = isDark ? DarkNeutral : LightNeutral;

        var colors = new Dictionary<AppColorRole, Color>
        {
            [AppColorRole.Accent] = accent,
            [AppColorRole.Selection] = selection,
            [AppColorRole.Favorite] = favorite,
        };

        foreach (var pair in neutral)
        {
            colors[pair.Key] = C(pair.Value);
        }

        // The album-art scrim is intentionally semi-transparent; keep its alpha channel.
        colors[AppColorRole.ArtOverlay] = isDark
            ? Color.FromArgb(0x7F, 0x3F, 0x3E, 0x3E)
            : Color.FromArgb(0x7F, 0xC1, 0xC2, 0xC2);

        return colors;
    }

    private static Color C(uint rgb) => Color.FromRgb(
        (byte)((rgb >> 16) & 0xFF),
        (byte)((rgb >> 8) & 0xFF),
        (byte)(rgb & 0xFF));

    private sealed record Seed(uint Accent, uint Selection, uint Favorite);

    private sealed record Family(string Key, string NameKey, Seed Light, Seed Dark);

    private static readonly IReadOnlyList<Family> Families = new Family[]
    {
        new("ocean", nameof(Strings.Resources.ThemeColor_Ocean),
            Light: new Seed(0x2C6E9B, 0xD2E4F0, 0xC72335),
            Dark: new Seed(0x6FB3D9, 0x234559, 0xE2566A)),
        new("forest", nameof(Strings.Resources.ThemeColor_Forest),
            Light: new Seed(0x3C6E57, 0xD7E7DC, 0xC72335),
            Dark: new Seed(0x7FC6A6, 0x2C4A3B, 0xE2566A)),
        new("violet", nameof(Strings.Resources.ThemeColor_Violet),
            Light: new Seed(0x6C4CA6, 0xE4DAF2, 0xC72335),
            Dark: new Seed(0xB69CE6, 0x3A2F55, 0xE2566A)),
        new("sunset", nameof(Strings.Resources.ThemeColor_Sunset),
            Light: new Seed(0xC15A6B, 0xF5DDE1, 0xC72335),
            Dark: new Seed(0xE890A0, 0x4E3038, 0xE2566A)),
        new("amber", nameof(Strings.Resources.ThemeColor_Amber),
            Light: new Seed(0xB07D1C, 0xF1E7C9, 0xC72335),
            Dark: new Seed(0xE0B85C, 0x4A3E22, 0xE2566A)),
    };

    private static readonly IReadOnlyDictionary<AppColorRole, uint> LightNeutral = new Dictionary<AppColorRole, uint>
    {
        [AppColorRole.PageBackground] = 0xF6F6F6,
        [AppColorRole.PanelBackground] = 0xFFFFFF,
        [AppColorRole.NavBackground] = 0xEFEFEF,
        [AppColorRole.PlaybackBarBackground] = 0xFBFBFB,
        [AppColorRole.PrimaryText] = 0x1A1A1A,
        [AppColorRole.SecondaryText] = 0x5E5E5E,
        [AppColorRole.DisabledText] = 0xA7A7A7,
        [AppColorRole.Hover] = 0xE6E6E6,
        [AppColorRole.ArtOverlay] = 0xC1C1C1,
        [AppColorRole.Divider] = 0xE2E2E2,
        [AppColorRole.ThumbnailPlaceholder] = 0xE6E6E6,
        [AppColorRole.ThumbnailGlyph] = 0x8B8B8B,
    };

    private static readonly IReadOnlyDictionary<AppColorRole, uint> DarkNeutral = new Dictionary<AppColorRole, uint>
    {
        [AppColorRole.PageBackground] = 0x161616,
        [AppColorRole.PanelBackground] = 0x1E1E1E,
        [AppColorRole.NavBackground] = 0x202020,
        [AppColorRole.PlaybackBarBackground] = 0x1B1B1B,
        [AppColorRole.PrimaryText] = 0xEFEFEF,
        [AppColorRole.SecondaryText] = 0xA4A4A4,
        [AppColorRole.DisabledText] = 0x6A6A6A,
        [AppColorRole.Hover] = 0x2A2A2A,
        [AppColorRole.ArtOverlay] = 0x3E3E3E,
        [AppColorRole.Divider] = 0x323232,
        [AppColorRole.ThumbnailPlaceholder] = 0x3A3A3A,
        [AppColorRole.ThumbnailGlyph] = 0x7C7C7C,
    };
}
