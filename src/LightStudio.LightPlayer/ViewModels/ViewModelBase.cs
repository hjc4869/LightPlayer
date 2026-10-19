using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels;

/// <summary>
/// Base for every view model. Registers with <see cref="LocalizedStrings"/> so that
/// localized text the view model resolves itself (rather than through a XAML binding)
/// is re-read when the interface language changes.
/// </summary>
public class ViewModelBase : ObservableObject, ILocalizedTextHost
{
    protected ViewModelBase()
    {
        LocalizedStrings.Register(this);
    }

    /// <summary>
    /// Re-raises every property so bindings pull the current language again. Only
    /// properties whose getters resolve the resource on each read actually change, so
    /// prefer <c>=> Localized(nameof(Resources.Key))</c> getters over strings captured
    /// in a constructor.
    /// </summary>
    public virtual void OnLocalizedTextChanged() => OnPropertyChanged((string?)null);

    /// <summary>
    /// Resolves a resource key (obtained with <c>nameof(Resources.Key)</c>) against the
    /// current language, falling back to <paramref name="fallback"/> when no key is set.
    /// </summary>
    protected static string Localized(string? resourceKey, string fallback = "") =>
        resourceKey is null ? fallback : LocalizedStrings.Instance[resourceKey];
}
