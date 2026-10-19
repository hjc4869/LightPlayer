#nullable enable
using System;
using System.Collections.Generic;
using LightStudio.FfmpegShim.Decoding;

namespace LightStudio.FfmpegShim.Transcoding;

/// <summary>
/// One FFmpeg encoder family and the hardware-frame path that feeds it. The encoder interface is
/// deliberately separate from <see cref="DeviceType"/>: D3D11VA decodes, scales and carries
/// surfaces on Windows, but Media Foundation, AMF or NVENC performs the actual encoding.
/// </summary>
public sealed record TranscodeEncoderInterface(
    string Name,
    string DisplayName,
    string EncoderSuffix,
    string DeviceType,
    string ScalerName,
    string ScalerWidthOption = "w",
    string ScalerHeightOption = "h",
    bool ProbeWithHardwareFrames = true)
{
    public string EncoderName(string codecName) => string.Concat(codecName, "_", EncoderSuffix);

    public bool HasScaler => ScalerName.Length > 0;

    public bool ScalerConvertsPixelFormat { get; init; } = true;
}

/// <summary>Known encoder interfaces, in automatic-selection order for each platform.</summary>
public static class TranscodeEncoderInterfaces
{
    public const string MediaFoundation = "mf";
    public const string QuickSyncVideo = "qsv";
    public const string AdvancedMediaFramework = "amf";
    public const string Nvenc = "nvenc";
    public const string Vaapi = "vaapi";
    public const string VideoToolbox = "videotoolbox";
    public const string MediaCodec = "mediacodec";

    private static readonly TranscodeEncoderInterface MediaFoundationDefinition = new(
        MediaFoundation,
        "Media Foundation",
        "mf",
        "d3d11va",
        "scale_d3d11",
        "width",
        "height",
        ProbeWithHardwareFrames: false);

    private static readonly TranscodeEncoderInterface QuickSyncVideoDefinition = new(
        QuickSyncVideo,
        "Intel Quick Sync Video (QSV)",
        "qsv",
        "qsv",
        "scale_qsv",
        ProbeWithHardwareFrames: false);

    private static readonly TranscodeEncoderInterface AdvancedMediaFrameworkDefinition = new(
        AdvancedMediaFramework,
        "AMD Advanced Media Framework (AMF)",
        "amf",
        "d3d11va",
        "scale_d3d11",
        "width",
        "height",
        ProbeWithHardwareFrames: false);

    private static readonly TranscodeEncoderInterface NvencDefinition = new(
        Nvenc,
        "NVIDIA NVENC",
        "nvenc",
        "d3d11va",
        "scale_d3d11",
        "width",
        "height",
        ProbeWithHardwareFrames: false);

    private static readonly TranscodeEncoderInterface VaapiDefinition = new(
        Vaapi,
        "Video Acceleration API (VA-API)",
        "vaapi",
        "vaapi",
        "scale_vaapi");

    private static readonly TranscodeEncoderInterface VideoToolboxDefinition = new(
        VideoToolbox,
        "VideoToolbox",
        "videotoolbox",
        "videotoolbox",
        "scale_vt",
        ProbeWithHardwareFrames: false)
    {
        ScalerConvertsPixelFormat = false,
    };

    private static readonly TranscodeEncoderInterface MediaCodecDefinition = new(
        MediaCodec,
        "MediaCodec",
        "mediacodec",
        "mediacodec",
        string.Empty,
        ProbeWithHardwareFrames: false);

    private static readonly IReadOnlyList<TranscodeEncoderInterface> All =
    [
        MediaFoundationDefinition,
        QuickSyncVideoDefinition,
        AdvancedMediaFrameworkDefinition,
        NvencDefinition,
        VaapiDefinition,
        VideoToolboxDefinition,
        MediaCodecDefinition,
    ];

