using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Shaders;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightMediaRenderer.Avalonia.Shaders;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed partial class VideoSettingsOption : ViewModelBase
{
    private readonly string fallbackTitle;
    private readonly string? titleKey;

    /// <summary>
    /// Filled into the title's <c>{0}</c>, for an option whose name describes something the app
    /// worked out rather than something fixed. Empty leaves the title as it is.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string titleArgument = string.Empty;

    public VideoSettingsOption(string value, string fallbackTitle, string? titleKey = null)
    {
        Value = value;
        this.fallbackTitle = fallbackTitle;
        this.titleKey = titleKey;
    }

    public string Value { get; }

    public string Title => TitleArgument.Length > 0
        ? string.Format(CultureInfo.CurrentCulture, Localized(titleKey, fallbackTitle), TitleArgument)
        : Localized(titleKey, fallbackTitle);
}

/// <summary>
/// One key/value argument a hardware profile offers, shown as a text box or a drop-down according
/// to what the profile says the option accepts.
/// </summary>
public sealed partial class HardwareOptionRowViewModel : ViewModelBase
{
    private readonly Action<HardwareOptionRowViewModel> changed;
    private readonly bool isLoaded;

    [ObservableProperty]
    private string text = string.Empty;

    [ObservableProperty]
    private VideoSettingsOption selectedChoice = null!;

    public HardwareOptionRowViewModel(
        HardwareOptionDescriptor descriptor,
        string value,
        Action<HardwareOptionRowViewModel> changed)
    {
        Descriptor = descriptor;
        this.changed = changed;

        List<VideoSettingsOption> choices =
        [
            new(string.Empty, string.Empty, "VideoSettings_HardwareOptionDefault"),
        ];
        foreach (HardwareSettingChoice choice in descriptor.Choices ?? [])
            choices.Add(new VideoSettingsOption(choice.Value, choice.Name));
        Choices = choices;

        text = value;
        selectedChoice = choices.FirstOrDefault(
            choice => string.Equals(choice.Value, value, StringComparison.Ordinal)) ?? choices[0];
        isLoaded = true;
    }

    public HardwareOptionDescriptor Descriptor { get; }

    public IReadOnlyList<VideoSettingsOption> Choices { get; }

    public bool IsChoice => Descriptor.Kind == HardwareSettingKind.Choice;

    public bool IsText => !IsChoice;

    public string Header => Descriptor.Name;

    public string Description => Descriptor.Description;

    /// <summary>The value to persist, empty when the option is left at FFmpeg's own default.</summary>
    public string Value => IsChoice ? SelectedChoice.Value : Text.Trim();

    partial void OnTextChanged(string value)
    {
        if (!isLoaded)
            return;

        // A half-typed number is not a value to store; the last valid one stands until it becomes one.
        if (Descriptor.Kind == HardwareSettingKind.Integer &&
            value.Length > 0 &&
            !int.TryParse(value, CultureInfo.InvariantCulture, out _))
        {
            return;
        }

        changed(this);
    }

    partial void OnSelectedChoiceChanged(VideoSettingsOption value)
    {
        if (isLoaded)
            changed(this);
    }
}

/// <summary>One bit of a flags argument, shown as a switch.</summary>
public sealed partial class HardwareFlagRowViewModel : ViewModelBase
{
    private readonly Action<HardwareFlagRowViewModel> changed;
    private readonly bool isLoaded;

    [ObservableProperty]
    private bool isEnabled;

    public HardwareFlagRowViewModel(
        HardwareFlagDescriptor descriptor,
        bool enabled,
        Action<HardwareFlagRowViewModel> changed)
    {
        Descriptor = descriptor;
        this.changed = changed;
        isEnabled = enabled;
        isLoaded = true;
    }

    public HardwareFlagDescriptor Descriptor { get; }

    public string Header => Descriptor.Name;

    public string Description => Descriptor.Description;

    partial void OnIsEnabledChanged(bool value)
    {
        if (isLoaded)
            changed(this);
    }
}

public sealed partial class VideoSettingsSectionViewModel : SettingsSectionViewModel
{
    private readonly IAppSettingsStore settingsStore;
    private readonly VideoShaderPackageCatalog? shaderPackages;
    private readonly IDialogService? dialogService;
    private readonly VideoSettingsOption automaticPanelBitDepth;
    private bool isLoaded;
    private bool suppressShaderPackageRebuild;

