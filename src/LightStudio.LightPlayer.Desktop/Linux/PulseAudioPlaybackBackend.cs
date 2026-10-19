using LightMediaRenderer.Avalonia.Audio;
using LightStudio.LightPlayer.Services.Playback;

namespace LightStudio.LightPlayer.Desktop.Linux;

/// <summary>Registers the PulseAudio playback backend as the desktop audio output on Linux.</summary>
public static class PulseAudioPlaybackBackend
{
    private static PulseAudioSettings? settings;

    public static void Register()
    {
        settings ??= new PulseAudioSettings();
        AudioOutputDeviceFactory.Register(
            (format, bufferTarget) => new PulseAudioOutputDevice(format, bufferTarget, settings),
            settings,
            settings,
            (format, bufferTarget, cancellationToken) =>
                PulseAudioOutputDevice.CreateAsync(format, bufferTarget, settings, cancellationToken));
    }
}