using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media;
using LightMediaRenderer.Avalonia.Pipeline;
using LightStudio.RemoteServices;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
public static partial class BrowserVideoBootstrap
{
    public static async Task RegisterVideoPipelineAsync()
    {
        BrowserMainThread.Capture();
        await MediaWorkerConnection.InitializeAsync();
        MediaDecodeSessionFactory.Register(request => new MediaDecodeSession(request.Options,
            request.Input ?? new BrowserSessionInput(request.Path, request.OpenSource, request.OpenFile), WorkerMediaEngine.Main),
            [WorkerMediaEngine.WebCodecsDeviceType]);
        RemoteVideoPacketSource.PreferWebSocket = UsesPacketSocket();
        HttpMediaStream.PreferBoundedResponses = RemoteVideoPacketSource.PreferWebSocket;
    }

    public static AppBuilder BuildAvaloniaApp<TApplication>()
        where TApplication : Application, new() =>
        AppBuilder.Configure<TApplication>()
            .WithInterFont()
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = 512 * 1024 * 1024,
            });

    [JSImport("usesPacketSocket", MediaWorkerConnection.ModuleName)]
    private static partial bool UsesPacketSocket();
}