    [ObservableProperty]
    private VideoSettingsOption selectedScaling = null!;

    [ObservableProperty]
    private VideoSettingsOption selectedDownscaling = null!;

    [ObservableProperty]
    private bool preferHardwareProcessing;

    [ObservableProperty]
    private VideoSettingsOption selectedDebanding = null!;

    [ObservableProperty]
    private VideoSettingsOption selectedBuffer = null!;

    [ObservableProperty]
    private bool enableHdrPassthrough;

    [ObservableProperty]
    private VideoSettingsOption selectedBt709Interpretation = null!;

    [ObservableProperty]
    private VideoSettingsOption selectedPanelBitDepth = null!;

    [ObservableProperty]
    private bool enableHardwareDecoding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HardwareDecoderDescription))]
    private VideoSettingsOption selectedHardwareDecoder = null!;

    [ObservableProperty]
    private VideoSettingsOption selectedPhotoDecodePreference = null!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHardwareDevices))]
    private IReadOnlyList<VideoSettingsOption> hardwareDeviceOptions = [];

    [ObservableProperty]
    private VideoSettingsOption? selectedHardwareDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShaderPackageStatus))]
    private string shaderPackageStatus = string.Empty;

    public VideoSettingsSectionViewModel(
        IAppSettingsStore settingsStore,
        VideoShaderPackageCatalog? shaderPackages = null,
        IDialogService? dialogService = null,
        IReadOnlyList<string>? availableHardwareDeviceTypes = null)
        : base("VideoSettings_Title", "VideoSettings_Subtitle")
    {
        this.settingsStore = settingsStore;
        this.shaderPackages = shaderPackages;
        this.dialogService = dialogService;

        ScalingOptions =
        [
            new(nameof(VideoScalingQuality.NearestNeighbour), string.Empty, "VideoSettings_ScalingNearest"),
            new(nameof(VideoScalingQuality.Bilinear), string.Empty, "VideoSettings_ScalingBilinear"),
            new(nameof(VideoScalingQuality.Trilinear), string.Empty, "VideoSettings_ScalingTrilinear"),
            new(nameof(VideoScalingQuality.Mitchell), "Mitchell"),
            new(nameof(VideoScalingQuality.CatmullRom), "Catmull-Rom"),
            new(nameof(VideoScalingQuality.Lanczos3), "Lanczos-3"),
            new(nameof(VideoScalingQuality.Jinc), "Jinc"),
        ];
        DebandingOptions =
        [
            new(nameof(VideoDebandingQuality.Off), "Off", "VideoSettings_DebandingOff"),
            new(nameof(VideoDebandingQuality.Low), "Low", "VideoSettings_DebandingLow"),
            new(nameof(VideoDebandingQuality.Medium), "Medium", "VideoSettings_DebandingMedium"),
            new(nameof(VideoDebandingQuality.High), "High", "VideoSettings_DebandingHigh"),
        ];
        BufferOptions =
        [
            new("100", "0.1 s"),
            new("250", "0.25 s"),
            new("500", "0.5 s"),
            new("1000", "1 s"),
            new("2000", "2 s"),
        ];

        Bt709InterpretationOptions =
        [
            new(nameof(Bt709Interpretation.PlatformPreferred), "Platform preferred", "VideoSettings_Bt709PlatformPreferred"),
            new(nameof(Bt709Interpretation.Gamma1961), "Gamma {0}", "VideoSettings_Bt709GammaFormat") { TitleArgument = "1.961" },
            new(nameof(Bt709Interpretation.Gamma22), "Gamma {0}", "VideoSettings_Bt709GammaFormat") { TitleArgument = "2.2" },
            new(nameof(Bt709Interpretation.Gamma24), "Gamma {0}", "VideoSettings_Bt709GammaFormat") { TitleArgument = "2.4 (BT.1886)" },
            new(nameof(Bt709Interpretation.Srgb), "sRGB"),
        ];

        automaticPanelBitDepth = new VideoSettingsOption(
            "0",
            "Default ({0})",
            "VideoSettings_PanelBitDepthAutomatic");
        var bitDepthOptions = new List<VideoSettingsOption> { automaticPanelBitDepth };
        foreach (int depth in VideoPanelBitDepthSettings.Supported)
        {
            bitDepthOptions.Add(new VideoSettingsOption(
                depth.ToString(CultureInfo.InvariantCulture),
                BitDepthName(depth)));
        }

        PanelBitDepthOptions = bitDepthOptions;

        var hardwareOptions = new List<VideoSettingsOption>
        {
            new(string.Empty, string.Empty, "VideoSettings_HardwareAuto"),
        };
        foreach (string type in availableHardwareDeviceTypes ?? LightMediaRenderer.Avalonia.Pipeline.MediaDecodeSessionFactory.HardwareDeviceTypes
            ?? VideoDecoderFactory.GetAvailableHardwareDeviceTypes())
            hardwareOptions.Add(new VideoSettingsOption(type, HardwareDisplayName(type)));
        HardwareDecoderOptions = hardwareOptions;

        PhotoDecodePreferenceOptions =
        [
            new(bool.FalseString, "Memory", "VideoSettings_PhotoDecodeMemory"),
            new(bool.TrueString, "Performance", "VideoSettings_PhotoDecodePerformance"),
        ];

        selectedScaling = FindOption(ScalingOptions, settingsStore.PreferredVideoScaling.ToString());
        selectedDownscaling = FindOption(ScalingOptions, settingsStore.PreferredVideoDownscaling.ToString());
        preferHardwareProcessing = settingsStore.PreferHardwareVideoProcessing;
        selectedDebanding = FindOption(DebandingOptions, settingsStore.VideoDebanding.ToString());
        selectedBuffer = FindOption(
            BufferOptions,
            ((int)settingsStore.VideoBufferDuration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
        enableHdrPassthrough = settingsStore.EnableHdrPassthrough;
        selectedBt709Interpretation = FindOption(Bt709InterpretationOptions, settingsStore.VideoBt709Interpretation.ToString());
        VideoSurfaceTransfers.Bt709Preference = settingsStore.VideoBt709Interpretation;
        enableHardwareDecoding = settingsStore.EnableHardwareDecoding;
        selectedHardwareDecoder = FindOption(HardwareDecoderOptions, settingsStore.PreferredHardwareDecoder);
        selectedPhotoDecodePreference = FindOption(
            PhotoDecodePreferenceOptions, settingsStore.PreferPhotoDecodePerformance.ToString());
        selectedPanelBitDepth = FindOption(
            PanelBitDepthOptions,
            settingsStore.DisplayPanelBitDepth.ToString(CultureInfo.InvariantCulture));
        UpdateAutomaticPanelBitDepth();
        LoadHardwareProfile();
        if (shaderPackages is not null)
        {
            shaderPackages.Changed += OnShaderPackagesChanged;
            RebuildShaderPackages();
        }
        isLoaded = true;
    }

    public IReadOnlyList<VideoSettingsOption> ScalingOptions { get; }

    public IReadOnlyList<VideoSettingsOption> DebandingOptions { get; }

    public IReadOnlyList<VideoSettingsOption> BufferOptions { get; }

    public IReadOnlyList<VideoSettingsOption> Bt709InterpretationOptions { get; }

    /// <summary>What the picture may be dithered towards, beyond following the output surface.</summary>
    public IReadOnlyList<VideoSettingsOption> PanelBitDepthOptions { get; }

    public IReadOnlyList<VideoSettingsOption> HardwareDecoderOptions { get; }

    public IReadOnlyList<VideoSettingsOption> PhotoDecodePreferenceOptions { get; }

    public bool SupportsPhotoDecodePreference => PlatformServices.SupportsPhotoDecodePreference;

    /// <summary>Rows for the arguments the selected decoder's profile offers, empty when it has none.</summary>
    public ObservableCollection<HardwareOptionRowViewModel> HardwareOptions { get; } = [];

    public ObservableCollection<HardwareFlagRowViewModel> HardwareFlags { get; } = [];

    public ObservableCollection<VideoShaderPackageRowViewModel> ShaderPackages { get; } = [];

    public bool HasHardwareDecoderSettings => HardwareDecoderOptions.Count > 1;

    public bool HasHardwareDevices => HardwareDeviceOptions.Count > 1;

    public bool HasShaderPackages => ShaderPackages.Count > 0;

    public bool ShowShaderPackages => CanImportShaderPackages || HasShaderPackages;

    public bool HasShaderPackageStatus => ShaderPackageStatus.Length > 0;

    public bool CanImportShaderPackages => shaderPackages is not null;

    public bool SupportsAdvancedColorOutput => PlatformServices.SupportsAdvancedColorOutput;

    public string ScalingHeader => Localized("VideoSettings_UpscalingHeader", "Upscaling");

    public string DebandingHeader => Localized("VideoSettings_DebandingHeader", "Debanding");

    public string DownscalingHeader => Localized("VideoSettings_DownscalingHeader", "Downscaling");

    public string PreferHardwareProcessingHeader => Localized(
        "VideoSettings_PreferHardwareProcessingHeader",
        "Prefer hardware processing");

    public string CustomFiltersHeader =>
        Localized("VideoSettings_CustomFiltersHeader", "Custom scaling filters");

    public string CustomFiltersDescription => Localized(
        "VideoSettings_CustomFiltersDescription",
        "Import and prioritize file-based pre-scaling filters. Only the first eligible filter runs.");

    public string FindShaderPackagesText => Localized(
        "VideoSettings_CustomFiltersFindOnline",
        "Find shader packages online");

    public string ImportFilterText =>
        Localized("VideoSettings_CustomFiltersImport", "Import shader package");

    public string NoCustomFiltersText => Localized(
        "VideoSettings_CustomFiltersEmpty",
        "No custom scaling filters are installed.");

    public string BufferHeader => Localized("VideoSettings_BufferHeader");

    public string HdrPassthroughHeader => Localized("VideoSettings_HdrPassthroughHeader");

    public string HdrPassthroughDescription => Localized("VideoSettings_HdrPassthroughDescription");

    public string Bt709InterpretationHeader => Localized("VideoSettings_Bt709InterpretationHeader");

    public string Bt709InterpretationDescription => Localized("VideoSettings_Bt709InterpretationDescription");

    public string PanelBitDepthHeader => Localized("VideoSettings_PanelBitDepthHeader");

    public string PanelBitDepthDescription => Localized("VideoSettings_PanelBitDepthDescription");

    public string HardwareEnabledHeader => Localized("VideoSettings_HardwareEnabledHeader");

    public string HardwareDecoderHeader => Localized("VideoSettings_HardwareDecoderHeader");

    public string PhotoDecodePreferenceHeader => Localized(
        "VideoSettings_PhotoDecodePreferenceHeader", "Photo decode preference");

    public string HardwareDecoderDescription => RequiresFrameCopyBack(SelectedHardwareDecoder.Value)
        ? Localized("VideoSettings_HardwareDecoderCopyBackDescription")
        : string.Empty;

    public string HardwareDeviceHeader => Localized("VideoSettings_HardwareDeviceHeader");

    public string HardwareDeviceDescription => Localized("VideoSettings_HardwareDeviceDescription");

    partial void OnSelectedScalingChanged(VideoSettingsOption value)
    {
        if (isLoaded && Enum.TryParse(value.Value, out VideoScalingQuality quality))
            settingsStore.PreferredVideoScaling = quality;
    }

    partial void OnSelectedDownscalingChanged(VideoSettingsOption value)
    {
        if (isLoaded && Enum.TryParse(value.Value, out VideoScalingQuality quality))
            settingsStore.PreferredVideoDownscaling = quality;
    }

    partial void OnSelectedDebandingChanged(VideoSettingsOption value)
    {
        if (isLoaded && Enum.TryParse(value.Value, out VideoDebandingQuality quality))
            settingsStore.VideoDebanding = quality;
    }

    partial void OnPreferHardwareProcessingChanged(bool value)
    {
        if (isLoaded)
            settingsStore.PreferHardwareVideoProcessing = value;
    }

    partial void OnSelectedBufferChanged(VideoSettingsOption value)
    {
        if (isLoaded && int.TryParse(value.Value, CultureInfo.InvariantCulture, out int milliseconds))
            settingsStore.VideoBufferDuration = TimeSpan.FromMilliseconds(milliseconds);
    }

    partial void OnEnableHdrPassthroughChanged(bool value)
    {
        if (isLoaded)
            settingsStore.EnableHdrPassthrough = value;

        UpdateAutomaticPanelBitDepth();
    }

    partial void OnSelectedBt709InterpretationChanged(VideoSettingsOption value)
    {
        if (!isLoaded || !Enum.TryParse(value.Value, out Bt709Interpretation interpretation))
            return;

        settingsStore.VideoBt709Interpretation = interpretation;
        VideoSurfaceTransfers.Bt709Preference = interpretation;
    }

    partial void OnSelectedPanelBitDepthChanged(VideoSettingsOption value)
    {
        if (isLoaded && int.TryParse(value.Value, CultureInfo.InvariantCulture, out int depth))
            settingsStore.DisplayPanelBitDepth = depth;
    }

    /// <summary>
    /// Names what leaving the choice alone currently amounts to. Only a label: what the renderer
    /// does without a chosen depth follows the surface, not this text.
    /// </summary>
    private void UpdateAutomaticPanelBitDepth() =>
        automaticPanelBitDepth.TitleArgument =
            BitDepthName(SupportsAdvancedColorOutput && EnableHdrPassthrough ? 10 : 8);

    partial void OnEnableHardwareDecodingChanged(bool value)
    {
        if (isLoaded)
            settingsStore.EnableHardwareDecoding = value;
    }

    partial void OnSelectedHardwareDecoderChanged(VideoSettingsOption value)
    {
        if (!isLoaded)
            return;

        settingsStore.PreferredHardwareDecoder = value.Value;
        LoadHardwareProfile();
    }

    partial void OnSelectedPhotoDecodePreferenceChanged(VideoSettingsOption value)
    {
        if (!isLoaded || !SupportsPhotoDecodePreference || !bool.TryParse(value.Value, out bool preferPerformance))
            return;

        settingsStore.PreferPhotoDecodePerformance = preferPerformance;
        PhotoDecodeResources.PreferThroughput = preferPerformance;
    }

    partial void OnSelectedHardwareDeviceChanged(VideoSettingsOption? value)
    {
        if (!isLoaded || value is null)
            return;

        HardwareDecoderConfiguration configuration = GetConfiguration();
        configuration.Device = value.Value;
        Persist(configuration);
    }

    public async Task ImportShaderPackageAsync(IStorageProvider? storageProvider)
    {
        if (shaderPackages is null || storageProvider is null || !storageProvider.CanPickFolder)
        {
            ShaderPackageStatus = Localized(
                "VideoSettings_CustomFiltersImportUnavailable",
                "Shader package folder import is unavailable on this device.");
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await storageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = Localized(
                    "VideoSettings_CustomFiltersImportPickerTitle",
                    "Choose a shader package folder"),
                AllowMultiple = false,
            });
        IStorageFolder? folder = folders.FirstOrDefault();
        if (folder is null)
            return;

        string? path = folder.TryGetLocalPath();
        try
        {
            VideoShaderPackage package = string.IsNullOrWhiteSpace(path)
                ? await shaderPackages.ImportAsync(folder)
                : await shaderPackages.ImportAsync(path);
            ShaderPackageStatus = string.Format(
                CultureInfo.CurrentCulture,
                Localized("VideoSettings_CustomFiltersImportedFormat", "Imported {0}."),
                package.Manifest.Name);
        }
        catch (Exception exception)
        {
            ShaderPackageStatus = AppErrorReporter.LogAndGetMessage(
                "Video.ShaderPackage",
                "Could not import the selected shader package.",
                exception);
        }
    }

    internal bool MoveShaderPackage(string id, int newIndex) =>
        shaderPackages?.Move(id, newIndex) == true;

    internal async Task RemoveShaderPackageAsync(VideoShaderPackage package)
    {
        if (dialogService is not null)
        {
            bool confirmed = await dialogService.ConfirmAsync(
                Localized("VideoSettings_CustomFiltersRemoveTitle", "Remove scaling filter"),
                string.Format(
                    CultureInfo.CurrentCulture,
                    Localized("VideoSettings_CustomFiltersRemoveMessageFormat", "Remove '{0}'?"),
                    package.Manifest.Name),
                Strings.Resources.Common_Delete);
            if (!confirmed)
                return;
        }

        shaderPackages?.Remove(package.Id);
    }

    internal void SetShaderPackageEnabled(string id, bool enabled) =>
        MutateShaderPackage(() => shaderPackages?.SetEnabled(id, enabled));

    internal void SetShaderPackageMaximumResolution(string id, int value) =>
        MutateShaderPackage(() => shaderPackages?.SetMaximumSourceResolution(id, value));

    private void MutateShaderPackage(Action mutation)
    {
        suppressShaderPackageRebuild = true;
        try
        {
            mutation();
        }
        finally
        {
            suppressShaderPackageRebuild = false;
        }
    }

    private void OnShaderPackagesChanged(object? sender, EventArgs eventArgs)
    {
        if (suppressShaderPackageRebuild)
            return;
        if (Dispatcher.UIThread.CheckAccess())
            RebuildShaderPackages();
        else
            Dispatcher.UIThread.Post(RebuildShaderPackages);
    }

    private void RebuildShaderPackages()
    {
        ShaderPackages.Clear();
        IReadOnlyList<VideoShaderPackage> installed = shaderPackages?.Packages ?? [];
        for (int index = 0; index < installed.Count; index++)
        {
            ShaderPackages.Add(new VideoShaderPackageRowViewModel(
                this,
                installed[index],
                index,
                installed.Count));
        }
        OnPropertyChanged(nameof(HasShaderPackages));
        OnPropertyChanged(nameof(ShowShaderPackages));
    }

    public void DisposeShaderPackages()
    {
        if (shaderPackages is not null)
            shaderPackages.Changed -= OnShaderPackagesChanged;
        ShaderPackages.Clear();
    }

    /// <summary>
    /// Rebuilds the device, option and flag rows from the profile registered for the chosen device
    /// type. Automatic selection configures nothing, because which type it lands on is only known
    /// once a video opens.
    /// </summary>
    private void LoadHardwareProfile()
    {
        bool loaded = isLoaded;
        isLoaded = false;

        HardwareOptions.Clear();
        HardwareFlags.Clear();

        string deviceType = SelectedHardwareDecoder.Value;
        IHardwareDecoderProfile? profile = HardwareDecoderProfiles.Find(deviceType);
        if (profile is null)
        {
            HardwareDeviceOptions = [];
            SelectedHardwareDevice = null;
            isLoaded = loaded;
            return;
        }

        settingsStore.HardwareDecoderConfigurations.TryGetValue(deviceType, out var configuration);

        List<VideoSettingsOption> devices =
        [
            new(string.Empty, string.Empty, "VideoSettings_HardwareDeviceAuto"),
        ];
        foreach (HardwareDeviceInfo device in profile.GetDevices())
            devices.Add(new VideoSettingsOption(device.Id, device.Name));

        HardwareDeviceOptions = devices;
        SelectedHardwareDevice = FindOption(devices, configuration?.Device ?? string.Empty);

        foreach (HardwareOptionDescriptor descriptor in profile.GetOptions())
        {
            string target = HardwareDecoderConfiguration.Target(descriptor.Api, descriptor.Parameter);
            string value = configuration?.Options.TryGetValue(target, out var values) == true &&
                values.TryGetValue(descriptor.Key, out var stored)
                ? stored
                : descriptor.DefaultValue;
            HardwareOptions.Add(new HardwareOptionRowViewModel(descriptor, value, OnHardwareOptionChanged));
        }

        foreach (HardwareFlagDescriptor descriptor in profile.GetFlags())
        {
            string target = HardwareDecoderConfiguration.Target(descriptor.Api, descriptor.Parameter);
            bool enabled = configuration?.Flags.TryGetValue(target, out var keys) == true
                ? keys.Contains(descriptor.Key, StringComparer.Ordinal)
                : descriptor.DefaultOn;
            HardwareFlags.Add(new HardwareFlagRowViewModel(descriptor, enabled, OnHardwareFlagChanged));
        }

        isLoaded = loaded;
    }

    private void OnHardwareOptionChanged(HardwareOptionRowViewModel row)
    {
        HardwareDecoderConfiguration configuration = GetConfiguration();
        string target = HardwareDecoderConfiguration.Target(row.Descriptor.Api, row.Descriptor.Parameter);
        if (!configuration.Options.TryGetValue(target, out var values))
            configuration.Options[target] = values = new Dictionary<string, string>(StringComparer.Ordinal);

        // An empty value is the absence of the option, not an option set to nothing.
        if (row.Value.Length == 0)
            values.Remove(row.Descriptor.Key);
        else
            values[row.Descriptor.Key] = row.Value;

        Persist(configuration);
    }

    private void OnHardwareFlagChanged(HardwareFlagRowViewModel row)
    {
        HardwareDecoderConfiguration configuration = GetConfiguration();
        string target = HardwareDecoderConfiguration.Target(row.Descriptor.Api, row.Descriptor.Parameter);
        if (!configuration.Flags.TryGetValue(target, out var keys))
            configuration.Flags[target] = keys = [];

        keys.Remove(row.Descriptor.Key);
        if (row.IsEnabled)
            keys.Add(row.Descriptor.Key);

        Persist(configuration);
    }

    private HardwareDecoderConfiguration GetConfiguration() =>
        settingsStore.HardwareDecoderConfigurations.TryGetValue(SelectedHardwareDecoder.Value, out var configuration)
            ? configuration
            : new HardwareDecoderConfiguration();

    private void Persist(HardwareDecoderConfiguration configuration) =>
        settingsStore.SetHardwareDecoderConfiguration(SelectedHardwareDecoder.Value, configuration);

    private static VideoSettingsOption FindOption(IReadOnlyList<VideoSettingsOption> options, string value) =>
        options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase))
        ?? options[0];

    private static bool RequiresFrameCopyBack(string type) => type is "vulkan" or "mediacodec";

    private static string BitDepthName(int depth) =>
        string.Format(CultureInfo.CurrentCulture, Localized("VideoSettings_BitDepthFormat", "{0}-bit"), depth);

    private static string HardwareDisplayName(string type) => type switch
    {
        "videotoolbox" => "VideoToolbox",
        "d3d11va" => "D3D11VA",
        "vulkan" => "Vulkan",
        "vaapi" => "VA-API",
        "mediacodec" => "MediaCodec",
        "webcodecs" => "WebCodecs",
        _ => type,
    };
}

