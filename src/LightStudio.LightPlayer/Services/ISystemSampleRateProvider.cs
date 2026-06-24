namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Provides the current audio output sample rate negotiated with the operating
/// system mixer. The value can change at runtime (the user may switch devices or
/// rates), so callers should query it on demand rather than caching it.
/// </summary>
public interface ISystemSampleRateProvider
{
    /// <summary>The system mixer sample rate in Hz, or 0 when it cannot be determined.</summary>
    int GetSystemSampleRate();
}

/// <summary>System sample rate provider that always returns 0 (fallback when the system rate cannot be resolved).</summary>
public sealed class UnknownSystemSampleRateProvider : ISystemSampleRateProvider
{
    public int GetSystemSampleRate() => 0;
}
