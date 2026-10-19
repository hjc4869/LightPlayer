using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace LightStudio.VideoValidation.Web;

internal sealed class ValidationWebApplication : Application
{
    public static ValidationLaunchOptions LaunchOptions { get; set; } = new();

    public static Uri CorpusBaseUri { get; set; } = new("http://localhost/corpus/");

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Default;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime browser)
            browser.MainView = new BrowserValidationView(LaunchOptions, CorpusBaseUri);
        base.OnFrameworkInitializationCompleted();
    }
}