using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Shaders;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using SkiaSharp;

namespace LightStudio.LightPlayer.WebWorkers;

/// <summary>
/// Photos the page views, opened in background jobs. Small pictures are decoded at once; large ones
/// stay as encoded tiles that the page asks for as it draws them.
/// </summary>
[SupportedOSPlatform("browser")]
internal static class WorkerPhotos
{
    private const int WindowBytes = 1024 * 1024;
    private const int Windows = 6;
    private const int Prefetch = 2;

    private static readonly ConcurrentDictionary<int, Session> sessions = [];
    private static int nextSession;
    internal static int Count => sessions.Count;

    public static string Open(WirePhotoRequest request)
    {
        var frames = new List<VideoFrame>();
        var session = new Session();
        int id = 0;
        double displayWidth = 0;
        double displayHeight = 0;
        try
        {
            WirePhoto photo;
            using (var stream = new SourceBridgeContext(request.Source.Id, (long)request.Source.Length, WindowBytes, Windows, Prefetch))
            {
                if (request.Preview is { } previewRequest)
                {
                    try
                    {
                        (displayWidth, displayHeight) = StillImage.ReadDisplaySize(stream);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        AppLog.Warning("Photo", $"Could not read image dimensions for {request.Source.Name}.", exception);
                    }
                    EmbeddedPhotoPreview? preview = PhotoDecoder.FindEmbeddedPreview(stream, previewRequest, default);
                    if (preview is null)
                        return WritePhotoResult(null);
                    using var encoded = preview.Encoded is { } data ? new MemoryStream(data, writable: false) : null;
                    photo = preview.Reduction > 1
                        ? new WirePhoto(0, AddFrame(PhotoDecoder.DecodeJxrPicture(encoded!, default, preview.Reduction), frames),
                            null, null, null, null, 0)
                        : OpenPhoto(encoded ?? (Stream)stream, frames, session, preview.Item, useTiles: false);
                    photo = photo with { Base = Orient(photo.Base, preview.Rotation, preview.Mirror, frames) };
                    double width = photo.Base.Width * photo.Base.PixelAspect;
                    bool better = photo.Base.Rotation % 180 == 0 ? previewRequest.IsBetter(width, photo.Base.Height)
                        : previewRequest.IsBetter(photo.Base.Height, width);
                    if (!better)
                    {
                        foreach (VideoFrame frame in frames)
                            frame.Release();
                        frames.Clear();
                        session.Dispose();
                        return WritePhotoResult(null);
                    }
                }
                else if (string.Equals(Path.GetExtension(request.Source.Name), ".tga", StringComparison.OrdinalIgnoreCase))
                {
                    using StillImageNative native = StillImage.DecodeNative(stream, null, default,
                        LightStudio.NativeBindings.Types.AVCodecID.AV_CODEC_ID_TARGA)
                        ?? throw new InvalidDataException("The photo could not be decoded.");
                    photo = new WirePhoto(0, AddNativeFrame(native, frames), null, null, null, null, 0);
                }
                else
                    photo = (request.RenderRaw == true ? null : OpenPreview(stream, frames, session, request.RenderRaw == false))
                        ?? OpenPhoto(stream, frames, session);
                if (photo.GainMap is null && session.Animation is null)
                    photo = photo with { PeakLuminance = MeasurePeak(photo, frames, session) };
            }

            if (session.Base is not null || session.Gain is not null || session.Animation is not null)
            {
                do
                    id = Interlocked.Increment(ref nextSession) & int.MaxValue;
                while (id == 0 || !sessions.TryAdd(id, session));
            }

            return WritePhotoResult(photo with { Session = id });
        }
        catch
        {
            if (id != 0)
                sessions.TryRemove(id, out _);
            foreach (VideoFrame frame in frames)
                frame.Release();
            session.Dispose();
            throw;
        }

        string WritePhotoResult(WirePhoto? photo) => WriteResult(JsonSerializer.Serialize(new WirePhotoResult(photo)
        {
            DisplayWidth = displayWidth,
            DisplayHeight = displayHeight,
        }, EngineWireContext.Default.WirePhotoResult), frames);
    }

