using System;
using System.Collections.Generic;
using System.Linq;

namespace LightStudio.RemoteApi;

/// <summary>
/// The web origins a browser is told it may read this API from. A page served from anywhere else
/// can still send requests, but the browser withholds the answer from it, so this decides which
/// hosted clients can drive the library and which are only able to knock.
/// </summary>
public sealed class CorsOriginPolicy
{
    /// <summary>Nothing cross-origin is answered; only same-origin pages and non-browser clients.</summary>
    public static CorsOriginPolicy None { get; } = new(false, [], []);

    private readonly bool allowAny;
    private readonly HashSet<string> origins;
    private readonly HashSet<string> hosts;

    private CorsOriginPolicy(bool allowAny, HashSet<string> origins, HashSet<string> hosts)
    {
        this.allowAny = allowAny;
        this.origins = origins;
        this.hosts = hosts;
    }

    public bool IsEmpty => !allowAny && origins.Count == 0 && hosts.Count == 0;

    /// <summary>Human-readable form for logs.</summary>
    public string Describe() =>
        allowAny ? "any" : IsEmpty ? "none" : string.Join(", ", origins.Concat(hosts));

    /// <summary>
    /// Reads a written list of origins. Entries are separated by commas, semicolons or whitespace,
    /// and each is either a full origin (<c>https://lmp.hjc.im</c>), a bare host name that matches
    /// any scheme (<c>lmp.hjc.im</c>), or <c>*</c> for every origin.
    /// </summary>
    public static IReadOnlyList<string> Split(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static CorsOriginPolicy Create(IEnumerable<string>? entries)
    {
        if (entries is null)
        {
            return None;
        }

        var allowAny = false;
        var origins = new HashSet<string>(StringComparer.Ordinal);
        var hosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var value = entry?.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (value == "*")
            {
                allowAny = true;
                continue;
            }

            if (value.Contains("://", StringComparison.Ordinal))
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out var parsed))
                {
                    origins.Add(Origin(parsed));
                }

                continue;
            }

            hosts.Add(value.ToLowerInvariant());
        }

        return allowAny || origins.Count > 0 || hosts.Count > 0
            ? new CorsOriginPolicy(allowAny, origins, hosts)
            : None;
    }

    /// <summary>Whether <paramref name="origin"/>, as sent in the request's Origin header, is listed.</summary>
    public bool Allows(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        return allowAny ||
            origins.Contains(Origin(parsed)) ||
            hosts.Contains(parsed.Host.ToLowerInvariant()) ||
            hosts.Contains($"{parsed.Host.ToLowerInvariant()}:{parsed.Port}");
    }

    /// <summary>Scheme, host and non-default port, which is all an Origin header ever carries.</summary>
    private static string Origin(Uri value) =>
        value.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
}
