#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.ImageEncoding;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.FfmpegShim.Engine;

// Messages between a page and the media worker that hosts an engine. Both ends compile these
// types, so the JSON shapes stay in step.

/// <summary>A source the worker script registered: a file, a URL, bytes or a page stream.</summary>
public sealed record WireSource(int Id, string Name, double Length)
{
    /// <summary>Reads ahead in large windows for playback rather than small ones for a probe.</summary>
    public bool ReadAhead { get; init; }
}

public sealed record WireOpenResult(
    int Demuxer,
    MediaInfo Info,
    int VideoStream,
    int AudioStream,
    bool CanSeek,
    IReadOnlyList<VideoRendition> Renditions);

public sealed record WireVideoDecoderRequest(
    int Demuxer,
    int ThreadCount,
    bool Hardware,
    string SoftwareFallbackReason,
    int FramePoolSize,
    long FramePoolByteCapacity,
    int PacketCapacity,
    long PacketByteCapacity)
{
    /// <summary>The WebCodecs configuration the worker script settled on, or null for FFmpeg.</summary>
    public string? WebCodecs { get; init; }

    /// <summary>Fails instead of using FFmpeg when WebCodecs cannot decode the stream.</summary>
    public bool RequireWebCodecs { get; init; }
}

public sealed record WireAudioDecoderRequest(
    int Demuxer,
    int Stream,
    AudioStreamFormat Format,
    double Speed,
    int PacketCapacity,
    long PacketByteCapacity);

public sealed record WireDecoderResult(int Decoder, int Stream, string Codec, VideoDecoderDiagnostics Diagnostics, AudioStreamFormat Format);

/// <summary>What the worker script needs to configure WebCodecs for a video stream.</summary>
public sealed record WireVideoStream(
    int Stream,
    string Codec,
    byte[] Extra,
    int Width,
    int Height,
    int Profile,
    int Bits,
    int Primaries,
    int Transfer,
    int Matrix,
    int Range,
    int Chroma,
    int FieldOrder,
    double PixelAspect,
    double FrameRate,
    int Rotation,
    bool Mirror,
    int DolbyVisionProfile,
    double Peak,
    double MinimumLuminance,
    double MaximumLuminance);

public sealed record WireLink(int Demuxer, int Stream, int Decoder);

/// <summary>Connects a decoder's frames to the page, which grants frames against <paramref name="Capacity"/>.</summary>
public sealed record WireOutput(int Decoder, int Capacity);

public sealed record WireSeek(int Demuxer, SeekRequest Request);

public sealed record WireRendition(int Demuxer, int Video, int Audio);

public sealed record WireRecover(int Decoder, VideoDecoderRecovery Recovery, string Reason)
{
    /// <summary>Frames the worker script already handed over for this decoder, set by the script.</summary>
    public long Sent { get; init; }
}

public sealed record WireSubtitles(WireSource Source, int Stream, bool PreserveContainerTimestamps);

/// <summary>A one-shot probe of a source; <paramref name="MaxDimension"/> bounds the preview picture.</summary>
public sealed record WireProbeRequest(WireSource Source, bool WithPreview, int MaxDimension)
{
    public int Operation { get; init; }
}

/// <summary>
/// Decodes a picture to fit <paramref name="MaxDimension"/>, or with <paramref name="Embedded"/> the artwork a
/// media file carries. A source larger than <paramref name="MaxEncodedBytes"/> is refused when that is set.
/// </summary>
public sealed record WireImageRequest(WireSource Source, int MaxDimension, bool Embedded, long MaxEncodedBytes)
{
    /// <summary>
    /// Standard deviation of a Gaussian blur, in pixels of a picture <see cref="MaxDimension"/> wide, applied to the
    /// result; zero for none. A blurred picture is sent premultiplied.
    /// </summary>
    public float BlurSigma { get; init; }

    public bool? RenderRaw { get; init; }
    public int Operation { get; init; }
}

/// <summary>An upright, tightly packed BGRA picture, sent along with the result.</summary>
public sealed record WirePicture(int Width, int Height);

public sealed record WireImageProbe(int Width, int Height, string? DateTaken,
    double? Latitude = null, double? Longitude = null);

/// <summary>The tags and duration the library index reads from a media file.</summary>
public sealed record WireMediaTags(long DurationTicks, IReadOnlyList<KeyValuePair<string, string>> Properties);

