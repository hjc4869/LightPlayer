using System.Collections.Generic;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// A named color slot in the application palette. Each role maps to one (or two)
/// resource keys in <c>Palette.axaml</c> and is the unit a color theme defines.
/// </summary>
public enum AppColorRole
{
    Accent,
    PageBackground,
    PanelBackground,
    NavBackground,
    PlaybackBarBackground,
    PrimaryText,
    SecondaryText,
    DisabledText,
    Selection,
    Hover,
    ArtOverlay,
    Divider,
    ThumbnailPlaceholder,
    ThumbnailGlyph,
    Favorite,
}

/// <summary>
/// Static metadata for a <see cref="AppColorRole"/>: the dynamic-resource keys it
/// feeds and whether it appears in the compact swatch row of a theme preview.
/// </summary>
/// <param name="Role">The palette role.</param>
/// <param name="BrushKey">The <c>SolidColorBrush</c> resource key updated for this role.</param>
/// <param name="ColorKey">The optional <c>Color</c> resource key updated alongside the brush.</param>
/// <param name="ShowInSwatchRow">True when the role is one of the few colors previewed as a dot.</param>
public sealed record AppColorRoleInfo(
    AppColorRole Role,
    string BrushKey,
    string? ColorKey,
    bool ShowInSwatchRow);

/// <summary>
/// The ordered catalog of palette roles and the resource keys each one drives.
/// Used to build the runtime resource dictionary and to enumerate editable colors.
/// </summary>
public static class AppColorRoles
{
    public static IReadOnlyList<AppColorRoleInfo> All { get; } = new AppColorRoleInfo[]
    {
        new(AppColorRole.Accent, "AppAccentBrush", "AppAccentColor", ShowInSwatchRow: true),
        new(AppColorRole.PageBackground, "AppPageBackgroundBrush", null, ShowInSwatchRow: false),
        new(AppColorRole.PanelBackground, "AppPanelBackgroundBrush", null, ShowInSwatchRow: true),
        new(AppColorRole.NavBackground, "AppNavBackgroundBrush", null, ShowInSwatchRow: false),
        new(AppColorRole.PlaybackBarBackground, "AppPlaybackBarBackgroundBrush", null, ShowInSwatchRow: false),
        new(AppColorRole.PrimaryText, "AppPrimaryTextBrush", null, ShowInSwatchRow: true),
        new(AppColorRole.SecondaryText, "AppSecondaryTextBrush", null, ShowInSwatchRow: false),
        new(AppColorRole.DisabledText, "AppDisabledTextBrush", null, ShowInSwatchRow: false),
        new(AppColorRole.Selection, "AppSelectionBrush", null, ShowInSwatchRow: true),
        new(AppColorRole.Hover, "AppHoverBrush", null, ShowInSwatchRow: false),
        new(AppColorRole.ArtOverlay, "AppArtOverlayBrush", null, ShowInSwatchRow: false),
        new(AppColorRole.Divider, "AppDividerBrush", null, ShowInSwatchRow: false),
        new(AppColorRole.ThumbnailPlaceholder, "AppThumbnailPlaceholderBrush", null, ShowInSwatchRow: false),
        new(AppColorRole.ThumbnailGlyph, "AppThumbnailGlyphBrush", null, ShowInSwatchRow: false),
        new(AppColorRole.Favorite, "AppFavoriteBrush", null, ShowInSwatchRow: true),
    };
}
