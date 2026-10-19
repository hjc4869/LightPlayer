using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Default <see cref="IFileShareService"/> for heads whose media paths are real filesystem paths.
/// ShellExecute hands the file to whatever the desktop has registered for it, without per-platform
/// branching.
/// </summary>
/// <remarks>
/// There is no share sheet on a desktop, and the "open with" chooser is the shell's own business:
/// Windows offers one on demand, and the freedesktop convention is that <c>xdg-open</c> picks. So
/// this asks for the file to be opened and leaves the choice to the platform.
/// </remarks>
public sealed class DesktopFileShareService : IFileShareService
{
    public bool CanShare => false;

    public Task OpenWithAsync(string filePath, string? mimeType)
    {
        if (!File.Exists(filePath))
        {
            throw new FileShareException(
                string.Format(Strings.Resources.Shell_OpenFolderMissingFormat, filePath));
        }

        try
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new FileShareException(ex.GetBaseException().Message, ex);
        }

        return Task.CompletedTask;
    }

    public Task ShareAsync(string filePath, string? mimeType) =>
        throw new FileShareException(Strings.Resources.Photo_ShareUnsupported);
}
