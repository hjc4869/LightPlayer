#nullable enable
using System;
using System.Buffers.Binary;
using LightMediaRenderer.Contracts;

namespace LightMediaRenderer.Software;

/// <summary>Identifies absolute PQ matrix/TRC profiles, including sampled PQ curves.</summary>
internal static class PqIccProfile
{
    public static bool IsPq(ReadOnlySpan<byte> profile)
    {
        ReadOnlySpan<byte> cicp = Tag(profile, 0x63696370);
        if (cicp.Length >= 12 && cicp[9] == 16)
            return true;
        ReadOnlySpan<byte> luminance = Tag(profile, 0x6c756d69);
        float white = luminance.Length >= 20 && luminance.StartsWith("XYZ "u8)
            ? BinaryPrimitives.ReadInt32BigEndian(luminance[12..]) / 65536f : 100;
        if (white is <= 0 or > 10000)
            return false;
        ReadOnlySpan<uint> signatures = [0x72545243, 0x67545243, 0x62545243];
        foreach (uint signature in signatures)
        {
            ReadOnlySpan<byte> curve = Tag(profile, signature);
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
                absolute &= MathF.Abs(linear - VideoToneCurve.PqToNits(encoded) / 10000) <= 3f / 65535;
                relative &= MathF.Abs(linear - VideoToneCurve.PqToNits(encoded * VideoToneCurve.NitsToPq(white)) / white) <= 3f / 65535;
            }
            if (!absolute && !relative)
                return false;
        }
        return true;
    }

    public static PhotoColorProfile? Read(ReadOnlySpan<byte> profile, bool hasAlpha)
    {
        if (!IsPq(profile))
            return null;
        // Inverse BT.2020-to-XYZ D50, matching ICC's chromatically adapted colorant tags.
        ReadOnlySpan<float> inverse =
        [
            1.6472752f, -0.3936025f, -0.2359803f,
            -0.6826176f, 1.6476178f, 0.0128163f,
            0.0296627f, -0.0629167f, 1.2533964f,
        ];
        ReadOnlySpan<uint> signatures = [0x7258595a, 0x6758595a, 0x6258595a];
        Span<float> xyz = stackalloc float[9];
        for (int column = 0; column < 3; column++)
        {
            ReadOnlySpan<byte> tag = Tag(profile, signatures[column]);
            if (tag.Length < 20 || !tag.StartsWith("XYZ "u8))
                return null;
            for (int row = 0; row < 3; row++)
                xyz[row * 3 + column] = BinaryPrimitives.ReadInt32BigEndian(tag[(8 + row * 4)..]) / 65536f;
        }
        float[] matrix = new float[9];
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
                matrix[row * 3 + column] = inverse[row * 3] * xyz[column] +
                    inverse[row * 3 + 1] * xyz[3 + column] + inverse[row * 3 + 2] * xyz[6 + column];
        return new PhotoColorProfile([1, 1, 0, 1, 0, 0, 0], matrix, 16, hasAlpha: hasAlpha);
    }

    public static ReadOnlySpan<byte> Tag(ReadOnlySpan<byte> profile, uint signature)
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
            return offset <= profile.Length && length <= profile.Length - offset
                ? profile.Slice((int)offset, (int)length) : default;
        }
        return default;
    }
}
