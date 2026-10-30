using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media;
using LightMediaRenderer.Avalonia.Pipeline;
using LightStudio.RemoteServices;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
public static class BrowserVideoBootstrap
{
    public static Task RegisterVideoPipelineAsync()
    {
        BrowserMainThread.Capture();
        RemoteVideoPacketSource.PreferWebSocket = UsesPacketSocket();
        HttpMediaStream.PreferBoundedResponses = RemoteVideoPacketSource.PreferWebSocket;
#if MULTI_THREAD_APP
        // Sessions use the factory's in-process engine, which decodes on this runtime's threads.
        return Task.CompletedTask;
#else
        return RegisterWorkerEngineAsync();
#endif
    }

#if !MULTI_THREAD_APP
    private static async Task RegisterWorkerEngineAsync()
    {
        await MediaWorkerConnection.InitializeAsync();
        MediaDecodeSessionFactory.Register(request => new MediaDecodeSession(request.Options,
            request.Input ?? new BrowserSessionInput(request.Path, request.OpenSource, request.OpenFile), WorkerMediaEngine.Main),
            [WorkerMediaEngine.WebCodecsDeviceType]);
    }
#endif

    public static AppBuilder BuildAvaloniaApp<TApplication>()
        where TApplication : Application, new() =>
        AppBuilder.Configure<TApplication>()
            .WithInterFont()
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = 512 * 1024 * 1024,
            });

    /// <summary>Whether remote video packets are streamed over a WebSocket rather than read with HTTP requests.</summary>
    private static bool UsesPacketSocket()
    {
        using var navigator = JSHost.GlobalThis.GetPropertyAsJSObject("navigator");
        return navigator?.GetPropertyAsString("userAgent")?.Contains("Firefox/", StringComparison.Ordinal) == true;
    }
}
