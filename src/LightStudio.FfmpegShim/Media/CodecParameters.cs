using System;
using System.Runtime.InteropServices;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Interop;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

/// <summary>
/// Everything a decoder needs to configure itself for one elementary stream, as plain managed data.
/// </summary>
/// <remarks>
/// <para>
/// This is the managed mirror of <c>AVCodecParameters</c> plus the packet time base. It exists so a
/// packet source that is not libavformat -- a network protocol, say -- can still stand a decoder up,
/// and so those parameters can be sent over a wire.
/// </para>
/// <para>
/// Codec, pixel format and sample format travel as names rather than as the enum values FFmpeg
/// assigns them: those numbers are not contractual across FFmpeg major versions, and the two ends
/// of a remote session are routinely different builds. Everything else here is either a plain
/// number or an enum whose values come from a published specification, so it crosses unchanged.
/// </para>
/// </remarks>
public sealed partial record CodecParameters
{
    private byte[] extradata = Array.Empty<byte>();
    private byte[] dolbyVisionConfiguration = Array.Empty<byte>();
    private byte[] dolbyVisionEnhancementConfiguration = Array.Empty<byte>();

    public MediaStreamKind Kind { get; init; } = MediaStreamKind.Other;

    /// <summary>FFmpeg codec short name, e.g. <c>h264</c>, <c>hevc</c>, <c>aac</c>, <c>flac</c>.</summary>
    public string CodecName { get; init; } = string.Empty;

    /// <summary>The container marks the stream's block additions as an alpha plane (Matroska AlphaMode).</summary>
    public bool AlphaMode { get; init; }

    public uint CodecTag { get; init; }

    /// <summary>Codec-private setup bytes, empty when the stream needs none.</summary>
    public byte[] Extradata
    {
        get => extradata;
        init => extradata = value ?? Array.Empty<byte>();
    }

    /// <summary>Pixel format name for video, null otherwise.</summary>
    public string PixelFormat { get; init; }

    /// <summary>Sample format name for audio, null otherwise.</summary>
    public string SampleFormat { get; init; }

    public long BitRate { get; init; }

    public int BitsPerCodedSample { get; init; }

    public int BitsPerRawSample { get; init; }

    public int Profile { get; init; }

