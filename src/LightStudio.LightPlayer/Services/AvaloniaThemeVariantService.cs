using Avalonia;
using Avalonia.Styling;

namespace LightStudio.LightPlayer.Services;

public sealed class AvaloniaThemeVariantService : IThemeVariantService
{
    public void Apply(AppThemePreference preference)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = preference switch
        {
            AppThemePreference.Light => ThemeVariant.Light,
            AppThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}