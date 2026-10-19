using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.Logging;

namespace LightStudio.VideoValidation;

public sealed record ValidationGenerationOptions(
    string OutputDirectory,
    string FfmpegPath = "ffmpeg",
    string FfprobePath = "ffprobe");

public sealed record GeneratedValidationAsset
{
    public string Id { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public string ReferencePath { get; init; } = string.Empty;

    /// <summary>
    /// Independent FFmpeg diagnostic normalized to the 203-nit BT.2408 content reference. Planes
    /// are G, B, R little-endian float32, matching <c>gbrpf32le</c>. Empty for non-HDR assets.
    /// </summary>
    public string LinearReferencePath { get; init; } = string.Empty;

    /// <summary>
    /// First frame after FFmpeg independently expands YUV range and matrix into encoded RGB, before
    /// PQ/HLG or gamut processing. Planes are G, B, R little-endian float32.
    /// </summary>
    public string SignalReferencePath { get; init; } = string.Empty;

    public string LogPath { get; init; } = string.Empty;

    public bool Generated { get; init; }

    public bool Skipped { get; init; }

    public string Message { get; init; } = string.Empty;

    public long ByteLength { get; init; }

    public string Sha256 { get; init; } = string.Empty;

    public string ProbeOutput { get; init; } = string.Empty;
}

public sealed record ValidationAssetManifest
{
    public const int CurrentVersion = 4;

    public int Version { get; init; } = CurrentVersion;

    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string FfmpegVersion { get; init; } = string.Empty;

    public IReadOnlyList<GeneratedValidationAsset> Assets { get; init; } = [];
}

public static class ValidationAssetGenerator
{
    private const int DurationSeconds = 3;
    private const int FrameCount = RawVideoSourceProvider.FrameRate * DurationSeconds;
    private const int HdrSourcePeakNits = 1000;
    private const int Bt2408ReferenceWhiteNits = 203;

