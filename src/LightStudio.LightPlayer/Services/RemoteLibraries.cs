using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Services;

public enum RemoteLibraryProvider
{
    LightPlayer,
    NextcloudMusic,
    WebDav,
}

public sealed record RemoteLibraryDefinition(
    LibrarySourceId Id,
    string Name,
    string Address,
    string ApiProxy = "",
    string Token = "",
    string Username = "",
    string Password = "",
    bool LoadMusic = true,
    bool LoadVideo = true,
    string StorageKey = "",
    RemoteLibraryProvider Provider = RemoteLibraryProvider.LightPlayer,
    bool RefreshAtStartup = false)
{
    /// <summary>The unnamed fallback is the target server, not the proxy used to reach it.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Address : Name;

    public bool HasApiProxy => !string.IsNullOrWhiteSpace(ApiProxy);

    public bool ParticipatesInBrowse => LoadMusic || LoadVideo;
}

/// <summary>
/// A remote library saved before the address field consistently represented the target server.
/// In this format <see cref="Address"/> is the connection endpoint and <see cref="ForwardTarget"/>
/// is the target server.
/// </summary>
public sealed record LegacyRemoteLibraryDefinition(
    LibrarySourceId Id,
    string Name,
    string Address,
    string ForwardTarget = "",
    string Token = "",
    string Username = "",
    string Password = "",
    bool LoadMusic = true,
    bool LoadVideo = true,
    string StorageKey = "");

public sealed record LibraryVisibilitySettings(bool PreferHigherPriorityDuplicates = false);

/// <summary>Aggregate progress for remote library refreshes currently known to the manager.</summary>
public sealed record RemoteLibraryRefreshStatus(
    long Revision,
    bool HasActivity,
    bool IsActive,
    int IndexedCount,
    int WarningCount,
    bool Failed);

/// <summary>
/// A server saved by a build that could only be connected to one at a time. Read once, to turn the
/// list into <see cref="RemoteLibraryDefinition"/>s, and written by nothing.
/// </summary>
public sealed record RemoteServerEntry(
    string Id,
    string Name,
    string Address,
    string Token = "",
    string Username = "",
    string Password = "",
    string ForwardTarget = "")
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Address : Name;

    public bool IsForwarded => !string.IsNullOrWhiteSpace(ForwardTarget);
}

/// <summary>
/// Thrown when a server is reachable but the caller holds no credentials it accepts. The caller is
/// expected to ask the user and retry through <see cref="IRemoteLibraryManager.SignInAsync"/>.
/// </summary>
public sealed class RemoteAuthenticationRequiredException(string serverName)
    : Exception($"'{serverName}' requires authentication.")
{
    public string ServerName { get; } = serverName;
}

public interface IRemoteLibraryManager : IDisposable
{
    event EventHandler<RemoteLibraryRefreshStatus>? RefreshStatusChanged;

    IReadOnlyList<RemoteLibraryDefinition> Definitions { get; }

    ILibrarySourceRegistry Registry { get; }

    RemoteLibraryRefreshStatus RefreshStatus { get; }

    LibraryVisibilitySettings VisibilitySettings { get; set; }

    Task ConnectEnabledAsync(CancellationToken cancellationToken = default);

    Task<RemoteLibraryDefinition> SaveAsync(
        RemoteLibraryDefinition definition,
        bool allowOffline = false,
        string? authenticationPassword = null,
        bool? rememberPassword = null,
        CancellationToken cancellationToken = default);

    Task<RemoteLibraryDefinition> SignInAsync(
        LibrarySourceId sourceId,
        string username,
        string password,
        bool rememberPassword,
        CancellationToken cancellationToken = default);

    Task<RemoteLibraryDefinition> AuthenticateWithBrowserAsync(
        RemoteLibraryDefinition definition,
        Func<Uri, CancellationToken, Task> openBrowser,
        CancellationToken cancellationToken = default);

    Task RetryAsync(LibrarySourceId sourceId, CancellationToken cancellationToken = default);

    Task RefreshAsync(LibrarySourceId sourceId, CancellationToken cancellationToken = default);

    bool Move(LibrarySourceId sourceId, int newIndex);

    void Remove(LibrarySourceId sourceId, bool deleteCaches);
}

public sealed class RemoteLibraryIncompatibleException(string message) : Exception(message);