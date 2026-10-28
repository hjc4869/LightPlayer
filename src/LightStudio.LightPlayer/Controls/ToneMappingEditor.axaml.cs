using Avalonia;
using Avalonia.Controls;

namespace LightStudio.LightPlayer.Controls;

/// <summary>Tone curve selection and parameter sliders for a <see cref="ViewModels.ToneMappingEditorViewModel"/>.</summary>
public partial class ToneMappingEditor : UserControl
{
    public static readonly StyledProperty<double> SelectorWidthProperty =
        AvaloniaProperty.Register<ToneMappingEditor, double>(nameof(SelectorWidth), 150);

    public ToneMappingEditor()
    {
        InitializeComponent();
    }

    /// <summary>The tone curve selector's width, so it can line up with the host's other selectors.</summary>
    public double SelectorWidth
    {
        get => GetValue(SelectorWidthProperty);
        set => SetValue(SelectorWidthProperty, value);
    }
}
