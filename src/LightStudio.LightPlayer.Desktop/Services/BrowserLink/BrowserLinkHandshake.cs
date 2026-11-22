using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Desktop.Services.BrowserLink;

/// <summary>An HTTP request head as the browser link listener reads it.</summary>
internal sealed record BrowserLinkRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;

    /// <summary>A WebSocket opening handshake of version 13.</summary>
    public bool IsWebSocketRequest =>
        Method == "GET" &&
        HasToken(Header("Connection"), "upgrade") &&
        HasToken(Header("Upgrade"), "websocket") &&
        !string.IsNullOrWhiteSpace(Header("Sec-WebSocket-Key")) &&
        Header("Sec-WebSocket-Version")?.Trim() == "13";

    public static bool HasToken(string? value, string token)
    {
        foreach (string part in (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(part, token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// The HTTP side of a browser link socket on a plain loopback connection: reads the opening
/// handshake and answers it, so the socket can be told to send small frames without delay.
/// </summary>
internal static class BrowserLinkHandshake
{
    private const int MaximumHeadBytes = 16 * 1024;
    private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>Reads a request head; null when the peer closed or sent something other than one request head.</summary>
    public static async Task<BrowserLinkRequest?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[MaximumHeadBytes];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return null;

            int searchFrom = Math.Max(0, length - 3);
            length += read;
            int end = buffer.AsSpan(searchFrom, length - searchFrom).IndexOf("\r\n\r\n"u8);
            if (end < 0)
                continue;

            // A client sends nothing more until it has the answer.
            end += searchFrom;
            return end + 4 == length ? Parse(Encoding.Latin1.GetString(buffer, 0, end)) : null;
        }

        return null;
    }

    public static Task RefuseAsync(Stream stream, HttpStatusCode status, CancellationToken cancellationToken)
    {
        string extra = status == HttpStatusCode.UpgradeRequired ? "Upgrade: websocket\r\nSec-WebSocket-Version: 13\r\n" : string.Empty;
        return WriteAsync(
            stream,
            $"HTTP/1.1 {(int)status} {Reason(status)}\r\n{extra}Content-Length: 0\r\nConnection: close\r\n\r\n",
            cancellationToken);
    }

    public static Task AcceptAsync(Stream stream, BrowserLinkRequest request, string subProtocol, CancellationToken cancellationToken)
    {
        string accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(request.Header("Sec-WebSocket-Key")!.Trim() + AcceptGuid)));
        return WriteAsync(
            stream,
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\nSec-WebSocket-Protocol: {subProtocol}\r\n\r\n",
            cancellationToken);
    }

    private static BrowserLinkRequest? Parse(string head)
    {
        string[] lines = head.Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < lines.Length; index++)
        {
            int colon = lines[index].IndexOf(':');
            if (colon <= 0)
                return null;
            string name = lines[index][..colon].Trim();
            string value = lines[index][(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out string? earlier) ? $"{earlier}, {value}" : value;
        }

        string target = requestLine[1];
        int query = target.IndexOfAny(['?', '#']);
        return new BrowserLinkRequest(requestLine[0], query < 0 ? target : target[..query], headers);
    }

    private static string Reason(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => "Bad Request",
        HttpStatusCode.Forbidden => "Forbidden",
        HttpStatusCode.NotFound => "Not Found",
        HttpStatusCode.UpgradeRequired => "Upgrade Required",
        HttpStatusCode.ServiceUnavailable => "Service Unavailable",
        _ => status.ToString(),
    };

    private static async Task WriteAsync(Stream stream, string text, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
