using System;
using System.IO;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using Windows.Storage;
using Windows.System;

namespace LightStudio.LightPlayer.Desktop.Windows;

/// <summary>
/// Windows implementation of <see cref="IFileRevealService"/>. Rather than shell-executing the
/// directory (which only opens the folder), the shell launcher is asked to open the folder with the
/// track already selected, matching Explorer's "Show in folder" behavior.
/// </summary>
internal sealed class WindowsFileRevealService : IFileRevealService
{
    public async Task RevealAsync(string filePath)
    {
        string? folderPath;
        try
        {
            folderPath = Path.GetDirectoryName(filePath);
        }
        catch (ArgumentException)
        {
            folderPath = null;
        }

        if (string.IsNullOrEmpty(folderPath))
        {
            throw new FileRevealException(Strings.Resources.Shell_OpenFolderUnknownLocation);
        }

        if (!Directory.Exists(folderPath))
        {
            throw new FileRevealException(
                string.Format(Strings.Resources.Shell_OpenFolderMissingFormat, folderPath));
        }

        bool launched;
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            var options = new FolderLauncherOptions();

            // Selecting the track is best effort: a file that has since been moved or deleted still
            // gets its folder opened.
            if (File.Exists(filePath))
            {
                options.ItemsToSelect.Add(await StorageFile.GetFileFromPathAsync(filePath));
            }

            launched = await Launcher.LaunchFolderAsync(folder, options);
        }
        catch (Exception ex)
        {
            throw new FileRevealException(ex.GetBaseException().Message, ex);
        }

        if (!launched)
        {
            throw new FileRevealException(Strings.Resources.Shell_OpenFolderRefused);
        }
    }
}
