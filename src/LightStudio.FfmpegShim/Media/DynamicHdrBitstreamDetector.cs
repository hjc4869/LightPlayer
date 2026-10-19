using System;

namespace LightStudio.FfmpegShim.Media;

[Flags]
public enum DynamicHdrMetadataKind
{
    None = 0,
    Hdr10Plus = 1,
    DolbyVision = 2,
}

/// <summary>Identifies dynamic-HDR payloads that a pixel-only decoder would otherwise discard.</summary>
public static class DynamicHdrBitstreamDetector
{
    private const int H264SeiNal = 6;
    private const int HevcPrefixSeiNal = 39;
    private const int HevcSuffixSeiNal = 40;
    private const int HevcDolbyVisionRpuNal = 62;
    private const int UserDataRegisteredItuT35 = 4;
    private const int Av1MetadataObu = 5;
    private const ulong Av1ItuT35Metadata = 4;

    public static DynamicHdrMetadataKind Detect(
        string codecName,
        ReadOnlySpan<byte> packet,
        ReadOnlySpan<byte> codecConfiguration)
    {
        if (packet.IsEmpty)
            return DynamicHdrMetadataKind.None;

        return codecName switch
        {
            "h264" => DetectNalUnits(packet, NalLengthSize(codecConfiguration, 4), hevc: false),
            "hevc" => DetectNalUnits(packet, NalLengthSize(codecConfiguration, 21), hevc: true),
            "av1" => DetectAv1(packet),
            _ => DynamicHdrMetadataKind.None,
        };
    }

    private static int NalLengthSize(ReadOnlySpan<byte> configuration, int offset) =>
        configuration.Length > offset && configuration[0] == 1
            ? (configuration[offset] & 0x03) + 1
            : 0;

    private static DynamicHdrMetadataKind DetectNalUnits(
        ReadOnlySpan<byte> packet,
        int nalLengthSize,
        bool hevc) =>
        nalLengthSize > 0
            ? DetectLengthPrefixedNalUnits(packet, nalLengthSize, hevc)
            : DetectAnnexBNalUnits(packet, hevc);

    private static DynamicHdrMetadataKind DetectLengthPrefixedNalUnits(
        ReadOnlySpan<byte> packet,
        int nalLengthSize,
        bool hevc)
    {
        DynamicHdrMetadataKind found = DynamicHdrMetadataKind.None;
        int offset = 0;
        while (offset + nalLengthSize <= packet.Length)
        {
            int length = 0;
            for (int index = 0; index < nalLengthSize; index++)
                length = (length << 8) | packet[offset++];

            if (length <= 0 || length > packet.Length - offset)
                break;

            found |= DetectNalUnit(packet.Slice(offset, length), hevc);
            offset += length;
        }

        return found;
    }

    private static DynamicHdrMetadataKind DetectAnnexBNalUnits(ReadOnlySpan<byte> packet, bool hevc)
    {
        DynamicHdrMetadataKind found = DynamicHdrMetadataKind.None;
        int searchFrom = 0;
        bool sawStartCode = false;
        while (TryFindStartCode(packet, searchFrom, out int start, out int startCodeLength))
        {
            sawStartCode = true;
            int nalStart = start + startCodeLength;
            int nalEnd = TryFindStartCode(packet, nalStart, out int next, out _)
                ? next
                : packet.Length;
            if (nalEnd > nalStart)
                found |= DetectNalUnit(packet[nalStart..nalEnd], hevc);
            searchFrom = nalEnd;
        }

        return sawStartCode ? found : DetectNalUnit(packet, hevc);
    }

    private static DynamicHdrMetadataKind DetectNalUnit(ReadOnlySpan<byte> nal, bool hevc)
    {
        int headerLength = hevc ? 2 : 1;
        if (nal.Length <= headerLength)
            return DynamicHdrMetadataKind.None;

        int type = hevc ? (nal[0] >> 1) & 0x3F : nal[0] & 0x1F;
        if (hevc && type == HevcDolbyVisionRpuNal)
            return DynamicHdrMetadataKind.DolbyVision;

        bool sei = hevc
            ? type is HevcPrefixSeiNal or HevcSuffixSeiNal
            : type == H264SeiNal;
        return sei ? DetectSei(nal[headerLength..]) : DynamicHdrMetadataKind.None;
    }