public sealed record WirePhotoRequest(WireSource Source, PhotoPreviewRequest? Preview = null)
{
    public bool? RenderRaw { get; init; }
    public bool WholeImage { get; init; }
    public int ThumbnailWidth { get; init; }
}
public sealed record WirePhotoResult(WirePhoto? Photo)
{
    public double DisplayWidth { get; init; }
    public double DisplayHeight { get; init; }
}

/// <summary>
/// One picture of a photo. It is either decoded already, as the result's frame at <paramref name="Frame"/>, or
/// split into <paramref name="Tiles"/> that the worker decodes on request.
/// </summary>
public sealed record WirePhotoImage(
    int Width,
    int Height,
    int Rotation,
    bool Mirror,
    double PixelAspect,
    byte[] IccProfile,
    int Primaries,
    int Transfer,
    int Frame,
    IReadOnlyList<PhotoTileBounds>? Tiles,
    long EncodedBytes)
{
    public bool HasScaledOverview { get; init; }
    public bool HasAlpha { get; init; }
    public bool CanUseWebCodecs { get; init; }
    public WirePhotoColor? Color { get; init; }
}

public sealed record WirePhotoColor(float[] Transfer, float[] Matrix, int TransferCode, float LinearScale, bool HasAlpha)
{
    public PhotoColorProfile ToProfile() => new(Transfer, Matrix, TransferCode, LinearScale, HasAlpha);
    public static WirePhotoColor? From(PhotoColorProfile? profile) => profile is null ? null :
        new(profile.Transfer.ToArray(), profile.ToBt2020.ToArray(), profile.TransferCode, profile.LinearScale, profile.HasAlpha);
}

public sealed record WireGainMap(
    float[] Minimum,
    float[] Maximum,
    float[] Gamma,
    float[] OffsetSdr,
    float[] OffsetHdr,
    float CapacityMin,
    float CapacityMax,
    bool BaseIsHdr,
    bool UseBaseColorSpace,
    bool IsApple);

/// <summary>A photo the worker holds open as <paramref name="Session"/> until the page closes it.</summary>
public sealed record WirePhoto(
    int Session,
    WirePhotoImage Base,
    WirePhotoImage? Gain,
    WireGainMap? GainMap,
    byte[]? BaseProfile,
    byte[]? MathProfile,
    int MathPrimaries)
{
    public bool IsEmbeddedRawPreview { get; init; }
    public long AnimationBytes { get; init; }
    public double? PeakLuminance { get; init; }
}

public sealed record WirePhotoTiles(int Session, bool Gain, IReadOnlyList<PhotoTileRequest> Requests)
{
    public int Operation { get; init; }
}
public sealed record WirePhotoPacket(WireVideoStream Stream, int Offset, int Length);
public sealed record WirePhotoPackets(IReadOnlyList<WirePhotoPacket> Packets);
public sealed record WirePhotoAnimation(int Session, int Playback);
public sealed record WirePhotoAnimationResult(WirePhotoImage? Image);

/// <summary>
/// A still-image encoder of the worker's FFmpeg, with its <c>AVPixelFormat</c> values; <paramref name="Hardware"/> names its
/// encoder interface.
/// </summary>
public sealed record WireStillImageEncoder(
    string Name,
    StillImageCodec Codec,
    string DisplayName,
    IReadOnlyList<int> PixelFormats,
    string? Hardware);

/// <summary>Everything the page told the worker since the last pump.</summary>
public sealed record WirePumpUpdates
{
    public IReadOnlyList<WireAllowance> Allowances { get; init; } = [];
    public IReadOnlyList<int> Releases { get; init; } = [];
    public IReadOnlyList<WirePosition> Positions { get; init; } = [];
    public IReadOnlyList<WireSpeed> Speeds { get; init; } = [];
    public IReadOnlyList<WireScriptFault> Faults { get; init; } = [];
}

/// <summary>The total number of frames the page will have taken from <paramref name="Decoder"/>.</summary>
public sealed record WireAllowance(int Decoder, long Allowance);

public sealed record WirePosition(int Demuxer, double Seconds);

public sealed record WireSpeed(int Decoder, double Speed);

