using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Avalonia;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;
using LightStudio.LightPlayer.Services.PhotoExport;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// One-shot media reads for the library and artwork views, admitted through bounded background lanes
/// into the shared media worker.
/// </summary>
[SupportedOSPlatform("browser")]
internal static class BrowserMediaOperations
{
    /// <summary>Largest picture decoded when the caller does not bound it.</summary>
    internal const int FullSizeDimension = 8192;

    private static int nextExport;

    private static int NextImageOperation() => nextExport = nextExport == int.MaxValue ? 1 : nextExport + 1;

    internal static async Task<WireMediaTags> ReadTagsAsync(MediaInput input, CancellationToken cancellationToken) =>
        (await RunAsync(input, "readTags", source => source, EngineWireContext.Default.WireSource,
            EngineWireContext.Default.WireMediaTags, cancellationToken)).Value
        ?? throw new InvalidDataException("The media worker returned no tags.");

    internal static async Task<(VideoProbeResult? Probe, byte[]? Preview)> ProbeVideoAsync(
        MediaInput input, bool withPreview, int maxDimension, CancellationToken cancellationToken)
    {
        int operation = NextImageOperation();
        return await RunAsync(input, "probeVideo", source => new WireProbeRequest(source, withPreview, maxDimension) { Operation = operation },
            EngineWireContext.Default.WireProbeRequest, EngineWireContext.Default.VideoProbeResult,
            cancellationToken, operation);
    }

    internal static async Task<IReadOnlyList<VideoProbeAudioStreamInfo>> ProbeAudioStreamsAsync(
        MediaInput input, CancellationToken cancellationToken) =>
        (await RunAsync(input, "probeAudioStreams", source => source, EngineWireContext.Default.WireSource,
            EngineWireContext.Default.IReadOnlyListVideoProbeAudioStreamInfo, cancellationToken)).Value ?? [];

    internal static async Task<WireImageProbe> ProbeImageAsync(MediaInput input, CancellationToken cancellationToken) =>
        (await RunAsync(input, "probeImage", source => source, EngineWireContext.Default.WireSource,
            EngineWireContext.Default.WireImageProbe, cancellationToken)).Value
        ?? throw new InvalidDataException("The media worker returned no picture description.");

    internal static async Task<MediaInspection?> InspectAsync(MediaInput input, CancellationToken cancellationToken) =>
        (await RunAsync(input, "inspect", source => source, EngineWireContext.Default.WireSource,
            EngineWireContext.Default.MediaInspection, cancellationToken)).Value;

    /// <summary>The encoded artwork embedded in a media file, or null when it carries none.</summary>
    internal static async Task<byte[]?> ReadAlbumCoverAsync(MediaInput input, CancellationToken cancellationToken) =>
        (await RunAsync(input, "albumCover", source => source, EngineWireContext.Default.WireSource,
            EngineWireContext.Default.WirePicture, cancellationToken)).Payload;

    /// <summary>
    /// Decodes an upright BGRA picture that fits <paramref name="maxDimension"/>, or with
    /// <paramref name="embedded"/> the artwork a media file carries. Null when there is none. A positive
    /// <paramref name="blurSigma"/> blurs the picture, which then comes back premultiplied.
    /// </summary>
    internal static async Task<BrowserPicture?> DecodeImageAsync(
        MediaInput input, int maxDimension, bool embedded, long maxEncodedBytes, CancellationToken cancellationToken,
        float blurSigma = 0, bool? renderRaw = null)
    {
        var (picture, pixels) = await RunAsync(input, "image",
            source => new WireImageRequest(source, maxDimension, embedded, maxEncodedBytes) { BlurSigma = blurSigma, RenderRaw = renderRaw },
            EngineWireContext.Default.WireImageRequest, EngineWireContext.Default.WirePicture,
            static (worker, method, arguments, argumentType, resultType) =>
                worker.CallWithNativePayloadAsync(method, arguments, argumentType, resultType),
            static result => result.Payload?.Dispose(), cancellationToken);
        if (picture is null || pixels is null)
        {
            pixels?.Dispose();
            return null;
        }

        if (picture.Width <= 0 || picture.Height <= 0 || (long)picture.Width * picture.Height * 4 != pixels.Length)
        {
            pixels.Dispose();
            throw new InvalidDataException("The media worker returned an incomplete picture.");
        }

        return new BrowserPicture(picture.Width, picture.Height, pixels);
    }

