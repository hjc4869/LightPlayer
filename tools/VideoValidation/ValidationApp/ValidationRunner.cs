using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.Services;
using LightMediaRenderer.Avalonia.Controls;
using LightMediaRenderer.Avalonia.Diagnostics;
using LightMediaRenderer.Avalonia.Pipeline;
using LightMediaRenderer.Avalonia.Rendering;
using LightStudio.Logging;

namespace LightStudio.VideoValidation;

public sealed class ValidationRunner : IAsyncDisposable
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(15);

    private readonly VideoView videoView;
    private readonly ValidationLaunchOptions options;
    private readonly Action<string> reportProgress;
    private readonly Action<ValidationCaseResult> reportResult;
    private readonly Action? exposeRenderTarget;
    private MediaPlayer? player;
    private bool disposed;

    public ValidationRunner(
        VideoView videoView,
        ValidationLaunchOptions options,
        Action<string> reportProgress,
        Action<ValidationCaseResult> reportResult,
        Action? exposeRenderTarget = null)
    {
        this.videoView = videoView;
        this.options = options;
        this.reportProgress = reportProgress;
        this.reportResult = reportResult;
        this.exposeRenderTarget = exposeRenderTarget;
    }

    public async Task<ValidationAssetManifest> GenerateAssetsAsync(CancellationToken cancellationToken = default) =>
        await ValidationAssetGenerator.GenerateAsync(
            new ValidationGenerationOptions(
                options.OutputDirectory,
                ValidationFfmpegTools.FfmpegPath,
                ValidationFfmpegTools.FfprobePath),
            new Progress<string>(reportProgress),
            cancellationToken);

    public async Task<ValidationRunReport> RunAsync(
        IReadOnlyList<ValidationCase> cases,
        bool generateAssets,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(cases);

        ValidationAssetManifest manifest = generateAssets
            ? await GenerateAssetsAsync(cancellationToken)
            : await LoadManifestAsync(cancellationToken);
        Dictionary<string, GeneratedValidationAsset> generated = manifest.Assets.ToDictionary(asset => asset.Id);
        Dictionary<string, ValidationAsset> assets = ValidationMatrix.Assets.ToDictionary(asset => asset.Id);

        DateTimeOffset started = DateTimeOffset.UtcNow;
        string runId = started.ToString("yyyyMMdd-HHmmss-fff");
        string runDirectory = Path.Combine(Path.GetFullPath(options.OutputDirectory), "runs", runId);
        string captureDirectory = Path.Combine(runDirectory, "captures");
        string caseLogDirectory = Path.Combine(runDirectory, "logs");
        Directory.CreateDirectory(captureDirectory);
        Directory.CreateDirectory(caseLogDirectory);

        var results = new List<ValidationCaseResult>(cases.Count);
        var environment = new ValidationEnvironmentReport { PlatformDetails = options.PlatformDetails };
        string reportPath = Path.Combine(runDirectory, "report.json");
        AppLog.Information("Validation.Run", $"Starting {cases.Count} cases; run={runId}; output={runDirectory}.");

        // Let the empty VideoView probe the real render context before a decoder chooses whether
        // it can hand surfaces or wide samples over.
        videoView.InvalidateVisual();
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);

        foreach (ValidationCase validationCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidationAsset asset = assets[validationCase.AssetId];
            reportProgress($"Running {validationCase.Id}");

            ValidationCaseResult result;
            if (!generated.TryGetValue(asset.Id, out GeneratedValidationAsset? generatedAsset) ||
                !generatedAsset.Generated ||
                !File.Exists(generatedAsset.FilePath) ||
                !File.Exists(generatedAsset.ReferencePath) ||
                !HasRequiredReferences(asset, generatedAsset))
            {
                string reason = !string.IsNullOrWhiteSpace(generatedAsset?.Message)
                    ? generatedAsset.Message
                    : "Asset or domain-correct reference is missing; regenerate the validation corpus.";
                result = new ValidationCaseResult
                {
                    CaseId = validationCase.Id,
                    AssetId = asset.Id,
                    Status = generatedAsset?.Skipped == true ? ValidationStatus.Skipped : ValidationStatus.Failed,
                    Message = reason,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                };
            }
            else
            {
                result = await RunCaseAsync(
                    validationCase,
                    asset,
                    generatedAsset,
                    captureDirectory,
                    caseLogDirectory,
                    cancellationToken);
            }

            results.Add(result);
            reportResult(result);
            await WriteReportAsync(reportPath, CreateReport(runId, started, environment, results), cancellationToken);
        }

        await StopPlayerAsync();
        ValidationRunReport report = CreateReport(runId, started, environment, results) with
        {
            FinishedAtUtc = DateTimeOffset.UtcNow,
        };
        await WriteReportAsync(reportPath, report, cancellationToken);
        AppLog.Information(
            "Validation.Run",
            $"Finished run={runId}; passed={report.Passed}; failed={report.Failed}; skipped={report.Skipped}; report={reportPath}.");
        reportProgress($"Complete: {report.Passed} passed, {report.Failed} failed, {report.Skipped} skipped");
        return report;
    }

    private static bool HasRequiredReferences(ValidationAsset asset, GeneratedValidationAsset generated)
    {
        bool hdr = asset.ColorTransfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg;
        if (string.IsNullOrWhiteSpace(generated.SignalReferencePath) ||
            !File.Exists(generated.SignalReferencePath))
        {
            return false;
        }

        return !hdr ||
            (!string.IsNullOrWhiteSpace(generated.LinearReferencePath) &&
             File.Exists(generated.LinearReferencePath));
    }

    private async Task<ValidationCaseResult> RunCaseAsync(
        ValidationCase validationCase,
        ValidationAsset asset,
        GeneratedValidationAsset generated,
        string captureDirectory,
        string caseLogDirectory,
        CancellationToken cancellationToken)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        string capturePath = Path.Combine(captureDirectory, $"{validationCase.Id}.png");
        string nativeCapturePath = Path.Combine(captureDirectory, $"{validationCase.Id}.scRGB.pfm");
        string caseLogPath = Path.Combine(caseLogDirectory, $"{validationCase.Id}.log");
        var errors = new List<string>();
        CancellationTokenSource? timeout = null;

        try
        {
            await StopPlayerAsync();
            var decoderOptions = VideoDecoderOptions.Default with
            {
                EnableHardwareDecoding = validationCase.DecodeMode == ValidationDecodeMode.HardwareAuto,
                HardwareDeviceType = validationCase.DecodeMode == ValidationDecodeMode.HardwareAuto
                    ? options.HardwareDeviceType
                    : null,
            };
            var playerOptions = new MediaPlayerOptions { VideoDecoder = decoderOptions };
            player = new MediaPlayer(playerOptions);
            player.ErrorOccurred += (_, message) => errors.Add(message);

            videoView.Width = validationCase.ViewportWidth;
            videoView.Height = validationCase.ViewportHeight;
            videoView.ScalingQuality = validationCase.Scaling;
            videoView.EnableHdrPassthrough = validationCase.HdrPassthrough;
            videoView.Player = player;
            timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(FrameTimeout);
            Task<VideoRenderCapture> captureTask = videoView.CaptureRenderedFrameAsync(timeout.Token);

            IMediaDecodeSession session = asset.Codec == "rawvideo"
                ? new MediaDecodeSession(playerOptions, new RawVideoSourceProvider(generated.FilePath, asset), ownsProvider: true)
                : MediaDecodeSessionFactory.Create(new(generated.FilePath, playerOptions,
                    _ => Task.FromResult<Stream>(File.OpenRead(generated.FilePath))));
            await player.OpenAsync(session, timeout.Token);
            player.Play();
            return await CaptureAndEvaluateAsync(captureTask, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await FailureAsync("Timed out waiting for a decoded and rendered frame.");
        }
        catch (Exception exception)
        {
            ValidationCaseResult result = await FailureAsync(exception.ToString());
            AppLog.Error("Validation.Case", $"{validationCase.Id} failed with an exception.", exception);
            return result;
        }
        finally
        {
            timeout?.Cancel();
            timeout?.Dispose();
            await StopPlayerAsync();
        }

        async Task<ValidationCaseResult> CaptureAndEvaluateAsync(
            Task<VideoRenderCapture> captureTask,
            CancellationToken timeoutToken)
        {
            PlaybackStatistics statistics = await WaitForPresentedFrameAsync(player!, timeoutToken);
            exposeRenderTarget?.Invoke();
            VideoRenderCapture capture = await captureTask;
            VideoOutputStatus output = VideoOutputDiagnostics.Current;
            RenderedFrameMetrics image = await RenderedFrameValidator.SaveAndValidateAsync(
                capture,
                capturePath,
                nativeCapturePath,
                generated.ReferencePath,
                generated.SignalReferencePath,
                asset,
                !output.HdrAdaptedToDisplay,
                output.LinearNitsPerUnit,
                output.ToneMapPeakNits,
                output,
                timeoutToken);
            statistics = player!.GetStatistics();
            VideoDecoderDiagnostics decoder = statistics.Decoder;
            (ValidationStatus status, string message) = Evaluate(
                validationCase,
                asset,
                statistics,
                output,
                image,
                errors);
            stopwatch.Stop();

            var result = new ValidationCaseResult
            {
                CaseId = validationCase.Id,
                AssetId = asset.Id,
                Status = status,
                Message = message,
                StartedAtUtc = started,
                Elapsed = stopwatch.Elapsed,
                CapturePath = capturePath,
                NativeCapturePath = File.Exists(nativeCapturePath) ? nativeCapturePath : string.Empty,
                CaseLogPath = caseLogPath,
                Decoder = decoder,
                Output = output,
                Statistics = statistics,
                Image = image,
            };
            await WriteCaseLogAsync(result, errors, cancellationToken);
            AppLog.Information("Validation.Case", $"{validationCase.Id}: {status} - {message}");
            return result;
        }

        async Task<ValidationCaseResult> FailureAsync(string message)
        {
            stopwatch.Stop();
            PlaybackStatistics statistics = player?.GetStatistics() ?? PlaybackStatistics.Empty;
            var result = new ValidationCaseResult
            {
                CaseId = validationCase.Id,
                AssetId = asset.Id,
                Status = ValidationStatus.Failed,
                Message = message,
                StartedAtUtc = started,
                Elapsed = stopwatch.Elapsed,
                CapturePath = capturePath,
                CaseLogPath = caseLogPath,
                Decoder = statistics.Decoder,
                Output = VideoOutputDiagnostics.Current,
                Statistics = statistics,
            };
            await WriteCaseLogAsync(result, errors, cancellationToken);
            return result;
        }

        async Task WriteCaseLogAsync(
            ValidationCaseResult result,
            IReadOnlyList<string> playbackErrors,
            CancellationToken token)
        {
            var text = new StringBuilder()
                .AppendLine($"Case: {validationCase.Id}")
                .AppendLine($"Asset: {generated.FilePath}")
                .AppendLine($"Status: {result.Status}")
                .AppendLine($"Message: {result.Message}")
                .AppendLine()
                .AppendLine(result.Statistics.ToDisplayString());
            if (result.Image is not null)
            {
                text.AppendLine().AppendLine(
                    JsonSerializer.Serialize(result.Image, ValidationJsonContext.Default.RenderedFrameMetrics));
            }
            if (playbackErrors.Count > 0)
                text.AppendLine().AppendLine("Playback errors:").AppendLine(string.Join(Environment.NewLine, playbackErrors));
            await File.WriteAllTextAsync(caseLogPath, text.ToString(), token);
        }
    }

    private static async Task<PlaybackStatistics> WaitForPresentedFrameAsync(
        MediaPlayer player,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlaybackStatistics statistics = player.GetStatistics();
            if (statistics.PresentedVideoFrames > 0 &&
                statistics.Decoder.DeliveryPath != VideoFrameDeliveryPath.Unknown &&
                statistics.Decoder.OutputFormat is not null)
            {
                return statistics;
            }

            if (statistics.State == PlaybackState.Error)
                throw new InvalidOperationException("Playback entered the error state before presenting a frame.");
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    private bool UsesWebCodecs(ValidationCase validationCase) =>
        validationCase.DecodeMode == ValidationDecodeMode.HardwareAuto &&
        string.Equals(options.HardwareDeviceType, "webcodecs", StringComparison.OrdinalIgnoreCase);

    private (ValidationStatus Status, string Message) Evaluate(
        ValidationCase validationCase,
        ValidationAsset asset,
        PlaybackStatistics statistics,
        VideoOutputStatus output,
        RenderedFrameMetrics image,
        IReadOnlyList<string> playbackErrors)
    {
        VideoDecoderDiagnostics decoder = statistics.Decoder;
        bool webCodecs = UsesWebCodecs(validationCase);
        bool decoderUnavailable = webCodecs
            ? decoder.Implementation != "WebCodecs worker"
            : validationCase.DecodeMode == ValidationDecodeMode.HardwareAuto && !decoder.IsHardwareAccelerated;
        var failures = new List<string>();
        if (!decoderUnavailable && !validationCase.AllowedDeliveryPaths.Contains(decoder.DeliveryPath))
            failures.Add($"delivery path was {decoder.DeliveryPath}");
        if (statistics.DecodeErrors > 0)
            failures.Add($"decoder reported {statistics.DecodeErrors} errors");
        if (playbackErrors.Count > 0)
            failures.Add($"playback reported {playbackErrors.Count} errors");
        if (!output.SurfaceObserved)
            failures.Add("no output surface was observed");
        if (output.Scaling != validationCase.Scaling)
            failures.Add($"renderer used {output.Scaling} scaling");
        if (output.EffectiveScaling is { } effective && effective != validationCase.Scaling)
        {
            failures.Add(
                $"the renderer drew with {effective} instead" +
                (output.ScalingDeclined.Length > 0 ? $": {output.ScalingDeclined}" : string.Empty));
        }
        bool rgbOutput = webCodecs && decoder.OutputFormat is VideoPixelFormat.ExternalTexture or VideoPixelFormat.Bgra32;
        bool convertedOutput = webCodecs && (decoder.OutputFormat == VideoPixelFormat.ExternalTexture ||
            rgbOutput && decoder.ColorTransfer == VideoColorTransfer.Srgb && decoder.ColorPrimaries == VideoColorPrimaries.Bt709);
        VideoColorTransfer expectedTransfer = convertedOutput ? VideoColorTransfer.Srgb : asset.ColorTransfer;
        VideoColorPrimaries expectedPrimaries = convertedOutput ? VideoColorPrimaries.Bt709 : asset.ColorPrimaries;
        if (decoder.ColorTransfer != expectedTransfer)
            failures.Add($"transfer was {decoder.ColorTransfer}");
        if (!rgbOutput && decoder.ColorSpace != asset.ColorSpace)
            failures.Add($"matrix was {decoder.ColorSpace}");
        if (decoder.ColorPrimaries != expectedPrimaries)
            failures.Add($"primaries were {decoder.ColorPrimaries}, expected {expectedPrimaries}");
        if (!image.IsPatterned || !image.ReferenceMatches)
            failures.Add(image.Message);

        if (validationCase.DecodeMode == ValidationDecodeMode.Software)
        {
            if (decoder.IsHardwareAccelerated)
                failures.Add("hardware decoding was active when software was requested");
            if (decoder.OutputFormat != asset.ExpectedFormat)
                failures.Add($"output format was {decoder.OutputFormat}, expected {asset.ExpectedFormat}");
            if (!string.Equals(decoder.SamplePixelFormat, asset.PixelFormat, StringComparison.OrdinalIgnoreCase))
                failures.Add($"sample format was {decoder.SamplePixelFormat}, expected {asset.PixelFormat}");
        }

        if (failures.Count > 0)
            return (ValidationStatus.Failed, string.Join("; ", failures));
        if (decoderUnavailable)
        {
            string reason = string.IsNullOrWhiteSpace(decoder.HardwareStatus)
                ? webCodecs ? "The browser did not select the WebCodecs decoder." : "No hardware decoder accepted this stream."
                : decoder.HardwareStatus;
            return (ValidationStatus.Skipped, $"{(webCodecs ? "WebCodecs" : "Hardware")} path unavailable: {reason}");
        }
        return (ValidationStatus.Passed, image.Message);
    }

    private async Task<ValidationAssetManifest> LoadManifestAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(Path.GetFullPath(options.OutputDirectory), "assets-manifest.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("Generate validation assets before running the matrix.", path);
        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(
            stream,
            ValidationJsonContext.Default.ValidationAssetManifest,
            cancellationToken)
            ?? throw new InvalidDataException($"Could not read validation asset manifest '{path}'.");
    }

    private static ValidationRunReport CreateReport(
        string runId,
        DateTimeOffset started,
        ValidationEnvironmentReport environment,
        IReadOnlyList<ValidationCaseResult> results) => new()
        {
            RunId = runId,
            StartedAtUtc = started,
            Environment = environment,
            Results = results.ToArray(),
            Passed = results.Count(result => result.Status == ValidationStatus.Passed),
            Failed = results.Count(result => result.Status == ValidationStatus.Failed),
            Skipped = results.Count(result => result.Status == ValidationStatus.Skipped),
        };

    private static async Task WriteReportAsync(
        string path,
        ValidationRunReport report,
        CancellationToken cancellationToken)
    {
        await using (FileStream stream = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                report,
                ValidationJsonContext.Default.ValidationRunReport,
                cancellationToken);
        }
        await ValidationReportHtml.WriteAsync(path, report, cancellationToken);
    }

    private async ValueTask StopPlayerAsync()
    {
        videoView.Player = null;
        MediaPlayer? closing = player;
        player = null;
        if (closing is not null)
            await closing.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        await StopPlayerAsync();
    }

}