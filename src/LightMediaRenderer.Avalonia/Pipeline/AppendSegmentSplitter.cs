using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using LightStudio.FfmpegShim.Demuxing;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>A complete initialization segment or media unit cut out of a buffer's byte stream.</summary>
/// <remarks>Media unit bytes are rented from <see cref="ArrayPool{T}.Shared"/>; <see cref="Release"/> returns them.</remarks>
internal readonly record struct SplitUnit(bool IsInitialization, byte[] Data, int Length, bool Encrypted)
{
    public ReadOnlySpan<byte> Bytes => Data.AsSpan(0, Length);

    public void Release()
    {
        if (!IsInitialization)
            ArrayPool<byte>.Shared.Return(Data);
    }
}

/// <summary>
/// Reassembles appends of arbitrary size into initialization segments and media units. A media unit
/// is one <c>moof</c> with the <c>mdat</c> boxes its sample data lies in, or one WebM Cluster.
/// </summary>
internal sealed class AppendSegmentSplitter
{
    private const uint Ftyp = 0x66747970, Moov = 0x6D6F6F76, Moof = 0x6D6F6F66, Mdat = 0x6D646174;
    private const uint Free = 0x66726565, Skip = 0x736B6970, Uuid = 0x75756964, Pssh = 0x70737368;
    private const uint Trak = 0x7472616B, Mdia = 0x6D646961, Minf = 0x6D696E66, Stbl = 0x7374626C, Stsd = 0x73747364;
    private const uint Mvex = 0x6D766578, Trex = 0x74726578, Traf = 0x74726166, Tfhd = 0x74666864, Trun = 0x7472756E;

    private const uint EbmlHeader = 0x1A45DFA3, Segment = 0x18538067, Info = 0x1549A966, Tracks = 0x1654AE6B;
    private const uint Cluster = 0x1F43B675, TrackEntry = 0xAE, ContentEncodings = 0x6D80, ContentEncoding = 0x6240;
    private const uint ContentEncodingType = 0x5033, ContentEncryption = 0x5035;

