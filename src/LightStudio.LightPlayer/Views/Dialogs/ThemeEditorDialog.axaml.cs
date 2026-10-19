using System;
using Avalonia.Controls;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Dialogs;

namespace LightStudio.LightPlayer.Views.Dialogs;

/// <summary>
/// Desktop window host for the custom color theme editor. The UI and all behavior
/// live in <see cref="ThemeEditorView"/> / <see cref="ThemeEditorViewModel"/>; this
/// window only closes with the view model's result (the saved theme, or null).
/// </summary>
public partial class ThemeEditorDialog : Window
{
    private ThemeEditorViewModel? viewModel;

    public ThemeEditorDialog()
    {
        InitializeComponent();
    }

    public ThemeEditorDialog(ThemeEditorViewModel viewModel)
        : this()
    {
        this.viewModel = viewModel;
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(ColorTheme? result) => Close(result);

    protected override void OnClosed(EventArgs e)
    {
        if (viewModel is not null)
        {
            viewModel.RequestClose -= OnRequestClose;
            viewModel = null;
        }

        base.OnClosed(e);
    }
}
