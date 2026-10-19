using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.ViewModels.Dialogs;

/// <summary>
/// One editable color in the custom theme editor: a role label, a live preview
/// swatch, and a hex text field that validates as the user types.
/// </summary>
public sealed partial class ThemeColorFieldViewModel : ObservableObject
{
    private readonly Action<ThemeColorFieldViewModel> onChanged;

    [ObservableProperty]
    private string hexText;

    [ObservableProperty]
    private bool isValid = true;

    public ThemeColorFieldViewModel(
        AppColorRole role,
        string label,
        Color color,
        Action<ThemeColorFieldViewModel> onChanged)
    {
        Role = role;
        Label = label;
        this.onChanged = onChanged;
        Color = color;
        Preview = new SolidColorBrush(color);
        hexText = ColorTheme.ToHex(color);
    }

    public AppColorRole Role { get; }

    public string Label { get; }

    /// <summary>The last valid color parsed from <see cref="HexText"/>.</summary>
    public Color Color { get; private set; }

    /// <summary>Live preview swatch; its color is mutated in place so bindings refresh.</summary>
    public SolidColorBrush Preview { get; }

    /// <summary>Sets the field's color programmatically (base copy / variant reset).</summary>
    public void ApplyColor(Color color) => HexText = ColorTheme.ToHex(color);

    partial void OnHexTextChanged(string value)
    {
        if (ColorTheme.TryParse(value, out var color))
        {
            IsValid = true;
            Color = color;
            Preview.Color = color;
            onChanged(this);
        }
        else
        {
            IsValid = false;
        }
    }
}
