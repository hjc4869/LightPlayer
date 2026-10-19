using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// The remote-library list shown as a section of the Library settings page (and of first-run
/// setup). Each library also shows the caches it keeps on this device once connected.
/// </summary>
public sealed partial class RemoteLibrariesSettingsSectionViewModel : ViewModelBase, IDisposable
{
    private readonly IRemoteLibraryManager manager;
    private readonly IDialogService? dialogService;
    private bool disposed;

    [ObservableProperty]
    private bool preferHigherPriorityDuplicates;

    public RemoteLibrariesSettingsSectionViewModel(
        IRemoteLibraryManager manager,
        IDialogService? dialogService)
    {
        this.manager = manager;
        this.dialogService = dialogService;
        preferHigherPriorityDuplicates = manager.VisibilitySettings.PreferHigherPriorityDuplicates;
        AddCommand = new AsyncRelayCommand(() => OpenEditorAsync(null));
        manager.Registry.DefinitionsChanged += OnRegistryChanged;
        manager.Registry.PriorityChanged += OnRegistryChanged;
        manager.Registry.StatusChanged += OnRegistryChanged;
        manager.Registry.CatalogChanged += OnRegistryChanged;
        RebuildRows();
    }

    public ObservableCollection<RemoteLibraryRowViewModel> Sources { get; } = [];

    public string SectionTitle => Localized("RemoteLibraries_Title", "Remote libraries");

    public string SectionDescription
    {
        get
        {
            var description = Localized("RemoteLibraries_Description", "Add and combine independent Light Player libraries.");
            return PlatformServices.CanManageLibraryFolders
                ? description
                : $"{description} {Localized("RemoteLibraries_RemoteOnlyDescription", "Local library folders are unavailable here, so only remote libraries can be used.")}";
        }
    }

    public string PreferDuplicatesText => Localized("RemoteLibraries_PreferDuplicates", "Prefer higher-priority duplicates");

    public string DuplicateDescription =>
        Localized("RemoteLibraries_PreferDuplicatesDescription", "Hide lower-priority copies of matching albums and songs.");

    public string AddText => Localized("RemoteLibraries_Add", "Add remote library");

    public IAsyncRelayCommand AddCommand { get; }

    partial void OnPreferHigherPriorityDuplicatesChanged(bool value)
    {
        if (!disposed)
        {
            manager.VisibilitySettings = new LibraryVisibilitySettings(value);
        }
    }

    internal Task OpenEditorAsync(RemoteLibraryDefinition? definition) =>
        dialogService is { } dialogs
            ? dialogs.ShowRemoteLibraryEditorAsync(new RemoteLibraryEditorViewModel(this, manager, definition))
            : Task.CompletedTask;

    internal bool Move(LibrarySourceId sourceId, int newIndex) => manager.Move(sourceId, newIndex);

    internal Task RetryAsync(LibrarySourceId sourceId) => manager.RetryAsync(sourceId);

    internal Task RefreshAsync(LibrarySourceId sourceId) => manager.RefreshAsync(sourceId);

    internal async Task RemoveAsync(RemoteLibraryDefinition definition)
    {
        if (dialogService is not null)
        {
            var confirmed = await dialogService.ConfirmAsync(
                Localized("RemoteLibraries_RemoveTitle", "Remove remote library"),
                string.Format(
                    CultureInfo.CurrentCulture,
                    Localized("RemoteLibraries_RemoveMessageFormat", "Remove '{0}'?"),
                    definition.DisplayName),
                Strings.Resources.Common_Delete);
            if (!confirmed)
            {
                return;
            }
        }

        manager.Remove(definition.Id, deleteCaches: true);
    }

    internal void RebuildRows()
    {
        if (disposed)
        {
            return;
        }

        Sources.Clear();
        var definitions = manager.Definitions;
        for (var index = 0; index < definitions.Count; index++)
        {
            Sources.Add(new RemoteLibraryRowViewModel(
                this,
                manager,
                dialogService,
                definitions[index],
                index,
                definitions.Count));
        }
    }

    private void OnRegistryChanged(object? sender, LibrarySourceChangedEventArgs eventArgs)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RebuildRows();
        }
        else
        {
            Dispatcher.UIThread.Post(RebuildRows);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        manager.Registry.DefinitionsChanged -= OnRegistryChanged;
        manager.Registry.PriorityChanged -= OnRegistryChanged;
        manager.Registry.StatusChanged -= OnRegistryChanged;
        manager.Registry.CatalogChanged -= OnRegistryChanged;
        Sources.Clear();
    }
}

