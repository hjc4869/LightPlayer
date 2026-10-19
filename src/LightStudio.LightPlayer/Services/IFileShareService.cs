using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Platform hook for handing a file to another application: opening it with whatever else on the
/// device can read it, and -- where the platform has such a thing at all -- offering it to the
/// system share sheet.
/// </summary>
/// <remarks>
/// A desktop has no share sheet, so <see cref="CanShare"/> is false there and only "Open with"
/// is offered; Android registers an implementation that goes through an intent chooser for both.
/// The path handed in is always one this device can read, so a picture that lives on a remote
/// library has already been written to a local file by the caller.
/// </remarks>
public interface IFileShareService
{
    /// <summary>Whether this platform has a share sheet to offer the file to.</summary>
    bool CanShare { get; }

    /// <summary>
    /// Opens the file with another application, letting the user pick one where the platform
    /// offers a choice. Throws <see cref="FileShareException"/> when nothing could open it.
    /// </summary>
    Task OpenWithAsync(string filePath, string? mimeType);

    /// <summary>Offers the file to the platform's share sheet.</summary>
    Task ShareAsync(string filePath, string? mimeType);
}