    private static double? MeasurePeak(WirePhoto photo, List<VideoFrame> frames, Session session)
    {
        WirePhotoImage image = photo.Base;
        using var space = PhotoColorProfile.CreateColorSpace(photo.BaseProfile ?? image.IccProfile,
            image.Primaries, image.Transfer is 0 or 2 ? 13 : image.Transfer);
        PhotoColorProfile? profile = image.Color?.ToProfile() ?? (space is null ? null : PhotoColorProfile.Create(space));
        bool pq = profile is not null ? profile.TransferCode == 16 : image.Transfer == 16;
        if (!pq)
            return null;
        if (session.Base is not { } tiles)
            return MeasureFrame(frames[image.Frame]);
        double peak = 0;
        for (int index = 0; index < tiles.Count; index++)
        {
            VideoFrame[] decoded = tiles.DecodeBatch(new[] { new PhotoTileRequest(index, 1) }, default);
            try
            {
                PhotoTileBounds bounds = tiles.GetBounds(index);
                var region = SKRectI.Create(-bounds.X, -bounds.Y, image.Width, image.Height);
                foreach (VideoFrame frame in decoded)
                    peak = Math.Max(peak, MeasureFrame(frame, region));
            }
            finally
            {
                foreach (VideoFrame frame in decoded)
                    frame.Release();
            }
        }
        return peak;

        double MeasureFrame(VideoFrame frame, SKRectI? region = null)
        {
            if (profile is not null)
            {
                frame.PhotoColor = profile;
                frame.ColorPrimaries = VideoColorPrimaries.Bt2020;
            }
            frame.ColorTransfer = VideoColorTransfer.Pq;
            return VideoFramePipeline.MeasurePhotoPeak(frame, sourceBounds: region);
        }
    }

    public static string DecodeTiles(WirePhotoTiles request)
    {
        Session session = sessions.TryGetValue(request.Session, out Session? open) ? open
            : throw new InvalidOperationException($"Photo {request.Session} is closed.");
        lock (session)
        {
            StillImage.TileSource tiles = (request.Gain ? session.Gain : session.Base)
                ?? throw new InvalidOperationException("The photo has no such tiled picture.");
            VideoFrame[] frames = tiles.DecodeBatch(request.Requests.ToArray(), default);
            try { return WriteResult("true", frames); }
            catch
            {
                foreach (VideoFrame frame in frames)
                    frame.Release();
                throw;
            }
        }
    }

    public static string ReadPackets(WirePhotoTiles request)
    {
        Session session = sessions.TryGetValue(request.Session, out Session? open) ? open
            : throw new InvalidOperationException($"Photo {request.Session} is closed.");
        lock (session)
        {
            StillImage.TileSource tiles = (request.Gain ? session.Gain : session.Base)
                ?? throw new InvalidOperationException("The photo has no such tiled picture.");
            if (request.Requests.Count is < 1 or > 32)
                throw new ArgumentOutOfRangeException(nameof(request));
            if (!CanUseWebCodecs(tiles))
                return Payloads.Envelope("null");
            var packets = new List<WirePhotoPacket>(request.Requests.Count);
            int length = 0;
            foreach (PhotoTileRequest tile in request.Requests)
            {
                if ((uint)tile.Index >= (uint)tiles.Count || tile.Reduction < 1)
                    throw new ArgumentOutOfRangeException(nameof(request));
                CodecParameters codec = tiles.GetCodecParameters(tile.Index);
                if (codec.CodecName is not ("hevc" or "av1") || codec.BitDepth != 8 || codec.DolbyVisionProfile != 0)
                    return Payloads.Envelope("null");
                int size = tiles.GetEncodedPacketSize(tile.Index);
                if (size <= 0 || size > 64 * 1024 * 1024 - length)
                    return Payloads.Envelope("null");
                var stream = new WireVideoStream(0, codec.CodecName, codec.Extradata, codec.Width, codec.Height,
                    codec.Profile, codec.BitDepth, codec.ColorPrimaries, codec.ColorTransfer, codec.ColorSpace,
                    codec.ColorRange, codec.ChromaLocation, 0, 1, 0, 0, false, 0, 0, 0, 0);
                packets.Add(new WirePhotoPacket(stream, length, size));
                length += size;
            }
            var buffer = new NativeBuffer(length);
            try
            {
                for (int index = 0; index < packets.Count; index++)
                {
                    WirePhotoPacket packet = packets[index];
                    tiles.CopyEncodedPacket(request.Requests[index].Index, buffer.GetSpan().Slice(packet.Offset, packet.Length));
                }
                return Payloads.Envelope(JsonSerializer.Serialize(new WirePhotoPackets(packets),
                    EngineWireContext.Default.WirePhotoPackets), buffer);
            }
            catch { buffer.Dispose(); throw; }
        }
    }

    public static void Close(int session)
    {
        if (sessions.TryRemove(session, out Session? open))
        {
            lock (open)
            {
                open.Dispose();
                open.Base = open.Gain = null;
            }
        }
    }

