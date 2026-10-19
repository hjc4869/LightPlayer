using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Puts a library item somewhere this device can name it, which is what another application needs
/// before it can be handed the file.
/// </summary>
/// <remarks>
/// An item on this device already has such a name -- a filesystem path, or on Android a content URI
/// the platform layer resolves -- and is passed through untouched. One in a remote library exists
/// only on its server, so it is written into the cache first; the copy is left there rather than
/// deleted, because the app it is handed to reads it after this call has returned.
/// </remarks>
internal static class LocalMediaFile
{
    private const string CacheFolderName = "shared";

    public static async Task<string> EnsureAsync(
        MediaReference media,
        IClientMediaResolver resolver,
        CancellationToken cancellationToken = default)
    {
        if (media.SourceId.IsLocal)
        {
            return media.Path;
        }

        IMultiMediaPlatformIO platform = await resolver
            .ResolvePlatformAsync(media, cancellationToken)
            .ConfigureAwait(true);

        string folder = Path.Combine(PlatformServices.CachePath, CacheFolderName);
        Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, SafeName(media.Path));

        await using Stream source = await platform
            .OpenMediaFileAsync(media.Path, cancellationToken)
            .ConfigureAwait(true);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(true);

        return destination;
    }

    /// <summary>
    /// A file name the cache folder can hold, built from the remote path's leaf. Anything that
    /// could name a directory is dropped: a server's path is not this device's to trust.
    /// </summary>
    private static string SafeName(string path)
    {
        string leaf = path.Replace('\\', '/');
        int separator = leaf.LastIndexOf('/');
        if (separator >= 0)
        {
            leaf = leaf[(separator + 1)..];
        }

        Span<char> cleaned = leaf.Length <= 128 ? stackalloc char[leaf.Length] : new char[leaf.Length];
        int length = 0;
        foreach (char character in leaf)
        {
            cleaned[length++] = char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or ' '
                ? character
                : '_';
        }

        string name = new string(cleaned[..length]).Trim(' ', '.');
        return name.Length == 0 ? "shared-file" : name;
    }
}