    public static async Task<ValidationAssetManifest> GenerateAsync(
        ValidationGenerationOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        string assetDirectory = Path.Combine(outputDirectory, "assets");
        string referenceDirectory = Path.Combine(outputDirectory, "references");
        string logDirectory = Path.Combine(outputDirectory, "logs", "generation");
        Directory.CreateDirectory(assetDirectory);
        Directory.CreateDirectory(referenceDirectory);
        Directory.CreateDirectory(logDirectory);

        ProcessResult version = await RunAsync(
            options.FfmpegPath,
            ["-hide_banner", "-version"],
            cancellationToken).ConfigureAwait(false);
        if (version.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg is not usable: {version.CombinedOutput}");

        ProcessResult encoders = await RunAsync(
            options.FfmpegPath,
            ["-hide_banner", "-encoders"],
            cancellationToken).ConfigureAwait(false);
        if (encoders.ExitCode != 0)
            throw new InvalidOperationException($"Could not inspect FFmpeg encoders: {encoders.CombinedOutput}");

        var generated = new List<GeneratedValidationAsset>(ValidationMatrix.Assets.Count);
        foreach (ValidationAsset asset in ValidationMatrix.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Generating {asset.Id}");
            generated.Add(await GenerateAssetAsync(
                asset,
                options,
                assetDirectory,
                referenceDirectory,
                logDirectory,
                encoders.CombinedOutput,
                cancellationToken).ConfigureAwait(false));
        }

        var manifest = new ValidationAssetManifest
        {
            FfmpegVersion = version.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty,
            Assets = generated,
        };

        string manifestPath = Path.Combine(outputDirectory, "assets-manifest.json");
        await using FileStream manifestStream = File.Create(manifestPath);
        await JsonSerializer.SerializeAsync(
            manifestStream,
            manifest,
            ValidationJsonContext.Default.ValidationAssetManifest,
            cancellationToken).ConfigureAwait(false);

        int failures = generated.Count(asset => !asset.Generated && !asset.Skipped);
        AppLog.Information(
            "Validation.Generate",
            $"Generated {generated.Count(asset => asset.Generated)} assets; " +
            $"skipped {generated.Count(asset => asset.Skipped)}; failed {failures}; manifest={manifestPath}.");
        return manifest;
    }

    public static string AssetPath(string outputDirectory, ValidationAsset asset) =>
        Path.Combine(
            Path.GetFullPath(outputDirectory),
            "assets",
            $"{asset.Id}.{(asset.Codec == "rawvideo" ? "raw" : "mkv")}");

    private static async Task<GeneratedValidationAsset> GenerateAssetAsync(
        ValidationAsset asset,
        ValidationGenerationOptions options,
        string assetDirectory,
        string referenceDirectory,
        string logDirectory,
        string encoders,
        CancellationToken cancellationToken)
    {
        string outputPath = AssetPath(options.OutputDirectory, asset);
        string referencePath = Path.Combine(referenceDirectory, $"{asset.Id}.png");
        string linearReferencePath = IsHdr(asset)
            ? Path.Combine(referenceDirectory, $"{asset.Id}.scRGB-gbrpf32le")
            : string.Empty;
        string signalReferencePath = NeedsSignalReference(asset)
            ? Path.Combine(referenceDirectory, $"{asset.Id}.signal-gbrpf32le")
            : string.Empty;
        string logPath = Path.Combine(logDirectory, $"{asset.Id}.log");
        string? encoder = SelectEncoder(asset.Codec, encoders);
        if (encoder is null)
        {
            string message = $"No supported software encoder is available for {asset.Codec}.";
            await File.WriteAllTextAsync(logPath, message + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            return Result(generated: false, skipped: true, message);
        }

        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-loglevel", "verbose",
            "-f", "lavfi",
            "-i", GenerationSource(asset),
            "-an",
            "-frames:v", FrameCount.ToString(CultureInfo.InvariantCulture),
            "-vf", GenerationFilter(asset),
            "-c:v", encoder,
        };
        AddEncoderArguments(arguments, asset.Codec);
        AddColorArguments(arguments, asset);
        if (asset.Codec == "rawvideo")
            arguments.AddRange(["-f", "rawvideo"]);
        arguments.AddRange(["-y", outputPath]);

        ProcessResult generation;
        try
        {
            generation = await RunAsync(options.FfmpegPath, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            await File.WriteAllTextAsync(logPath, exception.ToString(), cancellationToken).ConfigureAwait(false);
            return Result(generated: false, skipped: false, exception.Message);
        }

        await WriteProcessLogAsync(logPath, options.FfmpegPath, arguments, generation, cancellationToken).ConfigureAwait(false);
        if (generation.ExitCode != 0 || !File.Exists(outputPath))
            return Result(generated: false, skipped: false, $"FFmpeg exited with {generation.ExitCode}.");

        string probeOutput;
        if (asset.Codec == "rawvideo")
        {
            long expectedBytes = (long)RawVideoSourceProvider.FrameByteCount(asset) * FrameCount;
            long actualBytes = new FileInfo(outputPath).Length;
            if (actualBytes != expectedBytes)
            {
                return Result(
                    generated: false,
                    skipped: false,
                    $"Raw file is {actualBytes} bytes; expected {expectedBytes}.");
            }

            probeOutput = $"codec_name=rawvideo{Environment.NewLine}pix_fmt={asset.PixelFormat}";
        }
        else
        {
            ProcessResult probe = await RunAsync(
                options.FfprobePath,
                [
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-show_entries", "stream=codec_name,pix_fmt,width,height,color_range,color_space,color_transfer,color_primaries,chroma_location",
                    "-of", "default=noprint_wrappers=1",
                    outputPath,
                ],
                cancellationToken).ConfigureAwait(false);
            probeOutput = probe.StandardOutput.Trim();
            await File.AppendAllTextAsync(
                logPath,
                $"{Environment.NewLine}$ {options.FfprobePath} ...{Environment.NewLine}{probe.CombinedOutput}",
                cancellationToken).ConfigureAwait(false);
            if (probe.ExitCode != 0 || !ProbeMatches(asset, probe.StandardOutput))
                return Result(generated: false, skipped: false, "ffprobe did not report the requested codec, format, and size.", probeOutput);
        }

        ProcessResult reference = await GenerateReferenceAsync(
            asset,
            options,
            outputPath,
            referencePath,
            cancellationToken).ConfigureAwait(false);
        await File.AppendAllTextAsync(
            logPath,
            $"{Environment.NewLine}Reference frame:{Environment.NewLine}{reference.CombinedOutput}",
            cancellationToken).ConfigureAwait(false);
        if (reference.ExitCode != 0 || !File.Exists(referencePath))
            return Result(generated: false, skipped: false, "Reference frame generation failed.", probeOutput);

        long referenceBytes = checked((long)asset.Width * asset.Height * 3 * sizeof(float));
        if (NeedsSignalReference(asset))
        {
            ProcessResult signalReference = await GenerateSignalReferenceAsync(
                asset,
                options,
                outputPath,
                signalReferencePath,
                cancellationToken).ConfigureAwait(false);
            await File.AppendAllTextAsync(
                logPath,
                $"{Environment.NewLine}Encoded RGB signal reference:{Environment.NewLine}{signalReference.CombinedOutput}",
                cancellationToken).ConfigureAwait(false);
            if (signalReference.ExitCode != 0 ||
                !File.Exists(signalReferencePath) ||
                new FileInfo(signalReferencePath).Length != referenceBytes)
            {
                return Result(
                    generated: false,
                    skipped: false,
                    $"Encoded RGB signal reference failed or did not produce {referenceBytes} bytes.",
                    probeOutput);
            }
        }

        if (IsHdr(asset))
        {
            ProcessResult linearReference = await GenerateLinearReferenceAsync(
                asset,
                options,
                outputPath,
                linearReferencePath,
                cancellationToken).ConfigureAwait(false);
            await File.AppendAllTextAsync(
                logPath,
                $"{Environment.NewLine}BT.2408-normalized linear reference:{Environment.NewLine}{linearReference.CombinedOutput}",
                cancellationToken).ConfigureAwait(false);
            if (linearReference.ExitCode != 0 ||
                !File.Exists(linearReferencePath) ||
                new FileInfo(linearReferencePath).Length != referenceBytes)
            {
                return Result(
                    generated: false,
                    skipped: false,
                    $"Linear HDR reference generation failed or did not produce {referenceBytes} bytes.",
                    probeOutput);
            }
        }

        var info = new FileInfo(outputPath);
        return new GeneratedValidationAsset
        {
            Id = asset.Id,
            FilePath = outputPath,
            ReferencePath = referencePath,
            LinearReferencePath = linearReferencePath,
            SignalReferencePath = signalReferencePath,
            LogPath = logPath,
            Generated = true,
            ByteLength = info.Length,
            Sha256 = await HashAsync(outputPath, cancellationToken).ConfigureAwait(false),
            ProbeOutput = probeOutput,
        };

        GeneratedValidationAsset Result(bool generated, bool skipped, string message, string probe = "") => new()
        {
            Id = asset.Id,
            FilePath = outputPath,
            ReferencePath = referencePath,
            LinearReferencePath = linearReferencePath,
            SignalReferencePath = signalReferencePath,
            LogPath = logPath,
            Generated = generated,
            Skipped = skipped,
            Message = message,
            ByteLength = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0,
            ProbeOutput = probe,
        };
    }

    private static async Task<ProcessResult> GenerateReferenceAsync(
        ValidationAsset asset,
        ValidationGenerationOptions options,
        string inputPath,
        string referencePath,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "warning" };
        AddInputArguments(arguments, asset);

        arguments.AddRange(
        [
            "-i", inputPath,
            "-frames:v", "1",
            "-vf", IsHdr(asset)
                ? $"libplacebo=w={asset.Width}:h={asset.Height}:" +
                    "colorspace=bt709:color_primaries=bt709:color_trc=iec61966-2-1:range=pc:" +
                    "peak_detect=true:smoothing_period=0:scene_threshold_low=0:scene_threshold_high=0:" +
                    "tonemapping=bt.2390:gamut_mode=perceptual:" +
                    "contrast_recovery=0:dithering=none,format=rgba"

                // Only worth looking at, since zimg clips what leaves the gamut where the renderer
                // compresses it. The pass or fail comes from the signal reference instead.
                : asset.IsWideGamut
                ? "zscale=range=full:primaries=bt709:transfer=bt709:matrix=gbr,format=gbrp,format=rgba"
                : "format=rgba",
            "-update", "1",
            "-y", referencePath,
        ]);
        return await RunAsync(options.FfmpegPath, arguments, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProcessResult> GenerateLinearReferenceAsync(
        ValidationAsset asset,
        ValidationGenerationOptions options,
        string inputPath,
        string referencePath,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "warning" };
        AddInputArguments(arguments, asset);
        arguments.AddRange(
        [
            "-i", inputPath,
            "-frames:v", "1",
            "-vf", "zscale=range=full:primaries=bt709:transfer=linear:matrix=gbr:" +
                $"npl={Bt2408ReferenceWhiteNits},format=gbrpf32le",
            "-f", "rawvideo",
            "-y", referencePath,
        ]);
        return await RunAsync(options.FfmpegPath, arguments, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProcessResult> GenerateSignalReferenceAsync(
        ValidationAsset asset,
        ValidationGenerationOptions options,
        string inputPath,
        string referencePath,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "warning" };
        AddInputArguments(arguments, asset);
        arguments.AddRange(
        [
            "-i", inputPath,
            "-frames:v", "1",
            "-vf", "zscale=range=full:" +
                $"primaries={PrimariesName(asset)}:" +
                $"transfer={TransferName(asset)}:matrix=gbr,format=gbrpf32le",
            "-f", "rawvideo",
            "-y", referencePath,
        ]);
        return await RunAsync(options.FfmpegPath, arguments, cancellationToken).ConfigureAwait(false);
    }

    private static void AddInputArguments(List<string> arguments, ValidationAsset asset)
    {
        if (asset.Codec != "rawvideo")
            return;

        arguments.AddRange(
        [
            "-f", "rawvideo",
            "-pixel_format", asset.PixelFormat,
            "-video_size", $"{asset.Width}x{asset.Height}",
            "-framerate", RawVideoSourceProvider.FrameRate.ToString(CultureInfo.InvariantCulture),
        ]);
        AddColorArguments(arguments, asset);
    }

    private static bool ProbeMatches(ValidationAsset asset, string output) =>
        output.Contains($"codec_name={asset.Codec}", StringComparison.Ordinal) &&
        output.Contains($"pix_fmt={asset.PixelFormat}", StringComparison.Ordinal) &&
        output.Contains($"width={asset.Width}", StringComparison.Ordinal) &&
        output.Contains($"height={asset.Height}", StringComparison.Ordinal) &&
        output.Contains($"color_range={ProbeRange(asset)}", StringComparison.Ordinal) &&
        output.Contains($"color_space={ColorSpaceName(asset)}", StringComparison.Ordinal) &&
        output.Contains($"color_transfer={TransferName(asset)}", StringComparison.Ordinal) &&
        output.Contains($"color_primaries={PrimariesName(asset)}", StringComparison.Ordinal) &&
        output.Contains($"chroma_location={ChromaLocationName(asset)}", StringComparison.Ordinal);

    private static string? SelectEncoder(string codec, string encoders) => codec switch
    {
        "rawvideo" => "rawvideo",
        "h264" when HasEncoder(encoders, "libx264") => "libx264",
        "hevc" when HasEncoder(encoders, "libx265") => "libx265",
        "av1" when HasEncoder(encoders, "libsvtav1") => "libsvtav1",
        "av1" when HasEncoder(encoders, "libaom-av1") => "libaom-av1",
        _ => null,
    };

    private static bool HasEncoder(string output, string name) =>
        output.Split('\n').Any(line => line.Contains($" {name} ", StringComparison.Ordinal));

    private static void AddEncoderArguments(List<string> arguments, string codec)
    {
        switch (codec)
        {
            case "h264":
                arguments.AddRange(["-preset", "ultrafast", "-crf", "12", "-g", FrameCount.ToString(), "-bf", "0"]);
                break;
            case "hevc":
                arguments.AddRange(
                [
                    "-preset", "ultrafast",
                    "-crf", "12",
                    "-x265-params", $"log-level=error:keyint={FrameCount}:min-keyint={FrameCount}:scenecut=0:bframes=0",
                ]);
                break;
            case "av1":
                arguments.AddRange(["-preset", "12", "-crf", "20", "-g", FrameCount.ToString()]);
                break;
        }
    }

    private static void AddColorArguments(List<string> arguments, ValidationAsset asset)
    {
        arguments.AddRange(
        [
            "-color_range", ProbeRange(asset),
            "-colorspace", ColorSpaceName(asset),
            "-color_primaries", PrimariesName(asset),
            "-color_trc", TransferName(asset),
            "-chroma_sample_location", ChromaLocationName(asset),
        ]);
    }

    private static string FilterRange(ValidationAsset asset) =>
        asset.ColorRange == LightMediaRenderer.Contracts.VideoColorRange.Full ? "full" : "limited";

    private static string GenerationSource(ValidationAsset asset) => asset.ResolvedPattern switch
    {
        ValidationPattern.HdrGamutProbe => HdrGamutProbeSource(asset),
        ValidationPattern.HdrLadder => HdrLadderSource(asset),
        _ => $"smptehdbars=size={asset.Width}x{asset.Height}:" +
            $"rate={RawVideoSourceProvider.FrameRate}:duration={DurationSeconds}",
    };

    /// <summary>
    /// A grid of flat patches, one per <see cref="HdrGamutProbe.Patches"/> entry, laid out so the
    /// probe can read each one by its own coordinates.
    /// </summary>
    private static string HdrGamutProbeSource(ValidationAsset asset)
    {
        string index = $"floor(X*{HdrGamutProbe.Columns}/W)+{HdrGamutProbe.Columns}*floor(Y*{HdrGamutProbe.Rows}/H)";
        string red = HdrPatchChannel(index, PatchSignals(asset, patch => patch.RedNits));
        string green = HdrPatchChannel(index, PatchSignals(asset, patch => patch.GreenNits));
        string blue = HdrPatchChannel(index, PatchSignals(asset, patch => patch.BlueNits));
        return $"nullsrc=size={asset.Width}x{asset.Height}:rate={RawVideoSourceProvider.FrameRate}:" +
            $"duration={DurationSeconds},format=gbrpf32le,geq=r={red}:g={green}:b={blue}";
    }

    private static IReadOnlyList<double> PatchSignals(ValidationAsset asset, Func<HdrProbePatch, double> select)
    {
        var signals = new double[HdrGamutProbe.Columns * HdrGamutProbe.Rows];
        foreach (HdrProbePatch patch in HdrGamutProbe.Patches)
            signals[patch.Row * HdrGamutProbe.Columns + patch.Column] = HdrSignalForNits(asset, select(patch));
        return signals;
    }

    private static string HdrPatchChannel(string index, IReadOnlyList<double> values)
    {
        string expression = values[^1].ToString("0.########", CultureInfo.InvariantCulture);
        for (int item = values.Count - 2; item >= 0; item--)
        {
            string value = values[item].ToString("0.########", CultureInfo.InvariantCulture);
            expression = $"if(eq({index}\\,{item})\\,{value}\\,{expression})";
        }

        return expression;
    }

    private static string HdrLadderSource(ValidationAsset asset)
    {
        const string index8 = "floor(X*8/W)";
        string reference = HdrSignalForNits(asset, Bt2408ReferenceWhiteNits).ToString("0.########", CultureInfo.InvariantCulture);
        string black = HdrSignalForNits(asset, 20).ToString("0.########", CultureInfo.InvariantCulture);
        string topRed = $"{reference}*(between({index8}\\,0\\,1)+between({index8}\\,4\\,5))+{black}*eq({index8}\\,7)";
        string topGreen = $"{reference}*between({index8}\\,0\\,3)+{black}*eq({index8}\\,7)";
        string topBlue = $"{reference}*(eq({index8}\\,0)+eq({index8}\\,2)+eq({index8}\\,4)+eq({index8}\\,6))+{black}*eq({index8}\\,7)";
        double peakNits = asset.ColorTransfer == LightMediaRenderer.Contracts.VideoColorTransfer.Hlg
            ? 800
            : HdrSourcePeakNits;
        string ramp = HdrSignalForNits(asset, peakNits).ToString("0.########", CultureInfo.InvariantCulture) + "*X/(W-1)";
        double[] stepNits = asset.ColorTransfer == LightMediaRenderer.Contracts.VideoColorTransfer.Hlg
            ? [0, 10, 50, 100, Bt2408ReferenceWhiteNits, 400, 600, 800]
            : [0, 10, 50, 100, Bt2408ReferenceWhiteNits, 400, 700, HdrSourcePeakNits];
        double[] steps = stepNits.Select(nits => HdrSignalForNits(asset, nits)).ToArray();
        string red = HdrPatternChannel(topRed, ramp, index8, steps);
        string green = HdrPatternChannel(topGreen, ramp, index8, steps);
        string blue = HdrPatternChannel(topBlue, ramp, index8, steps);
        return $"nullsrc=size={asset.Width}x{asset.Height}:rate={RawVideoSourceProvider.FrameRate}:" +
            $"duration={DurationSeconds},format=gbrpf32le,geq=r={red}:g={green}:b={blue}";
    }

    private static string HdrPatternChannel(
        string top,
        string ramp,
        string index,
        IReadOnlyList<double> steps)
    {
        string bottom = steps[^1].ToString("0.######", CultureInfo.InvariantCulture);
        for (int item = steps.Count - 2; item >= 0; item--)
        {
            string value = steps[item].ToString("0.######", CultureInfo.InvariantCulture);
            bottom = $"if(eq({index}\\,{item})\\,{value}\\,{bottom})";
        }

        return $"if(lt(Y\\,H*0.6)\\,{top}\\,if(lt(Y\\,H*0.8)\\,{ramp}\\,{bottom}))";
    }

    private static double HdrSignalForNits(ValidationAsset asset, double nits)
    {
        if (nits <= 0)
            return 0;

        if (asset.ColorTransfer == LightMediaRenderer.Contracts.VideoColorTransfer.Pq)
        {
            const double m1 = 0.1593017578125;
            const double m2 = 78.84375;
            const double c1 = 0.8359375;
            const double c2 = 18.8515625;
            const double c3 = 18.6875;
            double level = Math.Pow(Math.Clamp(nits / 10000.0, 0, 1), m1);
            return Math.Pow((c1 + c2 * level) / (1 + c3 * level), m2);
        }

        const double sourcePeak = HdrSourcePeakNits;
        const double systemGamma = 1.2;
        double scene = Math.Pow(Math.Clamp(nits / sourcePeak, 0, 1), 1.0 / systemGamma);
        const double a = 0.17883277;
        const double b = 0.28466892;
        const double c = 0.55991073;
        return scene <= 1.0 / 12.0
            ? Math.Sqrt(3 * scene)
            : a * Math.Log(12 * scene - b) + c;
    }

    private static string GenerationFilter(ValidationAsset asset)
    {
        string metadata = "setparams=" +
            $"range={FilterRange(asset)}:" +
            $"color_primaries={PrimariesName(asset)}:" +
            $"color_trc={TransferName(asset)}:" +
            $"colorspace={ColorSpaceName(asset)}:" +
            $"chroma_location={ChromaLocationName(asset)}";
        if (asset.ExpectedFormat == LightMediaRenderer.Contracts.VideoPixelFormat.Bgra32)
            return $"format={asset.PixelFormat},{metadata}";

        string source = IsHdr(asset)
            ? "setparams=range=full:color_primaries=bt2020:" +
                $"color_trc={TransferName(asset)}:colorspace=gbr:chroma_location=left"

            // The bars are drawn in whatever primaries the asset is to carry, so their saturated
            // colors are that gamut's own rather than BT.709's expressed in it.
            : asset.ResolvedPattern == ValidationPattern.NativeGamutBars
            ? $"setparams=range=limited:color_primaries={PrimariesName(asset)}:color_trc=bt709:" +
                "colorspace=bt709:chroma_location=left"
            : "setparams=range=limited:color_primaries=bt709:color_trc=bt709:" +
                "colorspace=bt709:chroma_location=left";
        string convert = "zscale=" +
            $"range={FilterRange(asset)}:" +
            $"primaries={PrimariesName(asset)}:" +
            $"transfer={TransferName(asset)}:" +
            $"matrix={ColorSpaceName(asset)}:" +
            $"chromalin=left:chromal={ChromaLocationName(asset)}";
        return $"{source},{convert},format={asset.PixelFormat},{metadata}";
    }

    private static bool IsHdr(ValidationAsset asset) =>
        asset.ColorTransfer is LightMediaRenderer.Contracts.VideoColorTransfer.Pq
            or LightMediaRenderer.Contracts.VideoColorTransfer.Hlg;

    /// <summary>
    /// Whether FFmpeg can supply an encoded RGB signal for independently checking the renderer's
    /// matrix, transfer, gamut, and output stages.
    /// </summary>
    private static bool NeedsSignalReference(ValidationAsset asset) => asset.ColorTransfer is
        LightMediaRenderer.Contracts.VideoColorTransfer.Bt709 or
        LightMediaRenderer.Contracts.VideoColorTransfer.Srgb or
        LightMediaRenderer.Contracts.VideoColorTransfer.Linear or
        LightMediaRenderer.Contracts.VideoColorTransfer.Pq or
        LightMediaRenderer.Contracts.VideoColorTransfer.Hlg;

    private static string ProbeRange(ValidationAsset asset) =>
        asset.ColorRange == LightMediaRenderer.Contracts.VideoColorRange.Full ? "pc" : "tv";

    private static string ColorSpaceName(ValidationAsset asset) =>
        asset.ColorSpace switch
        {
            LightMediaRenderer.Contracts.VideoColorSpace.Bt601 => "smpte170m",
            LightMediaRenderer.Contracts.VideoColorSpace.Bt2020 => "bt2020nc",
            _ => "bt709",
        };

    private static string PrimariesName(ValidationAsset asset) =>
        asset.ColorPrimaries switch
        {
            LightMediaRenderer.Contracts.VideoColorPrimaries.Bt2020 => "bt2020",
            LightMediaRenderer.Contracts.VideoColorPrimaries.Smpte170M => "smpte170m",
            LightMediaRenderer.Contracts.VideoColorPrimaries.Bt470Bg => "bt470bg",
            LightMediaRenderer.Contracts.VideoColorPrimaries.Bt470M => "bt470m",
            LightMediaRenderer.Contracts.VideoColorPrimaries.DisplayP3 => "smpte432",
            LightMediaRenderer.Contracts.VideoColorPrimaries.DciP3 => "smpte431",
            _ => "bt709",
        };

    private static string TransferName(ValidationAsset asset) => asset.ColorTransfer switch
    {
        LightMediaRenderer.Contracts.VideoColorTransfer.Pq => "smpte2084",
        LightMediaRenderer.Contracts.VideoColorTransfer.Hlg => "arib-std-b67",
        LightMediaRenderer.Contracts.VideoColorTransfer.Srgb => "iec61966-2-1",
        LightMediaRenderer.Contracts.VideoColorTransfer.Linear => "linear",
        _ => "bt709",
    };

    private static string ChromaLocationName(ValidationAsset asset) => asset.ChromaLocation switch
    {
        LightMediaRenderer.Contracts.VideoChromaLocation.TopLeft => "topleft",
        LightMediaRenderer.Contracts.VideoChromaLocation.BottomLeft => "bottomleft",
        _ => asset.ChromaLocation.ToString().ToLowerInvariant(),
    };

    private static async Task WriteProcessLogAsync(
        string path,
        string executable,
        IReadOnlyList<string> arguments,
        ProcessResult result,
        CancellationToken cancellationToken)
    {
        string command = string.Join(" ", arguments.Select(Quote));
        await File.WriteAllTextAsync(
            path,
            $"$ {executable} {command}{Environment.NewLine}{result.CombinedOutput}",
            cancellationToken).ConfigureAwait(false);
    }

    private static string Quote(string value) =>
        value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => new StringBuilder(StandardOutput.Length + StandardError.Length)
            .Append(StandardOutput)
            .Append(StandardError)
            .ToString();
    }
}