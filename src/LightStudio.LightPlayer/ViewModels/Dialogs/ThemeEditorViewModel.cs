using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Dialogs;

/// <summary>
/// View model for the custom color theme editor. Lets the user name a theme,
/// choose its variant (light or dark), optionally base it on an existing theme of
/// the same variant, and edit each palette color with a live preview. A theme's
/// variant is fixed while editing so its colors can never migrate to the other
/// appearance. Overwriting an existing name is confirmed inline before saving.
/// </summary>
public sealed partial class ThemeEditorViewModel : ObservableObject
{
    private readonly IThemeService themeService;
    private readonly string id;
    private readonly bool isNew;
    private readonly Dictionary<AppColorRole, ThemeColorFieldViewModel> fieldsByRole = new();
    private bool applyingColors;
    private bool updatingVariant;
    private string? pendingOverwriteId;

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private bool isLightVariant;

    [ObservableProperty]
    private bool isDarkVariant;

    [ObservableProperty]
    private ColorTheme? selectedBaseTheme;

    [ObservableProperty]
    private string? validationMessage;

    [ObservableProperty]
    private bool isOverwritePromptVisible;

    [ObservableProperty]
    private string overwriteMessage = string.Empty;

    private ThemeEditorViewModel(
        IThemeService themeService,
        string id,
        string name,
        ColorThemeVariant variant,
        bool isNew,
        ColorTheme source)
    {
        this.themeService = themeService;
        this.id = id;
        this.isNew = isNew;
        this.name = name;
        CanChangeVariant = isNew;

        if (variant == ColorThemeVariant.Dark)
        {
            isDarkVariant = true;
        }
        else
        {
            isLightVariant = true;
        }

        PreviewAccentBrush = new SolidColorBrush();
        PreviewPageBackgroundBrush = new SolidColorBrush();
        PreviewPanelBackgroundBrush = new SolidColorBrush();
        PreviewNavBackgroundBrush = new SolidColorBrush();
        PreviewPrimaryTextBrush = new SolidColorBrush();
        PreviewSecondaryTextBrush = new SolidColorBrush();
        PreviewDividerBrush = new SolidColorBrush();

        Fields = new ObservableCollection<ThemeColorFieldViewModel>();
        foreach (var role in AppColorRoles.All)
        {
            var field = new ThemeColorFieldViewModel(role.Role, RoleLabel(role.Role), source.Get(role.Role), OnFieldChanged);
            Fields.Add(field);
            fieldsByRole[role.Role] = field;
        }

        BaseThemeOptions = new ObservableCollection<ColorTheme>();
        RebuildBaseOptions();
        RefreshPreview();

        SaveCommand = new RelayCommand(TrySave);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(null));
        ConfirmOverwriteCommand = new RelayCommand(ConfirmOverwrite);
        CancelOverwriteCommand = new RelayCommand(() => IsOverwritePromptVisible = false);
    }

    /// <summary>
    /// Creates an editor for a brand-new theme seeded from the palette currently in use for the
    /// variant. Starting from what the user is looking at is the only way to customize a theme
    /// whose colors the app owns rather than the user -- the system-accent one above all: pick it,
    /// add a custom theme, and its derived colors are already in the fields ready to be tweaked.
    /// </summary>
    public static ThemeEditorViewModel CreateNew(IThemeService themeService, ColorThemeVariant variant) =>
        new(themeService, $"custom.{Guid.NewGuid():N}", string.Empty, variant, isNew: true, themeService.GetSelectedTheme(variant));

    /// <summary>Creates an editor that edits an existing custom theme in place.</summary>
    public static ThemeEditorViewModel CreateForEdit(IThemeService themeService, ColorTheme theme) =>
        new(themeService, theme.Id, theme.Name, theme.Variant, isNew: false, theme);

    public string Title => isNew ? Strings.Resources.ThemeEditor_TitleNew : Strings.Resources.ThemeEditor_TitleEdit;

    /// <summary>False while editing an existing theme, locking the variant so colors stay in one appearance.</summary>
    public bool CanChangeVariant { get; }

    public ObservableCollection<ThemeColorFieldViewModel> Fields { get; }

    public ObservableCollection<ColorTheme> BaseThemeOptions { get; }

    public SolidColorBrush PreviewAccentBrush { get; }

    public SolidColorBrush PreviewPageBackgroundBrush { get; }

    public SolidColorBrush PreviewPanelBackgroundBrush { get; }

    public SolidColorBrush PreviewNavBackgroundBrush { get; }

    public SolidColorBrush PreviewPrimaryTextBrush { get; }

    public SolidColorBrush PreviewSecondaryTextBrush { get; }

    public SolidColorBrush PreviewDividerBrush { get; }

    public IRelayCommand SaveCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand ConfirmOverwriteCommand { get; }

    public IRelayCommand CancelOverwriteCommand { get; }

    /// <summary>Raised when the editor closes: with the saved theme, or null when canceled.</summary>
    public event Action<ColorTheme?>? RequestClose;

    private ColorThemeVariant Variant => IsDarkVariant ? ColorThemeVariant.Dark : ColorThemeVariant.Light;

    partial void OnIsLightVariantChanged(bool value)
    {
        if (updatingVariant || !value)
        {
            return;
        }

        updatingVariant = true;
        IsDarkVariant = false;
        updatingVariant = false;
        OnVariantChanged();
    }

    partial void OnIsDarkVariantChanged(bool value)
    {
        if (updatingVariant || !value)
        {
            return;
        }

        updatingVariant = true;
        IsLightVariant = false;
        updatingVariant = false;
        OnVariantChanged();
    }

    partial void OnSelectedBaseThemeChanged(ColorTheme? value)
    {
        if (applyingColors || value is null)
        {
            return;
        }

        ApplyColorsFrom(value);
    }

    private void OnVariantChanged()
    {
        // Switching variant reseeds from the palette in use for that appearance and offers only
        // same-variant themes as bases, so colors never cross between appearances.
        RebuildBaseOptions();
        ApplyColorsFrom(themeService.GetSelectedTheme(Variant));
    }

    private void RebuildBaseOptions()
    {
        applyingColors = true;
        BaseThemeOptions.Clear();
        foreach (var theme in themeService.GetThemes(Variant))
        {
            if (isNew || !string.Equals(theme.Id, id, StringComparison.Ordinal))
            {
                BaseThemeOptions.Add(theme);
            }
        }

        // Show the theme the fields were seeded from, so it is clear what is being copied. Only
        // for a new theme: an edit starts from its own colors, which are not on this list.
        SelectedBaseTheme = isNew ? FindOption(themeService.GetSelectedTheme(Variant).Id) : null;
        applyingColors = false;
    }

    private ColorTheme? FindOption(string themeId)
    {
        foreach (var theme in BaseThemeOptions)
        {
            if (string.Equals(theme.Id, themeId, StringComparison.Ordinal))
            {
                return theme;
            }
        }

        return null;
    }

    private void ApplyColorsFrom(ColorTheme theme)
    {
        applyingColors = true;
        foreach (var field in Fields)
        {
            field.ApplyColor(theme.Get(field.Role));
        }

        applyingColors = false;
        RefreshPreview();
    }

    private void OnFieldChanged(ThemeColorFieldViewModel field)
    {
        if (applyingColors)
        {
            return;
        }

        RefreshPreview();
    }

    private void RefreshPreview()
    {
        PreviewAccentBrush.Color = ColorOf(AppColorRole.Accent);
        PreviewPageBackgroundBrush.Color = ColorOf(AppColorRole.PageBackground);
        PreviewPanelBackgroundBrush.Color = ColorOf(AppColorRole.PanelBackground);
        PreviewNavBackgroundBrush.Color = ColorOf(AppColorRole.NavBackground);
        PreviewPrimaryTextBrush.Color = ColorOf(AppColorRole.PrimaryText);
        PreviewSecondaryTextBrush.Color = ColorOf(AppColorRole.SecondaryText);
        PreviewDividerBrush.Color = ColorOf(AppColorRole.Divider);
    }

    private Color ColorOf(AppColorRole role) =>
        fieldsByRole.TryGetValue(role, out var field) ? field.Color : Colors.Gray;

    private void TrySave()
    {
        var trimmed = Name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            ValidationMessage = Strings.Resources.ThemeEditor_NameRequired;
            return;
        }

        foreach (var field in Fields)
        {
            if (!field.IsValid)
            {
                ValidationMessage = Strings.Resources.ThemeEditor_InvalidColor;
                return;
            }
        }

        ValidationMessage = null;

        var conflictId = FindConflictingThemeId(trimmed);
        if (conflictId is not null)
        {
            pendingOverwriteId = conflictId;
            OverwriteMessage = string.Format(Strings.Resources.ThemeEditor_OverwriteMessageFormat, trimmed);
            IsOverwritePromptVisible = true;
            return;
        }

        CommitSave(id);
    }

    private void ConfirmOverwrite()
    {
        IsOverwritePromptVisible = false;
        CommitSave(pendingOverwriteId ?? id);
    }

    private void CommitSave(string finalId)
    {
        var colors = new Dictionary<AppColorRole, Color>();
        foreach (var field in Fields)
        {
            colors[field.Role] = field.Color;
        }

        var theme = new ColorTheme(finalId, Name.Trim(), Variant, isBuiltIn: false, colors);
        RequestClose?.Invoke(theme);
    }

    /// <summary>The id of another custom theme of this variant that already uses the name, if any.</summary>
    private string? FindConflictingThemeId(string trimmedName)
    {
        foreach (var theme in themeService.GetThemes(Variant))
        {
            if (theme.IsBuiltIn)
            {
                continue;
            }

            if (string.Equals(theme.Id, id, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(theme.Name, trimmedName, StringComparison.OrdinalIgnoreCase))
            {
                return theme.Id;
            }
        }

        return null;
    }

    private static string RoleLabel(AppColorRole role) => role switch
    {
        AppColorRole.Accent => Strings.Resources.ThemeColor_Role_Accent,
        AppColorRole.PageBackground => Strings.Resources.ThemeColor_Role_PageBackground,
        AppColorRole.PanelBackground => Strings.Resources.ThemeColor_Role_PanelBackground,
        AppColorRole.NavBackground => Strings.Resources.ThemeColor_Role_NavBackground,
        AppColorRole.PlaybackBarBackground => Strings.Resources.ThemeColor_Role_PlaybackBarBackground,
        AppColorRole.PrimaryText => Strings.Resources.ThemeColor_Role_PrimaryText,
        AppColorRole.SecondaryText => Strings.Resources.ThemeColor_Role_SecondaryText,
        AppColorRole.DisabledText => Strings.Resources.ThemeColor_Role_DisabledText,
        AppColorRole.Selection => Strings.Resources.ThemeColor_Role_Selection,
        AppColorRole.Hover => Strings.Resources.ThemeColor_Role_Hover,
        AppColorRole.ArtOverlay => Strings.Resources.ThemeColor_Role_ArtOverlay,
        AppColorRole.Divider => Strings.Resources.ThemeColor_Role_Divider,
        AppColorRole.ThumbnailPlaceholder => Strings.Resources.ThemeColor_Role_ThumbnailPlaceholder,
        AppColorRole.ThumbnailGlyph => Strings.Resources.ThemeColor_Role_ThumbnailGlyph,
        AppColorRole.Favorite => Strings.Resources.ThemeColor_Role_Favorite,
        _ => role.ToString(),
    };
}
