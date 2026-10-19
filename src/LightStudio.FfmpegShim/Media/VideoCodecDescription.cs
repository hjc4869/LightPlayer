using System;

namespace LightStudio.FfmpegShim.Media;

/// <summary>
/// Everything a decoder outside this assembly needs to configure itself for a video stream, without
/// having to reach into the container's native codec parameters.
/// </summary>
public sealed record VideoCodecDescription
{
    /// <summary>FFmpeg codec short name, e.g. <c>h264</c>, <c>hevc</c>, <c>vp9</c>, <c>av1</c>.</summary>
    public required string CodecName { get; init; }

    public required int StreamIndex { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>FFmpeg profile identifier, or <c>FF_PROFILE_UNKNOWN</c> when the container omits it.</summary>
    public int Profile { get; init; }

    /// <summary>FFmpeg level identifier, negative when unknown.</summary>
    public int Level { get; init; }

    /// <summary>Bits per luma sample, 0 when the pixel format is unknown before decoding.</summary>
    public int BitDepth { get; init; }

    public int SampleAspectRatioNum { get; init; }

    public int SampleAspectRatioDen { get; init; } = 1;

    public double PixelAspectRatio { get; init; } = 1.0;

    /// <summary>Degrees the picture has to be turned clockwise for display: 0, 90, 180 or 270.</summary>
    public int Rotation { get; init; }

    /// <summary>Whether the picture is mirrored left to right before <see cref="Rotation"/>.</summary>
    public bool MirrorHorizontally { get; init; }

    /// <summary>Container frame rate, 0 when it could not be guessed.</summary>
    public double FrameRate { get; init; }
    public int ColorRange { get; init; }
    public int ColorPrimaries { get; init; }
    public int ColorTransfer { get; init; }
    public int ColorSpace { get; init; }

    /// <summary>
    /// Codec-private setup bytes: the <c>avcC</c>/<c>hvcC</c> record for length-prefixed H.264/HEVC,
    /// empty for Annex B elementary streams.
    /// </summary>
    public byte[] Extradata { get; init; } = Array.Empty<byte>();

    public static VideoCodecDescription FromCodecParameters(CodecParameters parameters, int streamIndex)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return new VideoCodecDescription
        {
            CodecName = parameters.CodecName,
            StreamIndex = streamIndex,
            Width = parameters.Width,
            Height = parameters.Height,
            Profile = parameters.Profile,
            Level = parameters.Level,
            BitDepth = parameters.BitDepth,
            SampleAspectRatioNum = parameters.SampleAspectRatioNum,
            SampleAspectRatioDen = parameters.SampleAspectRatioDen,
            PixelAspectRatio = parameters.PixelAspectRatio,
            Rotation = parameters.Rotation,
            MirrorHorizontally = parameters.MirrorHorizontally,
            FrameRate = parameters.FrameRate,
            ColorRange = parameters.ColorRange,
            ColorPrimaries = parameters.ColorPrimaries,
            ColorTransfer = parameters.ColorTransfer,
            ColorSpace = parameters.ColorSpace,
            Extradata = parameters.Extradata,
        };
    }
}