    // An unknown-size Segment, so the initialization segment never claims the original file's length.
    private static readonly byte[] UnknownSegment = [0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    private byte[] pending = new byte[64 * 1024];
    private int start;
    private int scan;
    private int end;
    private SegmentContainer container;
    private bool requireInitialization;

    // Fragmented MP4: where the open initialization or fragment begins in the pending bytes.
    private int initializationStart = -1;
    private int fragmentStart = -1;
    private long fragmentDataEnd;
    private readonly Dictionary<uint, uint> defaultSampleSizes = new();

    // WebM.
    private WebMState webmState;
    private ByteBuilder? webmInitialization;
    private bool webmSeenInfo;
    private int clusterStart = -1;

    public AppendSegmentSplitter(SegmentContainer container) => this.container = container;

    private enum WebMState
    {
        None,
        Initialization,
        Segment,
        UnknownSizeCluster,
    }

    public SegmentContainer Container => container;

    /// <summary>Whether an initialization segment has been seen in the current container.</summary>
    public bool HasInitialization { get; private set; }

    /// <summary>Bytes received that do not yet form a complete unit.</summary>
    public int PendingBytes => end - start;

    /// <summary>
    /// Adds appended bytes and moves every unit they complete to <paramref name="units"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The byte stream is malformed.</exception>
    public void Push(ReadOnlySpan<byte> data, List<SplitUnit> units)
    {
        Append(data);
        if (container == SegmentContainer.Mp4)
            SplitMp4(units);
        else
            SplitWebM(units);
        Compact();
    }

    /// <summary>Completes what only the end of the stream delimits: an unknown-size Cluster.</summary>
    public void Finish(List<SplitUnit> units)
    {
        if (container == SegmentContainer.WebM && webmState == WebMState.UnknownSizeCluster)
            CloseUnknownCluster(scan, units);
        Compact();
    }

    /// <summary>Drops partial bytes, keeping what the last initialization segment established.</summary>
    public void Reset()
    {
        start = scan = end = 0;
        initializationStart = -1;
        fragmentStart = -1;
        fragmentDataEnd = 0;
        clusterStart = -1;
        webmInitialization = null;
        webmState = HasInitialization ? WebMState.Segment : WebMState.None;
    }

    /// <summary>Switches the byte stream format; an initialization segment has to come next.</summary>
    public void ChangeContainer(SegmentContainer value)
    {
        container = value;
        HasInitialization = false;
        requireInitialization = true;
        defaultSampleSizes.Clear();
        Reset();
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (end + data.Length > pending.Length)
        {
            int live = end - start;
            if (live + data.Length > pending.Length || start == 0)
            {
                long size = Math.Max(pending.Length * 2L, live + (long)data.Length);
                if (size > Array.MaxLength)
                    throw new InvalidDataException("An append is too large to buffer.");
                var grown = new byte[size];
                pending.AsSpan(start, live).CopyTo(grown);
                pending = grown;
            }
            else
            {
                pending.AsSpan(start, live).CopyTo(pending);
            }

            Rebase(start);
        }

        data.CopyTo(pending.AsSpan(end));
        end += data.Length;
    }

    private void Compact()
    {
        if (start == end)
        {
            Rebase(start);
            return;
        }

        // Moving a large unit that is still arriving on every append would cost a copy per slice.
        if (start > pending.Length / 2)
        {
            pending.AsSpan(start, end - start).CopyTo(pending);
            Rebase(start);
        }
    }

    private void Rebase(int offset)
    {
        if (offset == 0)
            return;
        start -= offset;
        scan -= offset;
        end -= offset;
        if (initializationStart >= 0)
            initializationStart -= offset;
        if (fragmentStart >= 0)
            fragmentStart -= offset;
        if (clusterStart >= 0)
            clusterStart -= offset;
    }

    private void SplitMp4(List<SplitUnit> units)
    {
        while (end - scan >= 8)
        {
            ReadOnlySpan<byte> header = pending.AsSpan(scan, end - scan);
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            int headerLength = 8;
            if (size == 1)
            {
                if (header.Length < 16)
                    return;
                size = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerLength = 16;
            }
            else if (size == 0)
            {
                throw new InvalidDataException("A box that extends to the end of the stream cannot be delimited.");
            }

            if (size < (ulong)headerLength)
                throw new InvalidDataException("A box is smaller than its header.");
            if (size > int.MaxValue)
                throw new InvalidDataException("A box is too large to buffer.");
            if ((ulong)(end - scan) < size)
                return;

            int boxStart = scan;
            int boxEnd = scan + (int)size;
            scan = boxEnd;
            Mp4Box(type, boxStart, boxEnd, units);
        }
    }

    private void Mp4Box(uint type, int boxStart, int boxEnd, List<SplitUnit> units)
    {
        switch (type)
        {
            case Ftyp:
                if (fragmentStart >= 0)
                    throw new InvalidDataException("An initialization segment starts inside a media segment.");
                initializationStart = boxStart;
                return;

            case Moov:
                if (fragmentStart >= 0)
                    throw new InvalidDataException("An initialization segment starts inside a media segment.");
                int initStart = initializationStart >= 0 ? initializationStart : boxStart;
                byte[] initialization = pending.AsSpan(initStart, boxEnd - initStart).ToArray();
                initializationStart = -1;
                start = boxEnd;
                ReadTrackDefaults(pending.AsSpan(boxStart, boxEnd - boxStart));
                HasInitialization = true;
                requireInitialization = false;
                units.Add(new SplitUnit(true, initialization, initialization.Length, Mp4Encrypted(initialization)));
                return;

            case Moof:
                if (!HasInitialization || requireInitialization)
                    throw new InvalidDataException("A media segment arrived before an initialization segment.");
                if (initializationStart >= 0)
                    throw new InvalidDataException("An ftyp box is not followed by moov.");
                if (fragmentStart >= 0)
                    throw new InvalidDataException("A moof box is not followed by mdat.");
                fragmentStart = boxStart;
                fragmentDataEnd = FragmentDataEnd(pending.AsSpan(boxStart, boxEnd - boxStart));
                return;

            case Mdat:
                if (fragmentStart < 0)
                    throw new InvalidDataException("An mdat box has no moof before it.");

                // Several mdat boxes may follow one moof; the unit ends once every sample it
                // describes has arrived.
                if (fragmentDataEnd > boxEnd - fragmentStart)
                    return;
                EmitRented(fragmentStart, boxEnd, units);
                fragmentStart = -1;
                start = boxEnd;
                return;

            default:
                if (initializationStart >= 0)
                {
                    if (type is Free or Skip or Uuid or Pssh)
                        return;
                    throw new InvalidDataException("An unexpected box lies inside an initialization segment.");
                }

                if (fragmentStart >= 0)
                    throw new InvalidDataException("A box other than mdat follows moof.");

                // styp, sidx, ssix, prft, emsg, free, skip and unknown boxes carry nothing for playback.
                start = boxEnd;
                return;
        }
    }

    private void EmitRented(int from, int to, List<SplitUnit> units)
    {
        int length = to - from;
        byte[] data = ArrayPool<byte>.Shared.Rent(length);
        pending.AsSpan(from, length).CopyTo(data);
        units.Add(new SplitUnit(false, data, length, false));
    }

    /// <summary>
    /// Offset from the start of a moof box to the end of the last sample its track runs describe, or
    /// 0 when that cannot be told, in which case the first mdat completes the unit.
    /// </summary>
    private long FragmentDataEnd(ReadOnlySpan<byte> moof)
    {
        long dataEnd = 0;
        long previousTrafEnd = 0;
        bool first = true;
        foreach ((uint type, int offset, int length) in Children(moof, 8))
        {
            if (type != Traf)
                continue;

            ReadOnlySpan<byte> traf = moof.Slice(offset, length);
            long baseOffset = first ? 0 : previousTrafEnd;
            first = false;
            uint defaultSize = 0;
            bool hasDefaultSize = false;
            long runEnd = -1;
            foreach ((uint childType, int childOffset, int childLength) in Children(traf, 8))
            {
                ReadOnlySpan<byte> box = traf.Slice(childOffset, childLength);
                if (childType == Tfhd)
                {
                    if (box.Length < 16)
                        return 0;
                    uint flags = BinaryPrimitives.ReadUInt32BigEndian(box[8..]) & 0xFFFFFF;
                    uint trackId = BinaryPrimitives.ReadUInt32BigEndian(box[12..]);
                    int position = 16;

                    // An absolute base data offset points into the original file, not into what was appended.
                    if ((flags & 0x1) != 0)
                        return 0;
                    if ((flags & 0x20000) != 0)
                        baseOffset = 0;
                    if ((flags & 0x2) != 0)
                        position += 4;
                    if ((flags & 0x8) != 0)
                        position += 4;
                    if ((flags & 0x10) != 0)
                    {
                        if (box.Length < position + 4)
                            return 0;
                        defaultSize = BinaryPrimitives.ReadUInt32BigEndian(box[position..]);
                        hasDefaultSize = true;
                    }
                    else if (defaultSampleSizes.TryGetValue(trackId, out uint trackDefault))
                    {
                        defaultSize = trackDefault;
                        hasDefaultSize = true;
                    }
                }
                else if (childType == Trun)
                {
                    if (box.Length < 16)
                        return 0;
                    uint flags = BinaryPrimitives.ReadUInt32BigEndian(box[8..]) & 0xFFFFFF;
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(box[12..]);
                    int position = 16;
                    long dataStart = runEnd >= 0 ? runEnd : baseOffset;
                    if ((flags & 0x1) != 0)
                    {
                        if (box.Length < position + 4)
                            return 0;
                        dataStart = baseOffset + BinaryPrimitives.ReadInt32BigEndian(box[position..]);
                        position += 4;
                    }

                    if ((flags & 0x4) != 0)
                        position += 4;

                    int fieldBytes = 4 * (((flags & 0x100) != 0 ? 1 : 0) + ((flags & 0x200) != 0 ? 1 : 0) +
                        ((flags & 0x400) != 0 ? 1 : 0) + ((flags & 0x800) != 0 ? 1 : 0));
                    long total = 0;
                    if ((flags & 0x200) != 0)
                    {
                        int sizeField = (flags & 0x100) != 0 ? 4 : 0;
                        if (box.Length < position + (long)count * fieldBytes)
                            return 0;
                        for (uint sample = 0; sample < count; sample++)
                        {
                            total += BinaryPrimitives.ReadUInt32BigEndian(box[(position + sizeField)..]);
                            position += fieldBytes;
                        }
                    }
                    else if (hasDefaultSize)
                    {
                        total = (long)count * defaultSize;
                    }
                    else
                    {
                        return 0;
                    }

                    runEnd = dataStart + total;
                    dataEnd = Math.Max(dataEnd, runEnd);
                }
            }

            if (runEnd >= 0)
                previousTrafEnd = runEnd;
        }

        return dataEnd;
    }

    private void ReadTrackDefaults(ReadOnlySpan<byte> moov)
    {
        defaultSampleSizes.Clear();
        foreach ((uint type, int offset, int length) in Children(moov, 8))
        {
            if (type != Mvex)
                continue;
            ReadOnlySpan<byte> mvex = moov.Slice(offset, length);
            foreach ((uint childType, int childOffset, int childLength) in Children(mvex, 8))
            {
                if (childType != Trex || childLength < 32)
                    continue;
                ReadOnlySpan<byte> trex = mvex.Slice(childOffset, childLength);
                defaultSampleSizes[BinaryPrimitives.ReadUInt32BigEndian(trex[12..])] =
                    BinaryPrimitives.ReadUInt32BigEndian(trex[24..]);
            }
        }
    }

    /// <summary>Whether an MP4 initialization segment declares a protected sample entry.</summary>
    internal static bool Mp4Encrypted(ReadOnlySpan<byte> initialization) => Mp4Protected(initialization, 0);

    private static bool Mp4Protected(ReadOnlySpan<byte> data, int headerSkip)
    {
        foreach ((uint type, int offset, int length) in Children(data, headerSkip))
        {
            ReadOnlySpan<byte> box = data.Slice(offset, length);
            switch (type)
            {
                case Moov or Trak or Mdia or Minf or Stbl:
                    if (Mp4Protected(box, 8))
                        return true;
                    break;

                // A full box header and an entry count come before the sample entries.
                case Stsd:
                    if (box.Length > 16 && Mp4Protected(box[16..], 0))
                        return true;
                    break;

                // encv, enca, enct, encs, encf, encm.
                case 0x656E6376 or 0x656E6361 or 0x656E6374 or 0x656E6373 or 0x656E6366 or 0x656E636D:
                    return true;
            }
        }

        return false;
    }

    /// <summary>Top-level boxes of <paramref name="data"/> after <paramref name="skip"/> bytes, as offsets into it.</summary>
    private static List<(uint Type, int Offset, int Length)> Children(ReadOnlySpan<byte> data, int skip)
    {
        var children = new List<(uint, int, int)>();
        int position = skip;
        while (data.Length - position >= 8)
        {
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]);
            if (size == 1)
            {
                if (data.Length - position < 16)
                    break;
                size = BinaryPrimitives.ReadUInt64BigEndian(data[(position + 8)..]);
            }
            else if (size == 0)
            {
                size = (ulong)(data.Length - position);
            }

            if (size < 8 || size > (ulong)(data.Length - position))
                break;
            children.Add((type, position, (int)size));
            position += (int)size;
        }

