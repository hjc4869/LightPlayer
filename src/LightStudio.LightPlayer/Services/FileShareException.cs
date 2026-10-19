using System;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Raised by an <see cref="IFileShareService"/> when a file could not be handed to another app.
/// The message is user-facing (already localized) and is surfaced by the caller.
/// </summary>
public sealed class FileShareException : Exception
{
    public FileShareException(string message)
        : base(message)
    {
    }

    public FileShareException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
