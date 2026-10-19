using LightMediaRenderer.Avalonia.Audio;
using LightStudio.LightPlayer.Services.Playback;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>Registers the macOS CoreAudio (AUHAL) playback backend.</summary>
public static class CoreAudioPlaybackBackend
{
    private static CoreAudioSettings? settings;

    public static void Register()
    {
        settings ??= new CoreAudioSettings();
        AudioOutputDeviceFactory.Register(
            (format, bufferTarget) => new CoreAudioOutputDevice(format, bufferTarget, settings),
            settings,
            settings,
            (format, bufferTarget, cancellationToken) =>
                CoreAudioOutputDevice.CreateAsync(format, bufferTarget, settings, cancellationToken));
    }
}
