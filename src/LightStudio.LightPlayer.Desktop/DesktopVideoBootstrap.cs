using System;
using Avalonia;
using LightMediaRenderer.Contracts;
using LightStudio.LightPlayer.Services.Playback;
using LightMediaRenderer.Avalonia.Rendering;
using LightStudio.Logging;
#if WINDOWS
using LightStudio.LightPlayer.Desktop.Windows;
#elif LINUX
using LightStudio.LightPlayer.Desktop.Linux;
#elif MACOS
using LightStudio.LightPlayer.Desktop.Apple;
#endif

namespace LightStudio.LightPlayer.Desktop;

/// <summary>Production desktop graphics and hardware-decoder setup shared by desktop hosts.</summary>
public static class DesktopVideoBootstrap
{
    public static void RegisterVideoPipeline()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            Direct3DSurfaceImporter.Register();
            HardwareDecoderProfiles.Register(new Direct3D11HardwareDecoderProfile());
        }
#elif LINUX
        if (OperatingSystem.IsLinux())
        {
            VaapiSurfaceImporter.Register();
            HardwareDecoderProfiles.Register(new VaapiHardwareDecoderProfile());
        }
#elif MACOS
        if (OperatingSystem.IsMacOS())
        {
            VideoToolboxSurfaceImporter.Register();
            VideoToolboxVideoScaler.Register();
        }
#endif
    }

    public static AppBuilder BuildAvaloniaApp<TApplication>(bool enableWayland = true)
        where TApplication : Application, new()
    {
        AppBuilder app = AppBuilder.Configure<TApplication>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions
            {
                RenderingMode = [X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software],
            })
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 256 * 1024 * 1024 })
            .WithInterFont()
            .LogToTrace();

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var options = new Win32PlatformOptions();
            EnableWindowsExtendedLinear(options);
            app.With(options);
        }
#elif LINUX
        if (OperatingSystem.IsLinux() && enableWayland && IsWaylandSession())
            EnableWayland(app);
        else
        {
            VideoOutputDiagnostics.RecordRequest(enableWayland
                ? "nothing (not Wayland)"
                : "nothing (Wayland disabled)");
        }
#else
        VideoOutputDiagnostics.RecordRequest("nothing");
#endif

        return app;
    }

#if WINDOWS
    internal static void EnableWindowsExtendedLinear(Win32PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

#if LMR_AVALONIA_FORK
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            options.ColorMode = Win32ColorMode.ExtendedLinear;
            VideoOutputDiagnostics.RecordRequest($"Win32 {Win32ColorMode.ExtendedLinear}");
            return;
        }
#endif
        VideoOutputDiagnostics.RecordRequest("Win32 standard color");
    }
#elif LINUX
    internal static void EnableWayland(AppBuilder app, bool enableHdrPassthrough = true)
    {
        ArgumentNullException.ThrowIfNull(app);
        bool forceClientSideDecorations = ReadBooleanEnvironmentVariable("LIGHTPLAYER_WAYLAND_FORCE_CSD");
        bool enablePerceptualHdr = ReadBooleanEnvironmentVariable("LIGHTPLAYER_WAYLAND_PERCEPTUAL_HDR");

#if LMR_AVALONIA_FORK
        // Only reached where the compositor reports no image description of its own; KWin and
        // Mutter both show an untagged surface through a pure 2.2 power law.
        VideoSurfaceTransfers.StandardRange = VideoSurfaceTransfer.Gamma22;
        WaylandColorPresentationMode[] preferences = !enableHdrPassthrough
            ? []
            : enablePerceptualHdr
                ? [
                    WaylandColorPresentationMode.LinearRelative, WaylandColorPresentationMode.LinearPerceptual,
                    WaylandColorPresentationMode.PqPerceptual, WaylandColorPresentationMode.WideColorGamut,
                    WaylandColorPresentationMode.Sdr
                ]
                : [
                    WaylandColorPresentationMode.LinearRelative, WaylandColorPresentationMode.WideColorGamut,
                    WaylandColorPresentationMode.Sdr
                ];
        var options = new WaylandPlatformOptions
        {
#pragma warning disable AVALONIA_WAYLAND_FORCE_CSD
            ForceDrawnDecorations = forceClientSideDecorations,
#pragma warning restore AVALONIA_WAYLAND_FORCE_CSD
            ColorPresentationPreferences = preferences,
        };
        app.With(options).UseWayland();
        VideoOutputDiagnostics.RecordRequest(preferences.Length == 0
            ? "Wayland standard color"
            : $"Wayland {string.Join(", ", preferences)}");
        if (enablePerceptualHdr && enableHdrPassthrough)
        {
            AppLog.Information("Desktop.Startup", "Enabling Wayland perceptual HDR presentation modes (LIGHTPLAYER_WAYLAND_PERCEPTUAL_HDR).");
        }
        if (forceClientSideDecorations)
        {
            AppLog.Information("Desktop.Startup", "Forcing Wayland client-side decorations (LIGHTPLAYER_WAYLAND_FORCE_CSD).");
        }
    #else
        if (forceClientSideDecorations)
        {
            AppLog.Warning("Desktop.Startup", "LIGHTPLAYER_WAYLAND_FORCE_CSD requires the Avalonia fork.");
        }
        if (enablePerceptualHdr)
        {
            AppLog.Warning("Desktop.Startup", "LIGHTPLAYER_WAYLAND_PERCEPTUAL_HDR requires the Avalonia fork.");
        }
        app.UseWayland();
        VideoOutputDiagnostics.RecordRequest("Wayland standard color");
    #endif
    }

    private static bool ReadBooleanEnvironmentVariable(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable)?.Trim().ToLowerInvariant();
        switch (value)
        {
            case "1":
            case "true":
                return true;
            case null:
            case "":
            case "0":
            case "false":
                return false;
            default:
                AppLog.Warning("Desktop.Startup", $"Invalid {variable} value '{value}'; expected 1, 0, true, or false. Leaving the override disabled.");
                return false;
        }
    }
#endif

    public static bool IsWaylandSession() =>
        string.Equals(
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            "wayland",
            StringComparison.OrdinalIgnoreCase);
}