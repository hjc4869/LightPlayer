using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed partial class AudioSeparationSettingsViewModel : ObservableObject, IDisposable, IInferenceModelSettingsHost
{
    private readonly IAudioSeparationService service;
    private InferenceModelCatalog catalog = new([]);
    private IReadOnlyList<InferenceDevice> devices;
    private IReadOnlyList<InferenceInstalledModel> installedModels;
    private InferenceInstalledModel? selectedModel;
    private bool disposed;
    private bool applyingStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    [NotifyPropertyChangedFor(nameof(SelectedDevice))]
    [NotifyPropertyChangedFor(nameof(SelectedModel))]
    [NotifyPropertyChangedFor(nameof(CanConfigure))]
    [NotifyPropertyChangedFor(nameof(CanSelectModel))]
    [NotifyPropertyChangedFor(nameof(IsDeviceFallback))]
    [NotifyPropertyChangedFor(nameof(DeviceFallbackDescription))]
    private AudioSeparationStatus status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfigure))]
    [NotifyPropertyChangedFor(nameof(CanSelectModel))]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private bool isChanging;

    public AudioSeparationSettingsViewModel(IAudioSeparationService service)
    {
        this.service = service;
        status = service.Status;
        devices = status.Devices;
        installedModels = status.Models;
        selectedModel = installedModels.FirstOrDefault(model => model.Id == status.ModelId);
        RefreshModels();
        service.Changed += OnServiceChanged;
        LocalizedStrings.Instance.PropertyChanged += OnLanguageChanged;
        _ = AppErrorReporter.GuardAsync(() => service.InitializeAsync());
    }

    public ObservableCollection<InferenceModelSettingsViewModel> Models { get; } = [];
    public IReadOnlyList<InferenceDevice> Devices => devices;
    public IReadOnlyList<InferenceInstalledModel> InstalledModels => installedModels;
    public string? ProviderDescription => catalog.Description;
    public bool CanImport => catalog.CanImport && !IsChanging;
    public bool CanConfigure => (Status.CanEnable || Status.Options.Enabled) && !IsChanging;
    public bool CanSelectModel => InstalledModels.Count > 0 && !IsChanging;
    public bool IsDeviceFallback => Status.IsAvailable && Status.ActiveDeviceId != Status.Options.DeviceId;
    public string DeviceFallbackDescription => string.Format(CultureInfo.CurrentCulture, Resources.Inference_DeviceFallback,
        Status.Devices.FirstOrDefault(device => device.Id == Status.ActiveDeviceId)?.Name ?? Status.ActiveDeviceId);

    public bool IsEnabled
    {
        get => Status.Options.Enabled;
        set
        {
            if (value != IsEnabled)
                _ = ChangeAsync(() => service.ConfigureAsync(Status.Options with { Enabled = value }));
        }
    }

    public InferenceDevice? SelectedDevice
    {
        get => Devices.FirstOrDefault(device => device.Id == Status.Options.DeviceId);
        set
        {
            if (value is not null && value.Id != Status.Options.DeviceId)
                _ = ChangeAsync(() => service.ConfigureAsync(Status.Options with { DeviceId = value.Id }));
        }
    }

    public InferenceInstalledModel? SelectedModel
    {
        get => selectedModel;
        set
        {
            if (value is not null && value.Id != Status.ModelId)
                _ = ChangeAsync(() => service.SelectModelAsync(value.Id));
        }
    }

    IReadOnlyList<InferenceInstalledModel> IInferenceModelSettingsHost.AllInstalledModels => Status.Models;
    IReadOnlyList<InferenceModelDownload> IInferenceModelSettingsHost.Downloads => Status.Downloads;
    bool IInferenceModelSettingsHost.CanDownload(string modelId) => catalog.Models.Any(model => model.Id == modelId);
    Task IInferenceModelSettingsHost.DownloadModelAsync(string modelId) => service.DownloadModelAsync(modelId);
    void IInferenceModelSettingsHost.PauseModelDownload(string modelId) => service.PauseModelDownload(modelId);
    Task IInferenceModelSettingsHost.CancelModelDownloadAsync(string modelId) => service.CancelModelDownloadAsync(modelId);
    Task IInferenceModelSettingsHost.DeleteModelsAsync(IReadOnlyList<InferenceInstalledModel> models) => ChangeAsync(async () =>
    {
        foreach (var model in models)
            await service.DeleteModelAsync(model.Id);
    });

    private async Task ChangeAsync(Func<Task> action)
    {
        if (disposed || IsChanging || applyingStatus)
            return;
        IsChanging = true;
        try
        {
            await AppErrorReporter.GuardAsync(action);
        }
        finally
        {
            if (!disposed)
            {
                Status = service.Status;
                IsChanging = false;
                OnPropertyChanged(nameof(IsEnabled));
                OnPropertyChanged(nameof(SelectedModel));
                OnPropertyChanged(nameof(SelectedDevice));
            }
        }
    }

    public async Task ImportAsync(IStorageProvider? storageProvider)
    {
        if (storageProvider is not { CanOpen: true } || !CanImport || disposed)
            return;
        IsChanging = true;
        IReadOnlyList<IStorageFile> selected = [];
        try
        {
            selected = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Resources.AudioSeparation_Import,
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("ONNX") { Patterns = ["*.onnx"] }],
            });
            if (selected.Count > 0 && !disposed)
                await service.ImportModelAsync(new InferenceModelFile(selected[0].Name, _ => selected[0].OpenReadAsync()));
        }
        finally
        {
            foreach (var file in selected)
                file.Dispose();
            if (!disposed)
            {
                Status = service.Status;
                IsChanging = false;
            }
        }
    }

    private void RefreshModels()
    {
        catalog = service.GetModelCatalog(Resources.Culture ?? CultureInfo.CurrentUICulture);
        foreach (var definition in catalog.Models)
        {
            var row = Models.FirstOrDefault(candidate => candidate.CatalogId == definition.Id);
            if (row is null)
                Models.Add(new InferenceModelSettingsViewModel(this, definition));
            else
                row.UpdateDefinition(definition);
        }
        var imported = Status.Models.Where(model => !catalog.Models.Any(definition => definition.Id == model.CatalogId)).ToArray();
        foreach (var row in Models.Where(row => row.ImportedId is not null && !imported.Any(model => model.Id == row.ImportedId)).ToArray())
            Models.Remove(row);
        foreach (var model in imported.Where(model => !Models.Any(row => row.ImportedId == model.Id)))
            Models.Add(new InferenceModelSettingsViewModel(this, model));
        foreach (var row in Models)
            row.Refresh();
        OnPropertyChanged(nameof(ProviderDescription));
    }

    partial void OnStatusChanged(AudioSeparationStatus? oldValue, AudioSeparationStatus newValue)
    {
        if (!devices.SequenceEqual(newValue.Devices))
        {
            devices = newValue.Devices;
            OnPropertyChanged(nameof(Devices));
        }
        if (oldValue is null || !ReferenceEquals(oldValue.Models, newValue.Models) || oldValue.Options.DeviceId != newValue.Options.DeviceId)
        {
            RefreshModels();
        }
        if (!installedModels.SequenceEqual(newValue.Models))
        {
            installedModels = newValue.Models;
            OnPropertyChanged(nameof(InstalledModels));
        }
        selectedModel = installedModels.FirstOrDefault(model => model.Id == newValue.ModelId);
        foreach (var row in Models)
            row.Refresh();
    }

    partial void OnIsChangingChanged(bool value)
    {
        foreach (var row in Models)
            row.Refresh();
    }

    private void OnServiceChanged(object? sender, EventArgs eventArgs) => Dispatcher.UIThread.Post(() =>
    {
        if (disposed)
            return;
        applyingStatus = true;
        try
        {
            Status = service.Status;
            OnPropertyChanged(nameof(SelectedDevice));
            OnPropertyChanged(nameof(SelectedModel));
        }
        finally
        {
            applyingStatus = false;
        }
    });

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        RefreshModels();
        OnPropertyChanged(nameof(DeviceFallbackDescription));
    }

    public void Dispose()
    {
        disposed = true;
        service.Changed -= OnServiceChanged;
        LocalizedStrings.Instance.PropertyChanged -= OnLanguageChanged;
    }
}