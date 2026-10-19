using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LightStudio.FfmpegShim.Transcoding;
using LightStudio.LightPlayer.Remote;
using LightStudio.LightPlayer.Services;

namespace LightStudio.RemoteApi;

/// <summary>Issues and validates the bearer tokens handed out by <c>POST /api/v1/auth/login</c>.</summary>
/// <remarks>
/// Tokens are signed with a key derived from the host's random secret and the current credentials,
/// so changing the user name or password invalidates them while a restart does not.
/// </remarks>
internal sealed class RemoteApiTokens
{
    private const string Prefix = "lp1.";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private readonly string username;
    private readonly byte[] key;

    public RemoteApiTokens(ReadOnlySpan<byte> secret, RemoteApiCredentials credentials)
    {
        username = credentials.Username;
        key = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(credentials.Username + "\u0000" + credentials.PasswordHash));
    }

    public RemoteLoginResult Issue()
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds();
        var payload = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{username}\n{expiresAt}"));
        var signature = HMACSHA256.HashData(key, payload);
        return new RemoteLoginResult($"{Prefix}{Base64Url(payload)}.{Base64Url(signature)}", expiresAt);
    }

    public bool Validate(string token)
    {
        if (!token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = token[Prefix.Length..];
        var separator = body.IndexOf('.');
        if (separator <= 0 || separator == body.Length - 1)
        {
            return false;
        }

        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = FromBase64Url(body[..separator]);
            signature = FromBase64Url(body[(separator + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(key, payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(signature, expected))
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(payloadBytes).Split('\n');
        return parts.Length == 2 &&
            string.Equals(parts[0], username, StringComparison.Ordinal) &&
            long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresAt) &&
            expiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
}

/// <summary>
/// Random host secret that session tokens are signed with. It is kept apart from the password hash
/// so that reading the hash is not enough to mint tokens.
/// </summary>
public static class RemoteApiTokenSecret
{
    private const string FileName = "remote-api-secret.key";
    private const int SecretBytes = 32;

    /// <summary>Reads the secret stored in <paramref name="directory"/>, creating it on first use.</summary>
    public static byte[] LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        if (File.Exists(path))
        {
            var stored = File.ReadAllBytes(path);
            if (stored.Length == SecretBytes)
            {
                return stored;
            }
        }

        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temporary, options))
        {
            stream.Write(secret);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
        return secret;
    }

    /// <summary>
    /// A password salt derived from <paramref name="secret"/>, so a password supplied in plain text
    /// hashes to the same value on every start and existing tokens stay valid.
    /// </summary>
    public static byte[] PasswordSalt(ReadOnlySpan<byte> secret) =>
        HMACSHA256.HashData(secret, "password-salt"u8)[..16];
}

/// <summary>Issues short-lived codes that can be exchanged for a normal session token.</summary>
internal sealed class RemoteApiPairingTokens
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private readonly Dictionary<string, PairingEntry> entries = new(StringComparer.Ordinal);

    public (string Token, long ExpiresAtUnixSeconds) Issue()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expiresAt = DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds();
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Hash(token);

        lock (gate)
        {
            foreach (var expired in entries.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToArray())
            {
                entries.Remove(expired);
            }

            entries.Add(tokenHash, new PairingEntry(expiresAt));
        }

        return (token, expiresAt);
    }

    public bool TryExchange(
        string? token,
        Func<RemoteLoginResult> createSession,
        out RemoteLoginResult result)
    {
        result = null!;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            return false;
        }

        var tokenHash = Hash(token);
        lock (gate)
        {
            if (!entries.TryGetValue(tokenHash, out var entry) ||
                entry.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                entries.Remove(tokenHash);
                return false;
            }

            result = entry.Result ?? createSession();
            entries[tokenHash] = entry with { Result = result };
            return true;
        }
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record PairingEntry(long ExpiresAt, RemoteLoginResult? Result = null);
}

/// <summary>Hosting options shared by the desktop head and the standalone server head.</summary>
public sealed class RemoteApiHostOptions
{
    /// <summary><see cref="System.Net.HttpListener"/> prefix, e.g. <c>http://*:47832/</c>.</summary>
    public string ListenerPrefix { get; init; } = RemoteApiDefaults.ListenerPrefix;

    /// <summary>
    /// Directory holding the published browser client. Left null the host serves the JSON API only
    /// and answers every non-API path with 404; the desktop head always does so.
    /// </summary>
    public string? StaticFilesPath { get; init; }

    /// <summary>Credentials every request must present. Left null the API is unauthenticated.</summary>
    public RemoteApiCredentials? Credentials { get; init; }

    /// <summary>
    /// Secret from <see cref="RemoteApiTokenSecret"/> that signs session tokens. Left null a random
    /// one is used, so tokens last only as long as this server instance.
    /// </summary>
    public byte[]? TokenSecret { get; init; }

    /// <summary>
    /// Web origins whose pages a browser may let read this API. Left empty no cross-origin page is
    /// answered, which is what a client served by this host itself needs no help with.
    /// </summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    /// <summary>
    /// How this host re-encodes what it sends, or null to send every track exactly as it is stored.
    /// The head builds it: which device does the work, and what it is opened with, are things only
    /// the head can know.
    /// </summary>
    public TranscodeOptions? Transcoding { get; init; }
}