    /// <summary>Encodes an upright thumbnail, keeping its gamut, gain map and HDR transfer.</summary>
    internal static async Task<byte[]?> EncodePhotoThumbnailAsync(MediaInput input, int maxDimension, CancellationToken cancellationToken,
        bool? renderRaw = null)
    {
        int operation = NextImageOperation();
        var (_, encoded) = await RunAsync(input, "photoThumbnail",
            source => new WireImageRequest(source, maxDimension, false, 0) { RenderRaw = renderRaw, Operation = operation },
            EngineWireContext.Default.WireImageRequest, EngineWireContext.Default.String, cancellationToken, operation);
        return encoded;
    }

    internal static Task<string> ReadLicenseNoticeAsync() =>
        BackgroundMediaJobs.RunAsync(worker => worker.CallRawAsync("licenseNotice", string.Empty), CancellationToken.None);

    /// <summary>The still-image encoders of the worker's FFmpeg.</summary>
    internal static async Task<IReadOnlyList<WireStillImageEncoder>> ListStillImageEncodersAsync() =>
        JsonSerializer.Deserialize(
            await BackgroundMediaJobs.RunAsync(worker => worker.CallRawAsync("stillImageEncoders", string.Empty), CancellationToken.None),
            EngineWireContext.Default.IReadOnlyListWireStillImageEncoder) ?? [];

    /// <summary>
    /// Decodes a photo again at full resolution and encodes it as <paramref name="options"/> ask, both in the worker.
    /// The file comes back in native memory that belongs to the caller.
    /// </summary>
    internal static async Task<NativePayload> ExportPhotoAsync(MediaInput input, bool? renderRaw, PhotoExportOptions options,
        CancellationToken cancellationToken)
    {
        int operation = NextImageOperation();
        var (failure, encoded) = await RunAsync(input, "photoExport",
            source => new WirePhotoExport(source, renderRaw, options.Encoder?.Name, options) { Operation = operation },
            PhotoExportWireContext.Default.WirePhotoExport, EngineWireContext.Default.String,
            static (worker, method, arguments, argumentType, resultType) =>
                worker.CallWithNativePayloadAsync(method, arguments, argumentType, resultType),
            static result => result.Payload?.Dispose(), cancellationToken, operation);
        if (failure is null)
            return encoded ?? throw new InvalidDataException("The media worker returned no saved photo.");
        encoded?.Dispose();
        throw new PhotoExportException(Enum.Parse<PhotoExportFailure>(failure));
    }

    /// <summary>
    /// Reads a text subtitle stream out as one ASS script. Reading one walks the whole container, so it runs
    /// in a background job.
    /// </summary>
    internal static async Task<string> ReadSubtitleScriptAsync(MediaInput input, int stream, CancellationToken cancellationToken) =>
        (await RunAsync(input, "subtitleScript", source => new WireSubtitles(source, stream, false),
            EngineWireContext.Default.WireSubtitles, EngineWireContext.Default.String, cancellationToken)).Value
        ?? throw new InvalidDataException("The media worker returned no subtitles.");

    /// <inheritdoc cref="ReadSubtitleScriptAsync"/>
    internal static async Task<BitmapSubtitleDocument> ReadBitmapSubtitlesAsync(MediaInput input, int stream,
        bool preserveContainerTimestamps, CancellationToken cancellationToken)
    {
        byte[] document = (await RunAsync(input, "bitmapSubtitles", source => new WireSubtitles(source, stream, preserveContainerTimestamps),
            EngineWireContext.Default.WireSubtitles, EngineWireContext.Default.String, cancellationToken)).Payload
            ?? throw new InvalidDataException("The media worker returned no subtitles.");
        return BitmapSubtitleCodec.Read(document);
    }

