using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Platform hook for the "Open containing folder" action: shows the folder a media file lives in
/// using the host's file browser. Desktops can shell-execute the directory path, but platforms
/// whose media paths are not filesystem paths (Android content URIs) need their own implementation,
/// which the head registers through <see cref="PlatformServices.FileRevealService"/>.
/// </summary>
public interface IFileRevealService
{
    /// <summary>
    /// Reveals the folder containing <paramref name="filePath"/>. Throws
    /// <see cref="FileRevealException"/> when the platform could not open it, so the caller can
    /// show the reason to the user instead of failing silently.
    /// </summary>
    Task RevealAsync(string filePath);
}
