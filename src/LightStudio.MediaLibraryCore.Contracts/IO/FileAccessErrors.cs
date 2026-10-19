#nullable enable

using System;
using System.IO;

namespace LightStudio.MediaLibraryCore.IO;

public static class FileAccessErrors
{
    public static bool IsAccessDenied(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException ||
                current is IOException && (current.HResult == unchecked((int)0x80070005) ||
                    !OperatingSystem.IsWindows() && current.HResult is 1 or 13))
            {
                return true;
            }
        }
        return false;
    }
}