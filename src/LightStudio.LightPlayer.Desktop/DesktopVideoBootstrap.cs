using System;
using Avalonia;
using LightMediaRenderer.Contracts;
using LightStudio.LightPlayer.Services.Playback;
using LightMediaRenderer.Avalonia.Rendering;
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
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 512 * 1024 * 1024 })
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

#if LMR_AVALONIA_FORK
        WaylandColorMode colorMode = enableHdrPassthrough ? WaylandColorMode.ExtendedLinear : WaylandColorMode.Standard;
        // Only reached where the compositor reports no image description of its own; KWin and
        // Mutter both show an untagged surface through a pure 2.2 power law.
        VideoSurfaceTransfers.StandardRange = VideoSurfaceTransfer.Gamma22;
        app.With(new WaylandPlatformOptions
        {
            ColorMode = colorMode,
            HdrPresentationPreferences = colorMode == WaylandColorMode.ExtendedLinear
                ? [
                    WaylandHdrPresentationMode.LinearRelative, WaylandHdrPresentationMode.PqRelative,
                    // WaylandHdrPresentationMode.LinearPerceptual, WaylandHdrPresentationMode.PqPerceptual,
                    WaylandHdrPresentationMode.Sdr
                ]
                : null,
        })
            .UseWayland();
        VideoOutputDiagnostics.RecordRequest(colorMode == WaylandColorMode.ExtendedLinear
            ? "Wayland extended linear, relative preferred"
            : $"Wayland {colorMode}");
    #else
        app.UseWayland();
        VideoOutputDiagnostics.RecordRequest("Wayland standard color");
    #endif
    }
#endif

    public static bool IsWaylandSession() =>
        string.Equals(
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            "wayland",
            StringComparison.OrdinalIgnoreCase);
}