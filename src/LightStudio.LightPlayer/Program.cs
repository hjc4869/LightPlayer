using Avalonia;
using System;
using System.Globalization;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var activationPaths = FileActivation.ExtractPaths(args);

        // Enforce a single instance and route "open file" launches to it. Under
        // Flatpak (and file-manager "Open with") every activation spawns a fresh
        // process; a secondary forwards its files to the running primary and exits,
        // so the user keeps one window whose queue is replaced by the opened files.
        var singleInstance = SingleInstanceServiceFactory.Create();
        if (!singleInstance.TryActivate(activationPaths))
        {
            singleInstance.Dispose();
            return;
        }

        App.SingleInstance = singleInstance;
        ApplyInterfaceLanguage();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void ApplyInterfaceLanguage()
    {
        try
        {
            var language = new JsonAppSettingsStore().InterfaceLanguage;
            if (string.IsNullOrWhiteSpace(language))
            {
                return;
            }

            var culture = new CultureInfo(language);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // Fall back to the operating system culture.
        }
    }
}
