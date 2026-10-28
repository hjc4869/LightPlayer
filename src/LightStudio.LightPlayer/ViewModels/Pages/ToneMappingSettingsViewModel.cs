using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightMediaRenderer.Contracts;
using LightStudio.LightPlayer.Services;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Manages tone mapping profiles, which ones images and videos use by default, and the profile
/// being edited. Every change is saved as it is made.
/// </summary>
public sealed partial class ToneMappingSettingsViewModel : ViewModelBase
{
    private readonly ToneMappingSettingsWriter writer;
    private readonly IDialogService? dialogService;
    private ToneMappingSettings settings;
    private bool loading;

    [ObservableProperty]
    private IReadOnlyList<VideoSettingsOption> defaultChoices = [];

    [ObservableProperty]
    private VideoSettingsOption? selectedImageDefault;

    [ObservableProperty]
    private VideoSettingsOption? selectedVideoDefault;

    [ObservableProperty]
    private IReadOnlyList<VideoSettingsOption> profileChoices = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedProfile))]
    [NotifyPropertyChangedFor(nameof(ChartToneMapping))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    private VideoSettingsOption? selectedProfile;

    [ObservableProperty]
    private string profileName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNameError))]
    private string nameError = string.Empty;

    [ObservableProperty]
    private VideoDisplayToneTarget displayTarget = new(1, VideoOutputColorSpaceExtensions.HdrReferenceWhiteNits);

    public ToneMappingSettingsViewModel(IAppSettingsStore settingsStore, IDialogService? dialogService)
    {
        writer = new ToneMappingSettingsWriter(settingsStore);
        this.dialogService = dialogService;
        settings = settingsStore.ToneMapping;
        Editor.Edited += OnEdited;
        AddProfileCommand = new RelayCommand(AddProfile);
        DeleteProfileCommand = new AsyncRelayCommand(DeleteProfileAsync, () => SelectedProfile is not null);
        Rebuild(settings.Profiles.FirstOrDefault()?.Name);
    }

    public ToneMappingEditorViewModel Editor { get; } = new();

    public bool HasSelectedProfile => SelectedProfile is not null;

    public bool HasProfiles => settings.Profiles.Count > 0;

    public bool HasNameError => NameError.Length > 0;

    /// <summary>The selected profile's options, or null to chart the built-in tone mapping.</summary>
    public VideoSdrToneMappingOptions? ChartToneMapping =>
        settings.Find(ToneMappingProfileChoices.ProfileName(SelectedProfile))?.Options;

    public IRelayCommand AddProfileCommand { get; }

    public IAsyncRelayCommand DeleteProfileCommand { get; }

    partial void OnSelectedImageDefaultChanged(VideoSettingsOption? value)
    {
        if (!loading)
            Save(settings with { ImageProfile = ToneMappingProfileChoices.ProfileName(value) });
    }

    partial void OnSelectedVideoDefaultChanged(VideoSettingsOption? value)
    {
        if (!loading)
            Save(settings with { VideoProfile = ToneMappingProfileChoices.ProfileName(value) });
    }

    partial void OnSelectedProfileChanged(VideoSettingsOption? value)
    {
        if (loading)
            return;
        LoadSelectedProfile();
    }

    partial void OnDisplayTargetChanged(VideoDisplayToneTarget value) => Editor.DisplayTarget = value;

    partial void OnProfileNameChanged(string value)
    {
        if (loading || ToneMappingProfileChoices.ProfileName(SelectedProfile) is not { } current)
            return;

        string name = value.Trim();
        NameError = ToneMappingProfileChoices.Validate(settings, name, current) ?? string.Empty;
        if (NameError.Length > 0 || name == current)
            return;

        Save(settings with
        {
            Profiles = settings.Profiles
                .Select(profile => ToneMappingSettings.NameComparer.Equals(profile.Name, current)
                    ? profile with { Name = name }
                    : profile)
                .ToArray(),
            ImageProfile = ToneMappingSettings.NameComparer.Equals(settings.ImageProfile, current) ? name : settings.ImageProfile,
            VideoProfile = ToneMappingSettings.NameComparer.Equals(settings.VideoProfile, current) ? name : settings.VideoProfile,
        });
        Rebuild(name);
    }

    private void OnEdited(VideoSdrToneMappingOptions options)
    {
        if (ToneMappingProfileChoices.ProfileName(SelectedProfile) is not { } name)
            return;

        Save(settings with
        {
            Profiles = settings.Profiles
                .Select(profile => ToneMappingSettings.NameComparer.Equals(profile.Name, name)
                    ? profile with { Options = options }
                    : profile)
                .ToArray(),
        });
        OnPropertyChanged(nameof(ChartToneMapping));
    }

    private void AddProfile()
    {
        string name = ToneMappingProfileChoices.NewName(settings);
        var options = new VideoSdrToneMappingOptions
        {
            Algorithm = VideoSdrToneMappingAlgorithm.Bt2390,
            Bt2390KneeOffset = 0.5f,
        };
        Save(settings with { Profiles = [.. settings.Profiles, new ToneMappingProfile { Name = name, Options = options }] });
        Rebuild(name);
    }

    private async Task DeleteProfileAsync()
    {
        if (ToneMappingProfileChoices.ProfileName(SelectedProfile) is not { } name)
            return;

        if (dialogService is not null)
        {
            bool confirmed = await dialogService.ConfirmAsync(
                Localized(nameof(Strings.Resources.ToneMapping_DeleteTitle)),
                string.Format(CultureInfo.CurrentCulture,
                    Localized(nameof(Strings.Resources.ToneMapping_DeleteMessageFormat)), name),
                Strings.Resources.Common_Delete);
            if (!confirmed)
                return;
        }

        int index = settings.Profiles.ToList().FindIndex(profile => ToneMappingSettings.NameComparer.Equals(profile.Name, name));
        ToneMappingProfile[] remaining = settings.Profiles
            .Where(profile => !ToneMappingSettings.NameComparer.Equals(profile.Name, name))
            .ToArray();
        Save(settings with
        {
            Profiles = remaining,
            ImageProfile = ToneMappingSettings.NameComparer.Equals(settings.ImageProfile, name) ? null : settings.ImageProfile,
            VideoProfile = ToneMappingSettings.NameComparer.Equals(settings.VideoProfile, name) ? null : settings.VideoProfile,
        });
        Rebuild(remaining.Length == 0 ? null : remaining[Math.Clamp(index, 0, remaining.Length - 1)].Name);
    }

    private void Save(ToneMappingSettings value)
    {
        settings = value;
        _ = AppErrorReporter.GuardAsync(() => writer.WriteAsync(value));
    }

    /// <summary>Recreates the choice lists after profiles were added, removed or renamed.</summary>
    private void Rebuild(string? selectedName)
    {
        loading = true;
        try
        {
            DefaultChoices = ToneMappingProfileChoices.Create(settings, includeBuiltIn: true);
            ProfileChoices = ToneMappingProfileChoices.Create(settings, includeBuiltIn: false);
            SelectedImageDefault = ToneMappingProfileChoices.Find(DefaultChoices, settings.GetImageOptions() is null ? null : settings.ImageProfile);
            SelectedVideoDefault = ToneMappingProfileChoices.Find(DefaultChoices, settings.GetVideoOptions() is null ? null : settings.VideoProfile);
            SelectedProfile = ToneMappingProfileChoices.Find(ProfileChoices, selectedName);
        }
        finally
        {
            loading = false;
        }

        OnPropertyChanged(nameof(HasProfiles));
        LoadSelectedProfile();
    }

    private void LoadSelectedProfile()
    {
        loading = true;
        try
        {
            ToneMappingProfile? profile = settings.Find(ToneMappingProfileChoices.ProfileName(SelectedProfile));
            ProfileName = profile?.Name ?? string.Empty;
            NameError = string.Empty;
            Editor.Options = profile?.Options;
        }
        finally
        {
            loading = false;
        }
    }
}
