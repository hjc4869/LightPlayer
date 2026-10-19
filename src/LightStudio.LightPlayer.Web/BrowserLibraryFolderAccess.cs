using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// Provides stable library identifiers for retained browser handles and temporary folder selections.
/// </summary>
/// <remarks>
/// The dialog is opened by the app's own module rather than Avalonia's storage provider: a browser
/// only opens its directory picker while the originating click is still the current user gesture.
/// </remarks>
[SupportedOSPlatform("browser")]
internal sealed class BrowserLibraryFolderAccess : ILibraryFolderAccess
{
    public bool ProvidesFolderPicker => true;

    public string AccessDescription => BrowserFileSystem.SupportsPersistentFolders
        ? Strings.Resources.LibrarySettings_BrowserFolderAccessDescription
        : Strings.Resources.LibrarySettings_BrowserTemporaryFolderAccessDescription;

    public async Task<LibraryFolderReference?> PickFolderAsync(bool forVideo)
    {
        var picked = await BrowserFileSystem.PickFolderAsync(forVideo);
        return picked is not { } root
            ? null
            : new LibraryFolderReference(new BrowserMediaPath(root.RootId, string.Empty).ToString(), root.Name);
    }

    public string? GetDisplayName(string identifier) =>
        BrowserMediaPath.TryParse(identifier, out var parsed) ? BrowserFileSystem.GetDisplayName(parsed.RootId) : null;

    public bool RequiresAccessGrant(string identifier) =>
        BrowserMediaPath.TryParse(identifier, out var parsed) &&
        BrowserFileSystem.GetAccess(parsed.RootId) is BrowserFolderAccess.Prompt or BrowserFolderAccess.Denied;

    public bool RequiresIndividualAccessGrant(string identifier) =>
        BrowserMediaPath.TryParse(identifier, out var parsed) && BrowserFileSystem.RequiresFolderSelection(parsed.RootId);

    public Task<bool> RequestAccessAsync(string identifier) =>
        BrowserMediaPath.TryParse(identifier, out var parsed)
            ? BrowserFileSystem.RequestAccessAsync(parsed.RootId)
            : Task.FromResult(false);

    public Task RequestAccessAsync(IReadOnlyList<string> identifiers) =>
        BrowserFileSystem.RequestAccessAsync(
            [.. identifiers
                .Select(identifier => BrowserMediaPath.TryParse(identifier, out var parsed) ? parsed.RootId : null)
                .OfType<string>()]);

    public Task ReleaseAsync(string identifier) =>
        BrowserMediaPath.TryParse(identifier, out var parsed)
            ? BrowserFileSystem.ForgetAsync(parsed.RootId)
            : Task.CompletedTask;
}
