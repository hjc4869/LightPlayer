using System;
using System.Collections.Generic;
using System.Linq;
using LightStudio.FfmpegShim.Transcoding;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace LightStudio.LightPlayer.Server.Tui;

/// <summary>
/// What this server re-encodes for clients that ask for it. Only the options of the selected encoder
/// family are shown; the values kept for the others are carried through untouched.
/// </summary>
internal sealed class TranscodeSettingsTab : View, IConfigurationTab
{
    private readonly CheckBox enabled;
    private readonly CheckBox hardware;
    private readonly CheckBox hardwareScaling;
    private readonly CheckBox allowSoftware;
    private readonly TuiChoice encoderInterface;
    private readonly TextField device;
    private readonly TuiChoice rateControl;
    private readonly TuiChoice quality;
    private readonly TuiChoice extra;
    private string appliedInterface;

    public TranscodeSettingsTab(ServerConfiguration config)
    {
        Title = "_Transcoding";
        Width = Dim.Fill();
        Height = Dim.Fill();

        enabled = TuiLayout.Check(this, 0, "Re-encode for clients that ask for it", config.Transcode);
        hardware = TuiLayout.Check(this, 1, "Encode on a hardware device", config.TranscodeHardware);
        hardwareScaling = TuiLayout.Check(this, 2, "Scale on that device as well", config.TranscodeHardwareScaling);
        allowSoftware = TuiLayout.Check(this, 3, "Fall back to the processor when no device can be used", config.TranscodeAllowSoftware);
        allowSoftware.Visible = FfmpegTranscodeCapabilities.HasSoftwareVideoEncoders();

        appliedInterface = TranscodeEncoderInterfaces.NormalizeName(config.TranscodeDeviceType);
        int encoderRow = allowSoftware.Visible ? 5 : 4;
        encoderInterface = new TuiChoice(this, encoderRow, "Encoder");
        encoderInterface.SetOptions(EncoderInterfaceOptions(appliedInterface), appliedInterface);
        encoderInterface.List.ValueChanged += (_, _) => OnEncoderInterfaceChanged(config);

        device = TuiLayout.Field(this, encoderRow + 1, "Device", config.TranscodeDevice);
        TuiLayout.Note(this, encoderRow + 2, "  A DRM render node such as /dev/dri/renderD128 for VA-API. Empty takes the platform default.");

        rateControl = new TuiChoice(this, encoderRow + 4, "Rate control");
        quality = new TuiChoice(this, encoderRow + 5, "Quality");
        extra = new TuiChoice(this, encoderRow + 6, "Tuning");
        LoadEncoderOptions(config);
    }

    public string? Apply(ServerConfiguration config)
    {
        CaptureEncoderOptions(config);
        config.Transcode = enabled.IsChecked();
        config.TranscodeHardware = hardware.IsChecked();
        config.TranscodeHardwareScaling = hardwareScaling.IsChecked();
        if (allowSoftware.Visible)
        {
            config.TranscodeAllowSoftware = allowSoftware.IsChecked();
        }

        config.TranscodeDeviceType = TranscodeEncoderInterfaces.NormalizeName(encoderInterface.Value);
        config.TranscodeDevice = device.Text?.Trim() ?? string.Empty;
        return null;
    }

    /// <summary>
    /// Automatic selection plus the encoders this platform can reach, with whatever the file already
    /// names added so a configuration written for another host survives a round trip.
    /// </summary>
    private static IReadOnlyList<(string Value, string Label)> EncoderInterfaceOptions(string configured)
    {
        var options = new List<(string Value, string Label)> { (string.Empty, "Automatic") };
        foreach (TranscodeEncoderInterface definition in TranscodeEncoderInterfaces.GetPreferredForCurrentPlatform())
        {
            options.Add((definition.Name, definition.DisplayName));
        }

        if (configured.Length > 0 &&
            !options.Any(option => string.Equals(option.Value, configured, StringComparison.OrdinalIgnoreCase)) &&
            TranscodeEncoderInterfaces.Find(configured) is { } other)
        {
            options.Add((other.Name, other.DisplayName));
        }

        return options;
    }

