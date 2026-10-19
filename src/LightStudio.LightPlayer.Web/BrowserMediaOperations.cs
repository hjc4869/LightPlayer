using System.Runtime.Versioning;
using System.Text.Json.Serialization.Metadata;
using Avalonia;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;
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

    internal static async Task<WireMediaTags> ReadTagsAsync(MediaInput input, CancellationToken cancellationToken) =>
        (await RunAsync(input, "readTags", source => source, EngineWireContext.Default.WireSource,
            EngineWireContext.Default.WireMediaTags, cancellationToken)).Value
        ?? throw new InvalidDataException("The media worker returned no tags.");

    internal static async Task<(VideoProbeResult? Probe, byte[]? Preview)> ProbeVideoAsync(
        MediaInput input, bool withPreview, int maxDimension, CancellationToken cancellationToken) =>
        await RunAsync(input, "probeVideo", source => new WireProbeRequest(source, withPreview, maxDimension),
            EngineWireContext.Default.WireProbeRequest, EngineWireContext.Default.VideoProbeResult, cancellationToken);

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

    /// <summary>Encodes an upright picture that fits <paramref name="maxDimension"/> as JPEG. Null when there is none.</summary>
    internal static async Task<byte[]?> EncodeImageAsync(MediaInput input, int maxDimension, CancellationToken cancellationToken, bool? renderRaw = null)
    {
        var (picture, encoded) = await RunAsync(input, "image",
            source => new WireImageRequest(source, maxDimension, false, 0) { EncodeJpeg = true, RenderRaw = renderRaw },
            EngineWireContext.Default.WireImageRequest, EngineWireContext.Default.WirePicture, cancellationToken);
        return picture is null ? null : encoded ?? throw new InvalidDataException("The media worker returned no picture.");
    }

    internal static Task<string> ReadLicenseNoticeAsync() =>
        BackgroundMediaJobs.RunAsync(worker => worker.CallRawAsync("licenseNotice", string.Empty), CancellationToken.None);

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
        PhotoPreviewRequest? preview = null, bool? renderRaw = null, IProgress<Size>? displaySize = null) =>
        BackgroundMediaJobs.RunAsync(async (worker, slot) =>
        {
            await using WorkerSource source = await WorkerSource.RegisterAsync(worker, input, readAhead: false, cancellationToken);
            using (cancellationToken.Register(source.Cancel))
            {
                try
                {
                    var (result, frames) = await worker.CallWithFramesAsync("photoOpen", new WirePhotoRequest(source.Wire, preview) { RenderRaw = renderRaw },
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
        });

    private static Task<(TResult? Value, byte[]? Payload)> RunAsync<TArguments, TResult>(
        MediaInput input,
        string method,
        Func<WireSource, TArguments> arguments,
        JsonTypeInfo<TArguments> argumentType,
        JsonTypeInfo<TResult> resultType,
        CancellationToken cancellationToken)
        where TResult : class =>
        RunAsync(input, method, arguments, argumentType, resultType,
            static (worker, method, arguments, argumentType, resultType) =>
                worker.CallWithPayloadAsync(method, arguments, argumentType, resultType),
            null, cancellationToken);

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
        CancellationToken cancellationToken)
        where TResult : class =>
        BackgroundMediaJobs.RunAsync(async (worker, _) =>
        {
            WorkerSource source = await WorkerSource.RegisterAsync(worker, input, readAhead: false, cancellationToken);
            TOutput result;
            try
            {
                using (cancellationToken.Register(source.Cancel))
                    result = await call(worker, method, arguments(source.Wire), argumentType, resultType);
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
/// and preserves ordering for photo sessions, including cancellation cleanup.
/// </summary>
[SupportedOSPlatform("browser")]
internal static class BackgroundMediaJobs
{
    internal const int Count = 2;
    private static readonly SemaphoreSlim[] turns = [new(1, 1), new(1, 1)];
    private static readonly int[] demand = new int[Count];

    /// <summary>Runs <paramref name="operation"/> on the lane with the least work in line.</summary>
    internal static Task<T> RunAsync<T>(Func<MediaWorkerConnection, Task<T>> operation, CancellationToken cancellationToken) =>
        RunAsync((worker, _) => operation(worker), cancellationToken);

    /// <inheritdoc cref="RunAsync{T}(Func{MediaWorkerConnection, Task{T}}, CancellationToken)"/>
    /// <remarks>The operation learns its lane, to continue there with <see cref="RunOnAsync"/>.</remarks>
    internal static Task<T> RunAsync<T>(Func<MediaWorkerConnection, int, Task<T>> operation, CancellationToken cancellationToken,
        Action<T>? discard = null)
    {
        int slot = 0;
        for (int index = 1; index < Count; index++)
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
