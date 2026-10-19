using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace LightStudio.LightPlayer.Strings;

/// <summary>
/// Implemented by objects that expose localized text and therefore have to refresh it
/// when the interface language changes. Hosts are tracked weakly, so they never need to
/// unsubscribe and are free to be collected while the app is running.
/// </summary>
public interface ILocalizedTextHost
{
    /// <summary>
    /// Called after the culture has been switched, on the thread that switched it
    /// (the UI thread for user-initiated changes).
    /// </summary>
    void OnLocalizedTextChanged();
}

/// <summary>
/// Observable facade over <see cref="Resources"/> so XAML bindings pick up a new
/// interface language without restarting the app. Bind through <see cref="Instance"/>.
/// </summary>
public sealed partial class LocalizedStrings : INotifyPropertyChanged
{
    private static readonly CultureInfo SystemCulture = CultureInfo.InstalledUICulture;
    private static readonly PropertyChangedEventArgs AllPropertiesChanged = new(null);
    private static readonly List<WeakReference<ILocalizedTextHost>> Hosts = [];

    private LocalizedStrings()
    {
    }

    /// <summary>Shared instance every binding listens to.</summary>
    public static LocalizedStrings Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Looks up a key that has no generated property (or is only known at runtime).</summary>
    public string this[string key] =>
        Resources.ResourceManager.GetString(key, Resources.Culture) ?? key;

    /// <summary>
    /// Switches the culture used by every resource lookup and refreshes all bindings.
    /// Pass an empty tag to follow the operating system.
    /// </summary>
    public static void ApplyLanguage(string? languageTag)
    {
        CultureInfo culture;
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            culture = SystemCulture;
        }
        else
        {
            try
            {
                culture = new CultureInfo(languageTag);
            }
            catch (CultureNotFoundException)
            {
                culture = SystemCulture;
            }
        }

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Resources.Culture = culture;
        Instance.Refresh();
    }

    /// <summary>Refreshes string bindings and the localized text held by registered hosts.</summary>
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, AllPropertiesChanged);
        NotifyHosts();
    }

    /// <summary>
    /// Tracks <paramref name="host"/> weakly so it is told to re-read its localized text
    /// whenever the interface language changes.
    /// </summary>
    public static void Register(ILocalizedTextHost host)
    {
        lock (Hosts)
        {
            Hosts.Add(new WeakReference<ILocalizedTextHost>(host));
        }
    }

    /// <summary>Notifies every live host and drops the entries that have been collected.</summary>
    private static void NotifyHosts()
    {
        List<ILocalizedTextHost> alive;
        lock (Hosts)
        {
            alive = new List<ILocalizedTextHost>(Hosts.Count);
            for (var i = Hosts.Count - 1; i >= 0; i--)
            {
                if (Hosts[i].TryGetTarget(out var host))
                {
                    alive.Add(host);
                }
                else
                {
                    Hosts.RemoveAt(i);
                }
            }
        }

        foreach (var host in alive)
        {
            host.OnLocalizedTextChanged();
        }
    }
}
