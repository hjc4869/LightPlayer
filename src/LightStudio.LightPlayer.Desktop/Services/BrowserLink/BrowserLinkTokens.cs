using System;
using System.Security.Cryptography;
using System.Text;

namespace LightStudio.LightPlayer.Desktop.Services.BrowserLink;

/// <summary>
/// Issues pairing tokens and checks them against stored verifiers. Tokens are 256 random bits, so a
/// plain SHA-256 digest is a sufficient verifier.
/// </summary>
internal static class BrowserLinkTokens
{
    private const string Scheme = "sha256$";

    public static string Create() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    public static string CreateVerifier(string token) =>
        Scheme + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool Matches(string token, string verifier) =>
        verifier.StartsWith(Scheme, StringComparison.Ordinal) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(CreateVerifier(token)),
            Encoding.ASCII.GetBytes(verifier));
}
