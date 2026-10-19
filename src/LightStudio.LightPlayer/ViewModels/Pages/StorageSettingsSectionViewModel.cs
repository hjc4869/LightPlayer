using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Lists the caches this device keeps for the library and lets the user empty them. Entries are
/// built only for the caches that are actually available here, so a head whose lyrics or media
/// blocks live on a server simply shows fewer rows.
/// </summary>
public sealed class StorageSettingsSectionViewModel : ViewModelBase
{
    public StorageSettingsSectionViewModel(
        IDialogService? dialogService,
        IStorageCache? lyrics,
        IStorageCache? mediaBlocks,
        IStorageCache? images = null,
        IStorageCache? videoPreviews = null,
        IStorageCache? photoPreviews = null)
    {
        Add(lyrics,
            nameof(Strings.Resources.StorageSettings_LyricsHeader),
            nameof(Strings.Resources.StorageSettings_LyricsDescription));
        Add(videoPreviews,
            nameof(Strings.Resources.StorageSettings_VideoPreviewsHeader),
            nameof(Strings.Resources.StorageSettings_VideoPreviewsDescription));
        Add(photoPreviews,
            nameof(Strings.Resources.StorageSettings_PhotoPreviewsHeader),
            nameof(Strings.Resources.StorageSettings_PhotoPreviewsDescription));
        Add(mediaBlocks,
            nameof(Strings.Resources.StorageSettings_MediaBlocksHeader),
            nameof(Strings.Resources.StorageSettings_MediaBlocksDescription));
        Add(images,
            nameof(Strings.Resources.StorageSettings_ImagesHeader),
            nameof(Strings.Resources.StorageSettings_ImagesDescription));

        _ = RefreshAsync();

        void Add(IStorageCache? cache, string headerKey, string descriptionKey)
        {
            if (cache is not null)
            {
                Items.Add(new StorageCacheItemViewModel(cache, dialogService, headerKey, descriptionKey));
            }
        }
    }

    public ObservableCollection<StorageCacheItemViewModel> Items { get; } = [];

    /// <summary>Whether any cache is manageable here; the section is hidden when none is.</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>Re-measures every listed cache.</summary>
    public Task RefreshAsync() => Task.WhenAll(Items.Select(item => item.RefreshAsync()));
}

/// <summary>One cache row: its current usage, an optional size budget, and the action that empties it.</summary>
public sealed partial class StorageCacheItemViewModel : ViewModelBase
{
    private const long BytesPerMebibyte = 1024 * 1024;

    private readonly IStorageCache cache;
    private readonly IStorageCacheLimit? limit;
    private readonly IDialogService? dialogService;
    private readonly string headerKey;
    private readonly string? descriptionKey;

    private readonly bool isLoaded;

    /// <summary>Null while the cache is still being measured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsageText))]
    private StorageCacheUsage? usage;

    [ObservableProperty]
    private bool isClearing;

    private decimal limitMebibytes;
    private bool isApplyingLimit;

    public StorageCacheItemViewModel(
        IStorageCache cache,
        IDialogService? dialogService,
        string headerKey,
        string? descriptionKey = null)
    {
        this.cache = cache;
        this.dialogService = dialogService;
        this.headerKey = headerKey;
        this.descriptionKey = descriptionKey;
        limit = cache as IStorageCacheLimit;
        limitMebibytes = limit is null ? 0 : ToMebibytes(limit.MaxBytes);
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => CanClear);
        isLoaded = true;
    }

    public string Header => Localized(headerKey);

    public string? Description => descriptionKey is null ? null : Localized(descriptionKey);

    /// <summary>Whether this cache has a user-adjustable size budget.</summary>
    public bool HasLimit => limit is not null;

    /// <summary>Budget in mebibytes, applied to the cache as soon as the user commits an edit.</summary>
    public decimal LimitMebibytes
    {
        get => limitMebibytes;
        set
        {
            if (SetProperty(ref limitMebibytes, value) && isLoaded && !isApplyingLimit && limit is not null)
            {
                _ = ApplyLimitAsync(value);
            }
        }
    }

    public decimal MinimumLimitMebibytes => limit is null ? 0 : ToMebibytes(limit.MinimumBytes);

    public decimal MaximumLimitMebibytes => limit is null ? 0 : ToMebibytes(limit.MaximumBytes);

    public string UsageText => Usage switch
    {
        null => Strings.Resources.StorageSettings_Measuring,
        { ItemCount: 0 } => Strings.Resources.StorageSettings_Empty,
        { } measured => string.Format(
            CultureInfo.CurrentCulture,
            Strings.Resources.StorageSettings_UsageFormat,
            measured.ItemCount,
            FormatSize(measured.TotalBytes)),
    };

    public IAsyncRelayCommand ClearCommand { get; }

    /// <summary>Clearing is destructive, so it stays disabled while no dialog can confirm it.</summary>
    private bool CanClear => !IsClearing && dialogService is not null && Usage is { ItemCount: > 0 };

    public async Task RefreshAsync()
    {
        Usage = null;
        Usage = await cache.MeasureAsync();
    }

    partial void OnUsageChanged(StorageCacheUsage? value) => ClearCommand.NotifyCanExecuteChanged();

    partial void OnIsClearingChanged(bool value) => ClearCommand.NotifyCanExecuteChanged();

    private async Task ApplyLimitAsync(decimal mebibytes)
    {
        await limit!.SetMaxBytesAsync((long)decimal.Truncate(mebibytes) * BytesPerMebibyte);

        // The cache clamps the budget to what it accepts; show what was applied without reapplying it.
        isApplyingLimit = true;
        try
        {
            LimitMebibytes = ToMebibytes(limit.MaxBytes);
        }
        finally
        {
            isApplyingLimit = false;
        }

        await RefreshAsync();
    }

    private static decimal ToMebibytes(long bytes) => bytes / BytesPerMebibyte;

    private async Task ClearAsync()
    {
        if (dialogService is null)
        {
            return;
        }

        var confirmed = await dialogService.ConfirmAsync(
            Strings.Resources.StorageSettings_ClearConfirmTitle,
            string.Format(CultureInfo.CurrentCulture, Strings.Resources.StorageSettings_ClearConfirmMessageFormat, Header),
            Strings.Resources.StorageSettings_Clear);
        if (!confirmed)
        {
            return;
        }

        IsClearing = true;
        try
        {
            await cache.ClearAsync();
            await RefreshAsync();
        }
        finally
        {
            IsClearing = false;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };
}
