using Avalonia.Controls;
using Avalonia.Interactivity;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class LibrarySettingsSectionView : UserControl
{
    public LibrarySettingsSectionView()
    {
        InitializeComponent();
    }

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibrarySettingsSectionViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        await viewModel.AddFolderAsync(topLevel?.StorageProvider);
    }

    private void OnRemoveFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibrarySettingsSectionViewModel viewModel ||
            sender is not Button { Tag: LibraryFolderModel folder })
        {
            return;
        }

        viewModel.RemoveFolderCommand.Execute(folder);
    }
}