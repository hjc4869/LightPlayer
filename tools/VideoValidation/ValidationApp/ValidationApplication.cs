using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace LightStudio.VideoValidation;

public sealed class ValidationApplication : Application
{
    public static ValidationLaunchOptions LaunchOptions { get; set; } = new();

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Default;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new ValidationWindow(LaunchOptions);
        base.OnFrameworkInitializationCompleted();
    }
}