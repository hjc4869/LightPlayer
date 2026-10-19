using LightMediaRenderer.Avalonia.Audio;
#nullable enable
using System;
using Android.Content;
using LightStudio.LightPlayer.Services.Playback;

namespace LightStudio.LightPlayer.Android.Playback;

public static class AAudioPlaybackBackend
{
    private static AAudioSettings? settings;

    public static void Register(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        settings ??= new AAudioSettings(context.ApplicationContext ?? context);
        AudioOutputDeviceFactory.Register(
            (format, bufferTarget) => new AAudioOutputDevice(format, bufferTarget, settings),
            settings,
            settings,
            (format, bufferTarget, cancellationToken) =>
                AAudioOutputDevice.CreateAsync(format, bufferTarget, settings, cancellationToken));
    }
}