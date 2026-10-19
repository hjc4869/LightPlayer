using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// A complete palette for one application appearance (light or dark). Built-in
/// themes ship with the app and cannot be edited or deleted; custom themes are
/// authored by the user and persisted to settings.
/// </summary>
public sealed class ColorTheme
{
    private static readonly Color Fallback = Color.FromRgb(0x80, 0x80, 0x80);

    private readonly string name;
    private readonly string? nameKey;

    public ColorTheme(
        string id,
        string name,
        ColorThemeVariant variant,
        bool isBuiltIn,
        IReadOnlyDictionary<AppColorRole, Color> colors,
        string? nameKey = null)
    {
        Id = id;
        this.name = name;
        this.nameKey = nameKey;
        Variant = variant;
        IsBuiltIn = isBuiltIn;
        Colors = colors;
    }

    public string Id { get; }

    /// <summary>
    /// Display name. Built-in themes carry a resource key instead of literal text, so their
    /// name follows the interface language; custom themes keep the name the user typed.
    /// </summary>
    public string Name => nameKey is { } key ? Strings.LocalizedStrings.Instance[key] : name;

    public ColorThemeVariant Variant { get; }

    public bool IsBuiltIn { get; }

    public IReadOnlyDictionary<AppColorRole, Color> Colors { get; }

    /// <summary>Returns the color for a role, or a neutral gray when unset.</summary>
    public Color Get(AppColorRole role) => Colors.TryGetValue(role, out var color) ? color : Fallback;

    /// <summary>Creates a copy with a new identity/name and (optionally) replaced colors.</summary>
    public ColorTheme Clone(
        string id,
        string name,
        ColorThemeVariant variant,
        bool isBuiltIn,
        IReadOnlyDictionary<AppColorRole, Color>? colors = null) =>
        new(id, name, variant, isBuiltIn, colors ?? new Dictionary<AppColorRole, Color>(Colors));

    /// <summary>Serializes the theme to its persisted record form.</summary>
    public CustomColorThemeRecord ToRecord()
    {
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in AppColorRoles.All)
        {
            colors[role.Role.ToString()] = ToHex(Get(role.Role));
        }

        return new CustomColorThemeRecord
        {
            Id = Id,
            Name = Name,
            Variant = Variant.ToString(),
            Colors = colors,
        };
    }

    /// <summary>Rebuilds a custom theme from its persisted record. Missing colors fall back to the base.</summary>
    public static ColorTheme FromRecord(CustomColorThemeRecord record, ColorTheme baseTheme)
    {
        var variant = Enum.TryParse<ColorThemeVariant>(record.Variant, ignoreCase: true, out var parsed)
            ? parsed
            : ColorThemeVariant.Light;

        var colors = new Dictionary<AppColorRole, Color>();
        foreach (var role in AppColorRoles.All)
        {
            if (record.Colors is not null &&
                record.Colors.TryGetValue(role.Role.ToString(), out var hex) &&
                TryParse(hex, out var color))
            {
                colors[role.Role] = color;
            }
            else
            {
                colors[role.Role] = baseTheme.Get(role.Role);
            }
        }

        var name = string.IsNullOrWhiteSpace(record.Name) ? record.Id : record.Name;
        return new ColorTheme(record.Id, name, variant, isBuiltIn: false, colors);
    }

    /// <summary>Formats a color as an <c>#AARRGGBB</c> hex string.</summary>
    public static string ToHex(Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Parses an <c>#RRGGBB</c> or <c>#AARRGGBB</c> hex string into a color.</summary>
    public static bool TryParse(string? hex, out Color color)
    {
        color = Fallback;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var text = hex.Trim();
        if (!text.StartsWith('#'))
        {
            text = "#" + text;
        }

        // Accept #RGB, #RRGGBB and #AARRGGBB via Avalonia's parser, but validate the
        // hex length first so partial user input does not throw.
        var digits = text.Length - 1;
        if (digits is not (3 or 6 or 8))
        {
            return false;
        }

        for (var i = 1; i < text.Length; i++)
        {
            if (!Uri.IsHexDigit(text[i]))
            {
                return false;
            }
        }

        try
        {
            color = Color.Parse(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
