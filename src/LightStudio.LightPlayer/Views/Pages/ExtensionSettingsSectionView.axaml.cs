using Avalonia.Controls;
using Avalonia.Interactivity;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class ExtensionSettingsSectionView : UserControl
{
    public ExtensionSettingsSectionView()
    {
        InitializeComponent();
    }

    private async void OnAddSourceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExtensionSettingsSectionViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        await viewModel.AddSourceAsync(topLevel?.StorageProvider);
    }

    private void OnRemoveSourceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExtensionSettingsSectionViewModel viewModel ||
            sender is not Button { Tag: LyricSourceModel source })
        {
            return;
        }

        viewModel.RemoveSourceCommand.Execute(source);
    }
}
