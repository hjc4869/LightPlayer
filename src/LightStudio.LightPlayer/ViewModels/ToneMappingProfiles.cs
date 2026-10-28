using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.ViewModels.Pages;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels;

/// <summary>Choice lists and name rules shared by the places that pick or create tone mapping profiles.</summary>
internal static class ToneMappingProfileChoices
{
    /// <summary>The built-in choice followed by each profile; an empty value stands for the built-in one.</summary>
    public static IReadOnlyList<VideoSettingsOption> Create(ToneMappingSettings settings, bool includeBuiltIn)
    {
        var choices = new List<VideoSettingsOption>();
        if (includeBuiltIn)
            choices.Add(new VideoSettingsOption(string.Empty, "Built-in", nameof(Strings.Resources.ToneMapping_BuiltIn)));
        foreach (ToneMappingProfile profile in settings.Profiles)
            choices.Add(new VideoSettingsOption(profile.Name, profile.Name));
        return choices;
    }

    public static VideoSettingsOption? Find(IReadOnlyList<VideoSettingsOption> choices, string? name) =>
        choices.FirstOrDefault(choice => ToneMappingSettings.NameComparer.Equals(choice.Value, name ?? string.Empty));

    public static string? ProfileName(VideoSettingsOption? choice) =>
        string.IsNullOrEmpty(choice?.Value) ? null : choice.Value;

    public static string NewName(ToneMappingSettings settings)
    {
        string format = Strings.LocalizedStrings.Instance[nameof(Strings.Resources.ToneMapping_NewProfileFormat)];
        for (int number = settings.Profiles.Count + 1; ; number++)
        {
            string name = string.Format(CultureInfo.CurrentCulture, format, number);
            if (settings.Find(name) is null)
                return name;
        }
    }

    /// <summary>Returns the reason <paramref name="name"/> cannot name a profile, or null when it can.</summary>
    public static string? Validate(ToneMappingSettings settings, string name, string? currentName = null)
    {
        if (name.Length == 0)
            return Strings.LocalizedStrings.Instance[nameof(Strings.Resources.ToneMapping_NameRequired)];
        return settings.Find(name) is { } existing && !ToneMappingSettings.NameComparer.Equals(existing.Name, currentName)
            ? Strings.LocalizedStrings.Instance[nameof(Strings.Resources.ToneMapping_NameExists)]
            : null;
    }
}

/// <summary>Persists tone mapping settings one write at a time, keeping only the latest pending value.</summary>
internal sealed class ToneMappingSettingsWriter(IAppSettingsStore settingsStore)
{
    private ToneMappingSettings? pending;
    private Task running = Task.CompletedTask;

    public Task WriteAsync(ToneMappingSettings settings)
    {
        pending = settings;
        if (running.IsCompleted)
            running = RunAsync();
        return running;
    }

    private async Task RunAsync()
    {
        while (pending is { } next)
        {
            pending = null;
            await settingsStore.SetToneMappingAsync(next);
        }
    }
}
