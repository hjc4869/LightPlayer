using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Subtitles;
using LightStudio.Logging;
using LightStudio.NativeBindings;
using LightStudio.NativeBindings.Types;

namespace LightStudio.LightPlayer.WebWorkers;

/// <summary>
/// Task-returning entry points keep the script available for source reads. Playback runs on the
/// managed owner thread; independent synchronous reads use bounded thread-pool jobs.
/// </summary>
[SupportedOSPlatform("browser")]
[Include("libavformat/avformat.h")]
[Include("libavutil/hwcontext.h")]
internal static partial class MediaWorker
{
    private static readonly int backgroundConcurrency = Math.Clamp(Environment.ProcessorCount - 1, 1, 2);
    private static readonly SemaphoreSlim background = new(backgroundConcurrency, backgroundConcurrency);
    internal static int ActiveBackgroundJobs => backgroundConcurrency - background.CurrentCount;

    [JSExport]
    internal static Task<string> Initialize(int ffmpegLogLevel, int appLogLevel) => Complete(() =>
    {
        av_log_set_level(ffmpegLogLevel);
        AppLog.ConfigureConsole((AppLogLevel)appLogLevel);
        return av_version_info()!;
    });

    [JSExport]
    internal static Task<string> LicenseNotice() => Complete(FfmpegLicenseHelper.BuildNoticeText);

    [JSExport]
    internal static Task<string> DescribeException(Exception exception) => Complete(exception.ToString);

    /// <summary>Runs one command of the engine protocol. Arguments and results are JSON.</summary>
    [JSExport]
    internal static Task<string> Call(string method, string arguments) => method switch
    {
        "subtitleScript" or "bitmapSubtitles" or "readTags" or "probeVideo" or "probeAudioStreams" or
        "probeImage" or "inspect" or "albumCover" or "image" or "photoOpen" or "photoTiles" or "photoPackets" or "photoClose" or
        "photoAnimationOpen" or "photoAnimationRead" or "photoAnimationClose" =>
            RunBackgroundAsync(method, arguments),
        _ => Complete(() => Execute(method, arguments)),
    };