public sealed partial class RemoteLibraryRowViewModel : ViewModelBase
{
    private readonly RemoteLibrariesSettingsSectionViewModel owner;
    private readonly IRemoteLibraryManager manager;
    private readonly RemoteLibraryDefinition definition;
    private bool loaded;

    [ObservableProperty]
    private bool loadMusic;

    [ObservableProperty]
    private bool loadVideo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool isRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRefreshResult))]
    private string refreshResult = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCategoryStatus))]
    private string categoryStatus = string.Empty;

    [ObservableProperty]
    private bool isSavingCategories;

    public RemoteLibraryRowViewModel(
        RemoteLibrariesSettingsSectionViewModel owner,
        IRemoteLibraryManager manager,
        IDialogService? dialogService,
        RemoteLibraryDefinition definition,
        int index,
        int count)
    {
        this.owner = owner;
        this.manager = manager;
        this.definition = definition;
        loadMusic = definition.LoadMusic;
        loadVideo = definition.LoadVideo;
        Index = index;
        Count = count;
        MoveUpCommand = new RelayCommand(() => owner.Move(definition.Id, Index - 1), () => Index > 0);
        MoveDownCommand = new RelayCommand(() => owner.Move(definition.Id, Index + 1), () => Index + 1 < Count);
        EditCommand = new AsyncRelayCommand(() => owner.OpenEditorAsync(definition));
        RemoveCommand = new AsyncRelayCommand(() => owner.RemoveAsync(definition));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanRefresh);
        RetryCommand = new AsyncRelayCommand(RetryAsync, CanRetry);
        if (Source?.Services is { } services)
        {
            AddCache(
                services.MediaBlockCache,
                nameof(Strings.Resources.StorageSettings_MediaBlocksHeader));
            AddCache(
                services.ImageCache,
                nameof(Strings.Resources.StorageSettings_ImagesHeader));
        }

        _ = Task.WhenAll(Caches.Select(cache => cache.RefreshAsync()));
        loaded = true;

        void AddCache(IStorageCache? cache, string headerKey)
        {
            if (cache is not null)
            {
                Caches.Add(new StorageCacheItemViewModel(cache, dialogService, headerKey));
            }
        }
    }

    public int Index { get; }

    public int Count { get; }

    public ObservableCollection<StorageCacheItemViewModel> Caches { get; } = [];

    public string DisplayName => definition.DisplayName;

    public string Address => definition.HasApiProxy
        ? $"{definition.ApiProxy} -> {definition.Address}"
        : definition.Address;

    public string PriorityText => string.Format(
        CultureInfo.CurrentCulture,
        Localized("RemoteLibraries_PriorityFormat", "Priority {0}"),
        Index + 1);

    public string StatusText => !LoadMusic && !LoadVideo
        ? Localized("RemoteLibraries_StatusDisabled", "Disabled")
        : IsRefreshing
        ? Localized("RemoteLibraries_StatusRefreshing", "Refreshing")
        : Status switch
    {
        LibrarySourceStatus.SignInRequired => Localized("RemoteLibraries_StatusSignIn", "Sign in required"),
        LibrarySourceStatus.Incompatible => Localized("RemoteLibraries_StatusIncompatible", "Incompatible server"),
        LibrarySourceStatus.Offline => Localized("RemoteLibraries_StatusOffline", "Offline"),
        LibrarySourceStatus.Connecting => Localized("RemoteLibraries_StatusConnecting", "Connecting"),
        LibrarySourceStatus.Disabled => Localized("RemoteLibraries_StatusDisabled", "Disabled"),
        _ => Localized("RemoteLibraries_StatusOnline", "Online"),
    };

    public string MoveUpText => Localized("RemoteLibraries_MoveUp", "Move up");

    public string MoveDownText => Localized("RemoteLibraries_MoveDown", "Move down");

    public string RefreshText => Localized("RemoteLibraries_Refresh", "Refresh library");

    public string RetryText => Localized("RemoteLibraries_Retry", "Retry connection");

    public string EditText => Localized("RemoteLibraries_Edit", "Edit library");

    public string MusicText => Localized("RemoteLibraries_LoadMusicShort", "Music");

    public string VideoText => Localized("RemoteLibraries_LoadVideoShort", "Video");

    public string? ErrorText => Source?.LastError;

    public LibrarySourceStatus Status => Source?.Status ?? LibrarySourceStatus.Offline;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool HasRefreshResult => RefreshResult.Length > 0;

    public bool HasCategoryStatus => CategoryStatus.Length > 0;

    public bool ShowRetry => Status is LibrarySourceStatus.Offline or LibrarySourceStatus.SignInRequired;

    public IRelayCommand MoveUpCommand { get; }

    public IRelayCommand MoveDownCommand { get; }

    public IAsyncRelayCommand EditCommand { get; }

    public IAsyncRelayCommand RemoveCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand RetryCommand { get; }

    private ILibrarySource? Source =>
        manager.Registry.TryGetSource(definition.Id, out var source) ? source : null;

    partial void OnLoadMusicChanged(bool value) => SaveCategories();

    partial void OnLoadVideoChanged(bool value) => SaveCategories();

    partial void OnIsRefreshingChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
    }

    private void SaveCategories()
    {
        if (loaded)
        {
            _ = SaveCategoriesAsync();
        }
    }

    private async Task SaveCategoriesAsync()
    {
        IsSavingCategories = true;
        CategoryStatus = "Saving";
        try
        {
            await manager.SaveAsync(
                definition with { LoadMusic = LoadMusic, LoadVideo = LoadVideo },
                allowOffline: true);
            CategoryStatus = string.Empty;
        }
        catch (Exception exception)
        {
            CategoryStatus = AppErrorReporter.LogAndGetMessage(
                "Remote.Library",
                $"Could not save categories for remote library {definition.Id}.",
                exception);
        }
        finally
        {
            IsSavingCategories = false;
        }
    }

    private bool CanRefresh() => !IsRefreshing && Status == LibrarySourceStatus.Online;

    private bool CanRetry() => !IsRefreshing && Status is LibrarySourceStatus.Offline or LibrarySourceStatus.SignInRequired;

    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        RefreshResult = string.Empty;
        try
        {
            await owner.RefreshAsync(definition.Id);
            RefreshResult = Localized("RemoteLibraries_RefreshComplete", "Refresh complete");
        }
        catch (Exception exception)
        {
            RefreshResult = AppErrorReporter.LogAndGetMessage(
                "Remote.Library",
                $"Could not refresh remote library {definition.Id}.",
                exception);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task RetryAsync()
    {
        IsRefreshing = true;
        RefreshResult = string.Empty;
        try
        {
            await owner.RetryAsync(definition.Id);
        }
        catch (Exception exception)
        {
            RefreshResult = AppErrorReporter.LogAndGetMessage(
                "Remote.Library",
                $"Could not reconnect remote library {definition.Id}.",
                exception);
        }
        finally
        {
            IsRefreshing = false;
        }
    }
}

