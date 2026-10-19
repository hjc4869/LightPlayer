using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Transcoding;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightStudio.LightPlayer.Strings;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed partial class RemoteApiSettingsSectionViewModel : SettingsSectionViewModel
{
    private const int FirstDynamicPort = 49152;
    private const int LastDynamicPortExclusive = 65536;

    /// <summary>Host of the web client this project publishes; see docs/browser-client.md.</summary>
    private const string OfficialWebAppHost = "lmp.hjc.im";

    private readonly IAppSettingsStore settingsStore;
    private readonly IRemoteApiServerController controller;
    private readonly IReadOnlyList<TranscodeEncoderInterface> availableEncoderInterfaces;
    private readonly bool hasSoftwareVideoEncoders;
    private string passwordHash;
    private string transcodeVaapiRateControl = VaapiEncoderOptions.DefaultRateControl;
    private string transcodeVaapiQuality = VaapiEncoderOptions.DefaultQualityPreset;
    private string transcodeMediaFoundationRateControl = MediaFoundationEncoderOptions.PreferredRateControl;
    private string transcodeMediaFoundationQuality = MediaFoundationEncoderOptions.DefaultQualityPreset;
    private string transcodeMediaFoundationScenario = MediaFoundationEncoderOptions.PreferredScenario;
    private string transcodeAmfRateControl = AmfEncoderOptions.DefaultRateControl;
    private string transcodeAmfQuality = AmfEncoderOptions.DefaultQualityPreset;
    private string transcodeAmfUsage = AmfEncoderOptions.DefaultUsage;
    private string transcodeNvencRateControl = NvencEncoderOptions.DefaultRateControl;
    private string transcodeNvencPreset = NvencEncoderOptions.DefaultPreset;
    private string transcodeNvencTune = NvencEncoderOptions.DefaultTune;
    private string transcodeQsvPreset = QsvEncoderOptions.DefaultPreset;
    private string transcodeQsvScenario = QsvEncoderOptions.DefaultScenario;
    private bool loadingTranscodeEncoderOptions;
    private bool isLoaded;

    [ObservableProperty]
    private bool enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FirewallNotice))]
    [NotifyPropertyChangedFor(nameof(ConnectionNotice))]
    private string host = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FirewallNotice))]
    [NotifyPropertyChangedFor(nameof(ConnectionNotice))]
    private decimal port;

    [ObservableProperty]
    private bool requireAuthentication;

    /// <summary>Comma-separated origins or host names; see <see cref="RemoteApiSettings.AllowedOrigins"/>.</summary>
    [ObservableProperty]
    private string allowedOrigins = string.Empty;

    [ObservableProperty]
    private string username = string.Empty;

    /// <summary>
    /// Newly typed password. Only read while applying: the stored hash cannot be turned back into
    /// text, so an empty box means "keep the password that is already set".
    /// </summary>
    [ObservableProperty]
    private string password = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string statusText = string.Empty;

    [ObservableProperty]
    private bool pairingLinkCopied;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSoftwareTranscodeSettings))]
    [NotifyPropertyChangedFor(nameof(HasHardwareTranscodeSettings))]
    [NotifyPropertyChangedFor(nameof(HasHardwareScalingSetting))]
    [NotifyPropertyChangedFor(nameof(HasTranscodeRateControlSettings))]
    [NotifyPropertyChangedFor(nameof(HasTranscodeQualitySettings))]
    [NotifyPropertyChangedFor(nameof(HasTranscodeUsageSettings))]
    [NotifyPropertyChangedFor(nameof(HasTranscodeDevices))]
    private bool transcodeEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHardwareTranscodeSettings))]
    [NotifyPropertyChangedFor(nameof(HasHardwareScalingSetting))]
    [NotifyPropertyChangedFor(nameof(HasTranscodeRateControlSettings))]
    [NotifyPropertyChangedFor(nameof(HasTranscodeQualitySettings))]
    [NotifyPropertyChangedFor(nameof(HasTranscodeUsageSettings))]
    [NotifyPropertyChangedFor(nameof(HasTranscodeDevices))]
    private bool transcodeHardware;

    [ObservableProperty]
    private bool transcodeHardwareScaling;

    [ObservableProperty]
    private bool transcodeAllowSoftware;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHardwareScalingSetting))]
    [NotifyPropertyChangedFor(nameof(HasTranscodeRateControlSettings))]
    [NotifyPropertyChangedFor(nameof(HasTranscodeQualitySettings))]
    [NotifyPropertyChangedFor(nameof(HasTranscodeUsageSettings))]
    private VideoSettingsOption selectedTranscodeDeviceType = null!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranscodeDevices))]
    private IReadOnlyList<VideoSettingsOption> transcodeDeviceOptions = [];

    [ObservableProperty]
    private VideoSettingsOption? selectedTranscodeDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranscodeRateControlSettings))]
    private IReadOnlyList<VideoSettingsOption> transcodeRateControlOptions = [];

    [ObservableProperty]
    private VideoSettingsOption? selectedTranscodeRateControl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranscodeQualitySettings))]
    private IReadOnlyList<VideoSettingsOption> transcodeQualityOptions = [];

    [ObservableProperty]
    private VideoSettingsOption? selectedTranscodeQuality;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranscodeUsageSettings))]
    private IReadOnlyList<VideoSettingsOption> transcodeUsageOptions = [];

    [ObservableProperty]
    private VideoSettingsOption? selectedTranscodeUsage;

    public RemoteApiSettingsSectionViewModel(
        IAppSettingsStore settingsStore,
        IRemoteApiServerController controller)
        : base(nameof(Resources.RemoteApiSettings_Title), nameof(Resources.RemoteApiSettings_Subtitle))
    {
        this.settingsStore = settingsStore;
        this.controller = controller;

        availableEncoderInterfaces = FfmpegTranscodeCapabilities.GetAvailableEncoderInterfaces();
        hasSoftwareVideoEncoders = FfmpegTranscodeCapabilities.HasSoftwareVideoEncoders();
        var deviceTypes = new List<VideoSettingsOption>
        {
            new(string.Empty, string.Empty, nameof(Resources.RemoteApiSettings_TranscodeAutomatic)),
        };
        foreach (TranscodeEncoderInterface encoderInterface in availableEncoderInterfaces)
        {
            deviceTypes.Add(new VideoSettingsOption(encoderInterface.Name, encoderInterface.DisplayName));
        }

        TranscodeDeviceTypeOptions = deviceTypes;

        var settings = settingsStore.RemoteApiSettings;
        enabled = settings.Enabled;
        host = settings.Host;
        port = settings.Port;
        requireAuthentication = settings.RequireAuthentication;
        username = settings.Username;
        passwordHash = settings.PasswordHash;
        allowedOrigins = settings.AllowedOrigins;
        transcodeEnabled = settings.TranscodeEnabled;
        transcodeHardware = settings.TranscodeHardware;
        transcodeHardwareScaling = settings.TranscodeHardwareScaling;
        transcodeAllowSoftware = settings.TranscodeAllowSoftware;
        transcodeVaapiRateControl = VaapiEncoderOptions.NormalizeRateControl(settings.TranscodeVaapiRateControl);
        transcodeVaapiQuality = VaapiEncoderOptions.NormalizeQualityPreset(settings.TranscodeVaapiQuality);
        transcodeMediaFoundationRateControl = MediaFoundationEncoderOptions.NormalizeRateControl(
            settings.TranscodeMediaFoundationRateControl);
        transcodeMediaFoundationQuality = MediaFoundationEncoderOptions.NormalizeQualityPreset(
            settings.TranscodeMediaFoundationQuality);
        transcodeMediaFoundationScenario = MediaFoundationEncoderOptions.NormalizeScenario(
            settings.TranscodeMediaFoundationScenario);
        transcodeAmfRateControl = AmfEncoderOptions.NormalizeRateControl(settings.TranscodeAmfRateControl);
        transcodeAmfQuality = AmfEncoderOptions.NormalizeQualityPreset(settings.TranscodeAmfQuality);
        transcodeAmfUsage = AmfEncoderOptions.NormalizeUsage(settings.TranscodeAmfUsage);
        transcodeNvencRateControl = NvencEncoderOptions.NormalizeRateControl(settings.TranscodeNvencRateControl);
        transcodeNvencPreset = NvencEncoderOptions.NormalizePreset(settings.TranscodeNvencPreset);
        transcodeNvencTune = NvencEncoderOptions.NormalizeTune(settings.TranscodeNvencTune);
        transcodeQsvPreset = QsvEncoderOptions.NormalizePreset(settings.TranscodeQsvPreset);
        transcodeQsvScenario = QsvEncoderOptions.NormalizeScenario(settings.TranscodeQsvScenario);
        selectedTranscodeDeviceType = Find(
            deviceTypes,
            TranscodeEncoderInterfaces.NormalizeName(settings.TranscodeDeviceType));
        LoadTranscodeDevices(settings.TranscodeDevice);
        LoadTranscodeEncoderOptions();
        ApplyCommand = new RelayCommand(Apply);
        isLoaded = true;
        UpdateStatus(controller.State);
    }

    /// <summary>Hardware interfaces this build could encode through, plus an automatic entry.</summary>
    public IReadOnlyList<VideoSettingsOption> TranscodeDeviceTypeOptions { get; }

    /// <summary>Whether the chosen interface offers anything to choose between.</summary>
    public bool HasTranscodeDevices =>
        HasHardwareTranscodeSettings && TranscodeDeviceOptions.Count > 1;

    public bool HasHardwareTranscodeSettings => TranscodeEnabled && TranscodeHardware;

    public bool HasSoftwareTranscodeSettings => TranscodeEnabled && hasSoftwareVideoEncoders;

    public bool HasHardwareScalingSetting =>
        HasHardwareTranscodeSettings && EffectiveEncoderInterface()?.HasScaler == true;

    public bool HasTranscodeRateControlSettings =>
        HasHardwareTranscodeSettings && TranscodeRateControlOptions.Count > 0;

    public bool HasTranscodeQualitySettings =>
        HasHardwareTranscodeSettings && TranscodeQualityOptions.Count > 0;

    public bool HasTranscodeUsageSettings =>
        HasHardwareTranscodeSettings && TranscodeUsageOptions.Count > 0;

    public IRelayCommand ApplyCommand { get; }

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    public bool CanCreatePairingLink => TryGetPairingClientOrigin(out _);

    public string PairingClientOrigin =>
        TryGetPairingClientOrigin(out var origin) ? origin : string.Empty;

    public bool PairingRequiresAuthentication =>
        CanCreatePairingLink && settingsStore.RemoteApiSettings.RequireAuthentication;

    public bool PairingWithoutAuthentication =>
        CanCreatePairingLink && !settingsStore.RemoteApiSettings.RequireAuthentication;

    public string FirewallNotice =>
        string.Format(Resources.RemoteApiSettings_FirewallNotice, Port > 0 ? Port.ToString("0") : Resources.RemoteApiSettings_PortUnset);

    public string ConnectionNotice =>
        Host.Trim() == "0.0.0.0"
            ? string.Format(Resources.RemoteApiSettings_ConnectionNoticeRemote, Port > 0 ? Port.ToString("0") : Resources.RemoteApiSettings_PortPlaceholder)
            : string.Format(
                Resources.RemoteApiSettings_ConnectionNoticeLocal,
                string.IsNullOrWhiteSpace(Host) ? Resources.RemoteApiSettings_HostPlaceholder : Host.Trim(),
                Port > 0 ? Port.ToString("0") : Resources.RemoteApiSettings_PortPlaceholder);

    public string SecurityNotice =>
        Resources.RemoteApiSettings_SecurityNotice;

    /// <summary>Fills in the official web client so the common case needs no typing.</summary>
    [RelayCommand]
    private void AddOfficialOrigin()
    {
        var current = AllowedOrigins.Trim();
        var listed = current.Split(
            [',', ';', ' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (listed.Any(IsOfficialWebApp))
        {
            return;
        }

        AllowedOrigins = current.Length == 0 ? OfficialWebAppHost : current + ", " + OfficialWebAppHost;
    }

    private static bool IsOfficialWebApp(string origin)
    {
        var value = origin.TrimEnd('/');
        return value.Equals(OfficialWebAppHost, StringComparison.OrdinalIgnoreCase)
            || value.Equals("https://" + OfficialWebAppHost, StringComparison.OrdinalIgnoreCase)
            || value.Equals("http://" + OfficialWebAppHost, StringComparison.OrdinalIgnoreCase);
    }

    public string? CreatePairingLink()
    {
        if (!TryGetPairingClientOrigin(out var clientOrigin) || CreatePairing() is not { } pairing)
        {
            return null;
        }

        return $"{clientOrigin}/#pair?server={Uri.EscapeDataString(pairing.ServerAddress)}" +
            $"&token={Uri.EscapeDataString(pairing.Token)}" +
            $"&expires={pairing.ExpiresAtUnixSeconds.ToString(CultureInfo.InvariantCulture)}";
    }

    public Uri? CreateAndroidPairingUri()
    {
        var pairing = CreatePairing();
        return pairing is null ? null : RemoteApiPairingUri.Create(pairing);
    }

    private RemoteApiPairingInfo? CreatePairing()
    {
        PairingLinkCopied = false;
        if (controller is not IRemoteApiPairingController pairingController)
        {
            return null;
        }

        try
        {
            return pairingController.CreatePairing();
        }
        catch (Exception exception)
        {
            AppLog.Warning("RemoteApi.Pairing", "A pairing link could not be created.", exception);
            StatusText = Resources.RemoteApiSettings_PairingUnavailable;
            return null;
        }
    }

    public void NotifyPairingLinkCopied()
    {
        UpdateStatus(controller.State);
        PairingLinkCopied = true;
    }

    public void NotifyPairingLinkCopyFailed()
    {
        PairingLinkCopied = false;
        StatusText = Resources.RemoteApiSettings_PairingCopyFailed;
    }

    public void NotifyPairingQrCodeFailed()
    {
        PairingLinkCopied = false;
        StatusText = Resources.RemoteApiSettings_PairingQrFailed;
    }

    private bool TryGetPairingClientOrigin(out string origin)
    {
        origin = string.Empty;
        var settings = settingsStore.RemoteApiSettings;
        if (controller is not IRemoteApiPairingController ||
            !controller.State.IsRunning ||
            !string.Equals(settings.Host.Trim(), "0.0.0.0", StringComparison.Ordinal))
        {
            return false;
        }

        var entries = settings.AllowedOrigins.Split(
            [',', ';', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in entries)
        {
            if (entry == "*")
            {
                continue;
            }

            var candidate = entry.Contains("://", StringComparison.Ordinal)
                ? entry.TrimEnd('/')
                : "https://" + entry.TrimEnd('/');
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttps && !(parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback)))
            {
                continue;
            }

            origin = parsed.GetLeftPart(UriPartial.Authority);
            return true;
        }

        return false;
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!isLoaded)
        {
            return;
        }

        if (value && (string.IsNullOrWhiteSpace(Host) || Port <= 0))
        {
            Host = "0.0.0.0";
            Port = Random.Shared.Next(FirstDynamicPort, LastDynamicPortExclusive);
        }

        Apply();
    }

    private void Apply()
    {
        var hostValue = Host.Trim();
        int portValue;
        if (Port != decimal.Truncate(Port))
        {
            StatusText = Resources.RemoteApiSettings_InvalidPort;
            return;
        }

        try
        {
            portValue = decimal.ToInt32(Port);
        }
        catch (OverflowException)
        {
            StatusText = Resources.RemoteApiSettings_InvalidPort;
            return;
        }

        if (Password.Length > 0)
        {
            passwordHash = RemoteApiCredentials.HashPassword(Password);
            Password = string.Empty;
        }

        if (RequireAuthentication && (string.IsNullOrWhiteSpace(Username) || passwordHash.Length == 0))
        {
            StatusText = Resources.RemoteApiSettings_MissingCredentials;
            return;
        }

        var settings = settingsStore.RemoteApiSettings with
        {
            Enabled = Enabled,
            Host = hostValue,
            Port = portValue,
            RequireAuthentication = RequireAuthentication,
            Username = Username.Trim(),
            PasswordHash = passwordHash,
            AllowedOrigins = AllowedOrigins.Trim(),
        };

        if (settings.Enabled && !settings.IsConfigured)
        {
            StatusText = Resources.RemoteApiSettings_MissingHostOrPort;
            return;
        }

        settingsStore.SetRemoteApiSettings(settings);
        UpdateStatus(controller.Apply(settings));
    }

    private void SaveTranscodeSettings()
    {
        if (!isLoaded || loadingTranscodeEncoderOptions)
        {
            return;
        }

        CaptureTranscodeEncoderOptions();
        var settings = settingsStore.RemoteApiSettings with
        {
            TranscodeEnabled = TranscodeEnabled,
            TranscodeHardware = TranscodeHardware,
            TranscodeHardwareScaling = TranscodeHardwareScaling,
            TranscodeVaapiRateControl = transcodeVaapiRateControl,
            TranscodeVaapiQuality = transcodeVaapiQuality,
            TranscodeMediaFoundationRateControl = transcodeMediaFoundationRateControl,
            TranscodeMediaFoundationQuality = transcodeMediaFoundationQuality,
            TranscodeMediaFoundationScenario = transcodeMediaFoundationScenario,
            TranscodeAmfRateControl = transcodeAmfRateControl,
            TranscodeAmfQuality = transcodeAmfQuality,
            TranscodeAmfUsage = transcodeAmfUsage,
            TranscodeNvencRateControl = transcodeNvencRateControl,
            TranscodeNvencPreset = transcodeNvencPreset,
            TranscodeNvencTune = transcodeNvencTune,
            TranscodeQsvPreset = transcodeQsvPreset,
            TranscodeQsvScenario = transcodeQsvScenario,
            TranscodeAllowSoftware = TranscodeAllowSoftware,
            TranscodeDeviceType = TranscodeEncoderInterfaces.NormalizeName(SelectedTranscodeDeviceType.Value),
            TranscodeDevice = SelectedTranscodeDevice?.Value ?? string.Empty,
        };

        settingsStore.SetRemoteApiSettings(settings);
        UpdateStatus(controller.Apply(settings));
    }

    private void UpdateStatus(RemoteApiServerState state)
    {
        StatusText = !string.IsNullOrEmpty(state.ErrorMessage)
            ? string.Format(Resources.RemoteApiSettings_ServerFailed, state.ErrorMessage)
            : state.IsRunning
                ? string.Format(Resources.RemoteApiSettings_ServerRunning, state.Address)
                : Resources.RemoteApiSettings_ServerDisabled;
            PairingLinkCopied = false;
            OnPropertyChanged(nameof(CanCreatePairingLink));
            OnPropertyChanged(nameof(PairingClientOrigin));
            OnPropertyChanged(nameof(PairingRequiresAuthentication));
            OnPropertyChanged(nameof(PairingWithoutAuthentication));
    }

    partial void OnTranscodeEnabledChanged(bool value) => SaveTranscodeSettings();

    partial void OnTranscodeHardwareChanged(bool value) => SaveTranscodeSettings();

    partial void OnTranscodeHardwareScalingChanged(bool value) => SaveTranscodeSettings();

    partial void OnTranscodeAllowSoftwareChanged(bool value) => SaveTranscodeSettings();

    partial void OnSelectedTranscodeDeviceChanged(VideoSettingsOption? value) => SaveTranscodeSettings();

    partial void OnSelectedTranscodeRateControlChanged(VideoSettingsOption? value) => SaveTranscodeSettings();

    partial void OnSelectedTranscodeQualityChanged(VideoSettingsOption? value) => SaveTranscodeSettings();

    partial void OnSelectedTranscodeUsageChanged(VideoSettingsOption? value) => SaveTranscodeSettings();

    partial void OnSelectedTranscodeDeviceTypeChanging(VideoSettingsOption value) =>
        CaptureTranscodeEncoderOptions();

    partial void OnSelectedTranscodeDeviceTypeChanged(VideoSettingsOption value)
    {
        loadingTranscodeEncoderOptions = true;
        try
        {
            LoadTranscodeDevices(string.Empty);
            LoadTranscodeEncoderOptions();
        }
        finally
        {
            loadingTranscodeEncoderOptions = false;
        }

        SaveTranscodeSettings();
    }

    /// <summary>
    /// Rebuilds the device list from the profile the head registered for the chosen interface.
    /// Automatic selection configures no device, because which interface it lands on is only known
    /// once something is actually encoded.
    /// </summary>
    private void LoadTranscodeDevices(string selected)
    {
        string encoderInterface = SelectedTranscodeDeviceType?.Value ?? string.Empty;
        string deviceType = TranscodeEncoderInterfaces.Find(encoderInterface)?.DeviceType ?? string.Empty;
        var devices = new List<VideoSettingsOption>
        {
            new(string.Empty, string.Empty, nameof(Resources.RemoteApiSettings_TranscodeAutomatic)),
        };

        foreach (HardwareDeviceInfo device in HardwareDecoderProfiles.Find(deviceType)?.GetDevices() ?? [])
        {
            devices.Add(new VideoSettingsOption(device.Id, device.Name));
        }

        TranscodeDeviceOptions = devices;
        SelectedTranscodeDevice = Find(devices, selected);
    }

    private TranscodeEncoderInterface? EffectiveEncoderInterface()
    {
        string selected = SelectedTranscodeDeviceType?.Value ?? string.Empty;
        return selected.Length > 0
            ? TranscodeEncoderInterfaces.Find(selected)
            : availableEncoderInterfaces.FirstOrDefault();
    }

    /// <summary>
    /// Presents the controls common to an encoder family in three rows. Their concrete values are
    /// kept separately, so changing from MF to NVENC and back does not overwrite either preset.
    /// </summary>
    private void LoadTranscodeEncoderOptions()
    {
        loadingTranscodeEncoderOptions = true;
        try
        {
            IReadOnlyList<VideoSettingsOption> rateControl = [];
            IReadOnlyList<VideoSettingsOption> quality = [];
            IReadOnlyList<VideoSettingsOption> usage = [];
            string selectedRateControl = string.Empty;
            string selectedQuality = string.Empty;
            string selectedUsage = string.Empty;

            switch (EffectiveEncoderInterface()?.Name)
            {
                case TranscodeEncoderInterfaces.Vaapi:
                    rateControl =
                    [
                        new(VaapiEncoderOptions.AutomaticRateControl, string.Empty,
                            nameof(Resources.RemoteApiSettings_TranscodeAutomatic)),
                        new(VaapiEncoderOptions.ConstantQualityRateControl, string.Empty,
                            nameof(Resources.RemoteApiSettings_TranscodeVaapiRateCqp)),
                        new(VaapiEncoderOptions.ConstantBitrateRateControl, string.Empty,
                            nameof(Resources.RemoteApiSettings_TranscodeVaapiRateCbr)),
                        new(VaapiEncoderOptions.VariableBitrateRateControl, string.Empty,
                            nameof(Resources.RemoteApiSettings_TranscodeVaapiRateVbr)),
                        new(VaapiEncoderOptions.IntelligentConstantQualityRateControl, string.Empty,
                            nameof(Resources.RemoteApiSettings_TranscodeVaapiRateIcq)),
                        new(VaapiEncoderOptions.QualityDefinedVariableBitrateRateControl, string.Empty,
                            nameof(Resources.RemoteApiSettings_TranscodeVaapiRateQvbr)),
                        new(VaapiEncoderOptions.AverageVariableBitrateRateControl, string.Empty,
                            nameof(Resources.RemoteApiSettings_TranscodeVaapiRateAvbr)),
                    ];
                    quality = BasicQualityOptions();
                    selectedRateControl = transcodeVaapiRateControl;
                    selectedQuality = transcodeVaapiQuality;
                    break;

                case TranscodeEncoderInterfaces.MediaFoundation:
                    rateControl =
                    [
                        AutomaticOption(MediaFoundationEncoderOptions.DefaultRateControl),
                        new(MediaFoundationEncoderOptions.ConstantBitrateRateControl, "Constant bitrate (CBR)"),
                        new(MediaFoundationEncoderOptions.PeakConstrainedVariableBitrateRateControl,
                            "Peak-constrained variable bitrate"),
                        new(MediaFoundationEncoderOptions.UnconstrainedVariableBitrateRateControl,
                            "Unconstrained variable bitrate"),
                        new(MediaFoundationEncoderOptions.QualityRateControl, "Quality-based"),
                        new(MediaFoundationEncoderOptions.LowDelayVariableBitrateRateControl,
                            "Low-delay variable bitrate"),
                        new(MediaFoundationEncoderOptions.GlobalVariableBitrateRateControl,
                            "Global variable bitrate"),
                        new(MediaFoundationEncoderOptions.GlobalLowDelayVariableBitrateRateControl,
                            "Global low-delay variable bitrate"),
                    ];
                    quality = BasicQualityOptions();
                    usage =
                    [
                        AutomaticOption(MediaFoundationEncoderOptions.DefaultScenario),
                        new(MediaFoundationEncoderOptions.DisplayRemotingScenario, "Display remoting"),
                        new(MediaFoundationEncoderOptions.VideoConferenceScenario, "Video conference"),
                        new(MediaFoundationEncoderOptions.ArchiveScenario, "Archive"),
                        new(MediaFoundationEncoderOptions.LiveStreamingScenario, "Live streaming"),
                        new(MediaFoundationEncoderOptions.CameraRecordScenario, "Camera recording"),
                        new(MediaFoundationEncoderOptions.DisplayRemotingFeatureMapScenario,
                            "Display remoting with feature map"),
                    ];
                    selectedRateControl = transcodeMediaFoundationRateControl;
                    selectedQuality = transcodeMediaFoundationQuality;
                    selectedUsage = transcodeMediaFoundationScenario;
                    break;

                case TranscodeEncoderInterfaces.AdvancedMediaFramework:
                    rateControl =
                    [
                        new(AmfEncoderOptions.ConstantQualityRateControl, "Constant quality (CQP)"),
                        new(AmfEncoderOptions.ConstantBitrateRateControl, "Constant bitrate (CBR)"),
                        new(AmfEncoderOptions.PeakVariableBitrateRateControl, "Peak variable bitrate"),
                        new(AmfEncoderOptions.LatencyVariableBitrateRateControl, "Latency-constrained VBR"),
                        new(AmfEncoderOptions.QualityVariableBitrateRateControl, "Quality variable bitrate"),
                        new(AmfEncoderOptions.HighQualityVariableBitrateRateControl, "High-quality VBR"),
                        new(AmfEncoderOptions.HighQualityConstantBitrateRateControl, "High-quality CBR"),
                    ];
                    quality =
                    [
                        .. BasicQualityOptions(),
                        new(AmfEncoderOptions.HighQualityPreset, "High quality"),
                    ];
                    usage =
                    [
                        new(AmfEncoderOptions.TranscodingUsage, "Transcoding"),
                        new(AmfEncoderOptions.UltraLowLatencyUsage, "Ultra-low latency"),
                        new(AmfEncoderOptions.LowLatencyUsage, "Low latency"),
                        new(AmfEncoderOptions.WebcamUsage, "Webcam"),
                        new(AmfEncoderOptions.HighQualityUsage, "High quality"),
                        new(AmfEncoderOptions.LowLatencyHighQualityUsage, "Low latency, high quality"),
                    ];
                    selectedRateControl = transcodeAmfRateControl;
                    selectedQuality = transcodeAmfQuality;
                    selectedUsage = transcodeAmfUsage;
                    break;

                case TranscodeEncoderInterfaces.Nvenc:
                    rateControl =
                    [
                        new(NvencEncoderOptions.ConstantQualityRateControl, "Constant quality"),
                        new(NvencEncoderOptions.VariableBitrateRateControl, "Variable bitrate (VBR)"),
                        new(NvencEncoderOptions.ConstantBitrateRateControl, "Constant bitrate (CBR)"),
                    ];
                    quality =
                    [
                        new(NvencEncoderOptions.FastestPreset, "P1 — fastest"),
                        new(NvencEncoderOptions.FasterPreset, "P2 — faster"),
                        new(NvencEncoderOptions.FastPreset, "P3 — fast"),
                        new(NvencEncoderOptions.BalancedPreset, "P4 — balanced"),
                        new(NvencEncoderOptions.QualityPreset, "P5 — quality"),
                        new(NvencEncoderOptions.HigherQualityPreset, "P6 — higher quality"),
                        new(NvencEncoderOptions.HighestQualityPreset, "P7 — highest quality"),
                    ];
                    usage =
                    [
                        new(NvencEncoderOptions.HighQualityTune, "High quality"),
                        new(NvencEncoderOptions.LowLatencyTune, "Low latency"),
                        new(NvencEncoderOptions.UltraLowLatencyTune, "Ultra-low latency"),
                        new(NvencEncoderOptions.LosslessTune, "Lossless"),
                    ];
                    selectedRateControl = transcodeNvencRateControl;
                    selectedQuality = transcodeNvencPreset;
                    selectedUsage = transcodeNvencTune;
                    break;

                case TranscodeEncoderInterfaces.QuickSyncVideo:
                    quality =
                    [
                        new(QsvEncoderOptions.VeryFastPreset, "Very fast"),
                        new(QsvEncoderOptions.FasterPreset, "Faster"),
                        new(QsvEncoderOptions.FastPreset, "Fast"),
                        new(QsvEncoderOptions.MediumPreset, "Medium"),
                        new(QsvEncoderOptions.SlowPreset, "Slow"),
                        new(QsvEncoderOptions.SlowerPreset, "Slower"),
                        new(QsvEncoderOptions.VerySlowPreset, "Very slow"),
                    ];
                    usage =
                    [
                        AutomaticOption(QsvEncoderOptions.UnknownScenario),
                        new(QsvEncoderOptions.DisplayRemotingScenario, "Display remoting"),
                        new(QsvEncoderOptions.VideoConferenceScenario, "Video conference"),
                        new(QsvEncoderOptions.ArchiveScenario, "Archive"),
                        new(QsvEncoderOptions.LiveStreamingScenario, "Live streaming"),
                        new(QsvEncoderOptions.CameraCaptureScenario, "Camera capture"),
                        new(QsvEncoderOptions.VideoSurveillanceScenario, "Video surveillance"),
                        new(QsvEncoderOptions.GameStreamingScenario, "Game streaming"),
                        new(QsvEncoderOptions.RemoteGamingScenario, "Remote gaming"),
                    ];
                    selectedQuality = transcodeQsvPreset;
                    selectedUsage = transcodeQsvScenario;
                    break;
            }

            TranscodeRateControlOptions = rateControl;
            SelectedTranscodeRateControl = FindOrNull(rateControl, selectedRateControl);
            TranscodeQualityOptions = quality;
            SelectedTranscodeQuality = FindOrNull(quality, selectedQuality);
            TranscodeUsageOptions = usage;
            SelectedTranscodeUsage = FindOrNull(usage, selectedUsage);
        }
        finally
        {
            loadingTranscodeEncoderOptions = false;
        }
    }

    private void CaptureTranscodeEncoderOptions()
    {
        if (loadingTranscodeEncoderOptions)
        {
            return;
        }

        string? rateControl = SelectedTranscodeRateControl?.Value;
        string? quality = SelectedTranscodeQuality?.Value;
        string? usage = SelectedTranscodeUsage?.Value;
        switch (EffectiveEncoderInterface()?.Name)
        {
            case TranscodeEncoderInterfaces.Vaapi:
                transcodeVaapiRateControl = VaapiEncoderOptions.NormalizeRateControl(rateControl);
                transcodeVaapiQuality = VaapiEncoderOptions.NormalizeQualityPreset(quality);
                break;
            case TranscodeEncoderInterfaces.MediaFoundation:
                transcodeMediaFoundationRateControl = MediaFoundationEncoderOptions.NormalizeRateControl(rateControl);
                transcodeMediaFoundationQuality = MediaFoundationEncoderOptions.NormalizeQualityPreset(quality);
                transcodeMediaFoundationScenario = MediaFoundationEncoderOptions.NormalizeScenario(usage);
                break;
            case TranscodeEncoderInterfaces.AdvancedMediaFramework:
                transcodeAmfRateControl = AmfEncoderOptions.NormalizeRateControl(rateControl);
                transcodeAmfQuality = AmfEncoderOptions.NormalizeQualityPreset(quality);
                transcodeAmfUsage = AmfEncoderOptions.NormalizeUsage(usage);
                break;
            case TranscodeEncoderInterfaces.Nvenc:
                transcodeNvencRateControl = NvencEncoderOptions.NormalizeRateControl(rateControl);
                transcodeNvencPreset = NvencEncoderOptions.NormalizePreset(quality);
                transcodeNvencTune = NvencEncoderOptions.NormalizeTune(usage);
                break;
            case TranscodeEncoderInterfaces.QuickSyncVideo:
                transcodeQsvPreset = QsvEncoderOptions.NormalizePreset(quality);
                transcodeQsvScenario = QsvEncoderOptions.NormalizeScenario(usage);
                break;
        }
    }

    private static IReadOnlyList<VideoSettingsOption> BasicQualityOptions() =>
    [
        new(EncoderQualityPresets.Quality, string.Empty,
            nameof(Resources.RemoteApiSettings_TranscodeVaapiQualityQuality)),
        new(EncoderQualityPresets.Balanced, string.Empty,
            nameof(Resources.RemoteApiSettings_TranscodeVaapiQualityBalanced)),
        new(EncoderQualityPresets.Speed, string.Empty,
            nameof(Resources.RemoteApiSettings_TranscodeVaapiQualitySpeed)),
    ];

    private static VideoSettingsOption AutomaticOption(string value) =>
        new(value, string.Empty, nameof(Resources.RemoteApiSettings_TranscodeAutomatic));

    private static VideoSettingsOption? FindOrNull(IReadOnlyList<VideoSettingsOption> options, string value) =>
        options.Count == 0 ? null : Find(options, value);

    private static VideoSettingsOption Find(IReadOnlyList<VideoSettingsOption> options, string value) =>
        options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.Ordinal))
        ?? options[0];
}