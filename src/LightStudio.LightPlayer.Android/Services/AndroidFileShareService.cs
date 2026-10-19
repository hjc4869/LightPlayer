#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Android.Content;
using AndroidX.Core.Content;
using LightStudio.LightPlayer.Services;
using AndroidUri = Android.Net.Uri;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Android.Services;

/// <summary>
/// Android implementation of <see cref="IFileShareService"/>. Both actions are intents shown
/// through a chooser, so the user picks the application rather than the system silently deciding.
/// </summary>
/// <remarks>
/// A file may only be handed to another app as a content URI. An existing content URI, such as a
/// picture opened through the Storage Access Framework, is passed on with a read grant attached;
/// a filesystem path is turned into one by the app's own <c>FileProvider</c>, declared in the
/// manifest against <c>Resources/xml/file_paths.xml</c>.
/// </remarks>
internal sealed class AndroidFileShareService(Context context, AndroidMultimediaPlatformIO platform) : IFileShareService
{
    private const string ProviderAuthority = "im.hjc.LightPlayer.fileprovider";

    private readonly Context context = context.ApplicationContext ?? context;

    public bool CanShare => true;

    public Task OpenWithAsync(string filePath, string? mimeType) =>
        LaunchAsync(filePath, mimeType, share: false);

    public Task ShareAsync(string filePath, string? mimeType) =>
        LaunchAsync(filePath, mimeType, share: true);

    private async Task LaunchAsync(string filePath, string? mimeType, bool share)
    {
        using AndroidUri uri = await Task.Run(() => ToContentUri(filePath));
        string type = string.IsNullOrEmpty(mimeType) ? "*/*" : mimeType;

        using var intent = new Intent(share ? Intent.ActionSend : Intent.ActionView);
        if (share)
        {
            intent.SetType(type);
            intent.PutExtra(Intent.ExtraStream, uri);
        }
        else
        {
            intent.SetDataAndType(uri, type);
        }

        intent.AddFlags(ActivityFlags.GrantReadUriPermission);

        string title = share ? Strings.Resources.Photo_Share : Strings.Resources.Photo_OpenWith;
        using Intent? chooser = Intent.CreateChooser(intent, title);
        if (chooser is null)
        {
            throw new FileShareException(Strings.Resources.Shell_OpenFolderNoApp);
        }

        chooser.AddFlags(ActivityFlags.NewTask);

        try
        {
            context.StartActivity(chooser);
        }
        catch (ActivityNotFoundException)
        {
            throw new FileShareException(Strings.Resources.Shell_OpenFolderNoApp);
        }
        catch (Exception ex)
        {
            throw new FileShareException(ex.GetBaseException().Message, ex);
        }
    }

    private AndroidUri ToContentUri(string filePath)
    {
        if (!filePath.StartsWith('/'))
        {
            return platform.ToDocumentUri(filePath)
                ?? throw new FileShareException(Strings.Resources.Shell_OpenFolderUnknownLocation);
        }

        try
        {
            return FileProvider.GetUriForFile(context, ProviderAuthority, new Java.IO.File(filePath))
                ?? throw new FileShareException(Strings.Resources.Shell_OpenFolderUnknownLocation);
        }
        catch (FileShareException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The provider refuses a path none of its declared roots covers, which is the only way
            // a library folder outside shared storage can end up here.
            throw new FileShareException(
                string.Format(Strings.Resources.Shell_OpenFolderMissingFormat, Path.GetFileName(filePath)),
                ex);
        }
    }
}