public sealed partial class RemoteLibraryEditorViewModel : ViewModelBase
{
    // Two views show this form: Views/Pages/RemoteLibraryEditorView.axaml on every head that draws
    // its own controls, and the HTML twin in the browser head
    // (LightStudio.LightPlayer.Web/wwwroot/remote-library-editor.js, driven by
    // BrowserRemoteLibraryEditor.cs) so the page's password manager can fill it in. A property added
    // here has to reach both of them.
    private readonly RemoteLibrariesSettingsSectionViewModel owner;
    private readonly IRemoteLibraryManager manager;
    private RemoteLibraryDefinition original;
    private Func<Uri, CancellationToken, Task>? openBrowser;
    private CancellationTokenSource? authenticationCancellation;

    /// <summary>
    /// Origin serving the browser client, or null on heads that reach the target without one.
    /// A page relays through this origin unless the user asks for a direct connection.
    /// </summary>
    private readonly string? webHost;

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private string address;

    [ObservableProperty]
    private string apiProxy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Provider))]
    [NotifyPropertyChangedFor(nameof(IsLightPlayer))]
    [NotifyPropertyChangedFor(nameof(IsNextcloudMusic))]
    [NotifyPropertyChangedFor(nameof(IsWebDav))]
    [NotifyPropertyChangedFor(nameof(CanLoadVideo))]
    [NotifyPropertyChangedFor(nameof(CanRefreshAtStartup))]
    [NotifyPropertyChangedFor(nameof(ShowCredentials))]
    [NotifyPropertyChangedFor(nameof(ShowApiProxy))]
    [NotifyPropertyChangedFor(nameof(ShowWebProxy))]
    [NotifyPropertyChangedFor(nameof(ShowLightPlayerAdvanced))]
    [NotifyPropertyChangedFor(nameof(CanAuthenticateWithBrowser))]
    private int selectedProviderIndex;

    [ObservableProperty]
    private bool useWebProxy;

    [ObservableProperty]
    private bool loadMusic;

    [ObservableProperty]
    private bool loadVideo;

    [ObservableProperty]
    private bool refreshAtStartup;

    [ObservableProperty]
    private string userName;

    [ObservableProperty]
    private string password;

    [ObservableProperty]
    private string apiKey;

    [ObservableProperty]
    private string authenticationStatus = string.Empty;

    [ObservableProperty]
    private bool rememberPassword;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLightPlayerAdvanced))]
    [NotifyPropertyChangedFor(nameof(ShowCredentials))]
    private bool showAdvanced;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(CanSaveOffline))]
    private string errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveOffline))]
    private bool isBusy;

    public RemoteLibraryEditorViewModel(
        RemoteLibrariesSettingsSectionViewModel owner,
        IRemoteLibraryManager manager,
        RemoteLibraryDefinition? definition)
    {
        this.owner = owner;
        this.manager = manager;
        IsNew = definition is null;
        original = definition ?? new RemoteLibraryDefinition(default, string.Empty, string.Empty);
        webHost = PlatformServices.WebHostAddress is { Length: > 0 } host ? host.TrimEnd('/') : null;
        name = original.Name;
        address = InitialAddress(original, IsNew, webHost);
        apiProxy = original.ApiProxy;
        selectedProviderIndex = original.Provider switch
        {
            RemoteLibraryProvider.NextcloudMusic => 1,
            RemoteLibraryProvider.WebDav => 2,
            _ => 0,
        };
        useWebProxy = original.HasApiProxy;
        loadMusic = original.LoadMusic;
        loadVideo = original.LoadVideo;
        refreshAtStartup = original.RefreshAtStartup;
        userName = original.Username;
        password = original.Password;
        apiKey = original.Provider == RemoteLibraryProvider.NextcloudMusic ? original.Token : string.Empty;
        rememberPassword = original.Password.Length > 0;
        showAdvanced = original.Provider == RemoteLibraryProvider.LightPlayer
            && ((original.HasApiProxy && webHost is null) || original.Username.Length > 0);
        if (original.Provider == RemoteLibraryProvider.NextcloudMusic && original.Username.Length > 0)
        {
            authenticationStatus = string.Format(
                CultureInfo.CurrentCulture,
                Localized("RemoteLibraries_ConnectedAsFormat", "Connected as {0}"),
                original.Username);
        }
        SaveCommand = new AsyncRelayCommand(() => SaveAsync(allowOffline: false), CanSave);
        SaveOfflineCommand = new AsyncRelayCommand(() => SaveAsync(allowOffline: true), () => CanSaveOffline);
        AuthenticateWithBrowserCommand = new AsyncRelayCommand(AuthenticateWithBrowserAsync, CanAuthenticate);
        CancelCommand = new RelayCommand(Cancel);
    }

    /// <summary>Raised when the form is done; the hosting window or overlay dismisses itself.</summary>
    public event Action? RequestClose;

    public bool IsNew { get; }

    public string Header => IsNew
        ? Localized("RemoteLibraries_AddHeader", "Add remote library")
        : Localized("RemoteLibraries_EditHeader", "Edit remote library");

    public string NameText => Localized(nameof(Strings.Resources.Connection_ServerName), "Name");

    public string NamePlaceholderText => Localized(nameof(Strings.Resources.RemoteLibraries_NamePlaceholder), "Home library");

    public string AddressText => Localized(nameof(Strings.Resources.Connection_ServerAddress), "Server address");

    public string ProviderText => Localized("RemoteLibraries_Provider", "Service");

    public IReadOnlyList<string> ProviderOptions =>
    [
        Localized("RemoteLibraries_ProviderLightPlayer", "Light Player"),
        Localized("RemoteLibraries_ProviderNextcloudMusic", "Nextcloud Music"),
        Localized("RemoteLibraries_ProviderWebDav", "WebDAV"),
    ];

    public RemoteLibraryProvider Provider => SelectedProviderIndex switch
    {
        1 => RemoteLibraryProvider.NextcloudMusic,
        2 => RemoteLibraryProvider.WebDav,
        _ => RemoteLibraryProvider.LightPlayer,
    };

    public bool IsLightPlayer => Provider == RemoteLibraryProvider.LightPlayer;

    public bool IsNextcloudMusic => Provider == RemoteLibraryProvider.NextcloudMusic;

    public bool IsWebDav => Provider == RemoteLibraryProvider.WebDav;

    public bool CanLoadVideo => !IsNextcloudMusic;

    public bool CanRefreshAtStartup => IsNextcloudMusic || IsWebDav;

    public string LoadMusicText => Localized("RemoteLibraries_LoadMusic", "Load music");

    public string LoadVideoText => Localized("RemoteLibraries_LoadVideo", "Load video");

    public string RefreshAtStartupText => Localized(
        "RemoteLibraries_RefreshAtStartup",
        "Refresh at startup");

    public string AdvancedText => Localized("RemoteLibraries_Advanced", "Advanced connection settings");

    public string ApiProxyText => Localized("RemoteLibraries_ApiProxy", "API proxy");

    public string ApiProxyPlaceholderText => Localized(nameof(Strings.Resources.RemoteLibraries_ApiProxyPlaceholder), "Leave blank to connect directly");

    public string WebProxyText => Localized("RemoteLibraries_WebProxy", "Relay API requests through this site");

    public string WebProxyHintText => Localized(
        "RemoteLibraries_WebProxyHint",
        "A direct call needs the server to allow this site, and the browser may ask for permission to reach a local network address. Turn this on when the server cannot be reached that way.");

    public string UserNameText => Localized(nameof(Strings.Resources.Connection_UserName), "User name");

    public string PasswordText => Localized(nameof(Strings.Resources.Connection_Password), "Password");

    public string RememberPasswordText => Localized("RemoteLibraries_RememberPassword", "Remember password on this device");

    public string ApiKeyText => Localized("RemoteLibraries_ApiKey", "Music API key");

    public string ApiKeyHintText => Localized(
        "RemoteLibraries_ApiKeyHint",
        "Connect in your browser, or paste an API key generated in the Nextcloud Music settings.");

    public string ConnectWithNextcloudText => Localized(
        "RemoteLibraries_ConnectNextcloud",
        "Connect with Nextcloud");

    public string OpenNextcloudMusicText => Localized(
        "RemoteLibraries_OpenNextcloudMusic",
        "Open Nextcloud Music");

    public string SaveOfflineText => Localized("RemoteLibraries_SaveOffline", "Save offline");

    /// <summary>A page can only relay through its own origin, so it picks that one instead.</summary>
    public bool ShowApiProxy => webHost is null && IsLightPlayer;

    /// <summary>Only a page can choose between relaying through its origin and calling the target.</summary>
    public bool ShowWebProxy => webHost is not null && IsLightPlayer;

    public bool ShowLightPlayerAdvanced => ShowAdvanced && IsLightPlayer;

    public bool ShowCredentials => IsWebDav || ShowLightPlayerAdvanced;

    public bool CanAuthenticateWithBrowser => IsNextcloudMusic && !OperatingSystem.IsBrowser();

    public bool HasError => ErrorMessage.Length > 0;

    public bool CanSaveOffline => !IsNew && HasError && !IsBusy;

    public IAsyncRelayCommand SaveCommand { get; }

    public IAsyncRelayCommand SaveOfflineCommand { get; }

    public IAsyncRelayCommand AuthenticateWithBrowserCommand { get; }

    public IRelayCommand CancelCommand { get; }

    partial void OnNameChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnAddressChanged(string value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        AuthenticateWithBrowserCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedProviderIndexChanged(int value)
    {
        if (IsNextcloudMusic)
        {
            LoadMusic = true;
            LoadVideo = false;
            UseWebProxy = false;
            ShowAdvanced = false;
        }
        else if (IsWebDav)
        {
            UseWebProxy = false;
            ShowAdvanced = false;
        }

        if (Provider != original.Provider)
        {
            ApiKey = string.Empty;
            UserName = string.Empty;
            Password = string.Empty;
            RememberPassword = false;
            AuthenticationStatus = string.Empty;
        }

        SaveCommand.NotifyCanExecuteChanged();
        AuthenticateWithBrowserCommand.NotifyCanExecuteChanged();
    }

    partial void OnApiKeyChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnLoadMusicChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnLoadVideoChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnErrorMessageChanged(string value) => SaveOfflineCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveOfflineCommand.NotifyCanExecuteChanged();
        AuthenticateWithBrowserCommand.NotifyCanExecuteChanged();
    }

    public void SetBrowserLauncher(Func<Uri, CancellationToken, Task>? launcher)
    {
        openBrowser = launcher;
        if (launcher is null)
        {
            authenticationCancellation?.Cancel();
        }
        AuthenticateWithBrowserCommand.NotifyCanExecuteChanged();
    }

    private bool CanSave() => !IsBusy
        && !string.IsNullOrWhiteSpace(Address)
        && (!IsNextcloudMusic || !string.IsNullOrWhiteSpace(ApiKey));

    private bool CanAuthenticate() => CanAuthenticateWithBrowser
        && openBrowser is not null
        && !IsBusy
        && !string.IsNullOrWhiteSpace(Address);

    private async Task SaveAsync(bool allowOffline)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var definition = BuildDefinition();
            await manager.SaveAsync(
                definition,
                allowOffline,
                authenticationPassword:
                    (IsLightPlayer || IsWebDav) && Password.Length > 0 ? Password : null,
                rememberPassword: (IsLightPlayer || IsWebDav) && RememberPassword);
            owner.RebuildRows();
            RequestClose?.Invoke();
        }
        catch (Exception exception)
        {
            var reason = exception.GetBaseException();
            ErrorMessage = AppErrorReporter.LogAndGetMessage(
                "Remote.Library",
                $"Could not save remote library {original.Id}.",
                exception);
            if (IsLightPlayer && NeedsCredentials(reason))
            {
                // Reveal the user name and password fields the user has to fill in to get past this.
                ShowAdvanced = true;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AuthenticateWithBrowserAsync()
    {
        if (openBrowser is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        AuthenticationStatus = string.Empty;
        using var cancellation = new CancellationTokenSource();
        authenticationCancellation = cancellation;
        try
        {
            var authenticated = await manager.AuthenticateWithBrowserAsync(
                BuildDefinition(),
                openBrowser,
                cancellation.Token);
            original = authenticated;
            Address = authenticated.Address;
            UserName = authenticated.Username;
            ApiKey = authenticated.Token;
            owner.RebuildRows();
            RequestClose?.Invoke();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ErrorMessage = AppErrorReporter.LogAndGetMessage(
                "Remote.Library",
                $"Could not authenticate Nextcloud library {original.Id}.",
                exception);
        }
        finally
        {
            if (ReferenceEquals(authenticationCancellation, cancellation))
            {
                authenticationCancellation = null;
            }
            IsBusy = false;
        }
    }

    private void Cancel()
    {
        authenticationCancellation?.Cancel();
        RequestClose?.Invoke();
    }

    private RemoteLibraryDefinition BuildDefinition() => original with
    {
        Name = Name.Trim(),
        Address = Address.Trim(),
        ApiProxy = IsLightPlayer ? ResolveApiProxy() : string.Empty,
        Token = IsNextcloudMusic ? ApiKey.Trim() : original.Token,
        Username = IsLightPlayer || IsWebDav ? UserName.Trim() : original.Username,
        Password = (IsLightPlayer || IsWebDav) && RememberPassword ? Password : string.Empty,
        LoadMusic = LoadMusic,
        LoadVideo = CanLoadVideo && LoadVideo,
        Provider = Provider,
        RefreshAtStartup = CanRefreshAtStartup && RefreshAtStartup,
    };

    /// <summary>
    /// The address always names the target server. A new browser entry starts at its own origin only
    /// when that origin also hosts a library.
    /// </summary>
    private static string InitialAddress(RemoteLibraryDefinition original, bool isNew, string? webHost)
    {
        if (!isNew || webHost is null)
        {
            return original.Address;
        }

        return PlatformServices.WebHostProxyOnly == false ? webHost : string.Empty;
    }

    private string ResolveApiProxy()
    {
        if (webHost is null)
        {
            return ApiProxy.Trim();
        }

        if (!UseWebProxy || string.Equals(Address.Trim().TrimEnd('/'), webHost, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return webHost;
    }

    private static bool NeedsCredentials(Exception exception) =>
        exception is RemoteAuthenticationRequiredException
        || exception is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };
}