    public static int OpenAnimation(int id)
    {
        Session session = sessions.TryGetValue(id, out Session? open) ? open
            : throw new ObjectDisposedException(nameof(Session));
        lock (session)
        {
            if (session.Animations.Count >= 4)
                throw new InvalidOperationException("Too many concurrent playbacks of this animated image.");
            StillImage.AnimationSource source = session.Animation ?? throw new ObjectDisposedException(nameof(Session));
            int playback;
            do { playback = ++session.NextAnimation & int.MaxValue; }
            while (playback == 0 || session.Animations.ContainsKey(playback));
            StillImage.AnimationDecoder decoder = source.CreateDecoder();
            try { session.Animations.Add(playback, decoder); }
            catch { decoder.Dispose(); throw; }
            return playback;
        }
    }

    public static string ReadAnimation(WirePhotoAnimation request)
    {
        Session session = sessions.TryGetValue(request.Session, out Session? open) ? open
            : throw new ObjectDisposedException(nameof(Session));
        lock (session)
        {
            StillImage.AnimationDecoder decoder = session.Animations.TryGetValue(request.Playback, out var active) ? active
                : throw new ObjectDisposedException(nameof(StillImage.AnimationDecoder));
            using StillImageNative? image = decoder.Read();
            var frames = new List<VideoFrame>(1);
            try
            {
                var result = new WirePhotoAnimationResult(image is null ? null : AddNativeFrame(image, frames));
                return WriteResult(JsonSerializer.Serialize(result, EngineWireContext.Default.WirePhotoAnimationResult), frames);
            }
            catch
            {
                foreach (VideoFrame frame in frames)
                    frame.Release();
                throw;
            }
        }
    }

    public static void CloseAnimation(WirePhotoAnimation request)
    {
        if (!sessions.TryGetValue(request.Session, out Session? session))
            return;
        lock (session)
            if (session.Animations.Remove(request.Playback, out var decoder))
                decoder.Dispose();
    }

    private static WirePhoto OpenPhoto(Stream stream, List<VideoFrame> frames, Session session, uint? item = null,
        bool useTiles = true)
    {
        if (useTiles && item is null && StillImage.OpenAnimation(stream, out StillImageNative first) is { } animation)
        {
            session.Animation = animation;
            using (first)
                return new WirePhoto(0, AddNativeFrame(first, frames), null, null, null, null, 0)
                    { AnimationBytes = animation.EncodedBytes };
        }
        PhotoGainMapLocation? gain = PhotoDecoder.FindGainMap(stream, default, item);
        WirePhotoImage image = OpenImage(stream, gain?.BaseItem ?? item, gain?.BaseRange, frames, out session.Base, useTiles);
        WirePhotoImage? gainImage = gain is null ? null : OpenGain(stream, gain, frames, out session.Gain, useTiles);
        return new WirePhoto(0, image, gainImage, gainImage is null ? null : gain!.Parameters,
            gain?.BaseProfile, gain?.MathProfile, gain?.MathPrimaries ?? 0);
    }

    private static WirePhoto? OpenPreview(Stream stream, List<VideoFrame> frames, Session session, bool includeReduced)
    {
        long origin = stream.Position;
        foreach (DngPreview preview in PhotoDecoder.FindDngPreviews(stream, default, includeReduced))
        {
            int count = frames.Count;
            try
            {
                WirePhoto photo = OpenPhoto(new SubStream(stream, preview.Offset, preview.Length), frames, session);
                if (photo.Base.Width != preview.Width || photo.Base.Height != preview.Height)
                    throw new InvalidDataException("The embedded DNG preview is not the size its directory gives.");
                return photo with { Base = Orient(photo.Base, preview.Orientation, frames), IsEmbeddedRawPreview = true };
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or EndOfStreamException)
            {
                AppLog.Warning("Worker.Photo", "Could not decode the embedded DNG preview.", exception);
                foreach (VideoFrame frame in frames.Skip(count))
                    frame.Release();
                frames.RemoveRange(count, frames.Count - count);
                session.Dispose();
                session.Base = session.Gain = null;
            }
            finally { stream.Position = origin; }
        }
        return null;
    }

    // The DNG orientation applies when the preview carries none of its own.
    private static WirePhotoImage Orient(WirePhotoImage image, uint orientation, List<VideoFrame> frames)
    {
        if (!ExifOrientation.TryDecode((int)orientation, out int rotation, out bool mirror))
            return image;
        return Orient(image, rotation, mirror, frames);
    }

