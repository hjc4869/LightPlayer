using Avalonia.Controls;
using Avalonia.Input;
using LightStudio.LightPlayer.ViewModels.Dialogs;

namespace LightStudio.LightPlayer.Views.Dialogs;

/// <summary>
/// The lyric search/manage content, shared by the desktop <see cref="LyricSearchDialog"/> window
/// and the mobile in-shell overlay. All actions are command-bound to
/// <see cref="LyricSearchDialogViewModel"/>; the host dismisses itself via the view model's
/// <see cref="LyricSearchDialogViewModel.RequestClose"/> event.
/// </summary>
public partial class LyricSearchView : UserControl
{
    public LyricSearchView()
    {
        InitializeComponent();
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Double-tapping a candidate is a shortcut for "Select".
        if (DataContext is LyricSearchDialogViewModel viewModel && viewModel.SelectCommand.CanExecute(null))
        {
            viewModel.SelectCommand.Execute(null);
        }
    }
}