    private static DynamicHdrMetadataKind DetectSei(ReadOnlySpan<byte> escapedRbsp)
    {
        var reader = new RbspReader(escapedRbsp);
        DynamicHdrMetadataKind found = DynamicHdrMetadataKind.None;
        Span<byte> identifier = stackalloc byte[6];
        while (reader.TryRead(out byte value))
        {
            int payloadType = 0;
            while (value == byte.MaxValue)
            {
                if (payloadType > int.MaxValue - byte.MaxValue || !reader.TryRead(out value))
                    return found;
                payloadType += byte.MaxValue;
            }
            payloadType += value;

            if (!reader.TryRead(out value))
                return found;
            int payloadSize = 0;
            while (value == byte.MaxValue)
            {
                if (payloadSize > int.MaxValue - byte.MaxValue || !reader.TryRead(out value))
                    return found;
                payloadSize += byte.MaxValue;
            }
            payloadSize += value;

            for (int index = 0; index < payloadSize; index++)
            {
                if (!reader.TryRead(out byte payloadByte))
                    return found;
                if (index < identifier.Length)
                    identifier[index] = payloadByte;
            }

            if (payloadType == UserDataRegisteredItuT35 && payloadSize >= 3)
                found |= DetectItuT35(identifier[..Math.Min(payloadSize, identifier.Length)]);
        }

        return found;
    }

    private static DynamicHdrMetadataKind DetectAv1(ReadOnlySpan<byte> packet)
    {
        DynamicHdrMetadataKind found = DynamicHdrMetadataKind.None;
        int offset = 0;
        while (offset < packet.Length)
        {
            byte header = packet[offset++];
            if ((header & 0x81) != 0)
                break;

            int type = (header >> 3) & 0x0F;
            bool extension = (header & 0x04) != 0;
            bool hasSize = (header & 0x02) != 0;
            if (extension && offset++ >= packet.Length)
                break;

            int payloadSize;
            if (hasSize)
            {
                if (!TryReadLeb128(packet, ref offset, out ulong encodedSize) ||
                    encodedSize > int.MaxValue || encodedSize > (ulong)(packet.Length - offset))
                {
                    break;
                }
                payloadSize = (int)encodedSize;
            }
            else
            {
                payloadSize = packet.Length - offset;
            }

            ReadOnlySpan<byte> payload = packet.Slice(offset, payloadSize);
            if (type == Av1MetadataObu)
            {
                int metadataOffset = 0;
                if (TryReadLeb128(payload, ref metadataOffset, out ulong metadataType) &&
                    metadataType == Av1ItuT35Metadata)
                {
                    found |= DetectItuT35(payload[metadataOffset..]);
                }
            }

            offset += payloadSize;
            if (!hasSize)
                break;
        }

        return found;
    }

    private static DynamicHdrMetadataKind DetectItuT35(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3 || payload[0] != 0xB5)
            return DynamicHdrMetadataKind.None;

        int providerCode = (payload[1] << 8) | payload[2];
        if (providerCode == 0x003B)
            return DynamicHdrMetadataKind.DolbyVision;

        return payload.Length >= 6 &&
               providerCode == 0x003C &&
               payload[3] == 0 && payload[4] == 1 &&
               payload[5] == 4
            ? DynamicHdrMetadataKind.Hdr10Plus
            : DynamicHdrMetadataKind.None;
    }

    private static bool TryReadLeb128(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift <= 56 && offset < data.Length; shift += 7)
        {
            byte next = data[offset++];
            value |= (ulong)(next & 0x7F) << shift;
            if ((next & 0x80) == 0)
                return true;
        }

        return false;
    }

    private static bool TryFindStartCode(
        ReadOnlySpan<byte> data,
        int searchFrom,
        out int start,
        out int length)
    {
        for (int index = searchFrom; index + 2 < data.Length; index++)
        {
            if (data[index] != 0 || data[index + 1] != 0)
                continue;

            if (data[index + 2] == 1)
            {
                start = index;
                length = 3;
                return true;
            }

            if (index + 3 < data.Length && data[index + 2] == 0 && data[index + 3] == 1)
            {
                start = index;
                length = 4;
                return true;
            }
        }

        start = 0;
        length = 0;
        return false;
    }

    private ref struct RbspReader
    {
        private readonly ReadOnlySpan<byte> escaped;
        private int offset;
        private int zeroes;

        public RbspReader(ReadOnlySpan<byte> escaped)
        {
            this.escaped = escaped;
        }

        public bool TryRead(out byte value)
        {
            while (offset < escaped.Length)
            {
                value = escaped[offset++];
                if (zeroes == 2 && value == 3)
                {
                    zeroes = 0;
                    continue;
                }

                zeroes = value == 0 ? Math.Min(2, zeroes + 1) : 0;
                return true;
            }

            value = 0;
            return false;
        }
    }
}