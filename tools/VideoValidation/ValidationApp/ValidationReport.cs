using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using LightStudio.FfmpegShim.Decoding;
using LightMediaRenderer.Avalonia.Diagnostics;
using LightMediaRenderer.Avalonia.Rendering;

namespace LightStudio.VideoValidation;

public enum ValidationStatus
{
    Passed,
    Failed,
    Skipped,
}

public sealed record ValidationEnvironmentReport
{
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string OperatingSystem { get; init; } = RuntimeInformation.OSDescription;

    public string Architecture { get; init; } = RuntimeInformation.ProcessArchitecture.ToString();

    public string Framework { get; init; } = RuntimeInformation.FrameworkDescription;

    public int ProcessorCount { get; init; } = Environment.ProcessorCount;

    public string CurrentCulture { get; init; } = CultureInfo.CurrentCulture.Name;

    public string SessionType { get; init; } = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? string.Empty;

    public string Desktop { get; init; } = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;

    public string Display { get; init; } = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")
        ?? Environment.GetEnvironmentVariable("DISPLAY")
        ?? string.Empty;

    public IReadOnlyDictionary<string, string> PlatformDetails { get; init; } =
        new Dictionary<string, string>();
}

public sealed record ValidationCaseResult
{
    public string CaseId { get; init; } = string.Empty;

    public string AssetId { get; init; } = string.Empty;

    public ValidationStatus Status { get; init; }

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset StartedAtUtc { get; init; }

    public TimeSpan Elapsed { get; init; }

    public string CapturePath { get; init; } = string.Empty;

    /// <summary>Native linear-light RGB capture in Portable FloatMap format, when available.</summary>
    public string NativeCapturePath { get; init; } = string.Empty;

    public string CaseLogPath { get; init; } = string.Empty;

    public VideoDecoderDiagnostics Decoder { get; init; } = VideoDecoderDiagnostics.None;

    public VideoOutputStatus Output { get; init; } = VideoOutputStatus.Unknown;

    public PlaybackStatistics Statistics { get; init; } = PlaybackStatistics.Empty;

    public RenderedFrameMetrics? Image { get; init; }
}

public sealed record ValidationRunReport
{
    public const int CurrentVersion = 3;

    public int Version { get; init; } = CurrentVersion;

    public string RunId { get; init; } = string.Empty;

    public DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset FinishedAtUtc { get; init; }

    public ValidationEnvironmentReport Environment { get; init; } = new();

    public IReadOnlyList<ValidationCaseResult> Results { get; init; } = [];

    public int Passed { get; init; }

    public int Failed { get; init; }

    public int Skipped { get; init; }
}