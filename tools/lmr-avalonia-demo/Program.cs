using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using LightMediaRenderer.Avalonia.Controls;
using LightMediaRenderer.Avalonia.Pipeline;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Core;
using LightStudio.Logging;

namespace LmrAvaloniaDemo;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppLog.ConfigureConsole();
        try
        {
            FfmpegLog.Attach();
            return AppBuilder.Configure<DemoApplication>()
                .UsePlatformDetect()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            AppLog.Error("Demo.Startup", "Could not start the player demo.", exception);
            return 1;
        }
        finally
        {
            AppLog.Shutdown();
        }
    }
}

public sealed class DemoApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new DemoWindow(desktop.Args is { Length: > 0 } args ? args[0] : null);
        base.OnFrameworkInitializationCompleted();
    }
}

public sealed class DemoWindow : Window
{
    private readonly MediaPlayer player = new();
    private readonly VideoView video = new() { Name = "Video", Background = Brushes.Black };
    private readonly Button open = new() { Name = "Open", Content = "Open", MinWidth = 64 };
    private readonly Button playPause = new() { Name = "PlayPause", Content = "Play", MinWidth = 64 };
    private readonly Button stop = new() { Name = "Stop", Content = "Stop", MinWidth = 64 };
    private readonly TextBlock fileName = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock message = new() { IsVisible = false, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private bool opening;
    private bool closeRequested;
    private bool closed;

    public DemoWindow(string? initialPath = null)
    {
        Title = "LMR Avalonia Demo";
        Width = 960;
        Height = 600;
        MinWidth = 360;
        MinHeight = 240;
        video.Player = player;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { open, playPause, stop },
        };
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(8),
            Children = { buttons, fileName },
        };
        Grid.SetColumn(fileName, 1);
        fileName.Margin = new Thickness(12, 0, 0, 0);
        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto,Auto"),
            Children = { video, toolbar, message },
        };
        Grid.SetRow(toolbar, 1);
        Grid.SetRow(message, 2);
        Content = layout;

        open.Click += async (_, _) => await OpenAsync(null);
        playPause.Click += (_, _) => player.TogglePause();
        stop.Click += (_, _) => player.Stop();
        player.StateChanged += (_, _) => Dispatcher.UIThread.Post(UpdateControls);
        player.ErrorOccurred += (_, error) => Dispatcher.UIThread.Post(() => ShowMessage(error));
        Opened += async (_, _) =>
        {
            if (initialPath is not null)
                await OpenAsync(initialPath);
        };
        Closing += (_, args) =>
        {
            if (opening)
            {
                closeRequested = true;
                args.Cancel = true;
            }
        };
        Closed += (_, _) =>
        {
            closed = true;
            video.Player = null;
            player.Dispose();
        };
        UpdateControls();
    }

    private async Task OpenAsync(string? path)
    {
        if (opening || closed)
            return;
        opening = true;
        message.IsVisible = false;
        UpdateControls();
        try
        {
            if (path is null)
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open media file",
                    AllowMultiple = false,
                });
                if (files.Count == 0)
                    return;
                using IStorageFile file = files[0];
                path = file.TryGetLocalPath() ?? throw new NotSupportedException("The selected file has no local path.");
            }

            fileName.Text = Path.GetFileName(path);
            Title = $"{fileName.Text} - LMR Avalonia Demo";
            await player.OpenAsync(path);
            if (!closeRequested)
                player.Play();
        }
        catch (Exception exception)
        {
            AppLog.Error("Demo.Playback", "Could not open the selected media file.", exception);
            ShowMessage(exception.Message);
        }
        finally
        {
            opening = false;
            if (closeRequested)
                Close();
            else
                UpdateControls();
        }
    }

    private void UpdateControls()
    {
        if (closed)
            return;
        open.IsEnabled = !opening;
        bool ready = !opening && player.Info is not null && player.State != PlaybackState.Error;
        playPause.IsEnabled = ready;
        stop.IsEnabled = ready;
        playPause.Content = player.State is PlaybackState.Playing or PlaybackState.Buffering ? "Pause" : "Play";
    }

    private void ShowMessage(string text)
    {
        if (closed)
            return;
        message.Text = text;
        message.IsVisible = true;
    }
}