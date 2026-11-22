using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>Turns the browser link listener on and off, sets its port, and lists the paired browsers.</summary>
public sealed partial class BrowserLinkSettingsSectionViewModel : SettingsSectionViewModel, IDisposable
{
    private readonly IAppSettingsStore settingsStore;
    private readonly IBrowserLinkController controller;
    private readonly bool isLoaded;

    [ObservableProperty]
    private bool enabled;

    [ObservableProperty]
    private decimal port;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPairedBrowsers))]
    private IReadOnlyList<BrowserLinkPairingRowViewModel> pairedBrowsers = [];

    public BrowserLinkSettingsSectionViewModel(IAppSettingsStore settingsStore, IBrowserLinkController controller)
        : base(nameof(Resources.BrowserLinkSettings_Title), nameof(Resources.BrowserLinkSettings_Subtitle))
    {
        this.settingsStore = settingsStore;
        this.controller = controller;
        BrowserLinkSettings settings = settingsStore.BrowserLinkSettings;
        enabled = settings.Enabled;
        port = settings.Port;
        LoadPairings(settings);
        ApplyCommand = new RelayCommand(Apply);
        settingsStore.BrowserLinkSettingsChanged += OnSettingsChanged;
        controller.StateChanged += OnStateChanged;
        LocalizedStrings.Instance.PropertyChanged += OnLanguageChanged;
        UpdateStatus();
        isLoaded = true;
    }

    public IRelayCommand ApplyCommand { get; }

    public bool HasPairedBrowsers => PairedBrowsers.Count > 0;

    public void Dispose()
    {
        settingsStore.BrowserLinkSettingsChanged -= OnSettingsChanged;
        controller.StateChanged -= OnStateChanged;
        LocalizedStrings.Instance.PropertyChanged -= OnLanguageChanged;
    }

    partial void OnEnabledChanged(bool value)
    {
        if (isLoaded)
            Apply();
    }

    private void Apply()
    {
        if (Port != decimal.Truncate(Port) || Port is < 1 or > 65535)
        {
            StatusText = Resources.BrowserLinkSettings_InvalidPort;
            return;
        }

        BrowserLinkSettings settings = settingsStore.BrowserLinkSettings with
        {
            Enabled = Enabled,
            Port = decimal.ToInt32(Port),
        };
        settingsStore.SetBrowserLinkSettings(settings);
        controller.Apply(settings);
        UpdateStatus();
    }

    internal void Remove(BrowserLinkPairing pairing)
    {
        BrowserLinkSettings current = settingsStore.BrowserLinkSettings;
        BrowserLinkSettings settings = current with
        {
            PairedBrowsers = current.PairedBrowsers.Where(existing => existing != pairing).ToArray(),
        };
        settingsStore.SetBrowserLinkSettings(settings);
        controller.Apply(settings);
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => LoadPairings(settingsStore.BrowserLinkSettings);

    private void OnStateChanged(object? sender, EventArgs e) => UpdateStatus();

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateStatus();
        LoadPairings(settingsStore.BrowserLinkSettings);
    }

    private void LoadPairings(BrowserLinkSettings settings) =>
        PairedBrowsers = settings.PairedBrowsers
            .OrderByDescending(pairing => pairing.CreatedAtUnixSeconds)
            .Select(pairing => new BrowserLinkPairingRowViewModel(this, pairing))
            .ToArray();

    private void UpdateStatus()
    {
        BrowserLinkState state = controller.State;
        StatusText = !string.IsNullOrEmpty(state.ErrorMessage)
            ? string.Format(CultureInfo.CurrentCulture, Resources.BrowserLinkSettings_FailedFormat, state.Port, state.ErrorMessage)
            : state.IsRunning
                ? string.Format(CultureInfo.CurrentCulture, Resources.BrowserLinkSettings_RunningFormat, state.Port)
                : Resources.BrowserLinkSettings_Disabled;
    }
}

/// <summary>One paired browser in the settings list.</summary>
public sealed class BrowserLinkPairingRowViewModel
{
    public BrowserLinkPairingRowViewModel(BrowserLinkSettingsSectionViewModel owner, BrowserLinkPairing pairing)
    {
        Label = pairing.Label;
        Origin = pairing.Origin;
        PairedOn = string.Format(
            CultureInfo.CurrentCulture,
            Resources.BrowserLinkSettings_PairedOnFormat,
            DateTimeOffset.FromUnixTimeSeconds(pairing.CreatedAtUnixSeconds).ToLocalTime().ToString("d", CultureInfo.CurrentCulture));
        RemoveCommand = new RelayCommand(() => owner.Remove(pairing));
    }

    public string Label { get; }

    public string Origin { get; }

    public string PairedOn { get; }

    public IRelayCommand RemoveCommand { get; }
}
