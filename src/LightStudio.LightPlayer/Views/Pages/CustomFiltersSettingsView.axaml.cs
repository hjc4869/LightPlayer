using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class CustomFiltersSettingsView : UserControl
{
    public CustomFiltersSettingsView()
    {
        InitializeComponent();
    }

    private async void OnImportShaderPackageClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is VideoSettingsSectionViewModel viewModel)
            await viewModel.ImportShaderPackageAsync(TopLevel.GetTopLevel(this)?.StorageProvider);
    }

    private async void OnFindShaderPackagesClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri("https://github.com/hjc4869/LightPlayer-Shaders"));
    }
}