public sealed partial class VideoShaderPackageRowViewModel : ViewModelBase
{
    private readonly VideoSettingsSectionViewModel owner;
    private readonly VideoShaderPackage package;
    private bool loaded;

    [ObservableProperty]
    private bool isEnabled;

    [ObservableProperty]
    private decimal maximumSourceResolution;

    public VideoShaderPackageRowViewModel(
        VideoSettingsSectionViewModel owner,
        VideoShaderPackage package,
        int index,
        int count)
    {
        this.owner = owner;
        this.package = package;
        Index = index;
        Count = count;
        isEnabled = package.IsEnabled;
        maximumSourceResolution = package.MaximumSourceResolution;
        MoveUpCommand = new RelayCommand(
            () => owner.MoveShaderPackage(package.Id, Index - 1),
            () => Index > 0);
        MoveDownCommand = new RelayCommand(
            () => owner.MoveShaderPackage(package.Id, Index + 1),
            () => Index + 1 < Count);
        RemoveCommand = new AsyncRelayCommand(() => owner.RemoveShaderPackageAsync(package));
        loaded = true;
    }

    public int Index { get; }

    public int Count { get; }

    public string DisplayName => package.Manifest.Name;

    public string Description => package.Manifest.Description;

    public string Details => string.Format(
        CultureInfo.CurrentCulture,
        Localized("VideoSettings_CustomFiltersDetailsFormat", "{0} | {1} | {2} | {3} passes"),
        package.Manifest.Version,
        package.Manifest.Author,
        package.Manifest.License,
        package.PassCount);

