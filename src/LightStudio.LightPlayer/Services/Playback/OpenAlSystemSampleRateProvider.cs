using OpenTK.Audio.OpenAL;

namespace LightStudio.LightPlayer.Services.Playback;

/// <summary>
/// Reads the mixer sample rate from the default OpenAL device. OpenAL Soft is
/// cross-platform, so this avoids per-OS audio APIs. A fresh device is opened
/// only for the query so it reflects the live system rate at call time.
/// </summary>
internal sealed class OpenAlSystemSampleRateProvider : ISystemSampleRateProvider
{
    // ALC_FREQUENCY context attribute key (alc.h). The device reports the mixer
    // rate as a key/value pair in its attribute list.
    private const int AlcFrequency = 0x1007;

    public int GetSystemSampleRate()
    {
        // A freshly opened device has NOT yet negotiated with the audio backend, so
        // its attribute list reports OpenAL Soft's placeholder rate (48000 Hz) instead
        // of the real mixer rate. The backend output stream is opened — and the true
        // rate resolved — only when the first context is created on the device. We
        // therefore CREATE a context to force that negotiation, but deliberately do NOT
        // call MakeContextCurrent: that is process-global and would steal the player's
        // active context, breaking its source ("Illegal Command"). Creating a context on
        // our own throwaway device leaves the current context untouched.
        var device = ALC.OpenDevice(null);
        if (device == ALDevice.Null)
        {
            return 0;
        }

        var context = ALContext.Null;
        try
        {
            context = ALC.CreateContext(device, new[] { 0 });
            if (context == ALContext.Null)
            {
                return 0;
            }

            ALC.GetInteger(device, AlcGetInteger.AttributesSize, out var size);
            if (size <= 0)
            {
                return 0;
            }

            var attributes = new int[size];
            ALC.GetInteger(device, AlcGetInteger.AllAttributes, size, attributes);
            for (var i = 0; i + 1 < attributes.Length; i += 2)
            {
                if (attributes[i] == 0)
                {
                    break;
                }

                if (attributes[i] == AlcFrequency && attributes[i + 1] > 0)
                {
                    return attributes[i + 1];
                }
            }

            return 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (context != ALContext.Null)
            {
                ALC.DestroyContext(context);
            }

            ALC.CloseDevice(device);
        }
    }
}
