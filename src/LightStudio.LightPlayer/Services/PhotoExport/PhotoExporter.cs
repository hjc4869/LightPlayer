using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.ImageEncoding;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Services.PhotoExport;

public enum PhotoExportFailure
{
    /// <summary>The picture or the requested size needs more memory than the device can spare.</summary>
    TooLarge,

    /// <summary>The picture cannot be saved as a still image, such as an animation.</summary>
    Unsupported,

    /// <summary>The picture could not be decoded again for saving.</summary>
    DecodeFailed,
}

public sealed class PhotoExportException(PhotoExportFailure failure) : Exception($"Photo export failed: {failure}.")
{
    public PhotoExportFailure Failure { get; } = failure;
}

/// <summary>Everything that decides how a photo is saved.</summary>
public sealed record PhotoExportOptions
{
    public required PhotoExportFormat Format { get; init; }

    /// <summary>The FFmpeg encoder of AVIF, HEIC and JPEG XL output.</summary>
    [JsonIgnore]
    public StillImageEncoderInfo? Encoder { get; init; }

    public required PhotoExportPixelFormat PixelFormat { get; init; }

    /// <summary>1 to 100.</summary>
    public int Quality { get; init; } = 90;

    public StillEncodeSpeed Speed { get; init; } = StillEncodeSpeed.Balanced;

