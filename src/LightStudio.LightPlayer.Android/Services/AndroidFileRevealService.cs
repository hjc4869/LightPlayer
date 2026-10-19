#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Android.Content;
using Android.Provider;
using LightStudio.LightPlayer.Services;
using AndroidUri = Android.Net.Uri;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Android.Services;

/// <summary>
/// Resolves library paths through <see cref="AndroidMultimediaPlatformIO"/> and opens their parent
/// document with an <c>ACTION_VIEW</c> directory intent. Filesystem paths use the external-storage provider.
/// </summary>
internal sealed class AndroidFileRevealService(Context context, AndroidMultimediaPlatformIO platform) : IFileRevealService
{
    // Documents provider that exposes the shared storage volumes reachable by filesystem path.
    private const string ExternalStorageAuthority = "com.android.externalstorage.documents";

    private readonly Context context = context.ApplicationContext ?? context;

    public async Task RevealAsync(string filePath)
    {
        filePath = await Task.Run(() => platform.GetMediaStoreFilePath(filePath)) ?? filePath;
        var isSafPath = !filePath.StartsWith('/');
        var folderUri = isSafPath
            ? await Task.Run(() => GetTreeParentDocumentUri(filePath))
            : GetExternalStorageFolderUri(filePath);

        if (folderUri is null)
        {
            throw new FileRevealException(Strings.Resources.Shell_OpenFolderUnknownLocation);
        }

        using var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(folderUri, DocumentsContract.Document.MimeTypeDir);
        intent.AddFlags(ActivityFlags.NewTask);

        // A read grant can only be passed on for a URI this app itself holds one for: the persisted
        // SAF tree. Flagging the documents-provider URI built from a filesystem path (which the app
        // reads through the media permissions instead) makes the system reject the intent.
        if (isSafPath)
        {
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        }

        try
        {
            context.StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            throw new FileRevealException(Strings.Resources.Shell_OpenFolderNoApp);
        }
        catch (Exception ex)
        {
            throw new FileRevealException(ex.GetBaseException().Message, ex);
        }
    }

    private AndroidUri? GetTreeParentDocumentUri(string filePath)
    {
        var parent = platform.GetParentPath(filePath);
        return parent is null ? null : platform.ToDocumentUri(parent);
    }

    /// <summary>
    /// Maps a shared-storage file path to its containing folder in the external storage documents
    /// provider, whose document ids are "&lt;volume&gt;:&lt;path relative to the volume root&gt;".
    /// </summary>
    private static AndroidUri? GetExternalStorageFolderUri(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var documentId = BuildExternalStorageDocumentId(directory);
        if (documentId is null)
        {
            return null;
        }

        try
        {
            return DocumentsContract.BuildDocumentUri(ExternalStorageAuthority, documentId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? BuildExternalStorageDocumentId(string directory)
    {
        var primaryRoot = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
        if (!string.IsNullOrEmpty(primaryRoot) && TryGetRelativePath(directory, primaryRoot, out var relative))
        {
            return $"primary:{relative}";
        }

        // Removable volumes are mounted at /storage/<volume id>/..., which is also their document ID prefix.
        const string storageRoot = "/storage/";
        if (directory.StartsWith(storageRoot, StringComparison.Ordinal))
        {
            var rest = directory[storageRoot.Length..];
            var separator = rest.IndexOf('/');
            return separator > 0 ? $"{rest[..separator]}:{rest[(separator + 1)..]}" : null;
        }

        return null;
    }

    private static bool TryGetRelativePath(string path, string root, out string relative)
    {
        relative = string.Empty;
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            return false;
        }

        if (path.Length == root.Length)
        {
            return true;
        }

        if (path[root.Length] != '/')
        {
            return false;
        }

        relative = path[(root.Length + 1)..];
        return true;
    }
}
