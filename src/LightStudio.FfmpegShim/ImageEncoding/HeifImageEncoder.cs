#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LightStudio.FfmpegShim.ImageEncoding;

/// <summary>A picture stored as one HEIF image item, with the color description of its samples.</summary>
public sealed class HeifImageLayer
{
    public required StillPicture Picture { get; init; }

    public required int BitDepth { get; init; }

    /// <summary>3 for color, 1 for monochrome.</summary>
    public required int Channels { get; init; }

    /// <summary>ITU-T H.273 colour_primaries.</summary>
    public required int Primaries { get; init; }

    /// <summary>ITU-T H.273 transfer_characteristics.</summary>
    public required int Transfer { get; init; }

    /// <summary>ITU-T H.273 matrix_coefficients.</summary>
    public required int Matrix { get; init; }

    public required bool FullRange { get; init; }
}

/// <summary>An ISO 21496-1 gain map and the HDR rendition it recovers from the primary image.</summary>
public sealed class HeifGainMapLayer
{
    public required HeifImageLayer Image { get; init; }

    /// <summary>The GainMapMetadata syntax of ISO 21496-1, starting at minimum_version.</summary>
    public required byte[] Metadata { get; init; }

    public required int AlternatePrimaries { get; init; }

    public required int AlternateTransfer { get; init; }

    public required int AlternateBitDepth { get; init; }

    public required int AlternateChannels { get; init; }

    /// <summary>Peak of the HDR rendition in nits, for its content light level.</summary>
    public int MaxContentLightLevel { get; init; }
}

/// <summary>EXIF and XMP describing the primary image.</summary>
public sealed class HeifMetadata
{
    /// <summary>A TIFF block, without the <c>Exif</c> identifier.</summary>
    public byte[]? Exif { get; init; }

    /// <summary>A serialized XMP packet.</summary>
    public byte[]? Xmp { get; init; }
}

/// <summary>
/// Writes HEIC and AVIF still images. Pictures larger than the encoder codes at once, or than a
/// requested tile size, become grids of tiles. With a gain map, an ISO 21496-1 <c>tmap</c> derived
/// image item combines the primary image and a hidden gain map item, and is grouped with the primary
/// in an <c>altr</c> group so HDR-aware readers prefer it. EXIF and XMP items describe both.
/// </summary>
public static class HeifImageEncoder
{
    /// <summary>The smallest tile the encoder codes at exactly its size.</summary>
    public static int MinimumTileSize(StillImageEncoderInfo encoder) => encoder.IsHardware ? 256 : 64;

    /// <param name="lightLevel">MaxCLL and MaxFALL of an absolute-luminance primary image.</param>
    /// <param name="tileSize">
    /// Tiles for images larger than one tile, as multiples of 64 within the encoder's limits; null tiles
    /// only images the encoder cannot code at once.
    /// </param>
    public static void Write(Stream output, StillImageEncoderInfo encoder, StillEncodeSettings settings,
        HeifImageLayer primary, HeifGainMapLayer? gainMap = null, (int MaxCll, int MaxFall)? lightLevel = null,
        (int Width, int Height)? tileSize = null, HeifMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(primary);
        if (encoder.Codec == StillImageCodec.JpegXl)
            throw new ArgumentException("HEIF images need an HEVC or AV1 encoder.", nameof(encoder));
        if (tileSize is { } tiles && (!IsTileDimension(encoder, tiles.Width) || !IsTileDimension(encoder, tiles.Height)))
            throw new ArgumentOutOfRangeException(nameof(tileSize), tiles, $"{encoder.Name} cannot code {tiles.Width}x{tiles.Height} tiles.");

        var coder = new ItemCoder(encoder, settings, tileSize);
        var items = new List<HeifItem>();
        var references = new List<(string, ushort, ushort[])>();
        ushort primaryId = coder.AddImage(items, references, primary, "Color", hidden: false);
        if (lightLevel is { } level)
            items.First(item => item.Id == primaryId).Properties.Add(
                new HeifProperty(HeifWriter.ContentLightLevel(level.MaxCll, level.MaxFall), false));

        ushort[]? alternatives = null;
        if (gainMap is not null)
        {
            ushort gainId = coder.AddImage(items, references, gainMap.Image, "GMap", hidden: true);
            ushort toneMapId = coder.NextId();
            var toneMap = new HeifItem { Id = toneMapId, Type = "tmap", Name = "TMap", Data = [0, .. gainMap.Metadata] };
            toneMap.Properties.Add(new HeifProperty(HeifWriter.Ispe(primary.Picture.Width, primary.Picture.Height), false));
            toneMap.Properties.Add(new HeifProperty(HeifWriter.Pixi(gainMap.AlternateChannels, gainMap.AlternateBitDepth), false));
            toneMap.Properties.Add(new HeifProperty(
                HeifWriter.Nclx(gainMap.AlternatePrimaries, gainMap.AlternateTransfer, 0, true), false));
            if (gainMap.MaxContentLightLevel > 0)
                toneMap.Properties.Add(new HeifProperty(HeifWriter.ContentLightLevel(gainMap.MaxContentLightLevel, 0), false));
            items.Add(toneMap);
            references.Add(("dimg", toneMapId, [primaryId, gainId]));
            alternatives = [toneMapId, primaryId];
        }

        ushort[] described = alternatives is null ? [primaryId] : [primaryId, alternatives[0]];
        if (metadata?.Exif is { } exif)
        {
            // A TIFF header offset of six skips the Exif identifier, as camera files store it.
            ushort id = coder.NextId();
            items.Add(new HeifItem { Id = id, Type = "Exif", Data = [0, 0, 0, 6, .. "Exif\0\0"u8, .. exif] });
            references.Add(("cdsc", id, described));
        }

        if (metadata?.Xmp is { } xmp)
        {
            ushort id = coder.NextId();
            items.Add(new HeifItem { Id = id, Type = "mime", ContentType = "application/rdf+xml", Data = xmp });
            references.Add(("cdsc", id, described));
        }

        var brands = new List<string>();
        if (encoder.Codec == StillImageCodec.Hevc)
        {
            brands.AddRange(["mif1", "heic", "miaf"]);
        }
        else
        {
            brands.AddRange(["avif", "mif1", "miaf"]);
            if (coder.Av1ProfileBrand() is { } profile)
                brands.Add(profile);
        }

        if (gainMap is not null)
            brands.Add("tmap");
        HeifWriter.Write(output, encoder.Codec == StillImageCodec.Hevc ? "heic" : "avif", brands, items, primaryId,
            references, alternatives);
    }

