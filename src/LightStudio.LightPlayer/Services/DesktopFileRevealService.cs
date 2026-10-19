using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Default <see cref="IFileRevealService"/> for heads whose media paths are real filesystem paths.
/// ShellExecute hands the directory to the OS file manager (Explorer / Finder / xdg-open) without
/// per-platform branching.
/// </summary>
public sealed class DesktopFileRevealService : IFileRevealService
{
    public Task RevealAsync(string filePath)
    {
        string? folder;
        try
        {
            folder = Path.GetDirectoryName(filePath);
        }
        catch (ArgumentException)
        {
            folder = null;
        }

        if (string.IsNullOrEmpty(folder))
        {
            throw new FileRevealException(Strings.Resources.Shell_OpenFolderUnknownLocation);
        }

        if (!Directory.Exists(folder))
        {
            throw new FileRevealException(
                string.Format(Strings.Resources.Shell_OpenFolderMissingFormat, folder));
        }

        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new FileRevealException(ex.GetBaseException().Message, ex);
        }

        return Task.CompletedTask;
    }
}
