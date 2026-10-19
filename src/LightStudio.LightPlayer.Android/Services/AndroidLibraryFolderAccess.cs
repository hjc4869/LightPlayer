#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Android.Content;
using Android.Provider;
using Avalonia.Platform.Storage;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using AndroidUri = Android.Net.Uri;

namespace LightStudio.LightPlayer.Android.Services;

/// <summary>
/// Android implementation of <see cref="ILibraryFolderAccess"/>. Turns a folder the user picked
/// through the Storage Access Framework into a durable library reference: it takes a persistable
/// read permission on the content tree (so access survives app restarts and reboots) and stores the
/// hierarchical path as the folder identifier consumed by <see cref="AndroidMultimediaPlatformIO"/>.
/// </summary>
internal sealed class AndroidLibraryFolderAccess(Context context, AndroidMultimediaPlatformIO platform) : ILibraryFolderAccess
{
    private readonly Context context = context.ApplicationContext ?? context;

    public async Task<LibraryFolderReference?> PersistFolderAccessAsync(IStorageFolder folder)
    {
        if (folder.Path is not { IsAbsoluteUri: true } path ||
            !AndroidStoragePath.TryParseDocumentUri(path.AbsoluteUri, out var tree, out _))
        {
            return null;
        }

        using var treeUri = AndroidUri.Parse(tree.TreeUri)!;
        try
        {
            context.ContentResolver!.TakePersistableUriPermission(treeUri, ActivityFlags.GrantReadUriPermission);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Android.Storage", "Could not persist access to the selected folder.", exception);
            return null;
        }

        var identifier = await platform.NormalizePathAsync(path.AbsoluteUri).ConfigureAwait(false);
        var displayName = string.IsNullOrWhiteSpace(folder.Name)
            ? GetDisplayName(identifier) ?? identifier
            : folder.Name;

        return new LibraryFolderReference(identifier, displayName);
    }

    /// <summary>
    /// Offers external storage, which the granular media permissions make readable by plain
    /// filesystem path, with no Storage Access Framework grant required.
    /// </summary>
    public IReadOnlyList<string> GetDefaultMusicFolderPaths() => GetExternalStoragePaths();

    /// <summary>Video lives alongside music on external storage, so the same root is offered.</summary>
    public IReadOnlyList<string> GetDefaultVideoFolderPaths() => GetExternalStoragePaths();

    private static IReadOnlyList<string> GetExternalStoragePaths()
    {
        var externalStorage = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
        return string.IsNullOrEmpty(externalStorage) ? [] : [externalStorage];
    }

    public string? GetDisplayName(string identifier)
    {
        if (identifier.StartsWith('/'))
        {
            return Path.GetFileName(identifier.TrimEnd('/'));
        }

        try
        {
            using var documentUri = platform.ToDocumentUri(identifier);
            if (documentUri is null)
            {
                return null;
            }

            using var cursor = context.ContentResolver!.Query(
                documentUri,
                new[] { DocumentsContract.Document.ColumnDisplayName },
                null,
                null,
                null);
            if (cursor is not null && cursor.MoveToFirst())
            {
                var name = cursor.GetString(0);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch (Exception exception)
        {
            AppLog.Warning("Android.Storage", $"Could not read the folder name for '{identifier}'.", exception);
        }

        return null;
    }
}
