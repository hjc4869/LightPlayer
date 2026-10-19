using Avalonia.Controls;

namespace LightStudio.LightPlayer.Views.Dialogs;

/// <summary>
/// The custom color theme editor UI. Hosted in a window on desktop and as an
/// in-shell overlay on single-view platforms; all behavior lives in
/// <see cref="ViewModels.Dialogs.ThemeEditorViewModel"/>.
/// </summary>
public partial class ThemeEditorView : UserControl
{
    public ThemeEditorView()
    {
        InitializeComponent();
    }
}
