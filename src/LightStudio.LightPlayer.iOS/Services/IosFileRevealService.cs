using System.IO;
using System.Threading.Tasks;
using Foundation;
using LightStudio.LightPlayer.Services;
using UIKit;

namespace LightStudio.LightPlayer.iOS.Services;

/// <summary>Shows the folder holding a file in the Files app, which opens folders given as <c>shareddocuments</c> URLs.</summary>
internal sealed class IosFileRevealService : IFileRevealService
{
    public async Task RevealAsync(string filePath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (string.IsNullOrEmpty(folder))
        {
            throw new FileRevealException(Strings.Resources.Shell_OpenFolderUnknownLocation);
        }

        if (!Directory.Exists(folder))
        {
            throw new FileRevealException(string.Format(Strings.Resources.Shell_OpenFolderMissingFormat, folder));
        }

        using var components = new NSUrlComponents(NSUrl.FromFilename(folder), false) { Scheme = "shareddocuments" };
        using var url = components.Url;
        if (url is null || !await UIApplication.SharedApplication.OpenUrlAsync(url, new UIApplicationOpenUrlOptions()))
        {
            throw new FileRevealException(Strings.Resources.Shell_OpenFolderRefused);
        }
    }
}
