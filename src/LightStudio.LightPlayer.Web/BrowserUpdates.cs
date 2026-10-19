using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
internal sealed partial class BrowserUpdates : IBrowserUpdateService
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool AutoCheckEnabled
    {
        get => IsAutoCheckEnabled();
        set => SetAutoCheckEnabled(value);
    }

    public static async Task RegisterAsync()
    {
        await BrowserAssets.ImportAsync("BrowserUpdates", "browser-updates.js");
        Configure(CultureInfo.CurrentUICulture.Name);
        Strings.LocalizedStrings.Instance.PropertyChanged += (sender, arguments) => Configure(CultureInfo.CurrentUICulture.Name);
        var updates = new BrowserUpdates();
        PlatformServices.BrowserUpdates = updates;
        _ = updates.ListenAsync();
    }

    public Task CheckForUpdatesAsync() => CheckAsync();

    private async Task ListenAsync()
    {
        try
        {
            while (true)
            {
                await WaitForChangeAsync();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoCheckEnabled)));
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("Web.Updates", "The browser update preference listener stopped.", exception);
        }
    }

    [JSImport("configure", "BrowserUpdates")]
    private static partial void Configure(string culture);

    [JSImport("autoCheckEnabled", "BrowserUpdates")]
    private static partial bool IsAutoCheckEnabled();

    [JSImport("setAutoCheckEnabled", "BrowserUpdates")]
    private static partial void SetAutoCheckEnabled(bool enabled);

    [JSImport("checkForUpdates", "BrowserUpdates")]
    private static partial Task CheckAsync();

    [JSImport("waitForChange", "BrowserUpdates")]
    private static partial Task WaitForChangeAsync();
}