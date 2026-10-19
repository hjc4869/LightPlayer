using System;
using System.Collections.Generic;
using System.Linq;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Services.Playback;

/// <summary>A device one hardware decoder can be opened on.</summary>
/// <param name="Id">
/// The string handed to <c>av_hwdevice_ctx_create</c>, in whatever form that device type takes it.
/// Empty means the platform's own default.
/// </param>
/// <param name="Name">Display name presented to the user.</param>
public sealed record HardwareDeviceInfo(string Id, string Name);

/// <summary>How an option is entered and validated.</summary>
public enum HardwareSettingKind
{
    Text,
    Integer,
    Choice,
}

/// <summary>One accepted value of a <see cref="HardwareSettingKind.Choice"/> option.</summary>
public sealed record HardwareSettingChoice(string Value, string Name);

/// <summary>
/// One key/value argument of a call the hardware path makes. The call and the argument it belongs
/// to are part of the descriptor, so the decoder asks for exactly the ones a given call takes and
/// a call added later (an encoder's, say) carries its own without disturbing these.
/// </summary>
/// <param name="Api">FFmpeg function the option is passed to, such as <c>av_hwdevice_ctx_create</c>.</param>
/// <param name="Parameter">Argument of that function the option belongs to, such as <c>opts</c>.</param>
/// <param name="Key">Option name FFmpeg itself reads.</param>
public sealed record HardwareOptionDescriptor(
    string Api,
    string Parameter,
    string Key,
    string Name,
    string Description = "",
    string DefaultValue = "",
    HardwareSettingKind Kind = HardwareSettingKind.Text,
    IReadOnlyList<HardwareSettingChoice>? Choices = null);

/// <summary>One bit of a flags argument of a call the hardware path makes.</summary>
/// <param name="Key">Stable name the choice is persisted under; <paramref name="Bit"/> may change.</param>
/// <param name="Bit">Value ORed into the argument when the user turns this on.</param>
public sealed record HardwareFlagDescriptor(
    string Api,
    string Parameter,
    string Key,
    string Name,
    int Bit,
    string Description = "",
    bool DefaultOn = false);

/// <summary>
/// What one hardware device type can be configured with on this platform. A head registers one
/// implementation per FFmpeg device type name; types with no implementation are offered as they
/// always were, with no device choice and no arguments.
/// </summary>
public interface IHardwareDecoderProfile
{
    /// <summary>FFmpeg device type this configures, as <c>av_hwdevice_find_type_by_name</c> spells it.</summary>
    string DeviceType { get; }

    /// <summary>
    /// Devices found on this machine. The list excludes the default entry, which the settings UI
    /// always offers; an empty list means the user has nothing to choose between.
    /// </summary>
    IReadOnlyList<HardwareDeviceInfo> GetDevices();

    IReadOnlyList<HardwareOptionDescriptor> GetOptions();

    IReadOnlyList<HardwareFlagDescriptor> GetFlags();
}

/// <summary>
/// Registration point for the hardware profiles a head supplies, and the one place the stored
/// choices are turned into the arguments the decoder passes to FFmpeg.
/// </summary>
public static class HardwareDecoderProfiles
{
    private static readonly Dictionary<string, IHardwareDecoderProfile> profiles =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Register(IHardwareDecoderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (profiles)
        {
            profiles[profile.DeviceType] = profile;
        }
    }

    public static IHardwareDecoderProfile? Find(string deviceType)
    {
        if (string.IsNullOrWhiteSpace(deviceType))
        {
            return null;
        }

        lock (profiles)
        {
            return profiles.TryGetValue(deviceType, out var profile) ? profile : null;
        }
    }

    /// <summary>
    /// Resolves the persisted choices against the registered profiles. Flags are stored by name and
    /// only the profile knows what each is worth, so a setting whose profile is gone is dropped
    /// rather than guessed at.
    /// </summary>
    public static HardwareDecoderSettings Compose(IAppSettingsStore settingsStore)
    {
        var builder = new HardwareDecoderSettingsBuilder();
        foreach ((string deviceType, HardwareDecoderConfiguration configuration) in
            settingsStore.HardwareDecoderConfigurations)
        {
            builder.SetDevice(deviceType, configuration.Device);

            foreach ((string target, Dictionary<string, string> values) in configuration.Options)
            {
                if (!HardwareDecoderConfiguration.TrySplitTarget(target, out var api, out var parameter))
                {
                    continue;
                }

                foreach ((string key, string value) in values)
                {
                    builder.SetOption(deviceType, api, parameter, key, value);
                }
            }

            var flags = Find(deviceType)?.GetFlags();
            if (flags is null || flags.Count == 0)
            {
                continue;
            }

            foreach ((string target, List<string> enabled) in configuration.Flags)
            {
                foreach (var descriptor in flags)
                {
                    string descriptorTarget = HardwareDecoderConfiguration.Target(descriptor.Api, descriptor.Parameter);
                    if (string.Equals(descriptorTarget, target, StringComparison.Ordinal) &&
                        enabled.Contains(descriptor.Key, StringComparer.Ordinal))
                    {
                        builder.AddFlag(deviceType, descriptor.Api, descriptor.Parameter, descriptor.Bit);
                    }
                }
            }
        }

        return builder.Build();
    }
}