/// <summary>A failure the worker script observed in a decoder it runs, such as WebCodecs.</summary>
public sealed record WireScriptFault(int Decoder, string Message, string RequiredSoftwareFallbackReason);

public sealed record WireDemuxerStatus(int Demuxer, DemuxerStatus Status);

public sealed record WireDecoderStatus(int Decoder, VideoDecoderDiagnostics Diagnostics);

public static class WireNotificationKind
{
    public const string Reconfigured = "reconfigured";
    public const string DemuxerFaulted = "demuxer-faulted";
    public const string DecoderFaulted = "decoder-faulted";
}

public sealed record WireNotification(int Component, string Kind, string Message)
{
    public MediaInfo? Info { get; init; }
    public double Position { get; init; } = double.NaN;
    public WireFault? Fault { get; init; }
}

public sealed record WireFault(
    string Message,
    int Serial,
    VideoDecoderDiagnostics Diagnostics,
    string RequiredSoftwareFallbackReason,
    bool CanRestart,
    bool EverDecoded,
    bool IsSoftware,
    bool Fatal);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WireSource))]
[JsonSerializable(typeof(WireOpenResult))]
[JsonSerializable(typeof(WireVideoDecoderRequest))]
[JsonSerializable(typeof(WireAudioDecoderRequest))]
[JsonSerializable(typeof(WireDecoderResult))]
[JsonSerializable(typeof(WireVideoStream))]
[JsonSerializable(typeof(WireLink))]
[JsonSerializable(typeof(WireOutput))]
[JsonSerializable(typeof(WireSeek))]
[JsonSerializable(typeof(WireSeekResult))]
[JsonSerializable(typeof(WireRelayStatus))]
[JsonSerializable(typeof(WireRendition))]
[JsonSerializable(typeof(WireRecover))]
[JsonSerializable(typeof(WireSubtitles))]
[JsonSerializable(typeof(WireProbeRequest))]
[JsonSerializable(typeof(WireImageRequest))]
[JsonSerializable(typeof(WirePicture))]
[JsonSerializable(typeof(WireImageProbe))]
[JsonSerializable(typeof(WireMediaTags))]
[JsonSerializable(typeof(WirePhotoRequest))]
[JsonSerializable(typeof(WirePhotoResult))]
[JsonSerializable(typeof(WirePhoto))]
[JsonSerializable(typeof(WirePhotoTiles))]
[JsonSerializable(typeof(WirePhotoPackets))]
[JsonSerializable(typeof(WirePhotoAnimation))]
[JsonSerializable(typeof(WirePhotoAnimationResult))]
[JsonSerializable(typeof(IReadOnlyList<WireStillImageEncoder>))]
[JsonSerializable(typeof(VideoProbeResult))]
[JsonSerializable(typeof(IReadOnlyList<VideoProbeAudioStreamInfo>))]
[JsonSerializable(typeof(MediaInspection))]
[JsonSerializable(typeof(WirePumpUpdates))]
[JsonSerializable(typeof(WireDemuxerStatus))]
[JsonSerializable(typeof(WireDecoderStatus))]
[JsonSerializable(typeof(WireNotification))]
[JsonSerializable(typeof(MediaInfo))]
[JsonSerializable(typeof(DemuxerStatus))]
[JsonSerializable(typeof(VideoDecoderDiagnostics))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
public sealed partial class EngineWireContext : JsonSerializerContext;

/// <summary>Writes a bitmap subtitle document as compact bytes and back, for a worker to hand one to the page.</summary>
public static class BitmapSubtitleCodec
{
    public static byte[] Write(BitmapSubtitleDocument document)
    {
        byte[] buffer = new byte[GetEncodedSize(document)];
        Write(document, buffer);
        return buffer;
    }

    public static int GetEncodedSize(BitmapSubtitleDocument document)
    {
        long size = 12;
        foreach (BitmapSubtitleCue cue in document.Cues)
        {
            size += 20;
            foreach (BitmapSubtitleRectangle rectangle in cue.Rectangles)
                size += 24 + (rectangle.Palette.Length * 4L) + rectangle.Pixels.Length;
        }

        return checked((int)size);
    }

    public static void Write(BitmapSubtitleDocument document, Span<byte> output)
    {
        if (output.Length != GetEncodedSize(document))
            throw new ArgumentException("The destination does not match the encoded subtitle size.", nameof(output));
        int offset = 0;
        WriteInt(output, ref offset, document.CanvasWidth);
        WriteInt(output, ref offset, document.CanvasHeight);
        WriteInt(output, ref offset, document.Cues.Length);
        foreach (BitmapSubtitleCue cue in document.Cues)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(output[offset..], cue.StartSeconds);
            BinaryPrimitives.WriteDoubleLittleEndian(output[(offset + 8)..], cue.EndSeconds);
            offset += 16;
            WriteInt(output, ref offset, cue.Rectangles.Length);
            foreach (BitmapSubtitleRectangle rectangle in cue.Rectangles)
            {
                WriteInt(output, ref offset, rectangle.X);
                WriteInt(output, ref offset, rectangle.Y);
                WriteInt(output, ref offset, rectangle.Width);
                WriteInt(output, ref offset, rectangle.Height);
                WriteInt(output, ref offset, rectangle.Palette.Length);
                foreach (uint color in rectangle.Palette)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(output[offset..], color);
                    offset += 4;
                }

                WriteInt(output, ref offset, rectangle.Pixels.Length);
                rectangle.Pixels.CopyTo(output[offset..]);
                offset += rectangle.Pixels.Length;
            }
        }

    }

    public static BitmapSubtitleDocument Read(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        int width = ReadInt(data, ref offset);
        int height = ReadInt(data, ref offset);
        var cues = new BitmapSubtitleCue[ReadCount(data, ref offset, 20)];
        for (int cue = 0; cue < cues.Length; cue++)
        {
            Require(data, offset, 16);
            double start = BinaryPrimitives.ReadDoubleLittleEndian(data[offset..]);
            double end = BinaryPrimitives.ReadDoubleLittleEndian(data[(offset + 8)..]);
            offset += 16;
            var rectangles = new BitmapSubtitleRectangle[ReadCount(data, ref offset, 24)];
            for (int index = 0; index < rectangles.Length; index++)
            {
                int x = ReadInt(data, ref offset);
                int y = ReadInt(data, ref offset);
                int rectangleWidth = ReadInt(data, ref offset);
                int rectangleHeight = ReadInt(data, ref offset);
                var palette = new uint[ReadCount(data, ref offset, 4)];
                for (int color = 0; color < palette.Length; color++)
                {
                    palette[color] = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
                    offset += 4;
                }

                int pixels = ReadCount(data, ref offset, 1);
                rectangles[index] = new BitmapSubtitleRectangle(x, y, rectangleWidth, rectangleHeight,
                    data.Slice(offset, pixels).ToArray(), palette);
                offset += pixels;
            }

            cues[cue] = new BitmapSubtitleCue(start, end, rectangles);
        }

        if (offset != data.Length)
            throw new InvalidDataException("The bitmap subtitle document has trailing bytes.");
        return new BitmapSubtitleDocument(width, height, cues);
    }

    private static void WriteInt(Span<byte> output, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(output[offset..], value);
        offset += 4;
    }

    private static int ReadInt(ReadOnlySpan<byte> data, ref int offset)
    {
        Require(data, offset, 4);
        int value = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        offset += 4;
        return value;
    }

    /// <summary>Reads a count of items of at least <paramref name="itemSize"/> bytes each that must still fit.</summary>
    private static int ReadCount(ReadOnlySpan<byte> data, ref int offset, int itemSize)
    {
        int count = ReadInt(data, ref offset);
        if (count < 0 || (long)count * itemSize > data.Length - offset)
            throw new InvalidDataException("The bitmap subtitle document is truncated.");
        return count;
    }

    private static void Require(ReadOnlySpan<byte> data, int offset, int count)
    {
        if (offset + count > data.Length)
            throw new InvalidDataException("The bitmap subtitle document is truncated.");
    }
}

