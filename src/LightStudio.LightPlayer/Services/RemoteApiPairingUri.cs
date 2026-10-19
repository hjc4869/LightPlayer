using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace LightStudio.LightPlayer.Services;

/// <summary>Creates and validates pairing activations handled by the Android app.</summary>
internal static class RemoteApiPairingUri
{
    public const string Scheme = "lmp";
    public const string Host = "pair";

    public static Uri Create(RemoteApiPairingInfo pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        return new Uri(
            $"{Scheme}://{Host}?server={Uri.EscapeDataString(pairing.ServerAddress)}" +
            $"&token={Uri.EscapeDataString(pairing.Token)}" +
            $"&expires={pairing.ExpiresAtUnixSeconds.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Absolute);
    }

    public static RemoteApiPairingInfo Parse(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new FormatException("The pairing link is malformed.");
        }

        var values = ParseQuery(uri.Query);
        if (values.Count != 3 ||
            !values.TryGetValue("server", out var serverText) ||
            !values.TryGetValue("token", out var token) ||
            !values.TryGetValue("expires", out var expiresText) ||
            !long.TryParse(expiresText, NumberStyles.None, CultureInfo.InvariantCulture, out var expiresAt) ||
            expiresAt <= 0 ||
            token.Length is < 32 or > 128 ||
            token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') ||
            !Uri.TryCreate(serverText, UriKind.Absolute, out var server) ||
            server.Scheme != Uri.UriSchemeHttp ||
            !string.IsNullOrEmpty(server.UserInfo) ||
            server.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(server.Query) ||
            !string.IsNullOrEmpty(server.Fragment) ||
            !IPAddress.TryParse(server.Host, out var address) ||
            !IsPrivateIpv4(address))
        {
            throw new FormatException("The pairing link is malformed.");
        }

        return new RemoteApiPairingInfo(server.GetLeftPart(UriPartial.Authority), token, expiresAt);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=');
                if (separator <= 0 || !values.TryAdd(
                    Uri.UnescapeDataString(part[..separator]),
                    Uri.UnescapeDataString(part[(separator + 1)..])))
                {
                    throw new FormatException("The pairing link is malformed.");
                }
            }
        }
        catch (UriFormatException exception)
        {
            throw new FormatException("The pairing link is malformed.", exception);
        }

        return values;
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
    }
}