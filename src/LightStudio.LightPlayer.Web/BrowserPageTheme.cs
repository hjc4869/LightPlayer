using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Reactive;
using Avalonia.Styling;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
internal static partial class BrowserPageTheme
{
    private static IDisposable? backgroundSubscription;
    private static IDisposable? modeSubscription;

    public static void Register()
    {
        if (Application.Current is not { ApplicationLifetime: ISingleViewApplicationLifetime { MainView: { } view } } app)
        {
            throw new InvalidOperationException("The browser application view is unavailable.");
        }

        backgroundSubscription?.Dispose();
        modeSubscription?.Dispose();
        backgroundSubscription = view.GetResourceObservable("AppPageBackgroundBrush")
            .Subscribe(new AnonymousObserver<object?>(_ => Synchronize(app)));
        modeSubscription = app.GetObservable(Application.RequestedThemeVariantProperty)
            .Subscribe(new AnonymousObserver<ThemeVariant?>(_ => Synchronize(app)));
    }

    private static void Synchronize(Application app)
    {
        var mode = app.RequestedThemeVariant == ThemeVariant.Dark ? "dark"
            : app.RequestedThemeVariant == ThemeVariant.Light ? "light" : "system";
        Update(mode, Background(app, ThemeVariant.Light, ColorThemeVariant.Light),
            Background(app, ThemeVariant.Dark, ColorThemeVariant.Dark));
    }

    private static string Background(Application app, ThemeVariant variant, ColorThemeVariant colorVariant)
    {
        var color = app.TryGetResource("AppPageBackgroundBrush", variant, out var resource) && resource is ISolidColorBrush brush
            ? brush.Color
            : BuiltInColorThemes.Default(colorVariant).Get(AppColorRole.PageBackground);
        return $"#{color.R:x2}{color.G:x2}{color.B:x2}";
    }

    [JSImport("globalThis.lightPlayerPageTheme.update")]
    private static partial void Update(string mode, string lightColor, string darkColor);
}