using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.WebWorkers;

/// <summary>
/// One-shot reads background jobs run for the library and the artwork views. Each opens its
/// source, does its work synchronously and closes the source again.
/// </summary>
[SupportedOSPlatform("browser")]
internal static class WorkerOperations
{
    private const int WindowBytes = 256 * 1024;
    private const int Windows = 4;

    public static string ReadTags(WireSource source)
    {
        FfmpegMediaInfo info = FfmpegCodec.GetMediaInfoFromStream(Open(source));
        return JsonSerializer.Serialize(new WireMediaTags(info.Duration.Ticks, info.AllProperties.ToArray()),
            EngineWireContext.Default.WireMediaTags);
    }

    public static async Task<string> ProbeVideoAsync(WireProbeRequest request, CancellationToken cancellationToken)
    {
        using Stream source = Open(request.Source);
        cancellationToken.ThrowIfCancellationRequested();
        VideoProbeResult? probe = VideoProbe.Probe(source, request.WithPreview, request.MaxDimension, leaveOpen: true,
            retainPreviewFrame: true);
        string value = probe is null ? "null" : JsonSerializer.Serialize(probe, EngineWireContext.Default.VideoProbeResult);
        if (probe?.PreviewFrame is { } frame)
        {
            try
            {
                try
                {
                    byte[] encoded = await PhotoThumbnailEncoder.EncodeAsync(frame.AddRef(), request.MaxDimension, cancellationToken);
                    return Payloads.Envelope(value, Payloads.Copy(encoded));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Warning("Preview", "Could not encode a color-managed video preview; encoding it in sRGB.", exception);
                }

                using NativeImageThumbnail thumbnail = StillImage.RenderThumbnail(frame, request.MaxDimension, cancellationToken);
                return Payloads.Envelope(value, thumbnail.TakePixels(), thumbnail.Width, thumbnail.Height, "jpeg");
            }
            finally { frame.Release(); }
        }
        return probe?.PreviewBgra is { } pixels
            ? Payloads.Envelope(value, pixels, probe.PreviewWidth, probe.PreviewHeight, "jpeg")
            : Payloads.Envelope(value);
    }

    public static string ProbeAudioStreams(WireSource source) =>
        JsonSerializer.Serialize(VideoProbe.ProbeAudioStreams(Open(source), leaveOpen: false),
            EngineWireContext.Default.IReadOnlyListVideoProbeAudioStreamInfo);

    public static string ProbeImage(WireSource source)
    {
        using Stream stream = Open(source);
        ExifPhotoMetadata exif = ExifMetadata.ReadPhoto(stream);
        stream.Position = 0;
        (int width, int height) = StillImage.ReadPixelSize(stream);
        return JsonSerializer.Serialize(new WireImageProbe(width > 0 ? width : Math.Max(0, exif.Width),
            height > 0 ? height : Math.Max(0, exif.Height), exif.DateTaken, exif.Latitude, exif.Longitude),
            EngineWireContext.Default.WireImageProbe);
    }

    public static string Inspect(WireSource source)
    {
        MediaInspection? inspection = MediaInspector.Inspect(Open(source), leaveOpen: false);
        return inspection is null ? "null" : JsonSerializer.Serialize(inspection, EngineWireContext.Default.MediaInspection);
    }

    public static string ReadAlbumCover(WireSource source)
    {
        NativeBuffer? cover = FfmpegCodec.ReadAlbumCoverFromStream(Open(source), Payloads.Copy);
        return cover is null ? Payloads.Envelope("null") : Payloads.Envelope("null", cover);
    }

