using LightMediaRenderer.Avalonia.Audio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightStudio.LightPlayer.ViewModels.Pages;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

namespace LightStudio.LightPlayer.ViewModels;

/// <summary>
/// First-run setup: library folders, remote libraries, appearance preferences, sample rate, and
/// third-party lyrics.
/// </summary>
/// <remarks>
/// This device's folders and any remote library are used together, so setup asks for both in one
/// library section rather than making the user choose between them.
/// </remarks>
public partial class InitialSetupViewModel : ViewModelBase, IShellOverlayHost
{
    private readonly IAppSettingsStore settingsStore;
    private readonly ILyricSourceService? lyricSourceService;
    private TaskCompletionSource? overlayClosed;

    /// <summary>
    /// Setup runs before any shell exists, so it hosts the modal dialogs its sections open the same
    /// way the shell does on single-view platforms.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverlayOpen))]
    private Control? overlayContent;

    public InitialSetupViewModel(
        IAppSettingsStore settingsStore,
        IThemeService themeService,
        ILyricSourceService? lyricSourceService)
    {
        this.settingsStore = settingsStore;
        this.lyricSourceService = lyricSourceService;

        var noScan = new AsyncRelayCommand(() => Task.CompletedTask);

        var dialogService = AppBootstrapper.CreateDialogService();
        if (dialogService is OverlayDialogService overlayDialogs)
        {
            overlayDialogs.Host = this;
        }

        // The setup view stacks the sections inside one padded scroll viewer, so the library section
        // must not add the inset it applies when it owns the settings page's detail pane - otherwise
        // it renders narrower than the sub-sections stacked beside it.
        Library = new LibrarySettingsSectionViewModel(settingsStore, noScan)
        {
            ShowScanButton = false,
            HostProvidesContentMargin = true,
            RemoteLibraries = PlatformServices.RemoteLibraryManager is { } remoteLibraryManager
                ? new RemoteLibrariesSettingsSectionViewModel(remoteLibraryManager, dialogService)
                : null,
        };
        if (PlatformServices.CanManageLibraryFolders)
        {
            Library.EnsureDefaultFolders();
        }

        Preferences = new AppearancePreferencesViewModel(settingsStore, themeService);
        Appearance = new ThemeSettingsSectionViewModel(themeService, dialogService: null, allowCustomThemes: false);
        SampleRate = new SampleRateSettingsSectionViewModel(
            settingsStore,
            systemSampleRateProvider: AudioOutputDeviceFactory.SystemSampleRateProvider);
        LyricSources = lyricSourceService?.BundledSources
            .Select(source => new InitialSetupLyricSourceViewModel(
                source,
                string.Equals(source.Name, "lrclib", StringComparison.OrdinalIgnoreCase)))
            .ToArray() ?? [];

        ContinueCommand = new AsyncRelayCommand(ContinueAsync);
        if (Library.RemoteLibraries is { } remoteLibraries && PlatformServices.WebHostAddress is { Length: > 0 })
        {
            _ = OfferWebHostLibraryAsync(remoteLibraries);
        }
    }

    /// <summary>
    /// A page served by a host that carries a library of its own has nothing left to set up but the
    /// sign-in, so the editor opens by itself with that address already filled in, as though the
    /// user had pressed "Add remote library". Closing it skips the step. Hosts that only relay to
    /// servers the user names cannot guess an address, so they are left alone.
    /// </summary>
    private static async Task OfferWebHostLibraryAsync(RemoteLibrariesSettingsSectionViewModel remoteLibraries)
    {
        if (PlatformServices.WebHostProbe is { } probe)
        {
            await probe;
        }

        if (PlatformServices.WebHostProxyOnly == false)
        {
            await remoteLibraries.OpenEditorAsync(null);
        }
    }

    public LibrarySettingsSectionViewModel Library { get; }

    public ThemeSettingsSectionViewModel Appearance { get; }

    public AppearancePreferencesViewModel Preferences { get; }

    public SampleRateSettingsSectionViewModel SampleRate { get; }

    public IReadOnlyList<InitialSetupLyricSourceViewModel> LyricSources { get; }

    public IAsyncRelayCommand ContinueCommand { get; }

    /// <inheritdoc />
    public bool IsOverlayOpen => OverlayContent is not null;

    /// <inheritdoc />
    public Task ShowOverlayAsync(Control content)
    {
        CloseOverlay();
        OverlayContent = content;
        overlayClosed = new TaskCompletionSource();
        return overlayClosed.Task;
    }

    /// <inheritdoc />
    public bool CloseOverlay()
    {
        if (OverlayContent is null)
        {
            return false;
        }

        OverlayContent = null;
        var pending = overlayClosed;
        overlayClosed = null;
        pending?.TrySetResult();
        return true;
    }

    /// <summary>Raised when the user finishes setup; the host opens the main window.</summary>
    public event Action? SetupCompleted;

    private async Task ContinueAsync()
    {
        var settings = PlatformServices.LocalServicesFactory?.Invoke().Settings ?? settingsStore;
        var selectedSources = LyricSources.Where(source => source.IsEnabled).ToArray();
        settings.EnableThirdPartyLyrics = selectedSources.Length > 0;
        if (selectedSources.Length > 0)
        {
            var sources = lyricSourceService ?? PlatformServices.LocalServicesFactory?.Invoke().LyricSources;
            if (sources is not null)
            {
                foreach (var source in selectedSources)
                {
                    await sources.ProvisionBundledSourceAsync(source.Name);
                }
            }
        }

        settings.InitialSetupCompleted = true;
        SetupCompleted?.Invoke();
    }
}

public sealed partial class InitialSetupLyricSourceViewModel : ObservableObject
{
    [ObservableProperty]
    private bool isEnabled;

    public InitialSetupLyricSourceViewModel(BundledLyricSource source, bool isEnabled)
    {
        Name = source.Name;
        Information = source.Information;
        this.isEnabled = isEnabled;
    }

    public string Name { get; }

    public IReadOnlyList<LyricSourceInfoItem> Information { get; }
}