    /// <summary>Decodes packets of a bitmap subtitle stream in the worker; they cross as one in-memory input.</summary>
    internal static async Task<BitmapSubtitleDocument> DecodeBitmapSubtitlesAsync(BitmapSubtitlePackets packets,
        CancellationToken cancellationToken)
    {
        var input = new MemoryMediaInput("subtitle packets", BitmapSubtitlePacketsCodec.Write(packets));
        byte[] document = (await RunAsync(input, "bitmapSubtitlePackets", source => new WireSubtitles(source, 0, false),
            EngineWireContext.Default.WireSubtitles, EngineWireContext.Default.String, cancellationToken)).Payload
            ?? throw new InvalidDataException("The media worker returned no subtitles.");
        return BitmapSubtitleCodec.Read(document);
    }

    /// <summary>Opens a photo for viewing. Its pictures are decoded or, when large, split into tiles decoded on request.</summary>
    internal static Task<BrowserPhoto?> OpenPhotoAsync(MediaInput input, CancellationToken cancellationToken,
        PhotoPreviewRequest? preview = null, bool? renderRaw = null, IProgress<Size>? displaySize = null, bool wholeImage = false,
        int thumbnailWidth = 0) =>
        BackgroundMediaJobs.RunAsync(async (worker, slot) =>
        {
            await using WorkerSource source = await WorkerSource.RegisterAsync(worker, input, readAhead: false, cancellationToken);
            using (cancellationToken.Register(source.Cancel))
            {
                try
                {
                    var (result, frames) = await worker.CallWithFramesAsync("photoOpen",
                        new WirePhotoRequest(source.Wire, preview)
                        { RenderRaw = renderRaw, WholeImage = wholeImage, ThumbnailWidth = thumbnailWidth },
                        EngineWireContext.Default.WirePhotoRequest, EngineWireContext.Default.WirePhotoResult);
                    if (result.DisplayWidth > 0 && result.DisplayHeight > 0 && !cancellationToken.IsCancellationRequested)
                        displaySize?.Report(new Size(result.DisplayWidth, result.DisplayHeight));
                    WirePhoto? photo = result.Photo;
                    if (photo is null)
                    {
                        foreach (BrowserFrameInfo frame in frames)
                            MediaWorkerConnection.DropFrame(frame);
                        return null;
                    }
                    return new BrowserPhoto(worker, slot, photo, frames);
                }
                catch (Exception exception) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Opening the photo was canceled.", exception, cancellationToken);
                }
            }
        }, cancellationToken, photo =>
        {
            if (photo is not null)
                _ = photo.DisposeAsync().AsTask();
        }, viewer: !wholeImage || thumbnailWidth > 0);

    private static Task<(TResult? Value, byte[]? Payload)> RunAsync<TArguments, TResult>(
        MediaInput input,
        string method,
        Func<WireSource, TArguments> arguments,
        JsonTypeInfo<TArguments> argumentType,
        JsonTypeInfo<TResult> resultType,
        CancellationToken cancellationToken,
        int operation = 0)
        where TResult : class =>
        RunAsync(input, method, arguments, argumentType, resultType,
            static (worker, method, arguments, argumentType, resultType) =>
                worker.CallWithPayloadAsync(method, arguments, argumentType, resultType),
            null, cancellationToken, operation);

    /// <summary>
    /// Registers <paramref name="input"/> with the shared worker and runs <paramref name="method"/> on it through
    /// <paramref name="call"/>. A result that arrives after the caller stopped waiting goes to <paramref name="discard"/>.
    /// </summary>
    private static Task<TOutput> RunAsync<TArguments, TResult, TOutput>(
        MediaInput input,
        string method,
        Func<WireSource, TArguments> arguments,
        JsonTypeInfo<TArguments> argumentType,
        JsonTypeInfo<TResult> resultType,
        Func<MediaWorkerConnection, string, TArguments, JsonTypeInfo<TArguments>, JsonTypeInfo<TResult>, Task<TOutput>> call,
        Action<TOutput>? discard,
        CancellationToken cancellationToken,
        int operation = 0)
        where TResult : class =>
        BackgroundMediaJobs.RunAsync(async (worker, _) =>
        {
            WorkerSource source = await WorkerSource.RegisterAsync(worker, input, readAhead: false, cancellationToken);
            TOutput result;
            try
            {
                using (cancellationToken.Register(source.Cancel))
                {
                    var pending = call(worker, method, arguments(source.Wire), argumentType, resultType);
                    using var registration = operation == 0 ? default : cancellationToken.Register(() => worker.CancelPhotoExport(operation));
                    result = await pending;
                }
            }
            catch (Exception exception)
            {
                await source.DisposeAsync();
                if (cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
                    throw new OperationCanceledException("The media read was canceled.", exception, cancellationToken);
                throw;
            }

            try
            {
                await source.DisposeAsync();
            }
            catch
            {
                discard?.Invoke(result);
                throw;
            }

            return result;
        }, cancellationToken, discard);
}

