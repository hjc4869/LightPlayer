using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Services;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed class WaylandModeOption : ViewModelBase
{
    private readonly string titleKey;

    public WaylandModeOption(WaylandMode mode, string titleKey)
    {
        Mode = mode;
        this.titleKey = titleKey;
    }

    public WaylandMode Mode { get; }

    public string Title => Localized(titleKey);
}

/// <summary>
/// Windowing backend choice for the Linux desktop head. Shown only where it applies - see
/// <see cref="PlatformServices.SupportsWaylandSettings"/> - and read on the next launch, since
/// the backend is picked before the Avalonia app starts.
/// </summary>
public partial class WaylandPreferenceViewModel : ViewModelBase
{
    private readonly IAppSettingsStore settingsStore;
    private bool isLoaded;

    [ObservableProperty]
    private WaylandModeOption selectedMode = null!;

    public WaylandPreferenceViewModel(IAppSettingsStore settingsStore)
    {
        this.settingsStore = settingsStore;

        ModeOptions =
        [
            new WaylandModeOption(WaylandMode.Auto, nameof(Strings.Resources.WaylandSettings_ModeAuto)),
            new WaylandModeOption(WaylandMode.Off, nameof(Strings.Resources.WaylandSettings_ModeOff)),
        ];

        selectedMode = FindOption(settingsStore.WaylandMode);
        isLoaded = true;
    }

    public IReadOnlyList<WaylandModeOption> ModeOptions { get; }

    partial void OnSelectedModeChanged(WaylandModeOption value)
    {
        if (!isLoaded)
        {
            return;
        }

        settingsStore.WaylandMode = value.Mode;
    }

    private WaylandModeOption FindOption(WaylandMode mode)
    {
        foreach (var option in ModeOptions)
        {
            if (option.Mode == mode)
            {
                return option;
            }
        }

        return ModeOptions[0];
    }
}
