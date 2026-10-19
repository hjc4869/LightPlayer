using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LightStudio.FfmpegShim.Transcoding;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// A user name and a verifier for its password. The plain password is never stored: callers keep
/// the <see cref="PasswordHash"/> produced by <see cref="HashPassword"/> in their settings file or
/// environment, and <see cref="Verify"/> checks a submitted password against it.
/// </summary>
public sealed record RemoteApiCredentials(string Username, string PasswordHash)
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Scheme = "pbkdf2-sha256";

    public bool IsUsable => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(PasswordHash);

    public static RemoteApiCredentials Create(string username, string password) =>
        new(username.Trim(), HashPassword(password));

    public static string HashPassword(string password) =>
        HashPassword(password, RandomNumberGenerator.GetBytes(SaltBytes));

    public static string HashPassword(string password, ReadOnlySpan<byte> salt)
    {
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <summary>Whether <paramref name="value"/> is already output of <see cref="HashPassword"/>.</summary>
    public static bool IsHash(string? value) =>
        value is not null && value.StartsWith(Scheme + "$", StringComparison.Ordinal);

    public bool Verify(string username, string password)
    {
        if (!IsUsable || !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(username), Encoding.UTF8.GetBytes(Username)))
        {
            return false;
        }

        var parts = PasswordHash.Split('$');
        if (parts.Length != 4 ||
            !string.Equals(parts[0], Scheme, StringComparison.Ordinal) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations) ||
            iterations is < 1 or > 10_000_000)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>
/// Persisted configuration of a head's HTTP server: where it listens and whether callers must
/// sign in.
/// </summary>
public sealed record RemoteApiSettings
{
    public static RemoteApiSettings Disabled { get; } = new();

    public bool Enabled { get; init; }

    public string Host { get; init; } = string.Empty;

    public int Port { get; init; }

    public bool RequireAuthentication { get; init; }

    public string Username { get; init; } = string.Empty;

    /// <summary>Output of <see cref="RemoteApiCredentials.HashPassword"/>; never the plain password.</summary>
    public string PasswordHash { get; init; } = string.Empty;

    /// <summary>
    /// Websites whose pages a browser may let read this API, written as a comma-separated list of
    /// origins or host names. Empty keeps every page served from elsewhere out.
    /// </summary>
    public string AllowedOrigins { get; init; } = string.Empty;

    /// <summary>Whether clients may ask this host to re-encode the tracks it sends them.</summary>
    public bool TranscodeEnabled { get; init; }

    /// <summary>Whether a graphics device does the encoding rather than the processor.</summary>
    public bool TranscodeHardware { get; init; } = true;

    /// <summary>Whether video is resized on the graphics device rather than the processor.</summary>
    public bool TranscodeHardwareScaling { get; init; }

    /// <summary>Rate-control mode passed to FFmpeg's VA-API encoders.</summary>
    public string TranscodeVaapiRateControl { get; init; } = VaapiEncoderOptions.DefaultRateControl;

    /// <summary>Speed-quality preset passed to FFmpeg's VA-API encoders.</summary>
    public string TranscodeVaapiQuality { get; init; } = VaapiEncoderOptions.DefaultQualityPreset;

    /// <summary>Rate-control mode passed to FFmpeg's Media Foundation encoders.</summary>
    public string TranscodeMediaFoundationRateControl { get; init; } =
        MediaFoundationEncoderOptions.PreferredRateControl;

    /// <summary>Speed-quality preset mapped to Media Foundation's 0-100 quality value.</summary>
    public string TranscodeMediaFoundationQuality { get; init; } =
        MediaFoundationEncoderOptions.DefaultQualityPreset;

    /// <summary>Media Foundation usage scenario.</summary>
    public string TranscodeMediaFoundationScenario { get; init; } =
        MediaFoundationEncoderOptions.PreferredScenario;

    /// <summary>Rate-control mode passed to FFmpeg's AMD AMF encoders.</summary>
    public string TranscodeAmfRateControl { get; init; } = AmfEncoderOptions.DefaultRateControl;

    public string TranscodeAmfQuality { get; init; } = AmfEncoderOptions.DefaultQualityPreset;

    public string TranscodeAmfUsage { get; init; } = AmfEncoderOptions.DefaultUsage;

    /// <summary>Rate-control mode passed to FFmpeg's NVIDIA NVENC encoders.</summary>
    public string TranscodeNvencRateControl { get; init; } = NvencEncoderOptions.DefaultRateControl;

    public string TranscodeNvencPreset { get; init; } = NvencEncoderOptions.DefaultPreset;

    public string TranscodeNvencTune { get; init; } = NvencEncoderOptions.DefaultTune;

    public string TranscodeQsvPreset { get; init; } = QsvEncoderOptions.DefaultPreset;

    public string TranscodeQsvScenario { get; init; } = QsvEncoderOptions.DefaultScenario;

    /// <summary>
    /// FFmpeg encoder interface, such as <c>mf</c>, <c>nvenc</c> or <c>vaapi</c>; empty tries the
    /// platform's interfaces in preference order. The property name is retained for settings-file
    /// compatibility with versions that incorrectly stored a decode device type here.
    /// </summary>
    public string TranscodeDeviceType { get; init; } = string.Empty;

    /// <summary>
    /// The device that type is opened on, in whatever form it takes -- a DRM render node for VA-API.
    /// Empty takes the platform's default.
    /// </summary>
    public string TranscodeDevice { get; init; } = string.Empty;

    /// <summary>
    /// Whether to encode on the processor when no device can be used. One such stream can cost more
    /// than everything else this host does put together, so it is off unless asked for.
    /// </summary>
    public bool TranscodeAllowSoftware { get; init; }

    public RemoteApiCredentials? Credentials =>
        RequireAuthentication && !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(PasswordHash)
            ? new RemoteApiCredentials(Username, PasswordHash)
            : null;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) &&
        Port is > 0 and <= 65535 &&
        (!RequireAuthentication || Credentials is not null);
}

public sealed record RemoteApiServerState(bool IsRunning, string Address, string ErrorMessage)
{
    public static RemoteApiServerState Stopped { get; } = new(false, string.Empty, string.Empty);
}

public sealed record RemoteApiPairingInfo(string ServerAddress, string Token, long ExpiresAtUnixSeconds);

public interface IRemoteApiServerController
{
    RemoteApiServerState State { get; }

    /// <summary>Raised whenever <see cref="State"/> changes, so the shell can show a live indicator.</summary>
    event EventHandler? StateChanged;

    RemoteApiServerState Apply(RemoteApiSettings settings);
}

public interface IRemoteApiPairingController
{
    RemoteApiPairingInfo CreatePairing();
}