    private static bool IsTileDimension(StillImageEncoderInfo encoder, int value) =>
        value % 64 == 0 && value >= MinimumTileSize(encoder) && value <= encoder.MaximumDimension;

    private sealed class ItemCoder(StillImageEncoderInfo encoder, StillEncodeSettings settings, (int Width, int Height)? tileSize)
    {
        private readonly List<Av1Bitstream.SequenceInfo> av1Sequences = [];
        private ushort lastId;

        public ushort NextId() => ++lastId;

        /// <summary>Adds a coded item, or a grid of coded tiles, and returns the item standing for the picture.</summary>
        public ushort AddImage(List<HeifItem> items, List<(string, ushort, ushort[])> references, HeifImageLayer layer,
            string name, bool hidden)
        {
            StillPicture picture = layer.Picture;
            HeifProperty[] description =
            [
                new(HeifWriter.Pixi(layer.Channels, layer.BitDepth), false),
                new(HeifWriter.Nclx(layer.Primaries, layer.Transfer, layer.Matrix, layer.FullRange), false),
            ];
            int maximum = encoder.MaximumDimension;
            bool requestedTiles = tileSize is { } requested && (picture.Width > requested.Width || picture.Height > requested.Height);
            if (!requestedTiles && picture.Width <= maximum && picture.Height <= maximum)
            {
                (int codedWidth, int codedHeight) = CodedSize(picture.Width, picture.Height);
                using StillPicture? padded = codedWidth == picture.Width && codedHeight == picture.Height
                    ? null : picture.Crop(0, 0, codedWidth, codedHeight);
                (HeifItem item, int decodedWidth, int decodedHeight) = Encode(NextId(), padded ?? picture, name, hidden);
                item.Properties.AddRange(description);
                if (decodedWidth != picture.Width || decodedHeight != picture.Height)
                    item.Properties.Add(new HeifProperty(
                        HeifWriter.TopLeftCrop(picture.Width, picture.Height, decodedWidth, decodedHeight), true));
                items.Add(item);
                return item.Id;
            }

            (int tileWidth, int tileHeight) = requestedTiles ? tileSize!.Value : (maximum / 2, maximum / 2);
            int columns = (picture.Width + tileWidth - 1) / tileWidth;
            int rows = (picture.Height + tileHeight - 1) / tileHeight;
            if (columns > 256 || rows > 256)
                throw new InvalidDataException("The image is too large for a HEIF grid.");
            var tiles = new ushort[rows * columns];
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    using StillPicture region = picture.Crop(column * tileWidth, row * tileHeight, tileWidth, tileHeight);
                    (HeifItem item, int decodedWidth, int decodedHeight) = Encode(NextId(), region, "Tile", hidden: true);
                    if (decodedWidth != tileWidth || decodedHeight != tileHeight)
                        throw new InvalidDataException(
                            $"A {tileWidth}x{tileHeight} grid tile was coded as {decodedWidth}x{decodedHeight}.");
                    item.Properties.AddRange(description);
                    items.Add(item);
                    tiles[row * columns + column] = item.Id;
                }
            }