    public int Level { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int SampleAspectRatioNum { get; init; }

    public int SampleAspectRatioDen { get; init; } = 1;

    /// <summary>
    /// Frame rate the container states or libavformat guessed, which is not always the one the
    /// codec parameters carry.
    /// </summary>
    public int FrameRateNum { get; init; }

    public int FrameRateDen { get; init; } = 1;

    public int FieldOrder { get; init; }

    public int ColorRange { get; init; }

    public int ColorPrimaries { get; init; }

    public int ColorTransfer { get; init; }

    public int ColorSpace { get; init; }

    public int ChromaLocation { get; init; }

    /// <summary>
    /// MaxCLL, falling back to mastering-display maximum luminance, in cd/m^2. Zero when the
    /// stream declares neither. Kept outside <c>AVCodecParameters</c> because packet side data is
    /// otherwise lost when this portable description crosses a process or managed boundary.
    /// </summary>
    public double HdrPeakLuminance { get; init; }

    public VideoLuminance? HdrMasteringLuminance { get; init; }

    /// <summary>
    /// FFmpeg's Dolby Vision decoder configuration record, retained verbatim because it is coded
    /// packet side data rather than a field of <c>AVCodecParameters</c>.
    /// </summary>
    public byte[] DolbyVisionConfiguration
    {
        get => dolbyVisionConfiguration;
        init => dolbyVisionConfiguration = value ?? Array.Empty<byte>();
    }

    /// <summary>HEVC decoder configuration for an embedded Dolby Vision enhancement layer.</summary>
    public byte[] DolbyVisionEnhancementConfiguration
    {
        get => dolbyVisionEnhancementConfiguration;
        init => dolbyVisionEnhancementConfiguration = value ?? Array.Empty<byte>();
    }

    public int DolbyVisionProfile => DolbyVisionConfiguration.Length > 2
        ? DolbyVisionConfiguration[2]
        : 0;

    public int DolbyVisionLevel => DolbyVisionConfiguration.Length > 3
        ? DolbyVisionConfiguration[3]
        : 0;

    public int DolbyVisionCompatibilityId => DolbyVisionConfiguration.Length > 7
        ? DolbyVisionConfiguration[7]
        : 0;

    /// <summary>
    /// Degrees the picture has to be turned clockwise before it is shown: 0, 90, 180 or 270. Taken
    /// from the container's display matrix, which is packet side data and would otherwise be lost
    /// when this portable description crosses a process or managed boundary.
    /// </summary>
    public int Rotation { get; init; }

    /// <summary>Whether the picture is mirrored left to right before <see cref="Rotation"/>.</summary>
    public bool MirrorHorizontally { get; init; }

    public int VideoDelay { get; init; }

    /// <summary><c>AVChannelOrder</c> of the audio layout.</summary>
    public int ChannelOrder { get; init; }

    public int ChannelCount { get; init; }

    /// <summary>Native channel mask; only meaningful for a native or ambisonic order.</summary>
    public ulong ChannelMask { get; init; }

    public int SampleRate { get; init; }

    public int BlockAlign { get; init; }

    public int FrameSize { get; init; }

    public int InitialPadding { get; init; }

    public int TrailingPadding { get; init; }

    public int SeekPreroll { get; init; }

    /// <summary>Time base the packet timestamps of this stream are expressed in.</summary>
    public int TimeBaseNum { get; init; } = 1;

    public int TimeBaseDen { get; init; } = 1000;

    public double TimeBase => TimeBaseDen > 0 ? TimeBaseNum / (double)TimeBaseDen : 0;

    public double FrameRate => FrameRateDen > 0 && FrameRateNum > 0 ? FrameRateNum / (double)FrameRateDen : 0;

    public double PixelAspectRatio =>
        SampleAspectRatioNum > 0 && SampleAspectRatioDen > 0
            ? SampleAspectRatioNum / (double)SampleAspectRatioDen
            : 1.0;

    /// <summary>Bits per luma sample, 0 when the pixel format is not known yet.</summary>
    public unsafe int BitDepth
    {
        get
        {
            if (PixelFormat is not { Length: > 0 })
            {
                return 0;
            }

            AVPixFmtDescriptor* descriptor = av_pix_fmt_desc_get(av_get_pix_fmt(PixelFormat));
            return descriptor != null && descriptor->nb_components > 0 ? descriptor->comp[0].depth : 0;
        }
    }

    /// <summary>Reads the parameters of one stream of an open container.</summary>
    public static unsafe CodecParameters FromNative(
        AVCodecParameters* parameters,
        AVRational timeBase,
        AVRational frameRate)
    {
        if (parameters == null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        var extradata = Array.Empty<byte>();
        if (parameters->extradata != null && parameters->extradata_size > 0)
        {
            extradata = new byte[parameters->extradata_size];
            Marshal.Copy((IntPtr)parameters->extradata, extradata, 0, parameters->extradata_size);
        }

        MediaStreamKind kind = parameters->codec_type switch
        {
            AVMediaType.AVMEDIA_TYPE_VIDEO => MediaStreamKind.Video,
            AVMediaType.AVMEDIA_TYPE_AUDIO => MediaStreamKind.Audio,
            AVMediaType.AVMEDIA_TYPE_SUBTITLE => MediaStreamKind.Subtitle,
            _ => MediaStreamKind.Other,
        };

        VideoDisplayMatrix.Read(parameters, out int rotation, out bool mirrorHorizontally);

        return new CodecParameters
        {
            Kind = kind,
            CodecName = avcodec_get_name(parameters->codec_id),
            CodecTag = parameters->codec_tag,
            Extradata = extradata,
            PixelFormat = kind == MediaStreamKind.Video
                ? av_get_pix_fmt_name((AVPixelFormat)parameters->format)
                : null,
            SampleFormat = kind == MediaStreamKind.Audio
                ? av_get_sample_fmt_name((AVSampleFormat)parameters->format)
                : null,
            BitRate = parameters->bit_rate,
            BitsPerCodedSample = parameters->bits_per_coded_sample,
            BitsPerRawSample = parameters->bits_per_raw_sample,
            Profile = parameters->profile,
            Level = parameters->level,
            Width = parameters->width,
            Height = parameters->height,
            SampleAspectRatioNum = parameters->sample_aspect_ratio.num,
            SampleAspectRatioDen = parameters->sample_aspect_ratio.den,
            FrameRateNum = frameRate.num,
            FrameRateDen = frameRate.den,
            FieldOrder = (int)parameters->field_order,
            ColorRange = (int)parameters->color_range,
            ColorPrimaries = (int)parameters->color_primaries,
            ColorTransfer = (int)parameters->color_trc,
            ColorSpace = (int)parameters->color_space,
            ChromaLocation = (int)parameters->chroma_location,
            HdrPeakLuminance = ReadHdrPeakLuminance(parameters),
            HdrMasteringLuminance = ReadHdrMasteringLuminance(parameters),
            DolbyVisionConfiguration = ReadPacketSideData(
                parameters,
                AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF),
#if FFMPEG9
            DolbyVisionEnhancementConfiguration = ReadPacketSideData(
                parameters,
                AVPacketSideDataType.AV_PKT_DATA_HEVC_CONF),
#endif
            Rotation = rotation,
            MirrorHorizontally = mirrorHorizontally,
            VideoDelay = parameters->video_delay,
            ChannelOrder = (int)parameters->ch_layout.order,
            ChannelCount = parameters->ch_layout.nb_channels,
            ChannelMask = parameters->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_NATIVE
                ? parameters->ch_layout.u.mask
                : 0,
            SampleRate = parameters->sample_rate,
            BlockAlign = parameters->block_align,
            FrameSize = parameters->frame_size,
            InitialPadding = parameters->initial_padding,
            TrailingPadding = parameters->trailing_padding,
            SeekPreroll = parameters->seek_preroll,
            TimeBaseNum = timeBase.num,
            TimeBaseDen = timeBase.den,
        };
    }

    private static unsafe double ReadHdrPeakLuminance(AVCodecParameters* parameters)
    {
        AVPacketSideData* sideData = parameters->coded_side_data != null && parameters->nb_coded_side_data > 0
            ? av_packet_side_data_get(parameters->coded_side_data, parameters->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL)
            : null;
        if (sideData != null && sideData->data != null && sideData->size >= (nuint)sizeof(AVContentLightMetadata))
        {
            var metadata = (AVContentLightMetadata*)sideData->data;
            if (metadata->MaxCLL > 0)
                return metadata->MaxCLL;
        }

        return ReadHdrMasteringLuminance(parameters)?.MaximumNits ?? 0;
    }

    internal static unsafe VideoLuminance? ReadHdrMasteringLuminance(AVCodecParameters* parameters)
    {
        AVPacketSideData* sideData = parameters->coded_side_data != null && parameters->nb_coded_side_data > 0
            ? av_packet_side_data_get(parameters->coded_side_data, parameters->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA)
            : null;
        if (sideData == null || sideData->data == null || sideData->size < (nuint)sizeof(AVMasteringDisplayMetadata))
        {
            return null;
        }

        return VideoFrameMetadata.ReadHdrMasteringLuminance((AVMasteringDisplayMetadata*)sideData->data);
    }

    private static unsafe byte[] ReadPacketSideData(
        AVCodecParameters* parameters,
        AVPacketSideDataType type)
    {
        AVPacketSideData* sideData = parameters->coded_side_data != null && parameters->nb_coded_side_data > 0
            ? av_packet_side_data_get(parameters->coded_side_data, parameters->nb_coded_side_data, type)
            : null;
        if (sideData == null || sideData->data == null || sideData->size == 0 || sideData->size > (nuint)int.MaxValue)
        {
            return Array.Empty<byte>();
        }

        var result = new byte[(int)sideData->size];
        Marshal.Copy((IntPtr)sideData->data, result, 0, result.Length);
        return result;
    }

    /// <summary>
    /// Builds an <c>AVCodecParameters</c> the caller owns and must release with
    /// <see cref="FreeNative"/>.
    /// </summary>
    /// <exception cref="FfmpegException">The codec name is not one this FFmpeg build knows.</exception>
    public unsafe AVCodecParameters* ToNative()
    {
        AVCodecParameters* parameters = avcodec_parameters_alloc();
        if (parameters == null)
        {
            throw new FfmpegException("avcodec_parameters_alloc returned null", 0);
        }

        try
        {
            parameters->codec_type = Kind switch
            {
                MediaStreamKind.Video => AVMediaType.AVMEDIA_TYPE_VIDEO,
                MediaStreamKind.Audio => AVMediaType.AVMEDIA_TYPE_AUDIO,
                MediaStreamKind.Subtitle => AVMediaType.AVMEDIA_TYPE_SUBTITLE,
                _ => AVMediaType.AVMEDIA_TYPE_UNKNOWN,
            };
            parameters->codec_id = ResolveCodecId(CodecName);
            parameters->codec_tag = CodecTag;
            parameters->format = Kind switch
            {
                MediaStreamKind.Video => (int)av_get_pix_fmt(PixelFormat ?? string.Empty),
                MediaStreamKind.Audio => (int)av_get_sample_fmt(SampleFormat ?? string.Empty),
                _ => -1,
            };
            parameters->bit_rate = BitRate;
            parameters->bits_per_coded_sample = BitsPerCodedSample;
            parameters->bits_per_raw_sample = BitsPerRawSample;
            parameters->profile = Profile;
            parameters->level = Level;
            parameters->width = Width;
            parameters->height = Height;
            parameters->sample_aspect_ratio = new AVRational { num = SampleAspectRatioNum, den = SampleAspectRatioDen };
            parameters->framerate = new AVRational { num = FrameRateNum, den = FrameRateDen };
            parameters->field_order = (AVFieldOrder)FieldOrder;
            parameters->color_range = (AVColorRange)ColorRange;
            parameters->color_primaries = (AVColorPrimaries)ColorPrimaries;
            parameters->color_trc = (AVColorTransferCharacteristic)ColorTransfer;
            parameters->color_space = (AVColorSpace)ColorSpace;
            parameters->chroma_location = (AVChromaLocation)ChromaLocation;
            parameters->video_delay = VideoDelay;
            parameters->sample_rate = SampleRate;
            parameters->block_align = BlockAlign;
            parameters->frame_size = FrameSize;
            parameters->initial_padding = InitialPadding;
            parameters->trailing_padding = TrailingPadding;
            parameters->seek_preroll = SeekPreroll;

            ApplyChannelLayout(&parameters->ch_layout);
            ApplyExtradata(parameters);
            ApplyDolbyVisionConfiguration(parameters);
#if FFMPEG9
            ApplyEnhancementLayerConfiguration(parameters);
#endif
            return parameters;
        }
        catch
        {
            FreeNative(&parameters);
            throw;
        }
    }

    public static unsafe void FreeNative(AVCodecParameters** parameters)
    {
        if (parameters != null && *parameters != null)
        {
            avcodec_parameters_free(parameters);
        }
    }

    public AVRational NativeTimeBase() => new() { num = TimeBaseNum, den = TimeBaseDen };

    /// <summary>Time base to stamp packets with when none was carried over the wire.</summary>
    public static AVRational MicrosecondTimeBase => new() { num = 1, den = AV_TIME_BASE };

    private unsafe void ApplyChannelLayout(AVChannelLayout* layout)
    {
        if (Kind != MediaStreamKind.Audio || ChannelCount <= 0)
        {
            return;
        }

        // Only a native order round-trips through a mask. A custom or ambisonic layout would need
        // its channel map carried too; the default layout for the channel count decodes correctly
        // and only mislabels which speaker each channel belongs to.
        if ((AVChannelOrder)ChannelOrder == AVChannelOrder.AV_CHANNEL_ORDER_NATIVE &&
            ChannelMask != 0 &&
            av_channel_layout_from_mask(layout, ChannelMask) >= 0 &&
            layout->nb_channels == ChannelCount)
        {
            return;
        }

        av_channel_layout_default(layout, ChannelCount);
    }

    private unsafe void ApplyExtradata(AVCodecParameters* parameters)
    {
        if (Extradata.Length == 0)
        {
            return;
        }

        // Decoders read past the end of extradata, so the trailing padding FFmpeg's own demuxers
        // leave has to be reproduced here. av_mallocz is not usable: the browser head only patches
        // av_malloc for its 32-bit size_t.
        int total = Extradata.Length + AV_INPUT_BUFFER_PADDING_SIZE;
        var buffer = (byte*)av_malloc((nuint)total);
        if (buffer == null)
        {
            throw new FfmpegException("av_malloc returned null for codec extradata", 0);
        }

        new Span<byte>(buffer, total).Clear();
        Extradata.AsSpan().CopyTo(new Span<byte>(buffer, Extradata.Length));
        parameters->extradata = buffer;
        parameters->extradata_size = Extradata.Length;
    }

    private unsafe void ApplyDolbyVisionConfiguration(AVCodecParameters* parameters)
    {
        if (DolbyVisionConfiguration.Length == 0)
            return;

        AVPacketSideData* sideData = av_packet_side_data_new(
            &parameters->coded_side_data,
            &parameters->nb_coded_side_data,
            AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF,
            (nuint)DolbyVisionConfiguration.Length,
            0);
        if (sideData == null)
            throw new FfmpegException("FFmpeg refused Dolby Vision codec side data", 0);

        DolbyVisionConfiguration.AsSpan().CopyTo(
            new Span<byte>(sideData->data, DolbyVisionConfiguration.Length));
    }

#if FFMPEG9
    private unsafe void ApplyEnhancementLayerConfiguration(AVCodecParameters* parameters)
    {
        if (DolbyVisionEnhancementConfiguration.Length == 0)
            return;

        AVPacketSideData* sideData = av_packet_side_data_new(
            &parameters->coded_side_data,
            &parameters->nb_coded_side_data,
            AVPacketSideDataType.AV_PKT_DATA_HEVC_CONF,
            (nuint)DolbyVisionEnhancementConfiguration.Length,
            0);
        if (sideData == null)
            throw new FfmpegException("FFmpeg refused Dolby Vision enhancement-layer codec side data", 0);

        DolbyVisionEnhancementConfiguration.AsSpan().CopyTo(
            new Span<byte>(sideData->data, DolbyVisionEnhancementConfiguration.Length));
    }
#endif

    private static unsafe AVCodecID ResolveCodecId(string codecName)
    {
        if (string.IsNullOrEmpty(codecName))
        {
            return AVCodecID.AV_CODEC_ID_NONE;
        }

        AVCodecDescriptor* descriptor = avcodec_descriptor_get_by_name(codecName);
        if (descriptor != null)
        {
            return descriptor->id;
        }

        AVCodec* decoder = avcodec_find_decoder_by_name(codecName);
        if (decoder != null)
        {
            return decoder->id;
        }

        throw new FfmpegException($"This build has no codec named '{codecName}'.", 0);
    }
}