    private static WirePhotoImage Orient(WirePhotoImage image, int rotation, bool mirror, List<VideoFrame> frames)
    {
        if (image.Rotation != 0 || image.Mirror)
            return image;
        if (image.Frame >= 0)
        {
            frames[image.Frame].Rotation = rotation;
            frames[image.Frame].MirrorHorizontally = mirror;
        }
        return image with { Rotation = rotation, Mirror = mirror };
    }

    private static WirePhotoImage OpenImage(Stream stream, uint? item, (long Offset, int Length)? range, List<VideoFrame> frames,
        out StillImage.TileSource? tiles, bool useTiles = true)
    {
        tiles = null;
        if (range is { } part)
            return OpenImage(new SubStream(stream, part.Offset, part.Length), null, null, frames, out tiles, useTiles);

        Span<byte> signature = stackalloc byte[12];
        long origin = stream.Position;
        int read = stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
        stream.Position = origin;
        if (useTiles && read >= 8 && (signature.Slice(4, 4).SequenceEqual("ftyp"u8) || signature.Slice(4, 4).SequenceEqual("jP  "u8) ||
            signature.StartsWith((ReadOnlySpan<byte>)[0xff, 0xd8]) ||
            signature.StartsWith((ReadOnlySpan<byte>)[0xff, 0x4f, 0xff, 0x51])) &&
            StillImage.OpenTiles(stream, item, default, includeSingleImages: true) is { } opened)
        {
            try
            {
                using var managed = signature.StartsWith((ReadOnlySpan<byte>)[0xff, 0xd8]) ? new SKManagedStream(stream, false) : null;
                using var codec = managed is null ? null : SKCodec.Create(managed);
                using var space = codec?.Info.ColorSpace ??
                    PhotoColorProfile.CreateColorSpace(opened.IccProfile, opened.ColorPrimaries, opened.ColorTransfer);
                stream.Position = origin;
                if (space is not null && PhotoColorProfile.Create(space) is { } profile)
                {
                    WirePhotoImage image = Describe(opened) with { Color = WirePhotoColor.From(profile) };
                    tiles = opened;
                    return image;
                }
            }
            finally
            {
                if (tiles is null)
                    opened.Dispose();
            }
        }

        stream.Position = origin;
        if (read >= 4 && JxrDecoder.IsJxr(signature[..4]))
            return AddFrame(PhotoDecoder.DecodeJxrPicture(stream, default), frames);
        if (read >= 4 && (signature[..3].SequenceEqual("GIF"u8) || signature[..2].SequenceEqual("BM"u8) ||
            read >= 12 && signature[..4].SequenceEqual("RIFF"u8) && signature.Slice(8, 4).SequenceEqual("WEBP"u8)))
        {
            using StillImageNative raster = StillImage.DecodeRaster(stream)
                ?? throw new InvalidDataException("The photo could not be decoded.");
            return AddNativeFrame(raster, frames);
        }
        // TIFF, and with it DNG, is developed by LibRaw into a picture that is already color managed.
        if (read >= 4 && (signature.StartsWith("II"u8) || signature.StartsWith("MM"u8)))
        {
            return AddFrame(StillImage.Decode(stream, leaveOpen: true, default)
                ?? throw new InvalidDataException("The photo could not be decoded."), frames);
        }

        return DecodeNative(stream, item, frames);
    }

    private static WirePhotoImage AddFrame(VideoFrame frame, List<VideoFrame> frames)
    {
        frames.Add(frame);
        return new WirePhotoImage(frame.Width, frame.Height, frame.Rotation, frame.MirrorHorizontally,
            frame.PixelAspectRatio, [], (int)PrimariesCode(frame.ColorPrimaries), (int)TransferCode(frame.ColorTransfer),
            frames.Count - 1, null, 0) { HasAlpha = frame.HasAlpha };
    }

    private static WirePhotoImage OpenGain(Stream stream, PhotoGainMapLocation gain, List<VideoFrame> frames,
        out StillImage.TileSource? tiles, bool useTiles)
    {
        tiles = null;
        if (useTiles && gain.GainItem is { } item && StillImage.OpenTiles(stream, item, default, includeSingleImages: true) is { } opened)
        {
            tiles = opened;
            return Describe(opened);
        }

        if (gain.EncodedGain is { } encoded)
            return DecodeNative(new MemoryStream(encoded, writable: false), null, frames);
        if (gain.GainRange is { } range)
            return OpenImage(new SubStream(stream, range.Offset, range.Length), null, null, frames, out tiles, useTiles);
        return DecodeNative(stream, gain.GainItem, frames);
    }

