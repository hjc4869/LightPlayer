using Avalonia.Controls;
using Avalonia.Interactivity;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class AudioSeparationSettingsView : UserControl
{
    public AudioSeparationSettingsView() => InitializeComponent();

    private void OnToggleModelsClick(object? sender, RoutedEventArgs eventArgs) =>
        AudioModelCatalog.IsVisible = !AudioModelCatalog.IsVisible;

    private async void OnImportClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is AudioSeparationSettingsViewModel model)
            await AppErrorReporter.GuardAsync(() => model.ImportAsync(TopLevel.GetTopLevel(this)?.StorageProvider));
    }

    private async void OnModelLinkClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: InferenceModelSettingsViewModel { HasDownloadLink: true, DownloadUri: { } uri } } &&
            TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            await AppErrorReporter.GuardAsync(async () => { await launcher.LaunchUriAsync(uri); });
    }
}