    public static IReadOnlyList<TranscodeEncoderInterface> GetPreferredForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            // Media Foundation delegates to the installed hardware MFT and works across vendors;
            // the vendor APIs remain available for users who need their additional controls.
            return
            [
                MediaFoundationDefinition,
                QuickSyncVideoDefinition,
                AdvancedMediaFrameworkDefinition,
                NvencDefinition,
            ];
        }

        if (OperatingSystem.IsLinux() && !OperatingSystem.IsBrowser())
        {
            return [VaapiDefinition];
        }

        if (OperatingSystem.IsMacOS())
        {
            return [VideoToolboxDefinition];
        }

        if (OperatingSystem.IsAndroid())
        {
            return [MediaCodecDefinition];
        }

        return [];
    }

    public static TranscodeEncoderInterface? Find(string? name)
    {
        string normalized = NormalizeName(name);
        foreach (TranscodeEncoderInterface definition in All)
        {
            if (string.Equals(definition.Name, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return definition;
            }
        }

        return null;
    }

    /// <summary>
    /// Normalizes persisted aliases to encoder interfaces. <c>d3d11va</c> maps to Media Foundation
    /// because D3D11VA is its frame device rather than an encoder.
    /// </summary>
    public static string NormalizeName(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "mediafoundation" or "media_foundation" or "d3d11va" => MediaFoundation,
        "quicksync" or "quick_sync" or "intel_qsv" => QuickSyncVideo,
        "amd_amf" => AdvancedMediaFramework,
        "nvidia" or "cuda" => Nvenc,
        MediaFoundation => MediaFoundation,
        QuickSyncVideo => QuickSyncVideo,
        AdvancedMediaFramework => AdvancedMediaFramework,
        Nvenc => Nvenc,
        Vaapi => Vaapi,
        VideoToolbox => VideoToolbox,
        MediaCodec => MediaCodec,
        _ => string.Empty,
    };
}

/// <summary>
/// The FFmpeg calls the encode path makes whose arguments a head can configure, named the same way
/// the decode path names its own so one settings store serves both.
/// </summary>
public static class HardwareEncoderApis
{
    /// <summary>Private options of the encoder, such as <c>rc_mode</c> or <c>quality</c>.</summary>
    public const string EncoderOpen = "avcodec_open2";

    /// <summary>Arguments of the on-device scaler the frames pass through, such as <c>scale_vaapi</c>.</summary>
    public const string Scaler = "avfilter_init_str";
}

/// <summary>Named arguments of those calls.</summary>
public static class HardwareEncoderParameters
{
    public const string Options = "opts";

    public const string Arguments = "args";
}

/// <summary>Quality/speed names shared by the user-facing encoder presets.</summary>
public static class EncoderQualityPresets
{
    public const string Quality = "quality";
    public const string Balanced = "balanced";
    public const string Speed = "speed";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Quality => Quality,
        Speed => Speed,
        _ => Balanced,
    };
}

/// <summary>User-facing presets for the options shared by FFmpeg's VA-API video encoders.</summary>
public static class VaapiEncoderOptions
{
    public const string DeviceType = TranscodeEncoderInterfaces.Vaapi;

    public const string AutomaticRateControl = "auto";
    public const string ConstantQualityRateControl = "CQP";
    public const string ConstantBitrateRateControl = "CBR";
    public const string VariableBitrateRateControl = "VBR";
    public const string IntelligentConstantQualityRateControl = "ICQ";
    public const string QualityDefinedVariableBitrateRateControl = "QVBR";
    public const string AverageVariableBitrateRateControl = "AVBR";
    public const string DefaultRateControl = VariableBitrateRateControl;

    public const string QualityPreset = EncoderQualityPresets.Quality;
    public const string BalancedPreset = EncoderQualityPresets.Balanced;
    public const string SpeedPreset = EncoderQualityPresets.Speed;
    public const string DefaultQualityPreset = BalancedPreset;