    private static WirePhotoImage DecodeNative(Stream stream, uint? item, List<VideoFrame> frames)
    {
        using StillImageNative native = StillImage.DecodeNative(stream, item, default)
            ?? throw new InvalidDataException("The photo could not be decoded.");
        return AddNativeFrame(native, frames);
    }

    private static WirePhotoImage AddNativeFrame(StillImageNative native, List<VideoFrame> frames)
    {
        VideoFrame frame = native.Frame.AddRef();
        frames.Add(frame);
        return new WirePhotoImage(frame.Width, frame.Height, frame.Rotation, frame.MirrorHorizontally, frame.PixelAspectRatio,
            native.IccProfile, native.ColorPrimaries, native.ColorTransfer, frames.Count - 1, null, 0)
            { HasAlpha = frame.HasAlpha, Color = WirePhotoColor.From(frame.PhotoColor) };
    }

    private static bool CanUseWebCodecs(StillImage.TileSource tiles) =>
        (long)tiles.Width * tiles.Height >= 1_000_000 &&
        !(AppContext.TryGetSwitch("LightStudio.FfmpegShim.DisablePhotoHardwareDecoding", out bool disabled) && disabled) &&
        tiles.GetCodecParameters(0) is { CodecName: "hevc" or "av1", BitDepth: 8, DolbyVisionProfile: 0 };

    private static WirePhotoImage Describe(StillImage.TileSource tiles) => new(tiles.Width, tiles.Height, tiles.Rotation,
        tiles.MirrorHorizontally, tiles.PixelAspectRatio, tiles.IccProfile, tiles.ColorPrimaries, tiles.ColorTransfer, -1,
        Enumerable.Range(0, tiles.Count).Select(tiles.GetBounds).ToArray(), tiles.EncodedBytes)
        { HasScaledOverview = tiles.HasScaledOverview, CanUseWebCodecs = CanUseWebCodecs(tiles) };

    // Frames follow the result in the form the page imports, and stay in this worker until it releases them.
    private static string WriteResult(string value, IReadOnlyList<VideoFrame> frames)
    {
        var registered = new List<int>(frames.Count);
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("value");
                writer.WriteRawValue(value);
                writer.WriteStartArray("frames");
                foreach (VideoFrame frame in frames)
                {
                    frame.AddRef();
                    int id;
                    try { id = FrameRegistry.Add(frame); }
                    catch { frame.Release(); throw; }
                    registered.Add(id);
                    writer.WriteStartObject();
                    writer.WriteNumber("kind", 1);
                    writer.WriteNumber("id", id);
                    FrameBatch.WriteVideo(writer, frame);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            string result = System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            foreach (VideoFrame frame in frames)
                frame.Release();
            return result;
        }
        catch
        {
            foreach (int id in registered)
                FrameRegistry.Release(id);
            throw;
        }
    }

    private static LightStudio.NativeBindings.Types.AVColorPrimaries PrimariesCode(VideoColorPrimaries primaries) => primaries switch
    {
        VideoColorPrimaries.Bt2020 => LightStudio.NativeBindings.Types.AVColorPrimaries.AVCOL_PRI_BT2020,
        VideoColorPrimaries.DisplayP3 => LightStudio.NativeBindings.Types.AVColorPrimaries.AVCOL_PRI_SMPTE432,
        _ => LightStudio.NativeBindings.Types.AVColorPrimaries.AVCOL_PRI_BT709,
    };

    private static LightStudio.NativeBindings.Types.AVColorTransferCharacteristic TransferCode(VideoColorTransfer transfer) => transfer switch
    {
        VideoColorTransfer.Pq => LightStudio.NativeBindings.Types.AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084,
        VideoColorTransfer.Hlg => LightStudio.NativeBindings.Types.AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67,
        VideoColorTransfer.Linear => LightStudio.NativeBindings.Types.AVColorTransferCharacteristic.AVCOL_TRC_LINEAR,
        _ => LightStudio.NativeBindings.Types.AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1,
    };

    private sealed class Session : IDisposable
    {
        public StillImage.TileSource? Base;
        public StillImage.TileSource? Gain;
        public StillImage.AnimationSource? Animation;
        public readonly Dictionary<int, StillImage.AnimationDecoder> Animations = [];
        public int NextAnimation;

        public void Dispose()
        {
            foreach (var decoder in Animations.Values)
                decoder.Dispose();
            Animations.Clear();
            Animation?.Dispose();
            Animation = null;
            Base?.Dispose();
            Gain?.Dispose();
        }
    }
}
