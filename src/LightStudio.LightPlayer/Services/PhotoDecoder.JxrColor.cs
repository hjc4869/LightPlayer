using System;
using System.Buffers.Binary;
using System.Numerics;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.LightPlayer.Services;

// JPEG XR pixel storage and ICC inspection. Free of rendering dependencies, so the browser's media worker
// stores JPEG XR pictures the same way after its own color conversion.
internal static unsafe partial class PhotoDecoder
{
    private static VideoFrame CreateJxrTarget(JxrImage image, bool hdr, bool preserveAlpha = true)
    {
        bool alpha = preserveAlpha && image.PixelFormat.HasAlpha;
        VideoFrame frame = VideoFrame.CreateStandalone(image.Width, image.Height,
            alpha ? VideoPixelFormat.Rgba64 : VideoPixelFormat.Yuv444p16);
        frame.HasAlpha = alpha;
        frame.ColorPrimaries = VideoColorPrimaries.Bt2020;
        frame.ColorSpace = VideoColorSpace.Bt2020;
        frame.ColorTransfer = hdr ? VideoColorTransfer.Pq : VideoColorTransfer.Linear;
        frame.ColorRange = VideoColorRange.Full;
        frame.ChromaLocation = VideoChromaLocation.TopLeft;
        frame.FieldOrder = VideoFieldOrder.Progressive;
        int orientation = image.Info.Orientation;
        frame.Rotation = orientation switch { 1 or 3 => 180, 4 or 5 => 90, 6 or 7 => 270, _ => 0 };
        frame.MirrorHorizontally = orientation is 1 or 2 or 5 or 6;
        return frame;
    }

    // Stores a row of linear BT.2020 pixels, or of PQ values in the profile gamut that pqMatrix converts to BT.2020.
    // Returns the row's brightest channel in nits.
    private static float StoreJxrRow(VideoFrame frame, int row, ReadOnlySpan<Vector4> pixels, bool hdr, float[]? pqMatrix)
    {
        var luma = (ushort*)((byte*)frame.GetPlane(0) + (long)row * frame.GetStride(0));
        var chromaBlue = frame.HasAlpha ? null : (ushort*)((byte*)frame.GetPlane(1) + (long)row * frame.GetStride(1));
        var chromaRed = frame.HasAlpha ? null : (ushort*)((byte*)frame.GetPlane(2) + (long)row * frame.GetStride(2));
        var packed = (ushort*)((byte*)frame.Data + (long)row * frame.Stride);
        bool pq = pqMatrix is not null;
        float peak = 0;
        for (int column = 0; column < frame.Width; column++)
        {
            Vector4 pixel = pixels[column];
            Vector3 rgb = new(pixel.X, pixel.Y, pixel.Z);
            if (pqMatrix is { } matrix)
            {
                Vector3 nits = new(JxrPqToNits(rgb.X), JxrPqToNits(rgb.Y), JxrPqToNits(rgb.Z));
                rgb = new Vector3(
                    matrix[0] * nits.X + matrix[1] * nits.Y + matrix[2] * nits.Z,
                    matrix[3] * nits.X + matrix[4] * nits.Y + matrix[5] * nits.Z,
                    matrix[6] * nits.X + matrix[7] * nits.Y + matrix[8] * nits.Z);
            }
            if (!frame.HasAlpha)
                rgb *= pixel.W;
            for (int channel = 0; channel < 3; channel++)
            {
                float value = float.IsFinite(rgb[channel]) ? Math.Max(rgb[channel], 0) : 0;
                if (hdr)
                {
                    float nits = Math.Min(value * (pq ? 1 : 80), 10000);
                    peak = Math.Max(peak, nits);
                    rgb[channel] = JxrNitsToPq(nits);
                }
                else
                {
                    rgb[channel] = Math.Min(value, 1);
                }
            }
            if (frame.HasAlpha)
            {
                packed[column * 4] = Quantize(rgb.X);
                packed[column * 4 + 1] = Quantize(rgb.Y);
                packed[column * 4 + 2] = Quantize(rgb.Z);
                packed[column * 4 + 3] = Quantize(pixel.W);
            }
            else
            {
                float brightness = 0.2627f * rgb.X + 0.6780f * rgb.Y + 0.0593f * rgb.Z;
                luma[column] = Quantize(brightness);
                chromaBlue[column] = Quantize((rgb.Z - brightness) / 1.8814f + 0.5f);
                chromaRed[column] = Quantize((rgb.X - brightness) / 1.4746f + 0.5f);
            }
        }
        return peak;
    }