    /// <summary>Adds normalized VA-API encoder options to an existing hardware configuration.</summary>
    public static HardwareDecoderSettings Apply(
        HardwareDecoderSettings settings,
        string? rateControl,
        string? qualityPreset) =>
        settings
            .WithOption(
                DeviceType,
                HardwareEncoderApis.EncoderOpen,
                HardwareEncoderParameters.Options,
                "rc_mode",
                NormalizeRateControl(rateControl))
            .WithOption(
                DeviceType,
                HardwareEncoderApis.EncoderOpen,
                HardwareEncoderParameters.Options,
                "compression_level",
                QualityLevel(qualityPreset));

    public static string NormalizeRateControl(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "AUTO" => AutomaticRateControl,
        ConstantQualityRateControl => ConstantQualityRateControl,
        ConstantBitrateRateControl => ConstantBitrateRateControl,
        VariableBitrateRateControl => VariableBitrateRateControl,
        IntelligentConstantQualityRateControl => IntelligentConstantQualityRateControl,
        QualityDefinedVariableBitrateRateControl => QualityDefinedVariableBitrateRateControl,
        AverageVariableBitrateRateControl => AverageVariableBitrateRateControl,
        _ => DefaultRateControl,
    };

    public static string NormalizeQualityPreset(string? value) => EncoderQualityPresets.Normalize(value);

    public static bool UsesVariableBitrate(HardwareDecoderSettings settings, string deviceType) =>
        string.Equals(deviceType, DeviceType, StringComparison.OrdinalIgnoreCase) &&
        settings.QueryOptions(deviceType, HardwareEncoderApis.EncoderOpen, HardwareEncoderParameters.Options)
            .TryGetValue("rc_mode", out string? mode) &&
        string.Equals(mode, VariableBitrateRateControl, StringComparison.OrdinalIgnoreCase);

    private static string QualityLevel(string? preset) => NormalizeQualityPreset(preset) switch
    {
        QualityPreset => "1",
        SpeedPreset => "7",
        _ => "4",
    };
}

/// <summary>User-facing options shared by FFmpeg's Media Foundation video encoders.</summary>
public static class MediaFoundationEncoderOptions
{
    public const string Interface = TranscodeEncoderInterfaces.MediaFoundation;

    public const string DefaultRateControl = "default";
    public const string ConstantBitrateRateControl = "cbr";
    public const string PeakConstrainedVariableBitrateRateControl = "pc_vbr";
    public const string UnconstrainedVariableBitrateRateControl = "u_vbr";
    public const string QualityRateControl = "quality";
    public const string LowDelayVariableBitrateRateControl = "ld_vbr";
    public const string GlobalVariableBitrateRateControl = "g_vbr";
    public const string GlobalLowDelayVariableBitrateRateControl = "gld_vbr";
    public const string PreferredRateControl = PeakConstrainedVariableBitrateRateControl;

    public const string DefaultScenario = "default";
    public const string DisplayRemotingScenario = "display_remoting";
    public const string VideoConferenceScenario = "video_conference";
    public const string ArchiveScenario = "archive";
    public const string LiveStreamingScenario = "live_streaming";
    public const string CameraRecordScenario = "camera_record";
    public const string DisplayRemotingFeatureMapScenario = "display_remoting_with_feature_map";
    public const string PreferredScenario = LiveStreamingScenario;

    public const string DefaultQualityPreset = EncoderQualityPresets.Balanced;

    public static HardwareDecoderSettings Apply(
        HardwareDecoderSettings settings,
        string? rateControl,
        string? qualityPreset,
        string? scenario) =>
        settings
            .WithOption(Interface, HardwareEncoderApis.EncoderOpen, HardwareEncoderParameters.Options,
                "hw_encoding", "1")
            .WithOption(Interface, HardwareEncoderApis.EncoderOpen, HardwareEncoderParameters.Options,
                "rate_control", NormalizeRateControl(rateControl))
            .WithOption(Interface, HardwareEncoderApis.EncoderOpen, HardwareEncoderParameters.Options,
                "quality", QualityLevel(qualityPreset))
            .WithOption(Interface, HardwareEncoderApis.EncoderOpen, HardwareEncoderParameters.Options,
                "scenario", NormalizeScenario(scenario));

