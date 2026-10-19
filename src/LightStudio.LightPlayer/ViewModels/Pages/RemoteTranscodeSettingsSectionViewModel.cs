using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Remote;
using LightStudio.LightPlayer.Services;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// What this device asks the servers behind its remote libraries to re-encode. Shown as soon as one
/// of those libraries is configured, because the preference is the device's own.
/// </summary>
/// <remarks>
/// Codec preferences belong to this device. Server availability does not change their stored values.
/// </remarks>
public sealed partial class RemoteTranscodeSettingsSectionViewModel : SettingsSectionViewModel
{
    private readonly IAppSettingsStore settingsStore;
    private bool isLoaded;

    [ObservableProperty]
    private bool video;

    [ObservableProperty]
    private bool audio;

    [ObservableProperty]
    private string videoCodecs = string.Empty;

    [ObservableProperty]
    private VideoSettingsOption selectedVideoCodec = null!;

    [ObservableProperty]
    private VideoSettingsOption selectedAudioCodec = null!;

    [ObservableProperty]
    private decimal fileBitrateThreshold;

    [ObservableProperty]
    private decimal videoBitrate;

    [ObservableProperty]
    private decimal videoHeightThreshold;

    [ObservableProperty]
    private decimal videoHeight;

    [ObservableProperty]
    private string audioCodecsToTranscode = string.Empty;

    [ObservableProperty]
    private decimal audioBitrate;

    public RemoteTranscodeSettingsSectionViewModel(
        IAppSettingsStore settingsStore,
        ILibrarySourceRegistry? sources = null)
        : base(
            nameof(Strings.Resources.RemoteTranscodeSettings_Title),
            nameof(Strings.Resources.RemoteTranscodeSettings_Subtitle))
    {
        this.settingsStore = settingsStore;

        RemoteTranscodeSettings settings = settingsStore.RemoteTranscodeSettings;
        RemoteTranscodeCapabilities? capabilities = Offered(sources);
        VideoCodecOptions = Options(["av1", "hevc", "h264"], settings.VideoCodec);
        AudioCodecOptions = Options(["opus"], settings.AudioCodec);
        IsHardware = capabilities?.Hardware ?? false;
        HasServerNotice = capabilities is not null;

        video = settings.Video;
        audio = settings.Audio;
        videoCodecs = settings.VideoCodecs;
        selectedVideoCodec = Find(VideoCodecOptions, settings.VideoCodec);
        selectedAudioCodec = Find(AudioCodecOptions, settings.AudioCodec);
        fileBitrateThreshold = Megabits(settings.FileBitrateThreshold);
        videoBitrate = Megabits(settings.VideoBitrate);
        videoHeightThreshold = settings.VideoHeightThreshold;
        videoHeight = settings.VideoHeight;
        audioCodecsToTranscode = settings.AudioCodecsToTranscode;
        audioBitrate = Kilobits(settings.AudioBitrate);

        isLoaded = true;
    }

    public IReadOnlyList<VideoSettingsOption> VideoCodecOptions { get; }

    public IReadOnlyList<VideoSettingsOption> AudioCodecOptions { get; }

    public bool HasVideoCodecs => VideoCodecOptions.Count > 1;

    public bool HasAudioCodecs => AudioCodecOptions.Count > 1;

    /// <summary>Whether every source that offers this encodes on a device, which decides the notice.</summary>
    public bool IsHardware { get; }

    /// <summary>False until a source has been reached, when there is nothing to say about one.</summary>
    public bool HasServerNotice { get; }

    public string ServerNotice => IsHardware
        ? Strings.Resources.RemoteTranscodeSettings_HardwareNotice
        : Strings.Resources.RemoteTranscodeSettings_SoftwareNotice;

    partial void OnVideoChanged(bool value) => Persist();

    partial void OnAudioChanged(bool value) => Persist();

    partial void OnVideoCodecsChanged(string value) => Persist();

    partial void OnSelectedVideoCodecChanged(VideoSettingsOption value) => Persist();

    partial void OnSelectedAudioCodecChanged(VideoSettingsOption value) => Persist();

    partial void OnFileBitrateThresholdChanged(decimal value) => Persist();

    partial void OnVideoBitrateChanged(decimal value) => Persist();

    partial void OnVideoHeightThresholdChanged(decimal value) => Persist();

    partial void OnVideoHeightChanged(decimal value) => Persist();

    partial void OnAudioCodecsToTranscodeChanged(string value) => Persist();

    partial void OnAudioBitrateChanged(decimal value) => Persist();

    private void Persist()
    {
        if (!isLoaded)
        {
            return;
        }

        var settings = new RemoteTranscodeSettings
        {
            Video = Video,
            Audio = Audio,
            VideoCodecs = VideoCodecs.Trim(),
            VideoCodec = SelectedVideoCodec?.Value ?? string.Empty,
            FileBitrateThreshold = Bits(FileBitrateThreshold, 1_000_000),
            VideoBitrate = Bits(VideoBitrate, 1_000_000),
            VideoHeightThreshold = (int)Math.Clamp(VideoHeightThreshold, 0, 8192),
            VideoHeight = (int)Math.Clamp(VideoHeight, 0, 8192),
            AudioCodec = SelectedAudioCodec?.Value ?? string.Empty,
            AudioCodecsToTranscode = AudioCodecsToTranscode.Trim(),
            AudioBitrate = Bits(AudioBitrate, 1_000),
        };

        settingsStore.SetRemoteTranscodeSettings(settings);
    }

    /// <summary>
    /// What the configured sources between them will re-encode to. One page covers every library, so
    /// a codec any reachable server can produce is offered, and the software notice wins over the
    /// hardware one because it is the limit the user needs to know about.
    /// </summary>
    private static RemoteTranscodeCapabilities? Offered(ILibrarySourceRegistry? sources)
    {
        if (sources is null)
        {
            return null;
        }

        List<string> videoCodecs = [];
        List<string> audioCodecs = [];
        bool offered = false;
        bool hardware = true;
        foreach (ILibrarySource source in sources.Sources)
        {
            if (source.Capabilities.Transcoding is not { } capabilities ||
                !RemoteTranscoding.Offers(capabilities))
            {
                continue;
            }

            offered = true;
            hardware &= capabilities.Hardware;
            Merge(videoCodecs, capabilities.VideoCodecs);
            Merge(audioCodecs, capabilities.AudioCodecs);
        }

        return offered
            ? new RemoteTranscodeCapabilities([.. videoCodecs], [.. audioCodecs], hardware)
            : null;
    }

    private static void Merge(List<string> merged, string[] codecs)
    {
        foreach (string codec in codecs)
        {
            if (!merged.Contains(codec, StringComparer.OrdinalIgnoreCase))
            {
                merged.Add(codec);
            }
        }
    }

    private static IReadOnlyList<VideoSettingsOption> Options(string[] codecs, string saved)
    {
        var options = new List<VideoSettingsOption>
        {
            new(string.Empty, string.Empty, nameof(Strings.Resources.RemoteTranscodeSettings_Automatic)),
        };
        foreach (string codec in codecs.Append(saved)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            options.Add(new VideoSettingsOption(codec, codec.ToUpperInvariant()));
        }

        return options;
    }

    private static VideoSettingsOption Find(IReadOnlyList<VideoSettingsOption> options, string value) =>
        options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase))
        ?? options[0];

    private static decimal Megabits(long bits) => Math.Round(bits / 1_000_000m, 1);

    private static decimal Kilobits(long bits) => Math.Round(bits / 1_000m);

    private static long Bits(decimal value, long unit) => (long)Math.Max(0, value * unit);
}