        return children;
    }

    private void SplitWebM(List<SplitUnit> units)
    {
        while (true)
        {
            ReadOnlySpan<byte> available = pending.AsSpan(scan, end - scan);
            int headerLength = ReadElementHeader(available, out uint id, out ulong size, out bool unknown);
            if (headerLength == 0)
                return;
            if (headerLength < 0)
                throw new InvalidDataException("An EBML element header is malformed.");

            if (webmState == WebMState.UnknownSizeCluster)
            {
                if (IsClusterChild(id))
                {
                    if (unknown)
                        throw new InvalidDataException("A Cluster child has an unknown size.");
                    if ((ulong)available.Length < (ulong)headerLength + size)
                        return;
                    scan += headerLength + (int)size;
                    continue;
                }

                // Anything that is not one of its children ends an unknown-size Cluster.
                CloseUnknownCluster(scan, units);
            }

            if (id == EbmlHeader)
            {
                if (unknown || (ulong)available.Length < (ulong)headerLength + size)
                {
                    if (unknown)
                        throw new InvalidDataException("The EBML header has an unknown size.");
                    return;
                }

                webmInitialization = new ByteBuilder();
                webmInitialization.Add(available[..(headerLength + (int)size)]);
                webmState = WebMState.Initialization;
                webmSeenInfo = false;
                Consume(headerLength + (int)size);
                continue;
            }

            if (id == Segment)
            {
                if (webmState != WebMState.Initialization || webmInitialization is null)
                    throw new InvalidDataException("A Segment has no EBML header before it.");
                webmInitialization.Add(UnknownSegment);
                Consume(headerLength);
                continue;
            }

            if (webmState == WebMState.None)
                throw new InvalidDataException("Data arrived before the EBML header.");

            if (id == Cluster)
            {
                if (webmState == WebMState.Initialization)
                    throw new InvalidDataException("A Cluster arrived before Tracks.");
                if (requireInitialization)
                    throw new InvalidDataException("A media segment arrived before an initialization segment.");
                if (unknown)
                {
                    clusterStart = scan;
                    scan += headerLength;
                    webmState = WebMState.UnknownSizeCluster;
                    continue;
                }

                if ((ulong)available.Length < (ulong)headerLength + size)
                    return;
                int clusterEnd = scan + headerLength + (int)size;
                EmitRented(scan, clusterEnd, units);
                scan = start = clusterEnd;
                continue;
            }

            if (unknown)
                throw new InvalidDataException("Only a Segment or a Cluster may have an unknown size.");
            if ((ulong)available.Length < (ulong)headerLength + size)
                return;

            int length = headerLength + (int)size;
            if (webmState == WebMState.Initialization && id is Info or Tracks && webmInitialization is not null)
            {
                webmInitialization.Add(available[..length]);
                if (id == Info)
                {
                    webmSeenInfo = true;
                }
                else
                {
                    if (!webmSeenInfo)
                        throw new InvalidDataException("Tracks arrived before Info.");
                    byte[] initialization = webmInitialization.ToArray();
                    webmInitialization = null;
                    webmState = WebMState.Segment;
                    HasInitialization = true;
                    requireInitialization = false;
                    units.Add(new SplitUnit(true, initialization, initialization.Length, WebMEncrypted(initialization)));
                }
            }

            // SeekHead, Void, CRC-32, Cues, Tags, Chapters, Attachments and unknown Segment children are skipped.
            Consume(length);
        }
    }

    private void Consume(int length)
    {
        scan += length;
        start = scan;
    }

    private void CloseUnknownCluster(int clusterEnd, List<SplitUnit> units)
    {
        EmitRented(clusterStart, clusterEnd, units);
        clusterStart = -1;
        start = clusterEnd;
        webmState = WebMState.Segment;
    }

    private static bool IsClusterChild(uint id) => id switch
    {
        // Timestamp, SilentTracks, Position, PrevSize, SimpleBlock, BlockGroup, EncryptedBlock, Void, CRC-32.
        0xE7 or 0x5854 or 0xA7 or 0xAB or 0xA3 or 0xA0 or 0xAF or 0xEC or 0xBF => true,
        _ => false,
    };

    /// <summary>Reads an element header: its length, 0 when more bytes are needed, or -1 when invalid.</summary>
    private static int ReadElementHeader(ReadOnlySpan<byte> data, out uint id, out ulong size, out bool unknown)
    {
        id = 0;
        size = 0;
        unknown = false;
        if (data.Length < 1)
            return 0;

        int idLength = (data[0] & 0x80) != 0 ? 1 : (data[0] & 0x40) != 0 ? 2 : (data[0] & 0x20) != 0 ? 3 : (data[0] & 0x10) != 0 ? 4 : -1;
        if (idLength < 0)
            return -1;
        if (data.Length < idLength + 1)
            return 0;

        for (int i = 0; i < idLength; i++)
            id = id << 8 | data[i];

        byte first = data[idLength];
        int sizeLength = 1;
        int mask = 0x80;
        while (sizeLength <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            sizeLength++;
        }

        if (sizeLength > 8)
            return -1;
        if (data.Length < idLength + sizeLength)
            return 0;

        size = (ulong)(first & (mask - 1));
        for (int i = 1; i < sizeLength; i++)
            size = size << 8 | data[idLength + i];
        unknown = size == (1UL << (7 * sizeLength)) - 1;
        return idLength + sizeLength;
    }

    /// <summary>Whether a WebM initialization segment declares encrypted content in a track.</summary>
    internal static bool WebMEncrypted(ReadOnlySpan<byte> data)
    {
        int position = 0;
        while (position < data.Length)
        {
            int headerLength = ReadElementHeader(data[position..], out uint id, out ulong size, out bool unknown);
            if (headerLength <= 0)
                break;
            int body = position + headerLength;
            int length = unknown || size > (ulong)(data.Length - body) ? data.Length - body : (int)size;
            switch (id)
            {
                case Segment or Tracks or TrackEntry or ContentEncodings or ContentEncoding:
                    if (WebMEncrypted(data.Slice(body, length)))
                        return true;
                    break;

                case ContentEncryption:
                    return true;

                case ContentEncodingType:
                    if (length >= 1 && data[body + length - 1] == 1)
                        return true;
                    break;
            }

            position = body + length;
        }

        return false;
    }

    private sealed class ByteBuilder
    {
        private byte[] buffer = new byte[4096];
        private int length;

        public void Add(ReadOnlySpan<byte> data)
        {
            if (length + data.Length > buffer.Length)
                Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + data.Length));
            data.CopyTo(buffer.AsSpan(length));
            length += data.Length;
        }

        public byte[] ToArray() => buffer.AsSpan(0, length).ToArray();
    }
}