    /// <summary>Tiles of AVIF and HEIC images larger than one tile, or null to tile only beyond the encoder's limits.</summary>
    public (int Width, int Height)? TileSize { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public PhotoExportResize Resize { get; init; } = PhotoExportResize.Stretch;

    /// <summary>Keeps transparency where the format can store it; otherwise the picture is placed on <see cref="BackgroundColor"/>.</summary>
    public bool TransparentBackground { get; init; }

    /// <summary>An sRGB color as 0xRRGGBB.</summary>
    public uint BackgroundColor { get; init; } = 0xFFFFFF;

    /// <summary>A further clockwise turn in degrees, as the picture is being viewed.</summary>
    public int Rotation { get; init; }

    public PhotoExportHdrEncoding Hdr { get; init; }

    /// <summary>The tone curve for absolute-luminance sources, or null for the built-in curve.</summary>
    public VideoSdrToneMappingOptions? ToneMapping { get; init; }

    /// <summary>Whether absolute-luminance sources saved as PQ are tone mapped to <see cref="TargetPeakNits"/>.</summary>
    public bool ToneMapHdr { get; init; }

    public double TargetPeakNits { get; init; } = 1000;

    /// <summary>Luminance SDR white of a gain-map source is given when saved as PQ.</summary>
    public double SdrWhiteNits { get; init; } = PhotoExportColor.ReferenceWhiteNits;

    /// <summary>The primaries of SDR and gain-map pictures; PQ pictures use BT.2020.</summary>
    public PhotoExportColorSpace ColorSpace { get; init; }

    public PhotoExportGamutMapping GamutMapping { get; init; } = PhotoExportGamutMapping.PreserveHue;

    public PhotoExportMetadataMode Metadata { get; init; }

    internal bool Dither { get; init; }
}

/// <summary>Decodes a photo again at full resolution and encodes it with the requested options.</summary>
internal static class PhotoExporter
{
    /// <summary>
    /// Encodes the photo <paramref name="decode"/> returns, with the source metadata <paramref name="readMetadata"/>
    /// returns, into a stream positioned at its start.
    /// </summary>
    public static async Task<MemoryStream> ExportAsync(Func<CancellationToken, Task<PhotoExportSourceMetadata?>> readMetadata,
        Func<CancellationToken, Task<VideoFrame?>> decode, PhotoExportOptions options, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        PhotoExportSourceMetadata? metadata = await readMetadata(cancellationToken).ConfigureAwait(false);
        long metadataRead = Stopwatch.GetTimestamp();
        VideoFrame frame = await decode(cancellationToken).ConfigureAwait(false)
            ?? throw new PhotoExportException(PhotoExportFailure.DecodeFailed);
        long decoded = Stopwatch.GetTimestamp();
        string source = $"{frame.Width}x{frame.Height}";
        PhotoExportScene scene;
        PhotoExportInput? input = null;
        try
        {
            EnsureMemory(frame, options);
            input = await PhotoExportSource.OpenAsync(frame, cancellationToken).ConfigureAwait(false);
            PhotoExportInput opened = input;
            scene = await Task.Run(() => Place(opened, options, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            input?.Dispose();
            Task? released = frame.PhotoSurface?.WhenReleased;
            frame.Release();
            if (released is not null)
                await released.ConfigureAwait(false);
        }

        string timings = $"metadata {Stopwatch.GetElapsedTime(started, metadataRead).TotalMilliseconds:F0} ms, " +
            $"decode {Stopwatch.GetElapsedTime(metadataRead, decoded).TotalMilliseconds:F0} ms, " +
            $"place {Stopwatch.GetElapsedTime(decoded).TotalMilliseconds:F0} ms";
        using (scene)
            return await Task.Run(() => Encode(scene, options, metadata, source, timings, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>The EXIF and XMP of a seekable source, or null when they cannot be read, so the picture is saved without them.</summary>
    public static PhotoExportSourceMetadata? ReadMetadata(Stream stream, string name, CancellationToken cancellationToken)
    {
        try
        {
            return PhotoExportMetadata.Read(stream, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            AppLog.Warning("Photo.Export", $"Could not read the metadata of {name}; the copy is saved without it.", exception);
            return null;
        }
    }

    /// <param name="gainScale">Canvas pixels each pixel of the stored gain map covers, or null to follow the source.</param>
    internal static PhotoExportScene Place(PhotoExportInput input, PhotoExportOptions options, CancellationToken cancellationToken,
        int? gainScale = null)
    {
        var orientation = new PhotoExportOrientation((input.Rotation + options.Rotation % 360 + 360) % 360, input.Mirror,
            input.PixelAspectRatio, input.Width, input.Height);
        PhotoExportLayout layout = PhotoExportLayout.Create(orientation.DisplayWidth, orientation.DisplayHeight,
            options.Width, options.Height, options.Resize);
        var statistics = new PhotoExportGamutStatistics();
        LinearImage canvas = PhotoExportGeometry.Place(input.Base, orientation, layout, 1, statistics, cancellationToken);
        LinearImage? gain = null;
        try
        {
            double gainRatio = 1;
            if (input.Gain is { } gainRows)
            {
                gainRatio = (gainRows.Width / (double)input.Width + gainRows.Height / (double)input.Height) / 2;
                gain = PhotoExportGeometry.Place(gainRows, orientation, layout, gainRatio, null, cancellationToken);
            }

            int sceneGainScale = 2;
            if (input.Kind == PhotoExportSourceKind.GainMap)
            {
                int ratio = Math.Clamp((int)Math.Round(1 / gainRatio), 1, 4);
                sceneGainScale = ratio == 3 ? 2 : ratio;
            }

            return new PhotoExportScene
            {
                Canvas = canvas,
                Gain = gain,
                GainParameters = input.GainParameters,
                Kind = input.Kind,
                DeclaredPeakNits = input.DeclaredPeakNits,
                SourceGamuts = input.SourceGamuts,
                Statistics = statistics,
                GainScale = gainScale ?? sceneGainScale,
            };
        }
        catch
        {
            canvas.Dispose();
            gain?.Dispose();
            throw;
        }
    }

    private static MemoryStream Encode(PhotoExportScene scene, PhotoExportOptions options, PhotoExportSourceMetadata? metadata,
        string source, string timings, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        options = ResolveHdr(scene, options);
        PhotoExportHdrEncoding hdr = options.Hdr;
        PhotoExportPrimaries primaries = hdr == PhotoExportHdrEncoding.Pq ? PhotoExportPrimaries.Bt2020 : options.ColorSpace switch
        {
            PhotoExportColorSpace.Srgb => PhotoExportPrimaries.Bt709,
            PhotoExportColorSpace.DisplayP3 => PhotoExportPrimaries.DisplayP3,
            PhotoExportColorSpace.Rec2020 => PhotoExportPrimaries.Bt2020,
            _ => scene.Statistics.NeedsWideGamut ? PhotoExportPrimaries.DisplayP3 : PhotoExportPrimaries.Bt709,
        };

        using PhotoExportPicture picture = Render(scene, options, primaries, cancellationToken);
        long rendered = Stopwatch.GetTimestamp();
        PhotoExportMetadataBlocks? blocks = PhotoExportMetadata.Adapt(metadata, options.Metadata, picture.Width, picture.Height,
            srgb: primaries == PhotoExportPrimaries.Bt709 && !picture.IsPq, jpeg: options.Format == PhotoExportFormat.Jpeg);
        var output = new MemoryStream();
        PhotoExportEncoders.Write(output, picture, options, blocks, cancellationToken);
        PhotoExportGamutStatistics statistics = scene.Statistics;
        double visible = Math.Max(1, statistics.Total);
        AppLog.Information("Photo.Export",
            $"Saved {options.Format} {picture.Width}x{picture.Height} from {source} ({options.PixelFormat}, hdr={hdr}, " +
            $"source={scene.Kind}, primaries={primaries}, gamut={options.GamutMapping}, " +
            $"outsideSrgb={statistics.OutsideSrgb / visible:P3}, outsideP3={statistics.OutsideDisplayP3 / visible:P3}, " +
            $"gainMap={picture.GainMap is not null}, metadata={options.Metadata} (exif={blocks?.Exif?.Length ?? 0} B, " +
            $"xmp={(blocks?.Xmp is null ? "no" : "yes")}), encoder={options.Encoder?.Name ?? "skia"}): {timings}, " +
            $"render {Stopwatch.GetElapsedTime(started, rendered).TotalMilliseconds:F0} ms, " +
            $"encode {Stopwatch.GetElapsedTime(rendered).TotalMilliseconds:F0} ms; {output.Length} bytes.");
        output.Position = 0;
        return output;
    }

    /// <summary>The options with the HDR storage the scene and format allow, and dithering for absolute sources.</summary>
    internal static PhotoExportOptions ResolveHdr(PhotoExportScene scene, PhotoExportOptions options)
    {
        PhotoExportHdrEncoding hdr = scene.Kind == PhotoExportSourceKind.Sdr ? PhotoExportHdrEncoding.None : options.Hdr switch
        {
            PhotoExportHdrEncoding.GainMap when !PhotoExportFormats.SupportsGainMap(options.Format) => PhotoExportHdrEncoding.None,
            PhotoExportHdrEncoding.Pq when !PhotoExportFormats.SupportsPq(options.Format) => PhotoExportHdrEncoding.None,
            _ => options.Hdr,
        };
        return options with { Hdr = hdr, Dither = scene.Kind == PhotoExportSourceKind.Absolute };
    }

    /// <summary>Renders the stored renditions of a placed scene with options from <see cref="ResolveHdr"/>.</summary>
    internal static PhotoExportPicture Render(PhotoExportScene scene, PhotoExportOptions options, PhotoExportPrimaries primaries,
        CancellationToken cancellationToken)
    {
        Vector3 background = new(
            PhotoExportColor.SrgbToLinear(((options.BackgroundColor >> 16) & 0xFF) / 255f),
            PhotoExportColor.SrgbToLinear(((options.BackgroundColor >> 8) & 0xFF) / 255f),
            PhotoExportColor.SrgbToLinear((options.BackgroundColor & 0xFF) / 255f));
        var settings = new PhotoExportRenderSettings(
            scene.Kind,
            options.Hdr,
            KeepAlpha: options.TransparentBackground && options.Hdr != PhotoExportHdrEncoding.GainMap &&
                PhotoExportFormats.SupportsAlpha(options.Format, options.PixelFormat),
            background,
            scene.DeclaredPeakNits,
            options.ToneMapping,
            options.ToneMapHdr,
            options.TargetPeakNits,
            options.SdrWhiteNits,
            primaries,
            new PhotoExportGamutMapper(options.GamutMapping, primaries, scene.SourceGamuts));
        return PhotoExportRenderer.Render(scene.Canvas, scene.Gain, scene.GainParameters, settings, scene.GainScale,
            cancellationToken);
    }

    /// <summary>
    /// Rejects work whose buffers would not fit in half of the available memory: the decoded source, rows
    /// resampled across, the placed canvas, and the rendered and encoder-ready output.
    /// </summary>
    private static void EnsureMemory(VideoFrame frame, PhotoExportOptions options)
    {
        long source = (long)frame.Width * frame.Height;
        long output = (long)options.Width * options.Height;
        bool quarterTurn = (frame.Rotation + options.Rotation) % 180 != 0;
        int targetWidth = quarterTurn ? options.Height : options.Width, targetHeight = quarterTurn ? options.Width : options.Height;
        long resampled = targetWidth == frame.Width && targetHeight == frame.Height ? 0 :
            PhotoExportGeometry.ResampledPixels(frame.Height, targetWidth, targetHeight);
        long required = source * 4 + resampled * 16 + output * 32;
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (output > int.MaxValue / 8 || available > 0 && required > available / 2)
            throw new PhotoExportException(PhotoExportFailure.TooLarge);
    }
}
