using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.ViewModels.Dialogs;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// One appearance mode (system / light / dark) shown as a radio option with a
/// small light and/or dark preview mock.
/// </summary>
public sealed partial class ThemeModeOption : ViewModelBase
{
    private readonly Action<ThemeModeOption> onSelected;
    private readonly string titleKey;
    private readonly string descriptionKey;

    [ObservableProperty]
    private bool isSelected;

    /// <summary>
    /// Takes resource keys (use <c>nameof(Resources.Key)</c>) so the option labels follow
    /// the interface language.
    /// </summary>
    public ThemeModeOption(
        string titleKey,
        string descriptionKey,
        AppThemePreference preference,
        bool showLightPreview,
        bool showDarkPreview,
        Action<ThemeModeOption> onSelected)
    {
        this.titleKey = titleKey;
        this.descriptionKey = descriptionKey;
        Preference = preference;
        ShowLightPreview = showLightPreview;
        ShowDarkPreview = showDarkPreview;
        this.onSelected = onSelected;
    }

    public string Title => Localized(titleKey);

    public string Description => Localized(descriptionKey);

    public AppThemePreference Preference { get; }

    public bool ShowLightPreview { get; }

    public bool ShowDarkPreview { get; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            onSelected(this);
        }
    }
}

/// <summary>
/// Drives the Appearance &gt; Theme section: the light/dark/system mode radios and
/// the light and dark color-theme pickers. In system mode both pickers are shown so
/// the user can choose one theme per appearance.
/// </summary>
public partial class ThemeSettingsSectionViewModel : SettingsSectionViewModel
{
    private readonly IThemeService themeService;
    private readonly IDialogService? dialogService;
    private readonly bool allowCustomThemes;
    private bool applyingMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVariantLabels))]
    private bool showLightThemes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVariantLabels))]
    private bool showDarkThemes;

    public ThemeSettingsSectionViewModel(
        IThemeService themeService,
        IDialogService? dialogService,
        bool allowCustomThemes)
        : base(nameof(Strings.Resources.ThemeSettings_Title), nameof(Strings.Resources.ThemeSettings_Subtitle))
    {
        this.themeService = themeService;
        this.dialogService = dialogService;
        this.allowCustomThemes = allowCustomThemes && dialogService is not null;

        ModeOptions =
        [
            new ThemeModeOption(
                nameof(Strings.Resources.Common_System),
                nameof(Strings.Resources.ThemeSettings_ModeSystemDescription),
                AppThemePreference.Default,
                showLightPreview: true,
                showDarkPreview: false,
                OnModeSelected),
            new ThemeModeOption(
                nameof(Strings.Resources.Common_ThemeLight),
                nameof(Strings.Resources.ThemeSettings_ModeLightDescription),
                AppThemePreference.Light,
                showLightPreview: true,
                showDarkPreview: false,
                OnModeSelected),
            new ThemeModeOption(
                nameof(Strings.Resources.Common_ThemeDark),
                nameof(Strings.Resources.ThemeSettings_ModeDarkDescription),
                AppThemePreference.Dark,
                showLightPreview: false,
                showDarkPreview: true,
                OnModeSelected),
        ];

        LightThemes = new ObservableCollection<ColorThemeChoiceViewModel>();
        DarkThemes = new ObservableCollection<ColorThemeChoiceViewModel>();

        AddLightThemeCommand = new AsyncRelayCommand(() => AddThemeAsync(ColorThemeVariant.Light));
        AddDarkThemeCommand = new AsyncRelayCommand(() => AddThemeAsync(ColorThemeVariant.Dark));

        RebuildThemes();
        FindMode(themeService.Mode).IsSelected = true;
    }

    public IReadOnlyList<ThemeModeOption> ModeOptions { get; }

    public ObservableCollection<ColorThemeChoiceViewModel> LightThemes { get; }

    public ObservableCollection<ColorThemeChoiceViewModel> DarkThemes { get; }

    /// <summary>True when the section may add/edit/delete custom themes (false during first-run setup).</summary>
    public bool AllowCustomThemes => allowCustomThemes;

    /// <summary>
    /// True where the window manager can paint a backdrop behind the app (Windows 11 Mica). The
    /// switch below stays hidden everywhere else, where it would change nothing.
    /// </summary>
    public bool IsMicaSupported => themeService.IsMicaSupported;

    /// <summary>Whether the shell blends into the system backdrop instead of painting its own page color.</summary>
    public bool MicaEnabled
    {
        get => themeService.IsMicaEnabled;
        set
        {
            if (themeService.IsMicaEnabled == value)
            {
                return;
            }

            themeService.IsMicaEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the app uses its native Apple UI style instead of the classic one.</summary>
    public bool AppleChromeEnabled
    {
        get => themeService.IsAppleChromeEnabled;
        set
        {
            if (themeService.IsAppleChromeEnabled == value)
            {
                return;
            }

            themeService.IsAppleChromeEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>True when both pickers are visible (system mode), so each needs a heading.</summary>
    public bool ShowVariantLabels => ShowLightThemes && ShowDarkThemes;

    public IAsyncRelayCommand AddLightThemeCommand { get; }

    public IAsyncRelayCommand AddDarkThemeCommand { get; }

    private void OnModeSelected(ThemeModeOption option)
    {
        if (applyingMode)
        {
            return;
        }

        applyingMode = true;
        foreach (var other in ModeOptions)
        {
            other.IsSelected = ReferenceEquals(other, option);
        }

        applyingMode = false;

        themeService.SetMode(option.Preference);
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var preference = SelectedPreference();
        ShowLightThemes = preference != AppThemePreference.Dark;
        ShowDarkThemes = preference != AppThemePreference.Light;
    }

    private AppThemePreference SelectedPreference()
    {
        foreach (var option in ModeOptions)
        {
            if (option.IsSelected)
            {
                return option.Preference;
            }
        }

        return AppThemePreference.Default;
    }

    private void RebuildThemes()
    {
        Fill(LightThemes, ColorThemeVariant.Light);
        Fill(DarkThemes, ColorThemeVariant.Dark);
    }

    private void Fill(ObservableCollection<ColorThemeChoiceViewModel> target, ColorThemeVariant variant)
    {
        target.Clear();
        var selectedId = themeService.GetSelectedTheme(variant).Id;
        foreach (var theme in themeService.GetThemes(variant))
        {
            target.Add(new ColorThemeChoiceViewModel(
                theme,
                allowCustomThemes,
                OnSelectChoice,
                EditThemeAsync,
                DeleteThemeAsync)
            {
                IsSelected = string.Equals(theme.Id, selectedId, StringComparison.Ordinal),
            });
        }
    }

    private void OnSelectChoice(ColorThemeChoiceViewModel choice)
    {
        themeService.SelectTheme(choice.Theme);
        var collection = choice.Theme.Variant == ColorThemeVariant.Dark ? DarkThemes : LightThemes;
        foreach (var item in collection)
        {
            item.IsSelected = ReferenceEquals(item, choice);
        }
    }

    private async Task AddThemeAsync(ColorThemeVariant variant)
    {
        if (dialogService is null)
        {
            return;
        }

        var editor = ThemeEditorViewModel.CreateNew(themeService, variant);
        var result = await dialogService.ShowThemeEditorAsync(editor);
        if (result is null)
        {
            return;
        }

        themeService.SaveCustomTheme(result);
        themeService.SelectTheme(result);
        RebuildThemes();
    }

    private async Task EditThemeAsync(ColorThemeChoiceViewModel choice)
    {
        if (dialogService is null || choice.IsBuiltIn)
        {
            return;
        }

        var editor = ThemeEditorViewModel.CreateForEdit(themeService, choice.Theme);
        var result = await dialogService.ShowThemeEditorAsync(editor);
        if (result is null)
        {
            return;
        }

        themeService.SaveCustomTheme(result);
        RebuildThemes();
    }

    private async Task DeleteThemeAsync(ColorThemeChoiceViewModel choice)
    {
        if (dialogService is null || choice.IsBuiltIn)
        {
            return;
        }

        var confirmed = await dialogService.ConfirmAsync(
            Strings.Resources.ThemeColor_DeleteTitle,
            string.Format(Strings.Resources.ThemeColor_DeleteMessageFormat, choice.Name),
            Strings.Resources.Common_Delete);
        if (!confirmed)
        {
            return;
        }

        themeService.DeleteCustomTheme(choice.Id);
        RebuildThemes();
    }

    private ThemeModeOption FindMode(AppThemePreference preference)
    {
        foreach (var option in ModeOptions)
        {
            if (option.Preference == preference)
            {
                return option;
            }
        }

        return ModeOptions[0];
    }
}