    private static bool IsJxrPqProfile(ReadOnlySpan<byte> profile)
    {
        ReadOnlySpan<byte> cicp = JxrIccTag(profile, 0x63696370);
        if (cicp.Length >= 12 && cicp[9] == 16)
            return true;
        ReadOnlySpan<byte> luminance = JxrIccTag(profile, 0x6c756d69);
        float white = luminance.Length >= 20 && luminance.StartsWith("XYZ "u8)
            ? BinaryPrimitives.ReadInt32BigEndian(luminance[12..]) / 65536f : 100;
        if (white is <= 0 or > 10000)
            return false;
        foreach (uint tag in new uint[] { 0x72545243, 0x67545243, 0x62545243 })
        {
            ReadOnlySpan<byte> curve = JxrIccTag(profile, tag);
            if (curve.Length < 12 || !curve.StartsWith("curv"u8))
                return false;
            uint count = BinaryPrimitives.ReadUInt32BigEndian(curve[8..]);
            if (count < 256 || count > (curve.Length - 12) / 2)
                return false;
            bool absolute = true, relative = true;
            for (int sample = 1; sample <= 32; sample++)
            {
                int index = (int)((count - 1) * sample / 32);
                float encoded = index / (float)(count - 1);
                float linear = BinaryPrimitives.ReadUInt16BigEndian(curve[(12 + index * 2)..]) / 65535f;
                absolute &= MathF.Abs(linear - JxrPqToNits(encoded) / 10000) <= 3f / 65535;
                relative &= MathF.Abs(linear - JxrPqToNits(encoded * JxrNitsToPq(white)) / white) <= 3f / 65535;
            }
            if (!absolute && !relative)
                return false;
        }
        return true;
    }

    // The profile's RGB to XYZ matrix in row-major order, from its colorant tags.
    private static float[]? ReadJxrIccMatrix(ReadOnlySpan<byte> profile)
    {
        uint[] tags = [0x7258595a, 0x6758595a, 0x6258595a];
        float[] matrix = new float[9];
        for (int column = 0; column < tags.Length; column++)
        {
            ReadOnlySpan<byte> tag = JxrIccTag(profile, tags[column]);
            if (tag.Length < 20 || !tag.StartsWith("XYZ "u8))
                return null;
            for (int row = 0; row < 3; row++)
                matrix[row * 3 + column] = BinaryPrimitives.ReadInt32BigEndian(tag[(8 + row * 4)..]) / 65536f;
        }
        return matrix;
    }

    private static ReadOnlySpan<byte> JxrIccTag(ReadOnlySpan<byte> profile, uint signature)
    {
        if (profile.Length < 132)
            return default;
        uint count = BinaryPrimitives.ReadUInt32BigEndian(profile[128..]);
        if (count > (profile.Length - 132) / 12)
            return default;
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> entry = profile.Slice(132 + index * 12, 12);
            if (BinaryPrimitives.ReadUInt32BigEndian(entry) != signature)
                continue;
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            return offset <= profile.Length && length <= profile.Length - offset ? profile.Slice((int)offset, (int)length) : default;
        }
        return default;
    }

    private static float JxrNitsToPq(float nits)
    {
        float power = MathF.Pow(nits / 10000, 2610f / 16384);
        return MathF.Pow((3424f / 4096 + 2413f / 128 * power) / (1 + 2392f / 128 * power), 2523f / 32);
    }

    private static float JxrPqToNits(float signal)
    {
        float power = MathF.Pow(Math.Clamp(signal, 0, 1), 32f / 2523);
        return 10000 * MathF.Pow(Math.Max(power - 3424f / 4096, 0) / Math.Max(2413f / 128 - 2392f / 128 * power, 1e-6f), 16384f / 2610);
    }

    private static ushort Quantize(float value) => (ushort)Math.Clamp((int)MathF.Round(value * 65535), 0, 65535);
}
