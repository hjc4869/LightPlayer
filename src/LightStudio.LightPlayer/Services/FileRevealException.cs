using System;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Raised by an <see cref="IFileRevealService"/> when a file's folder could not be shown. The
/// message is user-facing (already localized) and is surfaced in a dialog by the shell.
/// </summary>
public sealed class FileRevealException : Exception
{
    public FileRevealException(string message)
        : base(message)
    {
    }

    public FileRevealException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
