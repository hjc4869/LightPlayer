using Avalonia.Controls;
using Avalonia.Interactivity;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class PhotoIndexSettingsView : UserControl
{
    public PhotoIndexSettingsView()
    {
        InitializeComponent();
    }

    private async void OnImportEmbeddingModelClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is PhotoIndexSettingsViewModel photos)
            await AppErrorReporter.GuardAsync(() => photos.ImportAsync(TopLevel.GetTopLevel(this)?.StorageProvider));
    }

    private async void OnInferenceModelLinkClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: InferenceModelSettingsViewModel { HasDownloadLink: true, DownloadUri: { } uri } } &&
            TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            await AppErrorReporter.GuardAsync(async () => { await launcher.LaunchUriAsync(uri); });
    }
}
