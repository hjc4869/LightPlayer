using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightMediaRenderer.Contracts;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// A viewer's tone mapping choice. Picking a profile makes it the default for its media type;
/// adjustments apply to the picture on screen until they are saved.
/// </summary>
public sealed partial class PhotoToneMappingViewModel : ViewModelBase
{
    private readonly IAppSettingsStore settingsStore;
    private readonly ToneMappingSettingsWriter writer;
    private readonly bool forVideo;
    private ToneMappingSettings settings;
    private bool loading;

    [ObservableProperty]
    private IReadOnlyList<VideoSettingsOption> profileChoices = [];

    [ObservableProperty]
    private VideoSettingsOption? selectedProfile;

    /// <summary>The options the picture is drawn with, or null for the built-in tone mapping.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(BeginSaveAsCommand))]
    private VideoSdrToneMappingOptions? options;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmSaveAsCommand))]
    private bool isSaving;

    [ObservableProperty]
    private bool isNaming;

    [ObservableProperty]
    private string newProfileName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNameError))]
    private string nameError = string.Empty;

    public PhotoToneMappingViewModel(IAppSettingsStore settingsStore, bool forVideo = false)
    {
        this.settingsStore = settingsStore;
        writer = new ToneMappingSettingsWriter(settingsStore);
        this.forVideo = forVideo;
        settings = settingsStore.ToneMapping;
        Editor.Edited += edited => Options = edited;
        RevertCommand = new RelayCommand(Revert, () => IsModified);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => IsModified && Options is not null && SavedOptions is not null && !IsSaving);
        BeginSaveAsCommand = new RelayCommand(BeginSaveAs, () => Options is not null);
        ConfirmSaveAsCommand = new AsyncRelayCommand(ConfirmSaveAsAsync, () => !IsSaving);
        CancelSaveAsCommand = new RelayCommand(() => IsNaming = false);
        Reload(settings);
    }

    public ToneMappingEditorViewModel Editor { get; } = new();

    /// <summary>Whether the picture is drawn with adjustments that have not been saved.</summary>
    public bool IsModified => Options != SavedOptions;

    public bool HasNameError => NameError.Length > 0;

    public IRelayCommand RevertCommand { get; }

    public IAsyncRelayCommand SaveCommand { get; }

    public IRelayCommand BeginSaveAsCommand { get; }

    public IAsyncRelayCommand ConfirmSaveAsCommand { get; }

    public IRelayCommand CancelSaveAsCommand { get; }

    private VideoSdrToneMappingOptions? SavedOptions =>
        settings.Find(ToneMappingProfileChoices.ProfileName(SelectedProfile))?.Options;

    private ToneMappingSettings CurrentSettings => IsSaving ? settings : settingsStore.ToneMapping;

    public void Reload(ToneMappingSettings value)
    {
        settings = value;
        Rebuild(forVideo ? settings.VideoProfile : settings.ImageProfile);
        Revert();
    }

    /// <summary>Discards unsaved adjustments.</summary>
    public void Revert()
    {
        IsNaming = false;
        Options = SavedOptions;
    }

    partial void OnOptionsChanged(VideoSdrToneMappingOptions? value) => Editor.Options = value;

    partial void OnSelectedProfileChanged(VideoSettingsOption? value)
    {
        if (loading)
            return;
        Revert();
        _ = PersistAsync(WithSelectedProfile(CurrentSettings, ToneMappingProfileChoices.ProfileName(value)));
    }

    private async Task SaveAsync()
    {
        if (ToneMappingProfileChoices.ProfileName(SelectedProfile) is not { } name || Options is not { } edited)
            return;

        ToneMappingSettings current = CurrentSettings;
        await PersistAsync(current with
        {
            Profiles = current.Profiles
                .Select(profile => ToneMappingSettings.NameComparer.Equals(profile.Name, name)
                    ? profile with { Options = edited }
                    : profile)
                .ToArray(),
        });
        OnPropertyChanged(nameof(IsModified));
        RevertCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void BeginSaveAs()
    {
        NewProfileName = ToneMappingProfileChoices.NewName(settings);
        NameError = string.Empty;
        IsNaming = true;
    }

    private async Task ConfirmSaveAsAsync()
    {
        if (Options is not { } edited)
            return;

        ToneMappingSettings current = CurrentSettings;
        string name = NewProfileName.Trim();
        NameError = ToneMappingProfileChoices.Validate(current, name) ?? string.Empty;
        if (HasNameError)
            return;

        IsNaming = false;
        ToneMappingSettings updated = WithSelectedProfile(current with
        {
            Profiles = [.. current.Profiles, new ToneMappingProfile { Name = name, Options = edited }],
        }, name);
        settings = updated;
        Rebuild(name);
        OnPropertyChanged(nameof(IsModified));
        RevertCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        await PersistAsync(updated);
    }

    private ToneMappingSettings WithSelectedProfile(ToneMappingSettings value, string? name) =>
        forVideo ? value with { VideoProfile = name } : value with { ImageProfile = name };

    private async Task PersistAsync(ToneMappingSettings value)
    {
        settings = value;
        IsSaving = true;
        try
        {
            await AppErrorReporter.GuardAsync(() => writer.WriteAsync(value));
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void Rebuild(string? selectedName)
    {
        loading = true;
        try
        {
            ProfileChoices = ToneMappingProfileChoices.Create(settings, includeBuiltIn: true);
            SelectedProfile = ToneMappingProfileChoices.Find(ProfileChoices, selectedName);
        }
        finally
        {
            loading = false;
        }
    }
}