/// <summary>
/// Copies the dynamic HDR metadata a decoder parsed into bytes and back, so a frame decoded in a
/// worker carries it to the page without parsing the bitstream twice.
/// </summary>
public static class VideoFrameMetadataCodec
{
    public static byte[] Write(Hdr10PlusMetadata metadata)
    {
        var writer = new Writer(256);
        writer.Int(metadata.ApplicationVersion);
        writer.Int(metadata.WindowCount);
        writer.Float(metadata.TargetedSystemDisplayMaximumLuminance);
        writer.Float(metadata.EffectivePeakLuminance);
        writer.Float(metadata.SceneAverageLuminance);
        int windows = Math.Clamp(metadata.WindowCount, 0, Hdr10PlusMetadata.MaximumWindows);
        for (int index = 0; index < windows; index++)
        {
            Hdr10PlusProcessingWindow window = metadata.Windows[index];
            writer.Floats(window.SceneMaximumLuminance);
            writer.Float(window.SceneAverageLuminance);
            writer.Int(window.PercentileCount);
            writer.Bytes(window.Percentages);
            writer.Floats(window.PercentileLuminance);
            writer.Int(window.ToneMapping ? 1 : 0);
            writer.Float(window.KneeX);
            writer.Float(window.KneeY);
            writer.Int(window.AnchorCount);
            writer.Floats(window.Anchors);
        }

        return writer.ToArray();
    }