    public static string NormalizeRateControl(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        DefaultRateControl => DefaultRateControl,
        ConstantBitrateRateControl => ConstantBitrateRateControl,
        PeakConstrainedVariableBitrateRateControl => PeakConstrainedVariableBitrateRateControl,
        UnconstrainedVariableBitrateRateControl => UnconstrainedVariableBitrateRateControl,
        QualityRateControl => QualityRateControl,
        LowDelayVariableBitrateRateControl => LowDelayVariableBitrateRateControl,
        GlobalVariableBitrateRateControl => GlobalVariableBitrateRateControl,
        GlobalLowDelayVariableBitrateRateControl => GlobalLowDelayVariableBitrateRateControl,
        _ => PreferredRateControl,
    };

    public static string NormalizeQualityPreset(string? value) => EncoderQualityPresets.Normalize(value);

    public static string NormalizeScenario(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        DefaultScenario => DefaultScenario,
        DisplayRemotingScenario => DisplayRemotingScenario,
        VideoConferenceScenario => VideoConferenceScenario,
        ArchiveScenario => ArchiveScenario,
        LiveStreamingScenario => LiveStreamingScenario,
        CameraRecordScenario => CameraRecordScenario,
        DisplayRemotingFeatureMapScenario => DisplayRemotingFeatureMapScenario,
        _ => PreferredScenario,
    };

    private static string QualityLevel(string? value) => NormalizeQualityPreset(value) switch
    {
        EncoderQualityPresets.Quality => "75",
        EncoderQualityPresets.Speed => "25",
        _ => "50",
    };
}

/// <summary>User-facing options shared by FFmpeg's AMD AMF video encoders.</summary>
public static class AmfEncoderOptions
{
    public const string Interface = TranscodeEncoderInterfaces.AdvancedMediaFramework;

    public const string ConstantQualityRateControl = "cqp";
    public const string ConstantBitrateRateControl = "cbr";
    public const string PeakVariableBitrateRateControl = "vbr_peak";
    public const string LatencyVariableBitrateRateControl = "vbr_latency";
    public const string QualityVariableBitrateRateControl = "qvbr";
    public const string HighQualityVariableBitrateRateControl = "hqvbr";
    public const string HighQualityConstantBitrateRateControl = "hqcbr";
    public const string DefaultRateControl = PeakVariableBitrateRateControl;

    public const string TranscodingUsage = "transcoding";
    public const string UltraLowLatencyUsage = "ultralowlatency";
    public const string LowLatencyUsage = "lowlatency";
    public const string WebcamUsage = "webcam";
    public const string HighQualityUsage = "high_quality";
    public const string LowLatencyHighQualityUsage = "lowlatency_high_quality";
    public const string DefaultUsage = LowLatencyUsage;

    public const string HighQualityPreset = "high_quality";
    public const string DefaultQualityPreset = EncoderQualityPresets.Balanced;

    public static HardwareDecoderSettings Apply(
        HardwareDecoderSettings settings,
        string? rateControl,
        string? qualityPreset,
        string? usage) =>
        settings
            .WithOption(Interface, HardwareEncoderApis.EncoderOpen, HardwareEncoderParameters.Options,
                "rc", NormalizeRateControl(rateControl))
            .WithOption(Interface, HardwareEncoderApis.EncoderOpen, HardwareEncoderParameters.Options,
                "quality", NormalizeQualityPreset(qualityPreset))
            .WithOption(Interface, HardwareEncoderApis.EncoderOpen, HardwareEncoderParameters.Options,
                "usage", NormalizeUsage(usage));

