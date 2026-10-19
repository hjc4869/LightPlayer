using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Desktop.Windows;

/// <summary>
/// Selects the package-private Windows app-data roots when this process has package identity.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal static partial class WindowsApplicationDataPaths
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    /// <summary>
    /// Configures packaged storage and returns true for an MSIX process. An unpackaged process
    /// keeps the regular desktop paths already registered by <see cref="PlatformServices"/>.
    /// </summary>
    public static bool TryConfigure()
    {
        uint packageFullNameLength = 0;
        var result = GetCurrentPackageFullName(ref packageFullNameLength, 0);
        if (result == AppModelErrorNoPackage)
        {
            return false;
        }

        if (result != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(result, "Could not determine the current Windows package identity.");
        }

        var applicationData = global::Windows.Storage.ApplicationData.Current;
        PlatformServices.AppDataPath = applicationData.LocalFolder.Path;
        PlatformServices.CachePath = applicationData.LocalCacheFolder.Path;
        return true;
    }

    [LibraryImport("kernel32")]
    private static partial int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        nint packageFullName);
}