    private void OnEncoderInterfaceChanged(ServerConfiguration config)
    {
        CaptureEncoderOptions(config);
        appliedInterface = TranscodeEncoderInterfaces.NormalizeName(encoderInterface.Value);
        LoadEncoderOptions(config);
        SetNeedsDraw();
    }

    /// <summary>Fills the three option rows for the selected encoder, hiding the ones it has no use for.</summary>
    private void LoadEncoderOptions(ServerConfiguration config)
    {
        switch (EffectiveInterface())
        {
            case TranscodeEncoderInterfaces.Vaapi:
                Show(rateControl, "Rate control", VaapiRateControls, config.TranscodeVaapiRateControl);
                Show(quality, "Quality", QualityPresets, config.TranscodeVaapiQuality);
                extra.Visible = false;
                break;

            case TranscodeEncoderInterfaces.MediaFoundation:
                Show(rateControl, "Rate control", MediaFoundationRateControls, config.TranscodeMediaFoundationRateControl);
                Show(quality, "Quality", QualityPresets, config.TranscodeMediaFoundationQuality);
                Show(extra, "Scenario", MediaFoundationScenarios, config.TranscodeMediaFoundationScenario);
                break;

            case TranscodeEncoderInterfaces.AdvancedMediaFramework:
                Show(rateControl, "Rate control", AmfRateControls, config.TranscodeAmfRateControl);
                Show(quality, "Quality", AmfQualityPresets, config.TranscodeAmfQuality);
                Show(extra, "Usage", AmfUsages, config.TranscodeAmfUsage);
                break;

            case TranscodeEncoderInterfaces.Nvenc:
                Show(rateControl, "Rate control", NvencRateControls, config.TranscodeNvencRateControl);
                Show(quality, "Preset", NvencPresets, config.TranscodeNvencPreset);
                Show(extra, "Tuning", NvencTunes, config.TranscodeNvencTune);
                break;

            case TranscodeEncoderInterfaces.QuickSyncVideo:
                Show(quality, "Preset", QsvPresets, config.TranscodeQsvPreset);
                Show(extra, "Scenario", QsvScenarios, config.TranscodeQsvScenario);
                rateControl.Visible = false;
                break;

            default:
                rateControl.Visible = false;
                quality.Visible = false;
                extra.Visible = false;
                break;
        }
    }

    /// <summary>Stores the visible options back under the encoder family they were shown for.</summary>
    private void CaptureEncoderOptions(ServerConfiguration config)
    {
        switch (appliedInterface.Length > 0 ? appliedInterface : PlatformDefaultInterface())
        {
            case TranscodeEncoderInterfaces.Vaapi:
                config.TranscodeVaapiRateControl = VaapiEncoderOptions.NormalizeRateControl(rateControl.Value);
                config.TranscodeVaapiQuality = VaapiEncoderOptions.NormalizeQualityPreset(quality.Value);
                break;

            case TranscodeEncoderInterfaces.MediaFoundation:
                config.TranscodeMediaFoundationRateControl =
                    MediaFoundationEncoderOptions.NormalizeRateControl(rateControl.Value);
                config.TranscodeMediaFoundationQuality =
                    MediaFoundationEncoderOptions.NormalizeQualityPreset(quality.Value);
                config.TranscodeMediaFoundationScenario = MediaFoundationEncoderOptions.NormalizeScenario(extra.Value);
                break;

            case TranscodeEncoderInterfaces.AdvancedMediaFramework:
                config.TranscodeAmfRateControl = AmfEncoderOptions.NormalizeRateControl(rateControl.Value);
                config.TranscodeAmfQuality = AmfEncoderOptions.NormalizeQualityPreset(quality.Value);
                config.TranscodeAmfUsage = AmfEncoderOptions.NormalizeUsage(extra.Value);
                break;

            case TranscodeEncoderInterfaces.Nvenc:
                config.TranscodeNvencRateControl = NvencEncoderOptions.NormalizeRateControl(rateControl.Value);
                config.TranscodeNvencPreset = NvencEncoderOptions.NormalizePreset(quality.Value);
                config.TranscodeNvencTune = NvencEncoderOptions.NormalizeTune(extra.Value);
                break;

            case TranscodeEncoderInterfaces.QuickSyncVideo:
                config.TranscodeQsvPreset = QsvEncoderOptions.NormalizePreset(quality.Value);
                config.TranscodeQsvScenario = QsvEncoderOptions.NormalizeScenario(extra.Value);
                break;
        }
    }

