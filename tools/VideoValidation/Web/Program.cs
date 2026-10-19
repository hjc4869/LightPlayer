using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Avalonia.Browser;
using LightStudio.LightPlayer.Web;
using LightStudio.Logging;

namespace LightStudio.VideoValidation.Web;

[SupportedOSPlatform("browser")]
internal static class Program
{
    private const string OutputDirectory = "/artifacts/video-validation";

    private static async Task Main(string[] args)
    {
        AppLog.ConfigureConsole(AppLogLevel.Debug);
        await JSHost.ImportAsync("ValidationBridge", "/validation-bridge.js");

        try
        {
            BrowserValidationInterop.SetStatus("initializing", "Starting validation runtime");
            BrowserValidationConfiguration configuration = BrowserValidationInterop.GetConfiguration();
            await BrowserVideoBootstrap.RegisterVideoPipelineAsync();
            await JSHost.ImportAsync("WebAudio", "/web-audio.js");
            WebAudioPlaybackBackend.Register();

            ValidationWebApplication.LaunchOptions = new ValidationLaunchOptions
            {
                OutputDirectory = OutputDirectory,
                AutoRun = configuration.AutoRun,
                HardwareDeviceType = "webcodecs",
                CaseIds = configuration.CaseIds,
                PlatformDetails = BrowserValidationInterop.GetInventory(),
            };
            ValidationWebApplication.CorpusBaseUri = new Uri(configuration.CorpusBaseUri);

            await BrowserVideoBootstrap
                .BuildAvaloniaApp<ValidationWebApplication>()
                .StartBrowserAppAsync("out");
        }
        catch (Exception exception)
        {
            AppLog.Critical("Validation.Web.Startup", "Web validation could not start.", exception);
            BrowserValidationInterop.Fail(exception.ToString());
            throw;
        }
    }
}