            var grid = new HeifItem
            {
                Id = NextId(),
                Type = "grid",
                Name = name,
                Hidden = hidden,
                Data = HeifWriter.Grid(rows, columns, picture.Width, picture.Height),
            };
            grid.Properties.Add(new HeifProperty(HeifWriter.Ispe(picture.Width, picture.Height), false));
            grid.Properties.AddRange(description);
            items.Add(grid);
            references.Add(("dimg", grid.Id, tiles));
            return grid.Id;
        }

        /// <summary>The AVIF profile brand every AV1 item of the file satisfies, if any.</summary>
        public string? Av1ProfileBrand()
        {
            if (av1Sequences.Count == 0)
                return null;
            if (av1Sequences.All(s => s.Profile == 0 && s.Level <= 13))
                return "MA1B";
            if (av1Sequences.All(s => s.Profile <= 1 && s.Level <= 16))
                return "MA1A";
            return null;
        }

        private (HeifItem Item, int Width, int Height) Encode(ushort id, StillPicture picture, string name, bool hidden)
        {
            byte[] packet = StillImageEncoder.Encode(encoder, picture, settings);
            byte[] data;
            byte[] configuration;
            int width, height;
            if (encoder.Codec == StillImageCodec.Hevc)
            {
                List<byte[]> units = HevcBitstream.SplitAnnexB(packet);
                List<byte[]> vps = units.Where(u => HevcBitstream.NalType(u) == HevcBitstream.VpsType).ToList();
                List<byte[]> sps = units.Where(u => HevcBitstream.NalType(u) == HevcBitstream.SpsType).ToList();
                List<byte[]> pps = units.Where(u => HevcBitstream.NalType(u) == HevcBitstream.PpsType).ToList();
                if (sps.Count == 0)
                    throw new InvalidDataException("The HEVC encoder produced no sequence parameter set.");
                HevcBitstream.SequenceInfo info = HevcBitstream.ParseSps(sps[0]);
                (width, height) = (info.Width, info.Height);
                configuration = HeifWriter.Configuration("hvcC", HevcBitstream.ConfigurationRecord(info, vps, sps, pps));
                data = HevcBitstream.LengthPrefixed(units.Where(u => HevcBitstream.NalType(u) is not
                    (HevcBitstream.VpsType or HevcBitstream.SpsType or HevcBitstream.PpsType or HevcBitstream.AudType)));
            }
            else
            {
                (data, byte[] sequenceHeader) = Av1Bitstream.Prepare(packet);
                Av1Bitstream.SequenceInfo info = Av1Bitstream.ParseSequenceHeader(sequenceHeader);
                (width, height) = (info.Width, info.Height);
                av1Sequences.Add(info);
                configuration = HeifWriter.Configuration("av1C", Av1Bitstream.ConfigurationRecord(info));
            }

            if (width < picture.Width || height < picture.Height)
                throw new InvalidDataException($"The {encoder.Name} stream is {width}x{height}, expected {picture.Width}x{picture.Height}.");
            var item = new HeifItem
            {
                Id = id,
                Type = encoder.Codec == StillImageCodec.Hevc ? "hvc1" : "av01",
                Name = name,
                Hidden = hidden,
                Data = data,
            };
            item.Properties.Add(new HeifProperty(configuration, true));
            item.Properties.Add(new HeifProperty(HeifWriter.Ispe(width, height), false));
            return (item, width, height);
        }

        /// <summary>
        /// Pads to what the encoder codes reliably; a <c>clap</c> property crops the padding away again.
        /// Kvazaar and SVT-AV1 need multiples of 8 and a minimum size, and hardware encoders align to
        /// their coding blocks.
        /// </summary>
        private (int Width, int Height) CodedSize(int width, int height)
        {
            (int alignment, int minimum) = encoder.IsHardware ? (64, 256)
                : encoder.Codec == StillImageCodec.Hevc || encoder.Name == "libsvtav1" ? (8, 64)
                : (2, 16);
            return (Math.Max(minimum, (width + alignment - 1) / alignment * alignment),
                Math.Max(minimum, (height + alignment - 1) / alignment * alignment));
        }
    }
}