    /// <summary>Automatic selection lands on the platform's first encoder, so its options are the ones to show.</summary>
    private string EffectiveInterface() =>
        appliedInterface.Length > 0 ? appliedInterface : PlatformDefaultInterface();

    private static string PlatformDefaultInterface() =>
        TranscodeEncoderInterfaces.GetPreferredForCurrentPlatform().FirstOrDefault()?.Name ?? string.Empty;

    private static void Show(TuiChoice choice, string label, IReadOnlyList<(string, string)> options, string selected)
    {
        choice.Prompt.Text = label;
        choice.SetOptions(options, selected);
        choice.Visible = true;
    }

    private static readonly (string, string)[] QualityPresets =
    [
        (EncoderQualityPresets.Quality, "Quality"),
        (EncoderQualityPresets.Balanced, "Balanced"),
        (EncoderQualityPresets.Speed, "Speed"),
    ];

    private static readonly (string, string)[] VaapiRateControls =
    [
        (VaapiEncoderOptions.AutomaticRateControl, "Automatic"),
        (VaapiEncoderOptions.ConstantQualityRateControl, "Constant quality (CQP)"),
        (VaapiEncoderOptions.ConstantBitrateRateControl, "Constant bitrate (CBR)"),
        (VaapiEncoderOptions.VariableBitrateRateControl, "Variable bitrate (VBR)"),
        (VaapiEncoderOptions.IntelligentConstantQualityRateControl, "Intelligent constant quality (ICQ)"),
        (VaapiEncoderOptions.QualityDefinedVariableBitrateRateControl, "Quality-defined variable bitrate (QVBR)"),
        (VaapiEncoderOptions.AverageVariableBitrateRateControl, "Average variable bitrate (AVBR)"),
    ];

    private static readonly (string, string)[] MediaFoundationRateControls =
    [
        (MediaFoundationEncoderOptions.DefaultRateControl, "Automatic"),
        (MediaFoundationEncoderOptions.ConstantBitrateRateControl, "Constant bitrate (CBR)"),
        (MediaFoundationEncoderOptions.PeakConstrainedVariableBitrateRateControl, "Peak-constrained variable bitrate"),
        (MediaFoundationEncoderOptions.UnconstrainedVariableBitrateRateControl, "Unconstrained variable bitrate"),
        (MediaFoundationEncoderOptions.QualityRateControl, "Quality"),
        (MediaFoundationEncoderOptions.LowDelayVariableBitrateRateControl, "Low-delay variable bitrate"),
        (MediaFoundationEncoderOptions.GlobalVariableBitrateRateControl, "Global variable bitrate"),
        (MediaFoundationEncoderOptions.GlobalLowDelayVariableBitrateRateControl, "Global low-delay variable bitrate"),
    ];

    private static readonly (string, string)[] MediaFoundationScenarios =
    [
        (MediaFoundationEncoderOptions.DefaultScenario, "Automatic"),
        (MediaFoundationEncoderOptions.DisplayRemotingScenario, "Display remoting"),
        (MediaFoundationEncoderOptions.VideoConferenceScenario, "Video conference"),
        (MediaFoundationEncoderOptions.ArchiveScenario, "Archive"),
        (MediaFoundationEncoderOptions.LiveStreamingScenario, "Live streaming"),
        (MediaFoundationEncoderOptions.CameraRecordScenario, "Camera record"),
        (MediaFoundationEncoderOptions.DisplayRemotingFeatureMapScenario, "Display remoting with feature map"),
    ];