    public static string NormalizeRateControl(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        ConstantQualityRateControl => ConstantQualityRateControl,
        ConstantBitrateRateControl => ConstantBitrateRateControl,
        PeakVariableBitrateRateControl => PeakVariableBitrateRateControl,
        LatencyVariableBitrateRateControl => LatencyVariableBitrateRateControl,
        QualityVariableBitrateRateControl => QualityVariableBitrateRateControl,
        HighQualityVariableBitrateRateControl => HighQualityVariableBitrateRateControl,
        HighQualityConstantBitrateRateControl => HighQualityConstantBitrateRateControl,
        _ => DefaultRateControl,
    };

    public static string NormalizeQualityPreset(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        EncoderQualityPresets.Speed => EncoderQualityPresets.Speed,
        EncoderQualityPresets.Quality => EncoderQualityPresets.Quality,
        HighQualityPreset => HighQualityPreset,
        _ => DefaultQualityPreset,
    };

    public static string NormalizeUsage(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        TranscodingUsage => TranscodingUsage,
        UltraLowLatencyUsage => UltraLowLatencyUsage,
        LowLatencyUsage => LowLatencyUsage,
        WebcamUsage => WebcamUsage,
        HighQualityUsage => HighQualityUsage,
        LowLatencyHighQualityUsage => LowLatencyHighQualityUsage,
        _ => DefaultUsage,
    };
}

/// <summary>User-facing options shared by FFmpeg's NVIDIA NVENC video encoders.</summary>
public static class NvencEncoderOptions
{
    public const string Interface = TranscodeEncoderInterfaces.Nvenc;

    public const string ConstantQualityRateControl = "constqp";
    public const string VariableBitrateRateControl = "vbr";
    public const string ConstantBitrateRateControl = "cbr";
    public const string DefaultRateControl = VariableBitrateRateControl;

    public const string FastestPreset = "p1";
    public const string FasterPreset = "p2";
    public const string FastPreset = "p3";
    public const string BalancedPreset = "p4";
    public const string QualityPreset = "p5";
    public const string HigherQualityPreset = "p6";
    public const string HighestQualityPreset = "p7";
    public const string DefaultPreset = BalancedPreset;

    public const string HighQualityTune = "hq";
    public const string LowLatencyTune = "ll";
    public const string UltraLowLatencyTune = "ull";
    public const string LosslessTune = "lossless";
    public const string DefaultTune = LowLatencyTune;

    public static HardwareDecoderSettings Apply(
        HardwareDecoderSettings settings,
        string? rateControl,
        string? preset,
        string? tune) =>
        settings
            .WithOption(Interface, HardwareEncoderApis.EncoderOpen, HardwareEncoderParameters.Options,
                "rc", NormalizeRateControl(rateControl))
            .WithOption(Interface, HardwareEncoderApis.EncoderOpen, HardwareEncoderParameters.Options,
                "preset", NormalizePreset(preset))
            .WithOption(Interface, HardwareEncoderApis.EncoderOpen, HardwareEncoderParameters.Options,
                "tune", NormalizeTune(tune))
            .WithOption(Interface, HardwareEncoderApis.EncoderOpen, HardwareEncoderParameters.Options,
                "zerolatency", "1");

    public static string NormalizeRateControl(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        ConstantQualityRateControl => ConstantQualityRateControl,
        ConstantBitrateRateControl => ConstantBitrateRateControl,
        VariableBitrateRateControl => VariableBitrateRateControl,
        _ => DefaultRateControl,
    };

    public static string NormalizePreset(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        FastestPreset => FastestPreset,
        FasterPreset => FasterPreset,
        FastPreset => FastPreset,
        BalancedPreset => BalancedPreset,
        QualityPreset => QualityPreset,
        HigherQualityPreset => HigherQualityPreset,
        HighestQualityPreset => HighestQualityPreset,
        _ => DefaultPreset,
    };

