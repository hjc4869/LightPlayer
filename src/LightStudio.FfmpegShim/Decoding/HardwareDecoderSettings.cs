using System;
using System.Collections.Generic;

namespace LightStudio.FfmpegShim.Decoding;

/// <summary>
/// The FFmpeg calls in the hardware path whose arguments the user can configure. A setting is
/// addressed by the call it belongs to rather than by what it does, so an encoder added later
/// carries its own arguments without any of these having to be reinterpreted.
/// </summary>
public static class HardwareDecoderApis
{
    public const string DeviceCreate = "av_hwdevice_ctx_create";
}

/// <summary>Named arguments of those calls, as they are spelled in FFmpeg's own signatures.</summary>
public static class HardwareDecoderParameters
{
    public const string Options = "opts";
    public const string Flags = "flags";
}

/// <summary>
/// The user's hardware configuration, addressed by device type, the call it applies to and the
/// argument of that call it belongs to. Empty answers mean "whatever FFmpeg does by default", so a
/// decoder never has to know which settings a platform actually offers.
/// </summary>
public sealed class HardwareDecoderSettings
{
    private static readonly IReadOnlyDictionary<string, string> NoOptions =
        new Dictionary<string, string>(0, StringComparer.Ordinal);

    private readonly Dictionary<string, string> devices;
    private readonly Dictionary<string, Dictionary<string, string>> options;
    private readonly Dictionary<string, int> flags;

    internal HardwareDecoderSettings(
        Dictionary<string, string> devices,
        Dictionary<string, Dictionary<string, string>> options,
        Dictionary<string, int> flags)
    {
        this.devices = devices;
        this.options = options;
        this.flags = flags;
    }

    /// <summary>Configuration that leaves every call at its defaults.</summary>
    public static HardwareDecoderSettings Empty { get; } = new HardwareDecoderSettingsBuilder().Build();

    /// <summary>
    /// The device string to open this hardware type on, in whatever form FFmpeg's device creation
    /// takes it (a DRM node path for VA-API, an adapter index for D3D11VA). Empty selects the
    /// platform's own default.
    /// </summary>
    public string QueryDevice(string deviceType) =>
        deviceType != null && devices.TryGetValue(deviceType, out string device) ? device : string.Empty;

    public IReadOnlyDictionary<string, string> QueryOptions(string deviceType, string api, string parameter) =>
        options.TryGetValue(Key(deviceType, api, parameter), out Dictionary<string, string> values)
            ? values
            : NoOptions;

    /// <summary>The bits the user turned on, already combined into the value the call takes.</summary>
    public int QueryFlags(string deviceType, string api, string parameter) =>
        flags.TryGetValue(Key(deviceType, api, parameter), out int bits) ? bits : 0;

    /// <summary>
    /// The same configuration with one device type opened on a different device. The encode path
    /// uses it: a machine encoding for a client need not be using the adapter it plays on itself,
    /// but everything else about how that adapter is opened still applies.
    /// </summary>
    public HardwareDecoderSettings WithDevice(string deviceType, string device)
    {
        if (string.IsNullOrWhiteSpace(deviceType) || string.IsNullOrWhiteSpace(device))
            return this;

        var replaced = new Dictionary<string, string>(devices, StringComparer.OrdinalIgnoreCase)
        {
            [deviceType] = device,
        };
        return new HardwareDecoderSettings(replaced, options, flags);
    }

    /// <summary>Returns the same configuration with one call option added or replaced.</summary>
    public HardwareDecoderSettings WithOption(
        string deviceType,
        string api,
        string parameter,
        string key,
        string value)
    {
        if (string.IsNullOrWhiteSpace(deviceType) || string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(value))
            return this;

        string target = Key(deviceType, api, parameter);
        var replaced = new Dictionary<string, Dictionary<string, string>>(options, StringComparer.OrdinalIgnoreCase);
        var values = options.TryGetValue(target, out Dictionary<string, string> existing)
            ? new Dictionary<string, string>(existing, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        values[key] = value;
        replaced[target] = values;
        return new HardwareDecoderSettings(devices, replaced, flags);
    }

    internal static string Key(string deviceType, string api, string parameter) =>
        string.Concat(deviceType, "|", api, "|", parameter);
}

/// <summary>Collects hardware settings from wherever the application stores them.</summary>
public sealed class HardwareDecoderSettingsBuilder
{
    private readonly Dictionary<string, string> devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> options = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> flags = new(StringComparer.OrdinalIgnoreCase);

    public HardwareDecoderSettingsBuilder SetDevice(string deviceType, string device)
    {
        if (!string.IsNullOrWhiteSpace(deviceType) && !string.IsNullOrWhiteSpace(device))
            devices[deviceType] = device;
        return this;
    }

    public HardwareDecoderSettingsBuilder SetOption(
        string deviceType,
        string api,
        string parameter,
        string key,
        string value)
    {
        if (string.IsNullOrWhiteSpace(deviceType) || string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(value))
            return this;

        string target = HardwareDecoderSettings.Key(deviceType, api, parameter);
        if (!options.TryGetValue(target, out Dictionary<string, string> values))
            options[target] = values = new Dictionary<string, string>(StringComparer.Ordinal);

        values[key] = value;
        return this;
    }

    public HardwareDecoderSettingsBuilder AddFlag(string deviceType, string api, string parameter, int bit)
    {
        if (string.IsNullOrWhiteSpace(deviceType) || bit == 0)
            return this;

        string target = HardwareDecoderSettings.Key(deviceType, api, parameter);
        flags[target] = flags.TryGetValue(target, out int bits) ? bits | bit : bit;
        return this;
    }

    public HardwareDecoderSettings Build() => new(devices, options, flags);
}
