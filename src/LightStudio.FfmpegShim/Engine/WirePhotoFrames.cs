#nullable enable
using System;
using System.IO;
using System.Numerics;
using LightMediaRenderer.Contracts;
using SkiaSharp;

namespace LightStudio.FfmpegShim.Engine;

/// <summary>
/// Gives the pictures of a <see cref="WirePhoto"/> their color and gain map, alike on the page that shows a photo
/// and in the worker that saves one.
/// </summary>
public static class WirePhotoFrames
{
    /// <summary>Gain samples are untagged values from 0 to 1.</summary>
    public static PhotoColorProfile GainSampleProfile { get; } = new([1, 1, 0, 1, 0, 0, 0], [1, 0, 0, 0, 1, 0, 0, 0, 1]);

    public static bool HasGainMap(WirePhoto photo) => photo is { Gain: not null, GainMap: not null };

    /// <summary>The base picture's color, relative to SDR white when a gain map applies to it, or null when unsupported.</summary>
    public static PhotoColorProfile? BaseProfile(WirePhoto photo)
    {
        WirePhotoImage image = photo.Base;
        PhotoColorProfile? profile = image.Color?.ToProfile() ?? CreateProfile(photo.BaseProfile ?? image.IccProfile,
            image.Primaries, image.Transfer is 0 or 2 ? 13 : image.Transfer);
        if (profile is null || !HasGainMap(photo))
            return profile;
        return new PhotoColorProfile(profile.Transfer.ToArray(), profile.ToBt2020.ToArray(), profile.TransferCode,
            profile.TransferCode is 16 or 18 ? 1f / 203 : profile.LinearScale, profile.HasAlpha);
    }

    public static void ApplyProfile(VideoFrame frame, PhotoColorProfile profile, bool linear)
    {
        frame.PhotoColor = profile;
        frame.ColorPrimaries = VideoColorPrimaries.Bt2020;
        frame.ColorTransfer = linear ? VideoColorTransfer.Linear : profile.TransferCode switch
        {
            16 => VideoColorTransfer.Pq, 18 => VideoColorTransfer.Hlg, _ => VideoColorTransfer.Linear,
        };
        frame.FieldOrder = VideoFieldOrder.Progressive;
    }

    /// <summary>The photo's gain map over a base picture of <paramref name="baseProfile"/>, sampling <paramref name="samples"/>.</summary>
    public static PhotoGainMap CreateGainMap(WirePhoto photo, PhotoColorProfile? baseProfile, VideoFrame samples)
    {
        WireGainMap gain = photo.GainMap ?? throw new InvalidDataException("The photo has no gain map.");
        PhotoColorProfile math = (gain.UseBaseColorSpace ? baseProfile : CreateProfile(photo.MathProfile ?? [],
                photo.MathPrimaries == 0 ? 1 : photo.MathPrimaries, 8))
            ?? throw new NotSupportedException("The gain-map calculation color space is not supported.");
        var matrix = new SKColorSpaceXyz(math.ToBt2020.ToArray());
        return new PhotoGainMap(samples, Vector(gain.Minimum), Vector(gain.Maximum), Vector(gain.Gamma),
            Vector(gain.OffsetSdr), Vector(gain.OffsetHdr), gain.CapacityMin, gain.CapacityMax,
            matrix.Invert().Values, matrix.Values) { BaseIsHdr = gain.BaseIsHdr, IsApple = gain.IsApple };
    }

    private static PhotoColorProfile? CreateProfile(byte[] profile, int primaries, int transfer)
    {
        using SKColorSpace? space = PhotoColorProfile.CreateColorSpace(profile, primaries, transfer);
        return space is null ? null : PhotoColorProfile.Create(space);
    }

    private static Vector3 Vector(float[] values) => values.Length == 3 ? new Vector3(values[0], values[1], values[2]) :
        throw new InvalidDataException("Invalid gain-map vector.");
}
