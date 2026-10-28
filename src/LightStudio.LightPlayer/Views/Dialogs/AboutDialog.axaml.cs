using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Views.Dialogs;

/// <summary>Product information, bundled license notices, and maintenance actions.</summary>
public partial class AboutDialog : Window
{
    private const string GitHubProjectUrl = "https://github.com/hjc4869/LightPlayer";

    public AboutDialog()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "2.1.0.0";
        VersionText.Text = string.Format(Strings.Resources.About_VersionFormat, version, BuildInfo.SourceVersion);
        LicenseText.Text = LicenseNotices.LoadCombinedText();
        GitHubLink.Click += OnGitHubLinkClick;
        ExportLogsButton.IsEnabled = AppLog.CurrentFilePath is not null;
        ExportLogsButton.Click += OnExportLogsClick;
        BrowserDiagnosticsButton.IsVisible = OperatingSystem.IsBrowser();
        BrowserDiagnosticsButton.Click += OnBrowserDiagnosticsClick;
        ResetIndexDatabaseButton.IsVisible = PlatformServices.QueueIndexDatabaseResetAsync is not null;
        ResetIndexDatabaseButton.Click += OnResetIndexDatabaseClick;
        OkButton.Click += OnOk;
    }

    private async void OnGitHubLinkClick(object? sender, RoutedEventArgs e)
    {
        if (Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(new Uri(GitHubProjectUrl));
        }
    }

    private async void OnExportLogsClick(object? sender, RoutedEventArgs e) =>
        await AppErrorReporter.GuardAsync(
            () => LogExportService.ExportAsync(StorageProvider),
            Strings.Resources.About_ExportLogs);

    private async void OnBrowserDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        if (Launcher is { } launcher && PlatformServices.WebHostAddress is { Length: > 0 } host)
        {
            await launcher.LaunchUriAsync(new Uri($"{host.TrimEnd('/')}/diag"));
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();

    private async void OnResetIndexDatabaseClick(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app)
            throw new InvalidOperationException("The application is not initialized.");
        Close();
        await AppErrorReporter.GuardAsync(app.ResetIndexDatabaseAsync, Strings.Resources.About_ResetIndexDatabase);
    }
}
