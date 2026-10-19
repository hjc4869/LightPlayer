using System;
using Avalonia.Controls;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Views.Dialogs;

/// <summary>
/// Desktop window host for the remote-library add/edit form. The form saves itself, so this window
/// only has to close when the view model says it is done.
/// </summary>
public partial class RemoteLibraryEditorDialog : Window
{
    private RemoteLibraryEditorViewModel? viewModel;

    public RemoteLibraryEditorDialog()
    {
        InitializeComponent();
    }

    public RemoteLibraryEditorDialog(RemoteLibraryEditorViewModel viewModel)
        : this()
    {
        this.viewModel = viewModel;
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
    }

    private void OnRequestClose() => Close();

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