    public string PriorityText => string.Format(
        CultureInfo.CurrentCulture,
        Localized("VideoSettings_CustomFiltersPriorityFormat", "Priority {0}"),
        Index + 1);

    public string MoveUpText => Localized("VideoSettings_CustomFiltersMoveUp", "Move up");

    public string MoveDownText => Localized("VideoSettings_CustomFiltersMoveDown", "Move down");

    public string DeleteText => Localized("Common_Delete", "Delete");

    public string EnabledText => Localized("VideoSettings_CustomFiltersEnabled", "Use automatically");

    public string MaximumResolutionText => Localized(
        "VideoSettings_CustomFiltersMaximumResolution",
        "Maximum supported resolution");

    public string MaximumResolutionDescription => Localized(
        "VideoSettings_CustomFiltersMaximumResolutionDescription",
        "Largest source short edge in pixels for automatic selection. Use 0 for unlimited.");

    public int MaximumResolutionLimit => VideoShaderPackageCatalog.MaximumSourceResolution;

    public IRelayCommand MoveUpCommand { get; }

    public IRelayCommand MoveDownCommand { get; }

    public IAsyncRelayCommand RemoveCommand { get; }

    partial void OnIsEnabledChanged(bool value)
    {
        if (loaded)
            owner.SetShaderPackageEnabled(package.Id, value);
    }

    partial void OnMaximumSourceResolutionChanged(decimal value)
    {
        if (loaded)
        {
            owner.SetShaderPackageMaximumResolution(
                package.Id,
                (int)Math.Round(value, MidpointRounding.AwayFromZero));
        }
    }
}