    public static string NormalizeTune(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        HighQualityTune => HighQualityTune,
        LowLatencyTune => LowLatencyTune,
        UltraLowLatencyTune => UltraLowLatencyTune,
        LosslessTune => LosslessTune,
        _ => DefaultTune,
    };
}

/// <summary>User-facing options shared by FFmpeg's Intel QSV video encoders.</summary>
public static class QsvEncoderOptions
{
    public const string Interface = TranscodeEncoderInterfaces.QuickSyncVideo;

    public const string VeryFastPreset = "veryfast";
    public const string FasterPreset = "faster";
    public const string FastPreset = "fast";
    public const string MediumPreset = "medium";
    public const string SlowPreset = "slow";
    public const string SlowerPreset = "slower";
    public const string VerySlowPreset = "veryslow";
    public const string DefaultPreset = MediumPreset;

    public const string UnknownScenario = "unknown";
    public const string DisplayRemotingScenario = "displayremoting";
    public const string VideoConferenceScenario = "videoconference";
    public const string ArchiveScenario = "archive";
    public const string LiveStreamingScenario = "livestreaming";
    public const string CameraCaptureScenario = "cameracapture";
    public const string VideoSurveillanceScenario = "videosurveillance";
    public const string GameStreamingScenario = "gamestreaming";
    public const string RemoteGamingScenario = "remotegaming";
    public const string DefaultScenario = LiveStreamingScenario;

    public static HardwareDecoderSettings Apply(
        HardwareDecoderSettings settings,
        string? preset,
        string? scenario) =>
        settings
            .WithOption(Interface, HardwareEncoderApis.EncoderOpen, HardwareEncoderParameters.Options,
                "preset", NormalizePreset(preset))
            .WithOption(Interface, HardwareEncoderApis.EncoderOpen, HardwareEncoderParameters.Options,
                "scenario", NormalizeScenario(scenario));

    public static string NormalizePreset(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        VeryFastPreset => VeryFastPreset,
        FasterPreset => FasterPreset,
        FastPreset => FastPreset,
        MediumPreset => MediumPreset,
        SlowPreset => SlowPreset,
        SlowerPreset => SlowerPreset,
        VerySlowPreset => VerySlowPreset,
        _ => DefaultPreset,
    };

    public static string NormalizeScenario(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        UnknownScenario => UnknownScenario,
        DisplayRemotingScenario => DisplayRemotingScenario,
        VideoConferenceScenario => VideoConferenceScenario,
        ArchiveScenario => ArchiveScenario,
        LiveStreamingScenario => LiveStreamingScenario,
        CameraCaptureScenario => CameraCaptureScenario,
        VideoSurveillanceScenario => VideoSurveillanceScenario,
        GameStreamingScenario => GameStreamingScenario,
        RemoteGamingScenario => RemoteGamingScenario,
        _ => DefaultScenario,
    };
}