    private static readonly (string, string)[] AmfRateControls =
    [
        (AmfEncoderOptions.ConstantQualityRateControl, "Constant quality (CQP)"),
        (AmfEncoderOptions.ConstantBitrateRateControl, "Constant bitrate (CBR)"),
        (AmfEncoderOptions.PeakVariableBitrateRateControl, "Peak variable bitrate"),
        (AmfEncoderOptions.LatencyVariableBitrateRateControl, "Latency-constrained variable bitrate"),
        (AmfEncoderOptions.QualityVariableBitrateRateControl, "Quality-defined variable bitrate (QVBR)"),
        (AmfEncoderOptions.HighQualityVariableBitrateRateControl, "High-quality variable bitrate"),
        (AmfEncoderOptions.HighQualityConstantBitrateRateControl, "High-quality constant bitrate"),
    ];

    private static readonly (string, string)[] AmfQualityPresets =
    [
        (EncoderQualityPresets.Quality, "Quality"),
        (AmfEncoderOptions.HighQualityPreset, "High quality"),
        (EncoderQualityPresets.Balanced, "Balanced"),
        (EncoderQualityPresets.Speed, "Speed"),
    ];

    private static readonly (string, string)[] AmfUsages =
    [
        (AmfEncoderOptions.TranscodingUsage, "Transcoding"),
        (AmfEncoderOptions.UltraLowLatencyUsage, "Ultra-low latency"),
        (AmfEncoderOptions.LowLatencyUsage, "Low latency"),
        (AmfEncoderOptions.WebcamUsage, "Webcam"),
        (AmfEncoderOptions.HighQualityUsage, "High quality"),
        (AmfEncoderOptions.LowLatencyHighQualityUsage, "Low-latency high quality"),
    ];

    private static readonly (string, string)[] NvencRateControls =
    [
        (NvencEncoderOptions.ConstantQualityRateControl, "Constant quality (CQP)"),
        (NvencEncoderOptions.ConstantBitrateRateControl, "Constant bitrate (CBR)"),
        (NvencEncoderOptions.VariableBitrateRateControl, "Variable bitrate (VBR)"),
    ];

    private static readonly (string, string)[] NvencPresets =
    [
        (NvencEncoderOptions.FastestPreset, "Fastest (p1)"),
        (NvencEncoderOptions.FasterPreset, "Faster (p2)"),
        (NvencEncoderOptions.FastPreset, "Fast (p3)"),
        (NvencEncoderOptions.BalancedPreset, "Balanced (p4)"),
        (NvencEncoderOptions.QualityPreset, "Quality (p5)"),
        (NvencEncoderOptions.HigherQualityPreset, "Higher quality (p6)"),
        (NvencEncoderOptions.HighestQualityPreset, "Highest quality (p7)"),
    ];

    private static readonly (string, string)[] NvencTunes =
    [
        (NvencEncoderOptions.HighQualityTune, "High quality"),
        (NvencEncoderOptions.LowLatencyTune, "Low latency"),
        (NvencEncoderOptions.UltraLowLatencyTune, "Ultra-low latency"),
        (NvencEncoderOptions.LosslessTune, "Lossless"),
    ];

    private static readonly (string, string)[] QsvPresets =
    [
        (QsvEncoderOptions.VeryFastPreset, "Very fast"),
        (QsvEncoderOptions.FasterPreset, "Faster"),
        (QsvEncoderOptions.FastPreset, "Fast"),
        (QsvEncoderOptions.MediumPreset, "Medium"),
        (QsvEncoderOptions.SlowPreset, "Slow"),
        (QsvEncoderOptions.SlowerPreset, "Slower"),
        (QsvEncoderOptions.VerySlowPreset, "Very slow"),
    ];

    private static readonly (string, string)[] QsvScenarios =
    [
        (QsvEncoderOptions.UnknownScenario, "Automatic"),
        (QsvEncoderOptions.DisplayRemotingScenario, "Display remoting"),
        (QsvEncoderOptions.VideoConferenceScenario, "Video conference"),
        (QsvEncoderOptions.ArchiveScenario, "Archive"),
        (QsvEncoderOptions.LiveStreamingScenario, "Live streaming"),
        (QsvEncoderOptions.CameraCaptureScenario, "Camera capture"),
        (QsvEncoderOptions.VideoSurveillanceScenario, "Video surveillance"),
        (QsvEncoderOptions.GameStreamingScenario, "Game streaming"),
        (QsvEncoderOptions.RemoteGamingScenario, "Remote gaming"),
    ];
}
