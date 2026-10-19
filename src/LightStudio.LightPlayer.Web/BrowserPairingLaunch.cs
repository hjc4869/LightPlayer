using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Web;

internal sealed record BrowserPairingRequest(string ServerAddress, string Token, long ExpiresAtUnixSeconds);

[SupportedOSPlatform("browser")]
internal static partial class BrowserPairingLaunch
{
    public static async Task<BrowserPairingRequest?> TakeAsync()
    {
        var payload = await TakeLaunchAsync();
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var server = root.GetProperty("server").GetString() ?? string.Empty;
        var token = root.GetProperty("token").GetString() ?? string.Empty;
        var expiresAt = root.GetProperty("expiresAtUnixSeconds").GetInt64();
        if (!Uri.TryCreate(server, UriKind.Absolute, out var serverUri) ||
            serverUri.Scheme != Uri.UriSchemeHttp ||
            !string.IsNullOrEmpty(serverUri.UserInfo) ||
            !string.IsNullOrEmpty(serverUri.Query) ||
            !string.IsNullOrEmpty(serverUri.Fragment) ||
            !IPAddress.TryParse(serverUri.Host, out var serverAddress) ||
            !IsPrivateIpv4(serverAddress) ||
            token.Length is < 32 or > 128 ||
            expiresAt <= 0)
        {
            throw new InvalidDataException("The pairing link is malformed.");
        }

        if (expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            throw new InvalidDataException("The pairing link has expired.");
        }

        return new BrowserPairingRequest(serverUri.GetLeftPart(UriPartial.Authority), token, expiresAt);
    }

    public static Task CompleteAsync() => CompleteLaunchAsync();

    public static Task ShowResultAsync(bool succeeded, string message) =>
        ShowResultAsyncCore(succeeded, message);

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

    [JSImport("take", "BrowserPairingLaunch")]
    private static partial Task<string?> TakeLaunchAsync();

    [JSImport("complete", "BrowserPairingLaunch")]
    private static partial Task CompleteLaunchAsync();

    [JSImport("showResult", "BrowserPairingLaunch")]
    private static partial Task ShowResultAsyncCore(bool succeeded, string message);
}