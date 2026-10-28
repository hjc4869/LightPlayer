using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using LightStudio.LightPlayer.Controls;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightMediaRenderer.Avalonia.Pipeline;
using LightMediaRenderer.Avalonia.Shaders;
using LightStudio.LightPlayer.ViewModels.Pages;
using LightStudio.LightPlayer.Views;
using LightStudio.LightPlayer.Views.Pages;
using LightStudio.LocalServices;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Desktop;

internal sealed partial class StandaloneFileWindow : Window
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly IAppSettingsStore settings;
    private readonly IThemeService themeService;
    private readonly DesktopMultimediaPlatformIO platform;
    private readonly IClientMediaResolver resolver;
    private readonly DesktopDialogService dialogs = new();
    private readonly CancellationTokenSource cancellation = new();
    private IDisposable? viewer;
    private PlaybackController? playback;
    private VideoKeyboardShortcutHandler? shortcuts;
    private WindowState previousWindowState;
    private bool closing;
    private bool playbackReleased;

    public StandaloneFileWindow(string path, IAppSettingsStore settings)
    {
        InitializeComponent();
        this.settings = settings;
        platform = new DesktopMultimediaPlatformIO(AppPaths.DataDirectory);
        platform.InitializePlatform();
        resolver = new CapturedPlatformMediaResolver(platform);
        PlatformServices.ConfigureWindowChrome?.Invoke(this);
        themeService = new AvaloniaThemeService(settings);
        themeService.BackdropStyleChanged += ApplyTitleBar;
        PlatformServices.FileShareService ??= new DesktopFileShareService();
        PlatformServices.VideoShaderPackages = new VideoShaderPackageCatalog(
            Path.Combine(AppPaths.DataDirectory, "video-shader-packages"));
        Title = Path.GetFileName(path);
        if (OperatingSystem.IsMacOS())
        {
            Styles.Add(new Style(selector => selector.OfType<Border>().Name("TitleBar"))
            {
                Setters = { new Setter(WindowDecorationProperties.ElementRoleProperty, WindowDecorationsElementRole.TitleBar) },
            });
        }
        ApplyTitleBar();
        Opened += (_, _) =>
        {
            ApplyTitleBar();
            _ = OpenAsync(path);
        };
        Closing += async (_, args) =>
        {
            if (playbackReleased)
                return;
            args.Cancel = true;
            if (closing)
                return;
            closing = true;
            cancellation.Cancel();
            Content = null;
            try
            {
                if (viewer is IAsyncDisposable asyncViewer)
                    await asyncViewer.DisposeAsync();
                else
                    viewer?.Dispose();
            }
            catch (Exception exception)
            {
                AppLog.Error("Desktop.Activation", "Could not release standalone playback.", exception);
            }
            finally
            {
                viewer = null;
                playbackReleased = true;
                Close();
            }
        };
        Closed += (_, _) =>
        {
            themeService.BackdropStyleChanged -= ApplyTitleBar;
            cancellation.Cancel();
            shortcuts?.Dispose();
            Content = null;
            viewer?.Dispose();
            playback?.Dispose();
            PlatformServices.KeepScreenOnRequested?.Invoke(false);
            PlatformServices.VideoShaderPackages?.Dispose();
            PlatformServices.VideoShaderPackages = null;
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            var fullScreen = WindowState == WindowState.FullScreen;
            if (fullScreen)
                previousWindowState = change.GetOldValue<WindowState>();
            if (viewer is PhotoViewerPageViewModel photo)
                photo.IsFullScreen = fullScreen;
            else if (viewer is VideoPlayerPageViewModel video)
                video.IsFullScreen = fullScreen;
            ApplyTitleBar();
        }
    }

    private bool UseCustomWindowControls =>
        PlatformServices.SupportsClientSideWindowControls && themeService?.IsAppleChromeEnabled == true;

    private void ApplyTitleBar()
    {
        if (!IsInitialized)
            return;

        if (PlatformServices.SupportsClientSideWindowControls)
            WindowDecorations = UseCustomWindowControls ? WindowDecorations.BorderOnly : WindowDecorations.Full;

        ExtendClientAreaToDecorationsHint = OperatingSystem.IsMacOS() || UseCustomWindowControls;
        var windowed = WindowState != WindowState.FullScreen;
        Classes.Set("appleCsd", UseCustomWindowControls);
        Classes.Set("roundedCsd", UseCustomWindowControls && WindowState == WindowState.Normal);
        ClientWindowControls.IsVisible = UseCustomWindowControls && windowed;
        MaximizeWindowButton.Classes.Set("maximized", WindowState == WindowState.Maximized);
        ShellChrome.SetWindowControlsInset(this, windowed && ExtendClientAreaToDecorationsHint
            ? new Thickness(UseCustomWindowControls ? 104 : 80, 0, 0, 0)
            : default);
        if (PlatformServices.ApplyToolbarTitleBar is { } apply && TryGetPlatformHandle() is { } handle)
            apply(handle.Handle, ExtendClientAreaToDecorationsHint && windowed);
    }

    private void OnCloseWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => Close();

    private void OnMinimizeWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    internal static bool IsVisualFile(string path)
    {
        if (!File.Exists(path))
            return false;
        if (MediaContainerFormats.IsImage(path))
            return true;

        var extension = Path.GetExtension(path);
        if (MediaContainerFormats.DiscImage.Contains(extension))
            return true;
        if (!MediaContainerFormats.Video.Contains(extension))
            return false;
        try
        {
            using var stream = File.OpenRead(path);
            return VideoPreviewFactory.Probe(stream, withPreview: false, leaveOpen: true)
                is { IsStillImage: false };
        }
        catch (Exception exception)
        {
            AppLog.Warning("Desktop.Activation", $"Could not classify {path}.", exception);
            return true;
        }
    }

    internal static bool TryOpen(string path)
    {
        if (!IsVisualFile(path))
            return false;
        try
        {
            StartProcess(["--standalone", Path.GetFullPath(path)]);
        }
        catch (Exception exception)
        {
            AppLog.Error("Desktop.Activation", $"Could not open a new window for {path}.", exception);
            if (Application.Current is not null)
                Dispatcher.UIThread.Post(() => _ = new DesktopDialogService().ShowWarningDetailsAsync(
                    Path.GetFileName(path), exception.ToString()));
        }
        return true;
    }

    internal static void StartProcess(IReadOnlyList<string> arguments)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The application executable path is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "lmc.dll"));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The application process could not be started.");
    }

    private async Task OpenAsync(string path)
    {
        try
        {
            var siblings = await Task.Run(() => FindSiblings(path), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (MediaContainerFormats.IsImage(path))
            {
                var photos = siblings.Where(MediaContainerFormats.IsImage).Select(MediaReference.Local).ToArray();
                var selected = Array.FindIndex(photos, photo => PathComparer.Equals(photo.Path, path));
                var model = new PhotoViewerPageViewModel(settings, resolver,
                    new PhotoViewerRequest(photos, selected), new PhotoViewerCallbacks
                    {
                        Close = Close,
                        SetFullScreen = SetFullScreen,
                        LoadPreview = LoadPreviewAsync,
                        PreviewMaxDimension = 480,
                        ShowProperties = (media, title) => _ = dialogs.ShowPropertiesAsync(media, title, resolver),
                        RevealInFileManager = file => _ = RevealAsync(file),
                    })
                {
                    IsFullScreen = WindowState == WindowState.FullScreen,
                };
                viewer = model;
                DataContext = model;
                MediaHost.Content = new PhotoViewerPageView { DataContext = model };
            }
            else
            {
                var videos = new List<Sourced<DbVideoFile>>();
                foreach (var sibling in siblings.Where(candidate =>
                    MediaContainerFormats.Video.Contains(Path.GetExtension(candidate)) ||
                    MediaContainerFormats.DiscImage.Contains(Path.GetExtension(candidate))))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var probe = await platform.ProbeVideoAsync(sibling, withPreview: false,
                            cancellation.Token);
                        if (probe is not null && !probe.IsStillImage)
                        {
                            videos.Add(new(LibrarySourceId.Local, new DbVideoFile
                            {
                                Path = sibling,
                                FileName = Path.GetFileName(sibling),
                                FolderPath = Path.GetDirectoryName(sibling) ?? string.Empty,
                                Title = probe.Title,
                                Duration = probe.Duration,
                                Width = probe.Width,
                                Height = probe.Height,
                                FrameRate = probe.FrameRate,
                                VideoCodec = probe.VideoCodec,
                                VideoStreamIndex = probe.VideoStreamIndex,
                                MediaKind = probe.MediaKind,
                                DiscTitle = probe.DiscTitle,
                                ExternalAudioPaths = FindSidecars(sibling, siblings, MediaContainerFormats.ExternalAudio),
                                ExternalSubtitlePaths = FindSidecars(sibling, siblings, MediaContainerFormats.ExternalSubtitle),
                            }));
                        }
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        AppLog.Warning("Desktop.Activation", $"Could not probe {sibling}.", exception);
                    }
                }

                cancellation.Token.ThrowIfCancellationRequested();
                var selected = videos.FirstOrDefault(video => PathComparer.Equals(video.Value.Path, path))
                    ?? new Sourced<DbVideoFile>(LibrarySourceId.Local, new DbVideoFile
                    {
                        Path = path, FileName = Path.GetFileName(path),
                        FolderPath = Path.GetDirectoryName(path) ?? string.Empty,
                    });
                if (!videos.Contains(selected))
                    videos.Insert(0, selected);
                playback = new PlaybackController(mediaResolver: resolver);
                var model = new VideoPlayerPageViewModel(settings, playback, new SessionVideoStates(),
                    dialogs, null, resolver, SetFullScreen, _ => { },
                    keepOn => PlatformServices.KeepScreenOnRequested?.Invoke(keepOn),
                    null, Close, persistSubtitleImports: false)
                {
                    IsFullScreen = WindowState == WindowState.FullScreen,
                    OpenContainingFolder = video => _ = RevealAsync(video.Value.Path),
                    ShowProperties = video => _ = AppErrorReporter.GuardAsync(() => dialogs.ShowPropertiesAsync(
                        new MediaReference(video.SourceId, video.Value.Path),
                        video.Value.DisplayName ?? video.Value.FileName, resolver)),
                };
                viewer = model;
                DataContext = model;
                MediaHost.Content = new VideoPlayerPageView { DataContext = model };
                shortcuts = new VideoKeyboardShortcutHandler(this, useGlobalHandlers: true, standalonePlayer: model);
                await model.OpenAsync(selected, videos, resume: false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            AppLog.Error("Desktop.Activation", $"Could not open {path}.", exception);
            if (!cancellation.IsCancellationRequested)
                await dialogs.ShowWarningDetailsAsync(Title ?? Path.GetFileName(path), exception.ToString());
        }
    }

    private async Task RevealAsync(string path)
    {
        try
        {
            await (PlatformServices.FileRevealService ?? new DesktopFileRevealService()).RevealAsync(path);
        }
        catch (Exception exception)
        {
            AppLog.Error("Desktop.Activation", $"Could not reveal {path}.", exception);
            await dialogs.ShowWarningDetailsAsync(Path.GetFileName(path), exception.ToString());
        }
    }

    private static IReadOnlyList<string> FindSiblings(string path)
    {
        try
        {
            return Directory.EnumerateFiles(Path.GetDirectoryName(path)!)
                .Append(path).Distinct(PathComparer)
                .Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("Desktop.Activation", $"Could not list files beside {path}.", exception);
            return [path];
        }
    }

    private async Task<IImage?> LoadPreviewAsync(MediaReference media, CancellationToken token)
    {
        var thumbnail = await ImageDecoding.DecodePhotoThumbnailAsync(
            new PhotoDecodeRequest(platform, media.Path), 480, token).ConfigureAwait(false);
        return thumbnail is null ? null : PhotoBitmap.Create(thumbnail);
    }

    private static string FindSidecars(string video, IReadOnlyList<string> siblings, IReadOnlySet<string> formats) =>
        DbVideoFile.JoinPaths(siblings.Where(sibling => !PathComparer.Equals(sibling, video) &&
            Path.GetFileName(sibling).StartsWith(Path.GetFileNameWithoutExtension(video) + ".", StringComparison.OrdinalIgnoreCase) &&
            formats.Contains(Path.GetExtension(sibling))));

    private void SetFullScreen(bool fullScreen)
    {
        if (fullScreen && WindowState != WindowState.FullScreen)
            WindowState = WindowState.FullScreen;
        else if (!fullScreen && WindowState == WindowState.FullScreen)
            WindowState = previousWindowState;
    }

    private sealed class SessionVideoStates : IVideoPlaybackStateStore
    {
        private readonly Dictionary<MediaReference, VideoPlaybackState> states = new();
        public VideoPlaybackState? Get(MediaReference video) => states.GetValueOrDefault(video);
        public void Save(MediaReference video, VideoPlaybackState state) => states[video] = state;
        public void SaveAll(IReadOnlyList<(MediaReference Video, VideoPlaybackState State)> updates)
        {
            foreach (var update in updates)
                Save(update.Video, update.State);
        }
        public IReadOnlyList<VideoPlaybackProgress> GetInProgress(int limit) => [];
    }
}