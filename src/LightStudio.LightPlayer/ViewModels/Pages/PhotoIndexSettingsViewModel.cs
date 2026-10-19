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
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed partial class PhotoIndexSettingsViewModel : ObservableObject, IDisposable, IInferenceModelSettingsHost
{
    private readonly IPhotoIndexService service;
    private InferenceModelCatalog catalog = new([]);
    private IReadOnlyList<InferenceInstalledModel> installedModels;
    private InferenceInstalledModel? selectedModel;
    private bool disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    [NotifyPropertyChangedFor(nameof(AllowRemoteSearch))]
    [NotifyPropertyChangedFor(nameof(SelectedDevice))]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyPropertyChangedFor(nameof(IsDeviceFallback))]
    [NotifyPropertyChangedFor(nameof(DeviceFallbackDescription))]
    [NotifyPropertyChangedFor(nameof(CanConfigure))]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    private PhotoIndexStatus status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfigure))]
    [NotifyPropertyChangedFor(nameof(CanSelectModel))]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private bool isChanging;

    public PhotoIndexSettingsViewModel(IPhotoIndexService service)
    {
        this.service = service;
        status = service.Status;
        installedModels = status.Models.Where(model => model.IsCompatible).ToArray();
        selectedModel = installedModels.FirstOrDefault(model => model.Id == status.ModelId);
        RefreshModels();
        service.Changed += OnServiceChanged;
        LocalizedStrings.Instance.PropertyChanged += OnLanguageChanged;
        _ = AppErrorReporter.GuardAsync(() => service.InitializeAsync());
    }

    public bool IsEnabled
    {
        get => Status.Options.Enabled;
        set
        {
            if (value != IsEnabled && !IsChanging)
                _ = ChangeAsync(() => service.ConfigureAsync(Status.Options with { Enabled = value }));
        }
    }

    public bool AllowRemoteSearch
    {
        get => Status.Options.AllowRemoteSearch;
        set
        {
            if (value != AllowRemoteSearch && !IsChanging)
                _ = ChangeAsync(() => service.ConfigureAsync(Status.Options with { AllowRemoteSearch = value }));
        }
    }

    public InferenceDevice? SelectedDevice
    {
        get => Status.Devices.FirstOrDefault(device => device.Id == Status.Options.DeviceId) ?? Status.Devices.FirstOrDefault();
        set
        {
            if (value is not null && value.Id != Status.Options.DeviceId && !IsChanging)
                _ = ChangeAsync(() => service.ConfigureAsync(Status.Options with { DeviceId = value.Id }));
        }
    }

    public ObservableCollection<InferenceModelSettingsViewModel> Models { get; } = [];
    public IReadOnlyList<InferenceInstalledModel> InstalledModels => installedModels;
    public string? ProviderDescription => catalog.Description;
    public bool CanImport => catalog.CanImport && !IsChanging;

    internal bool CanDownload(string modelId) => catalog.Models.Any(model => model.Id == modelId);

    IReadOnlyList<InferenceInstalledModel> IInferenceModelSettingsHost.AllInstalledModels => Status.Models;
    IReadOnlyList<InferenceModelDownload> IInferenceModelSettingsHost.Downloads => Status.Downloads;
    bool IInferenceModelSettingsHost.CanDownload(string modelId) => CanDownload(modelId);
    Task IInferenceModelSettingsHost.DownloadModelAsync(string modelId) => service.DownloadModelAsync(modelId);
    void IInferenceModelSettingsHost.PauseModelDownload(string modelId) => service.PauseModelDownload(modelId);
    Task IInferenceModelSettingsHost.CancelModelDownloadAsync(string modelId) => service.CancelModelDownloadAsync(modelId);
    Task IInferenceModelSettingsHost.DeleteModelsAsync(IReadOnlyList<InferenceInstalledModel> models) => DeleteModelsAsync(models);

    public InferenceInstalledModel? SelectedModel
    {
        get => selectedModel;
        set
        {
            if (value is not null && value.Id != Status.ModelId && !IsChanging)
                _ = ChangeAsync(() => service.SelectModelAsync(value.Id));
        }
    }

    public bool CanConfigure => Status.CanEnable && !IsChanging;
    public bool CanSelectModel => InstalledModels.Count > 0 && !IsChanging;
    public bool CanScan => Status.IsAvailable && !Status.IsIndexing && !IsChanging;
    public bool IsDeviceFallback => Status.IsAvailable && Status.Options.DeviceId != Status.ActiveDeviceId;
    public string DeviceFallbackDescription => string.Format(CultureInfo.CurrentCulture, Resources.Inference_DeviceFallback,
        Status.Devices.FirstOrDefault(device => device.Id == Status.ActiveDeviceId)?.Name ?? Status.ActiveDeviceId);
    public string ProgressText => string.Format(CultureInfo.CurrentCulture, Resources.PhotoIndex_ProgressFormat,
        Status.IndexedCount, Status.ProcessedCount, Status.DiscoveredCount, Status.FailedCount);

    private async Task ChangeAsync(Func<Task> change)
    {
        if (disposed || IsChanging)
            return;
        IsChanging = true;
        try
        {
            await AppErrorReporter.GuardAsync(change);
        }
        finally
        {
            if (!disposed)
            {
                Status = service.Status;
                OnPropertyChanged(nameof(IsEnabled));
                OnPropertyChanged(nameof(AllowRemoteSearch));
                OnPropertyChanged(nameof(SelectedDevice));
                OnPropertyChanged(nameof(SelectedModel));
                IsChanging = false;
            }
        }
    }

    internal Task DeleteModelsAsync(IReadOnlyList<InferenceInstalledModel> models) => ChangeAsync(async () =>
    {
        foreach (var model in models)
            await service.DeleteModelAsync(model.Id);
    });

    partial void OnStatusChanged(PhotoIndexStatus? oldValue, PhotoIndexStatus newValue)
    {
        if (oldValue is null || !ReferenceEquals(oldValue.Models, newValue.Models) || oldValue.Options.DeviceId != newValue.Options.DeviceId ||
            oldValue.RuntimeAvailable != newValue.RuntimeAvailable ||
            !oldValue.Downloads.Where(download => download.State != InferenceModelDownloadState.NotStarted).Select(download => download.ModelId)
                .SequenceEqual(newValue.Downloads.Where(download => download.State != InferenceModelDownloadState.NotStarted).Select(download => download.ModelId)))
        {
            RefreshModels();
        }
        var compatibleModels = newValue.Models.Where(model => model.IsCompatible).ToArray();
        if (!installedModels.SequenceEqual(compatibleModels))
        {
            installedModels = compatibleModels;
            OnPropertyChanged(nameof(InstalledModels));
            OnPropertyChanged(nameof(CanSelectModel));
        }
        selectedModel = installedModels.FirstOrDefault(model => model.Id == newValue.ModelId);
        OnPropertyChanged(nameof(SelectedModel));
        foreach (var model in Models)
            model.Refresh();
    }

    partial void OnIsChangingChanged(bool value)
    {
        foreach (var model in Models)
            model.Refresh();
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
        foreach (var transfer in Status.Downloads.Where(download => download.State != InferenceModelDownloadState.NotStarted &&
            !Models.Any(row => row.CatalogId == download.ModelId)))
            Models.Add(new InferenceModelSettingsViewModel(this,
                new InferenceModelDefinition(transfer.ModelId, transfer.ModelId, string.Empty)));
        foreach (var row in Models.Where(row => row.CatalogId is { } id && !CanDownload(id) &&
            !Status.Models.Any(model => model.CatalogId == id) &&
            !Status.Downloads.Any(download => download.ModelId == id && download.State != InferenceModelDownloadState.NotStarted)).ToArray())
            Models.Remove(row);
        var imported = Status.Models.Where(model => !Models.Any(row => row.CatalogId is not null && row.CatalogId == model.CatalogId)).ToArray();
        foreach (var row in Models.Where(row => row.ImportedId is not null && !imported.Any(model => model.Id == row.ImportedId)).ToArray())
            Models.Remove(row);
        foreach (var model in imported.Where(model => !Models.Any(row => row.ImportedId == model.Id)))
            Models.Add(new InferenceModelSettingsViewModel(this, model));
        foreach (var model in Models)
            model.Refresh();
        OnPropertyChanged(nameof(ProviderDescription));
        OnPropertyChanged(nameof(CanImport));
    }

    [RelayCommand]
    private Task ScanAsync() => AppErrorReporter.GuardAsync(() => service.IndexAsync());

    [RelayCommand]
    private void Cancel() => service.CancelIndexing();

    public async Task ImportAsync(IStorageProvider? storageProvider)
    {
        if (storageProvider is not { CanPickFolder: true } || !CanImport || disposed)
            return;
        var selected = new List<IStorageItem>();
        var files = new List<InferenceModelFile>();
        IsChanging = true;
        try
        {
            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Resources.Inference_Model,
                AllowMultiple = false,
            });
            selected.AddRange(folders);
            if (folders.Count == 0 || disposed)
                return;
            await ReadFolderAsync(folders[0], includeChildren: true);
            if (!disposed)
                await service.ImportModelAsync(files);
        }
        finally
        {
            if (!disposed)
            {
                Status = service.Status;
                IsChanging = false;
            }
            foreach (var file in selected)
                file.Dispose();
        }

        async Task ReadFolderAsync(IStorageFolder folder, bool includeChildren)
        {
            await foreach (var item in folder.GetItemsAsync())
            {
                selected.Add(item);
                if (item is IStorageFile file)
                    files.Add(new InferenceModelFile(file.Name, _ => file.OpenReadAsync()));
                else if (includeChildren && item is IStorageFolder child)
                    await ReadFolderAsync(child, includeChildren: false);
            }
        }
    }

    private void OnServiceChanged(object? sender, EventArgs eventArgs) => Dispatcher.UIThread.Post(() =>
    {
        if (!disposed)
            Status = service.Status;
    });

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(DeviceFallbackDescription));
        RefreshModels();
    }

    public void Dispose()
    {
        disposed = true;
        service.Changed -= OnServiceChanged;
        LocalizedStrings.Instance.PropertyChanged -= OnLanguageChanged;
    }
}