/// <summary>An upright BGRA picture a media worker decoded, in native memory until disposed.</summary>
[SupportedOSPlatform("browser")]
internal sealed class BrowserPicture(int width, int height, NativePayload pixels) : IDisposable
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public NativePayload Pixels { get; } = pixels;

    public void Dispose() => Pixels.Dispose();
}

/// <summary>
/// Bounded background lanes in the shared media worker. Each lane admits one operation at a time
/// and preserves ordering for photo sessions, including cancellation cleanup. Viewer photo sessions
/// have lanes of their own, so library work never queues them.
/// </summary>
[SupportedOSPlatform("browser")]
internal static class BackgroundMediaJobs
{
    internal const int Count = 2;
    private const int ViewerCount = 2;
    private static readonly SemaphoreSlim[] turns = [new(1, 1), new(1, 1), new(1, 1), new(1, 1)];
    private static readonly int[] demand = new int[Count + ViewerCount];

    /// <summary>Runs <paramref name="operation"/> on the lane with the least work in line.</summary>
    internal static Task<T> RunAsync<T>(Func<MediaWorkerConnection, Task<T>> operation, CancellationToken cancellationToken) =>
        RunAsync((worker, _) => operation(worker), cancellationToken);

    /// <inheritdoc cref="RunAsync{T}(Func{MediaWorkerConnection, Task{T}}, CancellationToken)"/>
    /// <remarks>The operation learns its lane, to continue there with <see cref="RunOnAsync"/>.</remarks>
    internal static Task<T> RunAsync<T>(Func<MediaWorkerConnection, int, Task<T>> operation, CancellationToken cancellationToken,
        Action<T>? discard = null, bool viewer = false)
    {
        int first = viewer ? Count : 0;
        int slot = first;
        for (int index = first + 1; index < first + (viewer ? ViewerCount : Count); index++)
        {
            if (demand[index] < demand[slot])
                slot = index;
        }

        return RunOnAsync(slot, operation, cancellationToken, discard);
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on lane <paramref name="slot"/>. Cancellation ends the wait
    /// for its result at once, while the lane stays taken until the operation has actually finished;
    /// a result nobody waits for any more goes to <paramref name="discard"/>.
    /// </summary>
    internal static async Task<T> RunOnAsync<T>(int slot, Func<MediaWorkerConnection, int, Task<T>> operation,
        CancellationToken cancellationToken, Action<T>? discard = null)
    {
        Task<T> running = RunInTurnAsync(slot, operation, cancellationToken);
        try
        {
            return await running.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveAsync(running, discard);
            throw;
        }
    }

    private static async Task<T> RunInTurnAsync<T>(int slot, Func<MediaWorkerConnection, int, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        demand[slot]++;
        try
        {
            await turns[slot].WaitAsync(cancellationToken);
            try
            {
                return await operation(await MediaWorkerConnection.GetAsync(), slot);
            }
            finally
            {
                turns[slot].Release();
            }
        }
        finally
        {
            demand[slot]--;
        }
    }

    private static async Task ObserveAsync<T>(Task<T> operation, Action<T>? discard)
    {
        try
        {
            T result = await operation;
            discard?.Invoke(result);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
        }
        catch (Exception exception)
        {
            AppLog.Warning("Web.Media", "A canceled background media operation failed.", exception);
        }
    }
}
