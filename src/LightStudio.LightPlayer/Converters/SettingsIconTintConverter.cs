using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace LightStudio.LightPlayer.Converters;

/// <summary>
/// Maps a settings section's icon key to the fill of the tile its glyph stands on in the touch
/// section list, as the entries of iOS Settings have.
/// </summary>
public sealed class SettingsIconTintConverter : IValueConverter
{
    private static readonly IBrush Neutral = new ImmutableSolidColorBrush(0xFF8E8E93);

    private static readonly Dictionary<string, IBrush> Tints = new(StringComparer.Ordinal)
    {
        ["IconFolderHome"] = new ImmutableSolidColorBrush(0xFF007AFF),
        ["IconLibraryMusic"] = new ImmutableSolidColorBrush(0xFF30B0C7),
        ["IconImage"] = new ImmutableSolidColorBrush(0xFFFF9500),
        ["IconMusicNote"] = new ImmutableSolidColorBrush(0xFFAF52DE),
        ["IconShare"] = new ImmutableSolidColorBrush(0xFF34C759),
        ["IconDisplay"] = new ImmutableSolidColorBrush(0xFF5856D6),
        ["IconVolumeHigh"] = new ImmutableSolidColorBrush(0xFFFF2D55),
        ["IconVideo"] = new ImmutableSolidColorBrush(0xFFFF3B30),
        ["IconPlayCircle"] = new ImmutableSolidColorBrush(0xFFFF9F0A),
        ["IconOpenInNewWindow"] = new ImmutableSolidColorBrush(0xFF32ADE6),
    };

    public static SettingsIconTintConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Tints.TryGetValue(key, out var tint) ? tint : Neutral;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
