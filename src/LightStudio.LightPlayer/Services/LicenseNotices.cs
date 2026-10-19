using System;
using System.IO;
using System.Reflection;
using System.Text;
using LightStudio.FfmpegShim;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Loads the LICENSE and THIRDPARTY.txt files embedded in this assembly, appends the FFmpeg license
/// and component listing reported by the native libraries, and combines them into a single block of
/// text for the About screens (the desktop <c>AboutDialog</c> window and the mobile in-shell
/// overlay), so both surfaces show identical notices.
/// </summary>
internal static class LicenseNotices
{
    private const string LicenseResource = "LightStudio.LightPlayer.Resource.LICENSE.txt";
    private const string ThirdPartyResource = "LightStudio.LightPlayer.Resource.THIRDPARTY.txt";

    /// <summary>Returns the embedded LICENSE followed by the third-party notices, or an empty string.</summary>
    public static string LoadCombinedText()
    {
        var assembly = typeof(LicenseNotices).Assembly;
        var license = ReadResource(assembly, LicenseResource);
        var thirdParty = ReadResource(assembly, ThirdPartyResource);

        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(license))
        {
            builder.Append(license.TrimEnd());
        }

        if (!string.IsNullOrEmpty(thirdParty))
        {
            if (builder.Length > 0)
            {
                builder.Append("\n\n\n");
            }

            builder.Append(thirdParty.TrimEnd());
        }

        if (builder.Length > 0)
        {
            builder.Append("\n\n\n");
        }

        builder.Append(LoadFfmpegNotice());

        return builder.ToString();
    }

    /// <summary>Uses the platform notice or queries native FFmpeg on non-browser platforms.</summary>
    private static string LoadFfmpegNotice()
    {
        try
        {
            if (OperatingSystem.IsBrowser())
                return PlatformServices.FfmpegLicenseNotice?.TrimEnd() ?? "Failed to load FFmpeg license.";

            return FfmpegLicenseHelper.BuildNoticeText().TrimEnd();
        }
        catch (Exception exception)
        {
            AppLog.Warning("LicenseNotices", "Failed to load FFmpeg license.", exception);
            return "Failed to load FFmpeg license.";
        }
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return string.Empty;
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