/// <summary>Encoder-independent rate-control behavior used to size the output buffer.</summary>
public static class TranscodeEncoderRateControl
{
    public static bool UsesVariableBitrate(HardwareDecoderSettings settings, string encoderInterface)
    {
        IReadOnlyDictionary<string, string> values = settings.QueryOptions(
            encoderInterface,
            HardwareEncoderApis.EncoderOpen,
            HardwareEncoderParameters.Options);

        string key = string.Equals(encoderInterface, VaapiEncoderOptions.DeviceType, StringComparison.OrdinalIgnoreCase)
            ? "rc_mode"
            : string.Equals(encoderInterface, MediaFoundationEncoderOptions.Interface, StringComparison.OrdinalIgnoreCase)
                ? "rate_control"
                : "rc";
        if (!values.TryGetValue(key, out string? mode))
        {
            return false;
        }

        return encoderInterface.ToLowerInvariant() switch
        {
            TranscodeEncoderInterfaces.Vaapi => mode is
                VaapiEncoderOptions.VariableBitrateRateControl or
                VaapiEncoderOptions.QualityDefinedVariableBitrateRateControl or
                VaapiEncoderOptions.AverageVariableBitrateRateControl,
            TranscodeEncoderInterfaces.MediaFoundation => mode is
                MediaFoundationEncoderOptions.PeakConstrainedVariableBitrateRateControl or
                MediaFoundationEncoderOptions.UnconstrainedVariableBitrateRateControl or
                MediaFoundationEncoderOptions.LowDelayVariableBitrateRateControl or
                MediaFoundationEncoderOptions.GlobalVariableBitrateRateControl or
                MediaFoundationEncoderOptions.GlobalLowDelayVariableBitrateRateControl,
            TranscodeEncoderInterfaces.AdvancedMediaFramework => mode is
                AmfEncoderOptions.PeakVariableBitrateRateControl or
                AmfEncoderOptions.LatencyVariableBitrateRateControl or
                AmfEncoderOptions.QualityVariableBitrateRateControl or
                AmfEncoderOptions.HighQualityVariableBitrateRateControl,
            TranscodeEncoderInterfaces.Nvenc => mode == NvencEncoderOptions.VariableBitrateRateControl,
            _ => false,
        };
    }
}

/// <summary>What one video track should come out as. Zero means "leave it as the source has it".</summary>
public sealed record TranscodeVideoTarget
{
    public string CodecName { get; init; } = "h264";

    /// <summary>Bits per second the encoder is asked to hold to.</summary>
    public long BitRate { get; init; }

    /// <summary>Ceiling on the long edge of the picture; the aspect ratio is preserved.</summary>
    public int MaxWidth { get; init; }

    public int MaxHeight { get; init; }
}

/// <summary>What one audio track should come out as.</summary>
public sealed record TranscodeAudioTarget
{
    public string CodecName { get; init; } = "opus";

    public long BitRate { get; init; }
}

/// <summary>
/// How a head wants transcoding done, as opposed to what a client asked for. Everything platform
/// dependent lives here: the device to encode on and the arguments of the calls that open it, so
/// nothing in this assembly has to know what a machine actually has.
/// </summary>
public sealed record TranscodeOptions
{
    public static TranscodeOptions Disabled { get; } = new();

    /// <summary>Nothing is transcoded at all while this is off, whatever a client asks for.</summary>
    public bool Enabled { get; init; }

    public bool EnableHardware { get; init; } = true;

    /// <summary>
    /// Scaling on the device the frames were decoded on, rather than bringing them back across the
    /// bus to be resized. Off by default: a device scaler is the part of the hardware path that
    /// most often refuses a picture outright or hands back a discolored one.
    /// </summary>
    public bool EnableHardwareScaling { get; init; }

    /// <summary>
    /// Encoding on the processor, which costs an order of magnitude more than decoding does. Off by
    /// default: a server without a usable device is better off sending the original bytes.
    /// </summary>
    public bool AllowSoftwareEncoding { get; init; }

    /// <summary>
    /// FFmpeg encoder interface to use, or null to try the platform's own in preference order.
    /// This is an encoder family such as <c>mf</c> or <c>vaapi</c>, not a decode-only device such
    /// as <c>d3d11va</c>.
    /// </summary>
    public string? HardwareEncoder { get; init; }

    /// <summary>
    /// Arguments of the hardware calls, keyed by device type exactly as the decode path keys them.
    /// Null leaves every one of them at its FFmpeg default.
    /// </summary>
    public HardwareDecoderSettings? HardwareSettings { get; init; }

    /// <summary>0 lets libavcodec size the software decode and encode thread pools.</summary>
    public int ThreadCount { get; init; }

    /// <summary>Keyframe spacing, in seconds. A client seeks by asking for a new run, not by
    /// scanning what it has, so this only bounds how long a run takes to become watchable.</summary>
    public double KeyFrameIntervalSeconds { get; init; } = 2;
}
