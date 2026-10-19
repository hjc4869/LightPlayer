using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Platform hook that turns a user-picked folder without a usable local filesystem
/// path (e.g. an Android Storage Access Framework tree) into a durable identifier the
/// library can persist and later re-open. The desktop never needs this because its
/// picked folders always expose a local path via <see cref="IStorageItem.TryGetLocalPath"/>.
/// </summary>
/// <remarks>
/// Implementations live in the platform head (the Android project provides one backed by
/// the Storage Access Framework) and are registered through <see cref="PlatformServices"/>,
/// so the shared UI never references any platform-specific storage APIs.
/// </remarks>
public interface ILibraryFolderAccess
{
    /// <summary>
    /// Whether this platform opens the folder dialog itself rather than through Avalonia's storage
    /// provider. A browser has to, because it only opens its directory picker while the click that
    /// asked for it is still the current user gesture, and because its picked folder is reachable
    /// only as an opaque handle.
    /// </summary>
    bool ProvidesFolderPicker => false;

    string? AccessDescription => null;

    /// <summary>
    /// Picks a library folder with the platform's own dialog and returns a stable reference to it,
    /// returning <c>null</c> when the user canceled or access could not be kept. Only called when
    /// <see cref="ProvidesFolderPicker"/> is set.
    /// </summary>
    Task<LibraryFolderReference?> PickFolderAsync(bool forVideo) =>
        Task.FromResult<LibraryFolderReference?>(null);

    /// <summary>
    /// Secures durable read access to <paramref name="folder"/> (taking a persistable
    /// permission where the platform requires it) and returns a stable reference to store
    /// as the library folder path, or <c>null</c> when access could not be secured.
    /// </summary>
    Task<LibraryFolderReference?> PersistFolderAccessAsync(IStorageFolder folder) =>
        Task.FromResult<LibraryFolderReference?>(null);

    /// <summary>
    /// Produces a human-friendly display name for a previously persisted folder
    /// <paramref name="identifier"/> (e.g. a content-tree URI), or <c>null</c> to let the
    /// caller fall back to its default naming.
    /// </summary>
    string? GetDisplayName(string identifier);

    /// <summary>
    /// Whether a persisted folder is currently unreadable until the user grants access again.
    /// The browser's File System Access API is the case this exists for: a restored directory
    /// handle stays in the "prompt" state until a user gesture re-grants it.
    /// </summary>
    bool RequiresAccessGrant(string identifier) => false;

    bool RequiresIndividualAccessGrant(string identifier) => false;

    /// <summary>
    /// Asks the platform to re-grant read access to a persisted folder, reporting whether it was
    /// given. Must be reached directly from a user gesture, because that is the only point at
    /// which a browser answers the request.
    /// </summary>
    Task<bool> RequestAccessAsync(string identifier) => Task.FromResult(true);

    /// <summary>
    /// Asks the platform to re-grant several folders at once, for the startup prompt that offers to
    /// renew everything the library lost. Platforms whose grants are tied to a user gesture need
    /// this: a browser stops answering once the click that asked has passed, so the requests have
    /// to leave together rather than one await at a time.
    /// </summary>
    async Task RequestAccessAsync(IReadOnlyList<string> identifiers)
    {
        foreach (var identifier in identifiers)
        {
            await RequestAccessAsync(identifier);
        }
    }

    /// <summary>
    /// Drops any durable grant held for a folder the user removed from the library. Platforms that
    /// let a grant expire on its own do nothing.
    /// </summary>
    Task ReleaseAsync(string identifier) => Task.CompletedTask;

    /// <summary>
    /// Well-known locations the platform offers as music library folders on first run (e.g. Android's
    /// shared Music directory), so the user does not have to add them by hand. The paths are
    /// trusted without an existence check because they may still be unreadable while the runtime
    /// storage permission prompt is open.
    /// </summary>
    IReadOnlyList<string> GetDefaultMusicFolderPaths() => [];

    /// <summary>
    /// The equivalent of <see cref="GetDefaultMusicFolderPaths"/> for the video library.
    /// </summary>
    IReadOnlyList<string> GetDefaultVideoFolderPaths() => [];
}

/// <summary>A persisted library folder: the durable identifier stored as its path, and a display name.</summary>
public sealed record LibraryFolderReference(string Identifier, string DisplayName);
