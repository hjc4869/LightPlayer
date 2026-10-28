using LightMediaRenderer.Avalonia.Audio;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public partial class SettingsPageViewModel : PageViewModelBase, IInPageBackHandler, IInPageDetailState, IDisposable
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanHandleBack))]
    [NotifyPropertyChangedFor(nameof(DetailKey))]
    [NotifyPropertyChangedFor(nameof(ListLayoutDetail))]
    [NotifyPropertyChangedFor(nameof(SidebarLayoutDetail))]
    private SettingsNavigationItemViewModel? selectedSection;

    // Set by the adaptive view: true in the narrow single-pane layout, false in the wide
    // two-pane layout.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanHandleBack))]
    private bool isCompact;

    /// <summary>
    /// Whether an Apple-style sidebar beside the page lists the sections, leaving the page to show only
    /// the selected one. Set by the host: the shell's Apple layout or the macOS settings window.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanHandleBack))]
    [NotifyPropertyChangedFor(nameof(ListLayoutDetail))]
    [NotifyPropertyChangedFor(nameof(SidebarLayoutDetail))]
    private bool isSidebarLayout;

    public SettingsPageViewModel(
        IAppSettingsStore settingsStore,
        IThemeService themeService,
        IDialogService? dialogService,
        IAsyncRelayCommand scanLibraryCommand,
        ILyricSourceService lyricSourceService,
        Action<int>? sampleRateChanged = null,
        Action<bool>? alwaysResampleChanged = null,
        ISystemSampleRateProvider? systemSampleRateProvider = null,
        IAudioOutputSettings? audioOutputSettings = null,
        Func<Task>? clearPlaybackHistory = null,
        ILyricsService? lyricsService = null)
    {
        TitleKey = nameof(Strings.Resources.Nav_Settings);

        // Only the caches this head actually owns are listed; a service that keeps its cache on a
        // server does not implement IStorageCache, so its row is left out.
        var storage = new StorageSettingsSectionViewModel(
            dialogService,
            lyricsService as IStorageCache,
            PlatformServices.MediaBlockCache,
            PlatformServices.ImageCache,
            PlatformServices.VideoPreviewCache,
            PlatformServices.PhotoPreviewCache);

        if (PlatformServices.RemoteLibraryManager is { } remoteLibraryManager)
        {
            RemoteLibraries = new RemoteLibrariesSettingsSectionViewModel(remoteLibraryManager, dialogService);
        }

        Library = new LibrarySettingsSectionViewModel(settingsStore, scanLibraryCommand)
        {
            ClearPlaybackHistoryAsync = clearPlaybackHistory,
            DialogService = dialogService,
            Storage = storage.HasItems ? storage : null,
        };
        PhotoIndex = PlatformServices.PhotoIndex is { } photoIndex ? new PhotoIndexSettingsViewModel(photoIndex) : null;
        var extensions = new ExtensionSettingsSectionViewModel(lyricSourceService);
        Appearance = new AppearanceSettingsSectionViewModel(settingsStore, themeService, dialogService);
        Playback = new PlaybackSettingsSectionViewModel(
            settingsStore,
            sampleRateChanged,
            alwaysResampleChanged,
            systemSampleRateProvider,
            audioOutputSettings,
            PlatformServices.VideoShaderPackages,
            dialogService);

        if (PlatformServices.SupportsRemoteApiSettings && PlatformServices.RemoteApiServerController is { } controller)
        {
            RemoteAccess = new RemoteApiSettingsSectionViewModel(settingsStore, controller);
        }

        var libraryPages = new System.Collections.Generic.List<SettingsNavigationItemViewModel>
        {
            new(nameof(Strings.Resources.SettingsPage_LocalLibrary), Library, "IconFolderHome"),
        };
        if (RemoteLibraries is not null)
        {
            libraryPages.Add(Embedded(nameof(Strings.Resources.SettingsPage_RemoteLibrary), RemoteLibraries, "IconLibraryMusic"));
        }
        if (PhotoIndex is not null)
        {
            libraryPages.Add(Embedded(nameof(Strings.Resources.SettingsPage_PhotoIndex), PhotoIndex, "IconImage"));
        }
        libraryPages.Add(Embedded(nameof(Strings.Resources.ExtensionSettings_Title), extensions, "IconMusicNote"));
        if (RemoteAccess is not null)
        {
            libraryPages.Add(new(RemoteAccess.Key, RemoteAccess, "IconShare"));
        }

        var playbackPages = new System.Collections.Generic.List<SettingsNavigationItemViewModel>
        {
            Embedded(nameof(Strings.Resources.SettingsPage_Display), new DisplaySettingsViewModel(Playback.Video), "IconDisplay"),
            Embedded(nameof(Strings.Resources.SettingsPage_VideoPlayback), Playback.Video, "IconVideo"),
            Embedded(nameof(Strings.Resources.SettingsPage_CustomFilters), new CustomFiltersSettingsViewModel(Playback.Video), "IconSettings"),
        };
        if (!OperatingSystem.IsBrowser())
        {
            playbackPages.Insert(1, new(nameof(Strings.Resources.SettingsPage_AudioPlayback), Playback, "IconVolumeHigh"));
        }
        if (Playback.RemoteTranscode is not null)
        {
            playbackPages.Add(Embedded(nameof(Strings.Resources.SettingsPage_ServerStreaming), Playback.RemoteTranscode, "IconPlayCircle"));
        }

        Categories =
        [
            new(Appearance, new SettingsNavigationItemViewModel(nameof(Strings.Resources.SettingsPage_General), Appearance, "IconPalette")),
            new(Library, libraryPages.ToArray()),
            new(Playback, playbackPages.ToArray()),
        ];
        Sections = new(Categories.SelectMany(category => new[] { category }.Concat(category.Children)));

        BackCommand = new RelayCommand(() => SelectedSection = null);
        SelectSectionCommand = new RelayCommand<SettingsNavigationItemViewModel>(section => SelectedSection = section);
    }

    private static SettingsNavigationItemViewModel Embedded(string titleKey, object content, string iconKey) =>
        new(titleKey, new SettingsContentSectionViewModel(titleKey, content), iconKey);

    public ObservableCollection<SettingsNavigationItemViewModel> Sections { get; }

    public SettingsNavigationItemViewModel[] Categories { get; }

    public LibrarySettingsSectionViewModel Library { get; }

    public AppearanceSettingsSectionViewModel Appearance { get; }

    public PlaybackSettingsSectionViewModel Playback { get; }

    public RemoteLibrariesSettingsSectionViewModel? RemoteLibraries { get; }

    public PhotoIndexSettingsViewModel? PhotoIndex { get; }

    public RemoteApiSettingsSectionViewModel? RemoteAccess { get; }

    /// <summary>Clears the selected section, returning to the section list on narrow layouts.</summary>
    public IRelayCommand BackCommand { get; }

    /// <summary>Opens the given section from the sidebar.</summary>
    public IRelayCommand<SettingsNavigationItemViewModel> SelectSectionCommand { get; }

    /// <summary>The selected section's content while the page lists the sections itself.</summary>
    public object? ListLayoutDetail => IsSidebarLayout ? null : SelectedSection?.Content;

    /// <summary>The selected section's content while a sidebar lists the sections.</summary>
    public object? SidebarLayoutDetail => IsSidebarLayout ? SelectedSection?.Content : null;

    /// <summary>Header shown in the detail placeholder on wide layouts when no section is selected.</summary>
    public string DetailPlaceholderHeader => Strings.Resources.Settings_SelectSectionHeader;

    /// <summary>Description shown in the detail placeholder on wide layouts when no section is selected.</summary>
    public string DetailPlaceholderDescription => Strings.Resources.Settings_ChooseFromListDescription;

    /// <inheritdoc />
    public bool CanHandleBack => !IsSidebarLayout && IsCompact && SelectedSection is { IsCategory: false };

    public bool CanPreviewBack => CanHandleBack;

    /// <inheritdoc />
    public string? DetailKey => SelectedSection is { IsCategory: false } section ? section.Key : null;

    /// <inheritdoc />
    public void RestoreDetail(string? key)
    {
        var section = key is null ? null : Sections.FirstOrDefault(section => section.Key == key);
        SelectedSection = section is { IsCategory: true } ? section.Children.FirstOrDefault() : section;
    }

    partial void OnSelectedSectionChanged(SettingsNavigationItemViewModel? oldValue, SettingsNavigationItemViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    // A sidebar always has a section open, as the shell's sidebar always has a destination.
    partial void OnIsSidebarLayoutChanged(bool value)
    {
        if (value && SelectedSection is null)
        {
            SelectedSection = Sections.FirstOrDefault(section => !section.IsCategory);
        }
    }

    /// <summary>
    /// Sections apply their own content margins (the detail pane has no padding of its own), so the
    /// shell's layout width has to reach them.
    /// </summary>
    protected override void OnIsNarrowChanged(bool value)
    {
        foreach (var section in Sections.Select(item => item.Content).OfType<SettingsSectionViewModel>())
        {
            section.IsNarrow = value;
        }
    }

    /// <summary>
    /// On the narrow single-pane layout an open section fills the page, so the first back returns to
    /// the section list instead of leaving the Settings page. On wide layouts both panes are visible,
    /// so back is left to the shell.
    /// </summary>
    public bool TryHandleBack()
    {
        if (CanHandleBack)
        {
            SelectedSection = null;
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        Appearance.Dispose();
        PhotoIndex?.Dispose();
        Playback.Dispose();
        RemoteLibraries?.Dispose();
    }
}