    private static Task<string> RunBackgroundAsync(string method, string arguments)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ExecuteBackgroundAsync(method, arguments, completion);
        return completion.Task;
    }

    private static async Task ExecuteBackgroundAsync(string method, string arguments, TaskCompletionSource<string> completion)
    {
        await background.WaitAsync();
        try
        {
            completion.SetResult(await Task.Run(() => Execute(method, arguments)));
        }
        catch (OperationCanceledException exception)
        {
            AppLog.Debug("Worker.Media", $"The background '{method}' command was canceled.");
            completion.SetException(exception);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Worker.Media", "A background media command failed.", exception);
            completion.SetException(exception);
        }
        finally { background.Release(); }
    }

    private static string Execute(string method, string arguments) => method switch
    {
        "open" => Serialize(WorkerGraph.Open(Read(arguments, EngineWireContext.Default.WireSource)),
            EngineWireContext.Default.WireOpenResult),
        "openRelay" => Serialize(WorkerGraph.OpenRelay(Read(arguments, PacketRelayJsonContext.Default.WireRelaySource)),
            EngineWireContext.Default.WireOpenResult),
        "describeVideo" => Serialize(WorkerGraph.DescribeVideo(Read(arguments, EngineWireContext.Default.Int32)),
            EngineWireContext.Default.WireVideoStream),
        "createVideoDecoder" => Serialize(WorkerGraph.CreateVideoDecoder(Read(arguments, EngineWireContext.Default.WireVideoDecoderRequest)),
            EngineWireContext.Default.WireDecoderResult),
        "createAudioDecoder" => Serialize(WorkerGraph.CreateAudioDecoder(Read(arguments, EngineWireContext.Default.WireAudioDecoderRequest)),
            EngineWireContext.Default.WireDecoderResult),
        "link" => Done(() => WorkerGraph.Link(Read(arguments, EngineWireContext.Default.WireLink))),
        "setOutput" => Done(() => WorkerGraph.SetOutput(Read(arguments, EngineWireContext.Default.WireOutput))),
        "start" => Done(() => WorkerGraph.Start(Read(arguments, EngineWireContext.Default.Int32))),
        "seek" => Serialize(WorkerGraph.Seek(Read(arguments, EngineWireContext.Default.WireSeek)), EngineWireContext.Default.WireSeekResult),
        "rendition" => Done(() => WorkerGraph.SelectRendition(Read(arguments, EngineWireContext.Default.WireRendition))),
        "recover" => Serialize(WorkerGraph.Recover(Read(arguments, EngineWireContext.Default.WireRecover)), EngineWireContext.Default.Boolean),
        "dispose" => Done(() => WorkerGraph.Dispose(Read(arguments, EngineWireContext.Default.Int32))),
        "subtitleScript" => Serialize(ReadSubtitleScript(Read(arguments, EngineWireContext.Default.WireSubtitles)),
            EngineWireContext.Default.String),
        "bitmapSubtitles" => ReadBitmapSubtitles(Read(arguments, EngineWireContext.Default.WireSubtitles)),
        "bitmapSubtitlePackets" => DecodeBitmapSubtitles(Read(arguments, EngineWireContext.Default.WireSubtitles)),
        "readTags" => WorkerOperations.ReadTags(Read(arguments, EngineWireContext.Default.WireSource)),
        "probeVideo" => WorkerOperations.ProbeVideo(Read(arguments, EngineWireContext.Default.WireProbeRequest)),
        "probeAudioStreams" => WorkerOperations.ProbeAudioStreams(Read(arguments, EngineWireContext.Default.WireSource)),
        "probeImage" => WorkerOperations.ProbeImage(Read(arguments, EngineWireContext.Default.WireSource)),
        "inspect" => WorkerOperations.Inspect(Read(arguments, EngineWireContext.Default.WireSource)),
        "albumCover" => WorkerOperations.ReadAlbumCover(Read(arguments, EngineWireContext.Default.WireSource)),
        "image" => WorkerOperations.DecodeImage(Read(arguments, EngineWireContext.Default.WireImageRequest)),
        "photoOpen" => WorkerPhotos.Open(Read(arguments, EngineWireContext.Default.WirePhotoRequest)),
        "photoTiles" => WorkerPhotos.DecodeTiles(Read(arguments, EngineWireContext.Default.WirePhotoTiles)),
        "photoPackets" => WorkerPhotos.ReadPackets(Read(arguments, EngineWireContext.Default.WirePhotoTiles)),
        "photoClose" => Done(() => WorkerPhotos.Close(Read(arguments, EngineWireContext.Default.Int32))),
        "photoAnimationOpen" => Serialize(WorkerPhotos.OpenAnimation(Read(arguments, EngineWireContext.Default.Int32)),
            EngineWireContext.Default.Int32),
        "photoAnimationRead" => WorkerPhotos.ReadAnimation(Read(arguments, EngineWireContext.Default.WirePhotoAnimation)),
        "photoAnimationClose" => Done(() => WorkerPhotos.CloseAnimation(Read(arguments, EngineWireContext.Default.WirePhotoAnimation))),
        _ => throw new NotSupportedException($"Unknown media worker command '{method}'."),
    };

    /// <summary>Frees a native payload once the worker script has copied it.</summary>
    [JSExport]
    internal static Task ReleasePayload(int handle) => Complete(() =>
    {
        Payloads.Release(handle);
        return true;
    });

    /// <summary>Steps the engine and returns what it produced; see <see cref="WorkerGraph.Pump"/>.</summary>
    [JSExport]
    internal static Task<string> Pump(double budgetMilliseconds, string updates) =>
        Complete(() => WorkerGraph.Pump(budgetMilliseconds, Read(updates, EngineWireContext.Default.WirePumpUpdates)));

    /// <summary>Takes a batch of packets the page relayed for a demuxer; see <see cref="WorkerGraph.Relay"/>.</summary>
    [JSExport]
    internal static Task Relay(int demuxer, int generation, byte[] batch) => Complete(() =>
    {
        WorkerGraph.Relay(demuxer, generation, batch);
        return true;
    });

    /// <summary>Frees a source's windows once JavaScript confirmed it stopped writing to them.</summary>
    [JSExport]
    internal static Task ReleaseSource(int source) => Complete(() =>
    {
        SourceBridgeContext.ReleaseClosed(source);
        return true;
    });

    private static string ReadSubtitleScript(WireSubtitles request)
    {
        using var stream = new SourceBridgeContext(request.Source.Id, (long)request.Source.Length, 1024 * 1024, 4, 2);
        using FfmpegDemuxer reader = FfmpegDemuxer.Open(stream, request.Source.Name, leaveOpen: true);
        return FfmpegSubtitleReader.Read(reader, request.Stream);
    }

    /// <summary>Decodes a bitmap subtitle stream; the compositions travel as bytes beside the result.</summary>
    private static string ReadBitmapSubtitles(WireSubtitles request)
    {
        using var stream = new SourceBridgeContext(request.Source.Id, (long)request.Source.Length, 1024 * 1024, 4, 2);
        using FfmpegDemuxer reader = FfmpegDemuxer.Open(stream, request.Source.Name, leaveOpen: true);
        return EncodeBitmapSubtitles(FfmpegSubtitleReader.ReadBitmaps(reader, request.Stream,
            preserveContainerTimestamps: request.PreserveContainerTimestamps));
    }

    /// <summary>Decodes bitmap subtitle packets the page sent as one in-memory source, reading one packet at a time.</summary>
    private static string DecodeBitmapSubtitles(WireSubtitles request)
    {
        using var stream = new SourceBridgeContext(request.Source.Id, (long)request.Source.Length, 1024 * 1024, 4, 2);
        return EncodeBitmapSubtitles(FfmpegSubtitleReader.ReadBitmaps(BitmapSubtitlePacketsCodec.Read(stream)));
    }

    private static string EncodeBitmapSubtitles(BitmapSubtitleDocument document)
    {
        var bytes = new NativeBuffer(BitmapSubtitleCodec.GetEncodedSize(document));
        try
        {
            BitmapSubtitleCodec.Write(document, bytes.GetSpan());
            return Payloads.Envelope("null", bytes);
        }
        catch
        {
            bytes.Dispose();
            throw;
        }
    }

    private static T Read<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        JsonSerializer.Deserialize(json, type) ?? throw new InvalidOperationException("The media worker received no arguments.");

    private static string Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        JsonSerializer.Serialize(value, type);

    private static string Done(Action action)
    {
        action();
        return "true";
    }

    private static Task<T> Complete<T>(Func<T> work)
    {
        try
        {
            return Task.FromResult(work());
        }
        catch (Exception exception)
        {
            AppLog.Warning("Worker.Media", "A media worker command failed.", exception);
            return Task.FromException<T>(exception);
        }
    }
}