    public static bool TryRead(ReadOnlySpan<byte> data, Hdr10PlusMetadata destination)
    {
        destination.Reset();
        var reader = new Reader(data);
        destination.ApplicationVersion = reader.Int();
        destination.WindowCount = reader.Int();
        destination.TargetedSystemDisplayMaximumLuminance = reader.Float();
        destination.EffectivePeakLuminance = reader.Float();
        destination.SceneAverageLuminance = reader.Float();
        int windows = Math.Clamp(destination.WindowCount, 0, Hdr10PlusMetadata.MaximumWindows);
        for (int index = 0; index < windows; index++)
        {
            Hdr10PlusProcessingWindow window = destination.Windows[index];
            reader.Floats(window.SceneMaximumLuminance);
            window.SceneAverageLuminance = reader.Float();
            window.PercentileCount = reader.Int();
            reader.Bytes(window.Percentages);
            reader.Floats(window.PercentileLuminance);
            window.ToneMapping = reader.Int() != 0;
            window.KneeX = reader.Float();
            window.KneeY = reader.Float();
            window.AnchorCount = reader.Int();
            reader.Floats(window.Anchors);
        }

        if (!reader.Complete)
        {
            destination.Reset();
            return false;
        }

        destination.IsPresent = true;
        return true;
    }

    public static byte[] Write(DolbyVisionMetadata metadata)
    {
        var writer = new Writer(1024);
        writer.Int(metadata.Profile);
        writer.Int(metadata.Level);
        writer.Int(metadata.CompatibilityId);
        writer.Int(metadata.BaseLayerBitDepth);
        writer.Int(metadata.EnhancementLayerBitDepth);
        writer.Int(metadata.BaseLayerFullRange ? 1 : 0);
        writer.Int(metadata.EnhancementLayerResidual ? 1 : 0);
        writer.Floats(metadata.NlqOffset);
        writer.Floats(metadata.NlqDeadzoneSlope);
        writer.Floats(metadata.NlqDeadzoneThreshold);
        writer.Float(metadata.SourceMinimumPq);
        writer.Float(metadata.SourceMaximumPq);
        writer.Float(metadata.SceneMaximumPq);
        writer.Float(metadata.SceneAveragePq);
        writer.Double(metadata.EffectivePeakLuminance);
        writer.Floats(metadata.NonlinearOffset);
        writer.Floats(metadata.NonlinearMatrix);
        writer.Floats(metadata.LinearMatrix);
        foreach (DolbyVisionReshapingCurve curve in metadata.Curves)
        {
            // Only the pieces in use are carried; the rest stay cleared.
            int pivots = Math.Clamp(curve.PivotCount, 0, DolbyVisionReshapingCurve.MaximumPivots);
            int pieces = Math.Max(0, pivots - 1);
            writer.Int(pivots);
            writer.Floats(curve.Pivots.AsSpan(0, pivots));
            writer.Floats(curve.Methods.AsSpan(0, pieces));
            writer.Floats(curve.PolynomialCoefficients.AsSpan(0, pieces * DolbyVisionReshapingCurve.PolynomialCoefficientCount));
            writer.Floats(curve.MmrOrders.AsSpan(0, pieces));
            writer.Floats(curve.MmrConstants.AsSpan(0, pieces));
            writer.Floats(curve.MmrCoefficients.AsSpan(0,
                pieces * DolbyVisionReshapingCurve.MaximumMmrOrder * DolbyVisionReshapingCurve.MmrCoefficientCount));
        }

        return writer.ToArray();
    }

