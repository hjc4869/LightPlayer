using System;
using System.Collections.Generic;
using System.IO;

namespace LightStudio.VideoValidation;

public sealed record ValidationLaunchOptions
{
    public string OutputDirectory { get; init; } = Path.GetFullPath("artifacts/video-validation");

    public string? RenderReportPath { get; init; }

    public bool AutoRun { get; init; }

    public bool GenerateAssets { get; init; }

    public bool GenerateOnly { get; init; }

    public bool ExitWhenComplete { get; init; }

    public bool SoftwareOnly { get; init; }

    public string? HardwareDeviceType { get; init; }

    public bool EnableWayland { get; init; } = true;

    public IReadOnlyList<string> CaseIds { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> PlatformDetails { get; init; } =
        new Dictionary<string, string>();
}