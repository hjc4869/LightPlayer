using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// A single selectable color theme in the "Theme color" picker. Exposes the
/// preview brushes for the mock card and swatch row, tracks selection, and routes
/// select/edit/delete back to the owning section view model.
/// </summary>
public sealed class ColorThemeChoiceViewModel : ViewModelBase
{
    private bool isSelected;

    public ColorThemeChoiceViewModel(
        ColorTheme theme,
        bool allowCustomize,
        Action<ColorThemeChoiceViewModel> onSelect,
        Func<ColorThemeChoiceViewModel, Task> onEdit,
        Func<ColorThemeChoiceViewModel, Task> onDelete)
    {
        Theme = theme;
        IsBuiltIn = theme.IsBuiltIn;
        CanCustomize = allowCustomize && !theme.IsBuiltIn;

        AccentBrush = Brush(AppColorRole.Accent);
        PageBackgroundBrush = Brush(AppColorRole.PageBackground);
        PanelBackgroundBrush = Brush(AppColorRole.PanelBackground);
        NavBackgroundBrush = Brush(AppColorRole.NavBackground);
        PrimaryTextBrush = Brush(AppColorRole.PrimaryText);
        SecondaryTextBrush = Brush(AppColorRole.SecondaryText);
        DividerBrush = Brush(AppColorRole.Divider);

        var swatches = new List<IBrush>();
        foreach (var role in AppColorRoles.All)
        {
            if (role.ShowInSwatchRow)
            {
                swatches.Add(Brush(role.Role));
            }
        }

        SwatchBrushes = swatches;

        SelectCommand = new RelayCommand(() => onSelect(this));
        EditCommand = new AsyncRelayCommand(() => onEdit(this), () => CanCustomize);
        DeleteCommand = new AsyncRelayCommand(() => onDelete(this), () => CanCustomize);
    }

    public ColorTheme Theme { get; }

    public string Id => Theme.Id;

    public string Name => Theme.Name;

    public bool IsBuiltIn { get; }

    /// <summary>True when the theme is user-authored and the section allows editing.</summary>
    public bool CanCustomize { get; }

    public IBrush AccentBrush { get; }

    public IBrush PageBackgroundBrush { get; }

    public IBrush PanelBackgroundBrush { get; }

    public IBrush NavBackgroundBrush { get; }

    public IBrush PrimaryTextBrush { get; }

    public IBrush SecondaryTextBrush { get; }

    public IBrush DividerBrush { get; }

    /// <summary>The few colors previewed as dots beneath the mock card.</summary>
    public IReadOnlyList<IBrush> SwatchBrushes { get; }

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    public IRelayCommand SelectCommand { get; }

    public IAsyncRelayCommand EditCommand { get; }

    public IAsyncRelayCommand DeleteCommand { get; }

    private SolidColorBrush Brush(AppColorRole role) => new(Theme.Get(role));
}