    public static bool TryRead(ReadOnlySpan<byte> data, DolbyVisionMetadata destination)
    {
        destination.Reset();
        var reader = new Reader(data);
        destination.Profile = reader.Int();
        destination.Level = reader.Int();
        destination.CompatibilityId = reader.Int();
        destination.BaseLayerBitDepth = reader.Int();
        destination.EnhancementLayerBitDepth = reader.Int();
        destination.BaseLayerFullRange = reader.Int() != 0;
        destination.EnhancementLayerResidual = reader.Int() != 0;
        reader.Floats(destination.NlqOffset);
        reader.Floats(destination.NlqDeadzoneSlope);
        reader.Floats(destination.NlqDeadzoneThreshold);
        destination.SourceMinimumPq = reader.Float();
        destination.SourceMaximumPq = reader.Float();
        destination.SceneMaximumPq = reader.Float();
        destination.SceneAveragePq = reader.Float();
        destination.EffectivePeakLuminance = reader.Double();
        reader.Floats(destination.NonlinearOffset);
        reader.Floats(destination.NonlinearMatrix);
        reader.Floats(destination.LinearMatrix);
        foreach (DolbyVisionReshapingCurve curve in destination.Curves)
        {
            int pivots = Math.Clamp(reader.Int(), 0, DolbyVisionReshapingCurve.MaximumPivots);
            int pieces = Math.Max(0, pivots - 1);
            curve.PivotCount = pivots;
            reader.Floats(curve.Pivots.AsSpan(0, pivots));
            reader.Floats(curve.Methods.AsSpan(0, pieces));
            reader.Floats(curve.PolynomialCoefficients.AsSpan(0, pieces * DolbyVisionReshapingCurve.PolynomialCoefficientCount));
            reader.Floats(curve.MmrOrders.AsSpan(0, pieces));
            reader.Floats(curve.MmrConstants.AsSpan(0, pieces));
            reader.Floats(curve.MmrCoefficients.AsSpan(0,
                pieces * DolbyVisionReshapingCurve.MaximumMmrOrder * DolbyVisionReshapingCurve.MmrCoefficientCount));
        }

        if (!reader.Complete)
        {
            destination.Reset();
            return false;
        }

        destination.IsPresent = true;
        return true;
    }

    private ref struct Writer(int capacity)
    {
        private byte[] buffer = new byte[capacity];
        private int length;

        public void Int(int value)
        {
            Grow(4);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(length), value);
            length += 4;
        }

        public void Float(float value)
        {
            Grow(4);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(length), value);
            length += 4;
        }

        public void Double(double value)
        {
            Grow(8);
            BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(length), value);
            length += 8;
        }

        public void Floats(ReadOnlySpan<float> values)
        {
            foreach (float value in values)
                Float(value);
        }

        public void Bytes(ReadOnlySpan<byte> values)
        {
            Grow(values.Length);
            values.CopyTo(buffer.AsSpan(length));
            length += values.Length;
        }

        public readonly byte[] ToArray() => buffer.AsSpan(0, length).ToArray();

        private void Grow(int count)
        {
            if (length + count > buffer.Length)
                Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + count));
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private int position;
        private bool overrun;

        public readonly bool Complete => !overrun && position == data.Length;

        public int Int() => Take(4) is { IsEmpty: false } bytes ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : 0;

        public float Float() => Take(4) is { IsEmpty: false } bytes ? BinaryPrimitives.ReadSingleLittleEndian(bytes) : 0;

        public double Double() => Take(8) is { IsEmpty: false } bytes ? BinaryPrimitives.ReadDoubleLittleEndian(bytes) : 0;

        public void Floats(Span<float> values)
        {
            for (int index = 0; index < values.Length; index++)
                values[index] = Float();
        }

        public void Bytes(Span<byte> values)
        {
            if (Take(values.Length) is { IsEmpty: false } bytes)
                bytes.CopyTo(values);
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count == 0 || overrun)
                return default;
            if (position + count > data.Length)
            {
                overrun = true;
                return default;
            }

            ReadOnlySpan<byte> bytes = data.Slice(position, count);
            position += count;
            return bytes;
        }
    }
}
