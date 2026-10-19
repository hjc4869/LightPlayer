using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using LightMediaRenderer.Avalonia.Controls;
using LightMediaRenderer.Avalonia.Pipeline;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.LightPlayer.Web;
using LightStudio.Logging;

namespace LmrAvaloniaDemo.Web;

[SupportedOSPlatform("browser")]
public static partial class BrowserProgram
{
    private static readonly SemaphoreSlim commands = new(1, 1);
    internal static VideoView Video { get; private set; } = null!;
    internal static PhotoView Photo { get; private set; } = null!;
    private static MediaPlayer? player;
    private static VideoFrame? image;
    private static string error = string.Empty;

    internal static Control CreateView()
    {
        Video = new VideoView { Background = Brushes.Black };
        Photo = new PhotoView { IsVisible = false };
        return new Grid { Children = { Video, Photo } };
    }

    public static async Task Main()
    {
        AppLog.ConfigureConsole();
        BrowserMainThread.Capture();
        await JSHost.ImportAsync("WebAudio", "/web-audio.js");
        await MediaWorkerConnection.InitializeAsync();
        MediaDecodeSessionFactory.Register(request => new MediaDecodeSession(request.Options,
            request.Input ?? new BrowserSessionInput(request.Path, request.OpenSource, request.OpenFile), WorkerMediaEngine.Main),
            [WorkerMediaEngine.WebCodecsDeviceType]);
        WebAudioPlaybackBackend.Register();
        await AppBuilder.Configure<DemoApplication>().WithInterFont().With(new SkiaOptions { MaxGpuResourceSizeBytes = 512 * 1024 * 1024 })
            .StartBrowserAppAsync("out");
    }

    [JSExport]
    public static async Task OpenAsync(string path, string decoder, bool photo)
    {
        await commands.WaitAsync();
        try
        {
            await CloseAsync();
            error = string.Empty;
            Photo.IsVisible = photo;
            Video.IsVisible = !photo;
            if (photo)
            {
                image = await BrowserImageDecoder.DecodePhotoAsync(path, null, null, CancellationToken.None);
                Photo.Source = image;
                Photo.SourceKey = path;
            }
            else
            {
                player = new MediaPlayer();
                player.ErrorOccurred += (_, message) => error = message;
                Video.Player = player;
                WorkerMediaEngine.RequireWebCodecs = decoder == WorkerMediaEngine.WebCodecsDeviceType;
                var options = new MediaPlayerOptions
                {
                    VideoDecoder = VideoDecoderOptions.Default with
                    {
                        EnableHardwareDecoding = decoder != "ffmpeg",
                        HardwareDeviceType = WorkerMediaEngine.WebCodecsDeviceType,
                    },
                };
                await player.OpenAsync(new MediaDecodeSession(options, new BrowserSessionInput(path, null), WorkerMediaEngine.Main));
                await player.PlayAsync();
            }
        }
        catch (Exception exception) { error = exception.ToString(); throw; }
        finally { commands.Release(); }
    }

    [JSExport]
    public static async Task CommandAsync(string command, double value)
    {
        if (command == "zoom") { Photo.ZoomTo(value); return; }
        await commands.WaitAsync();
        try
        {
            if (command == "stop") { await CloseAsync(); return; }
            if (player is null) return;
            if (command == "play") await player.PlayAsync();
            else if (command == "pause") await player.PauseAsync();
            else if (command == "seek") await player.SeekAsync(TimeSpan.FromSeconds(value));
            else if (command == "step") await player.StepFrameAsync();
            else if (command == "speed") player.Speed = value;
            else if (command == "volume") player.Volume = (float)value;
        }
        finally { commands.Release(); }
    }

    private static async Task CloseAsync()
    {
        Video.Player = null;
        if (player is not null) { await player.DisposeAsync(); player = null; }
        Photo.Source = null;
        var tiles = image?.PhotoTiles as BrowserPhotoTileSource;
        image?.Release();
        image = null;
        if (tiles is not null) await tiles.WhenReleased;
    }

    [JSExport]
    public static string Status()
    {
        var statistics = player?.GetStatistics();
        return JsonSerializer.Serialize(new DemoStatus(player?.State.ToString() ?? (image is null ? "Stopped" : "Photo"),
            player?.Position.TotalSeconds ?? 0, player?.Duration.TotalSeconds ?? 0,
            statistics?.DecodedVideoFrames ?? 0, statistics?.PresentedVideoFrames ?? 0, statistics?.DroppedVideoFrames ?? 0,
            statistics?.DecodeFps ?? 0, statistics?.RenderFps ?? 0, statistics?.Decoder.SamplePixelFormat ?? string.Empty,
            statistics?.Decoder.DecoderDescription ?? string.Empty, image?.PhotoTiles?.Count ?? 0,
            image?.Width ?? 0, image?.Height ?? 0, statistics?.Decoder.BitDepth ?? 0, statistics?.Decoder.ThreadCount ?? 0,
            statistics?.Decoder.DolbyVisionActive ?? false, statistics?.Decoder.Hdr10PlusActive ?? false,
            statistics?.Decoder.DolbyVisionEnhancementLayerActive ?? false, BrowserVideoPlanes.ReleasedCount, error), DemoJsonContext.Default.DemoStatus);
    }
}

internal sealed class DemoApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime lifetime)
            lifetime.MainView = BrowserProgram.CreateView();
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed record DemoStatus(string State, double Position, double Duration, long Decoded, long Presented, long Dropped,
    double DecodeFps, double RenderFps, string Format, string Delivery, int Tiles, int Width, int Height,
    int BitDepth, int Threads, bool DolbyVision, bool Hdr10Plus, bool Enhancement, int ReleasedFrames, string Error);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DemoStatus))]
internal sealed partial class DemoJsonContext : JsonSerializerContext;