    public static string DecodeImage(WireImageRequest request)
    {
        if (!request.Embedded && request.MaxEncodedBytes > 0 && request.Source.Length > request.MaxEncodedBytes)
            throw new InvalidDataException("The picture exceeds the encoded byte budget.");

        using Stream source = Open(request.Source);
        Stream picture = source;
        using NativeBuffer? cover = request.Embedded
            ? FfmpegCodec.ReadAlbumCoverFromStream(source, Payloads.Copy, close: false) : null;
        if (request.Embedded)
        {
            if (cover is null)
                return Payloads.Envelope("null");
            picture = cover.OpenRead();
        }

        using (picture == source ? null : picture)
        {
            using NativeImageThumbnail? thumbnail = (request.RenderRaw == false
                ? PhotoDecoder.DecodeNativeDngThumbnail(picture, request.MaxDimension, default) : null) ?? (PhotoDecoder.IsJxr(picture)
                ? PhotoDecoder.DecodeJxrThumbnail(picture, request.MaxDimension, default)
                : StillImage.DecodeRasterThumbnail(picture, request.MaxDimension)
                    ?? StillImage.DecodeNativeThumbnail(picture, request.MaxDimension, leaveOpen: true));
            if (thumbnail is null)
                return Payloads.Envelope("null");
            if (request.BlurSigma > 0)
            {
                PictureBlur.Apply(thumbnail.Pixels.GetSpan(), thumbnail.Width, thumbnail.Height,
                    request.BlurSigma * thumbnail.Width / Math.Max(1, request.MaxDimension));
            }

            return Payloads.Envelope(JsonSerializer.Serialize(new WirePicture(thumbnail.Width, thumbnail.Height),
                EngineWireContext.Default.WirePicture), thumbnail.TakePixels(), thumbnail.Width, thumbnail.Height);
        }
    }

    private static SourceBridgeContext Open(WireSource source) =>
        new(source.Id, (long)source.Length, WindowBytes, Windows, 0);
}

/// <summary>
/// Bytes a result carries outside its JSON. Their native storage belongs to the registry until the
/// worker script has copied them out.
/// </summary>
internal static class Payloads
{
    private static readonly ConcurrentDictionary<int, NativeBuffer> buffers = [];
    private static int nextHandle;
    internal static int Count => buffers.Count;

    /// <summary>Wraps a result that carries no bytes.</summary>
    public static string Envelope(string value) => $"{{\"value\":{value}}}";

    /// <summary>
    /// Wraps a result with <paramref name="bytes"/>. A BGRA picture of the given size may be asked to
    /// be <paramref name="encode"/>d by the worker script, which has the browser's image encoders.
    /// </summary>
    public static string Envelope(string value, byte[] bytes, int width = 0, int height = 0, string? encode = null)
        => Envelope(value, Copy(bytes), width, height, encode);

    public static NativeBuffer Copy(ReadOnlySpan<byte> bytes)
    {
        var buffer = new NativeBuffer(bytes.Length);
        bytes.CopyTo(buffer.GetSpan());
        return buffer;
    }

    public static string Envelope(string value, NativeBuffer bytes, int width = 0, int height = 0, string? encode = null)
    {
        int handle = 0;
        bool registered = false;
        try
        {
            do
                handle = Interlocked.Increment(ref nextHandle) & int.MaxValue;
            while (handle == 0 || !buffers.TryAdd(handle, bytes));
            registered = true;
            var json = new StringBuilder(value.Length + 128);
            json.Append("{\"value\":").Append(value)
                .Append(",\"payload\":{\"handle\":").Append(handle)
                .Append(",\"address\":").Append((ulong)(nuint)bytes.Address)
                .Append(",\"length\":").Append(bytes.Length)
                .Append(",\"width\":").Append(width)
                .Append(",\"height\":").Append(height);
            if (encode is not null)
                json.Append(",\"encode\":\"").Append(encode).Append('"');
            return json.Append("}}").ToString();
        }
        catch
        {
            if (registered)
                Release(handle);
            else
                bytes.Dispose();
            throw;
        }
    }

    public static void Release(int handle)
    {
        if (buffers.TryRemove(handle, out NativeBuffer? buffer))
            buffer.Dispose();
    }
}
