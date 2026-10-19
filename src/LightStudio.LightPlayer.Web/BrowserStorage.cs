using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
internal static class BrowserStorage
{
    private const string Prefix = "lightplayer.";
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
    private static string origin = string.Empty;

    public static async Task InitializeAsync()
    {
        var payload = await BrowserStorageInterop.InitializeAsync(Prefix);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var loadedValues = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in root.GetProperty("entries").EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                loadedValues[property.Name] = property.Value.GetString()!;
            }
        }

        lock (SyncRoot)
        {
            Values.Clear();
            foreach (var entry in loadedValues)
            {
                Values.Add(entry.Key, entry.Value);
            }

            origin = root.GetProperty("origin").GetString() ?? string.Empty;
        }
    }

    public static string? Get(string key)
    {
        lock (SyncRoot)
        {
            return Values.GetValueOrDefault(Prefix + key);
        }
    }

    public static void Set(string key, string value)
    {
        var storageKey = Prefix + key;
        lock (SyncRoot)
        {
            Values[storageKey] = value;
        }

        _ = BrowserMainThread.InvokeAsync(() => BrowserStorageInterop.SetItemAsync(storageKey, value));
    }

    public static async Task SetAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var storageKey = Prefix + key;
        var persisted = await BrowserMainThread
            .InvokeAsync(() => BrowserStorageInterop.SetItemAsync(storageKey, value))
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!persisted)
        {
            throw new IOException("Browser storage did not persist the value.");
        }

        lock (SyncRoot)
        {
            Values[storageKey] = value;
        }
    }

    public static void Remove(string key)
    {
        var storageKey = Prefix + key;
        lock (SyncRoot)
        {
            Values.Remove(storageKey);
        }

        _ = BrowserMainThread.InvokeAsync(() => BrowserStorageInterop.RemoveItemAsync(storageKey));
    }

    public static async Task RemoveAsync(string key)
    {
        var storageKey = Prefix + key;
        var removed = await BrowserMainThread
            .InvokeAsync(() => BrowserStorageInterop.RemoveItemAsync(storageKey))
            .ConfigureAwait(false);
        if (!removed)
            throw new IOException("Browser storage did not remove the value.");

        lock (SyncRoot)
        {
            Values.Remove(storageKey);
        }
    }

    public static string GetOrigin()
    {
        lock (SyncRoot)
        {
            return origin;
        }
    }

    /// <summary>
    /// Origin selected by the picker for the same-origin proxy to forward API requests to.
    /// Falls back to the page's own origin for a combined client and API deployment.
    /// </summary>
    public static string GetServerBaseUrl()
    {
        var stored = Get("server.baseUrl");
        return string.IsNullOrWhiteSpace(stored) ? GetOrigin() : stored;
    }

    /// <summary>Bearer token issued by the picker's sign-in, or empty when the server is open.</summary>
    public static string GetServerToken() => Get("server.token") ?? string.Empty;

    public static bool GetBoolean(string key, bool defaultValue = false) =>
        bool.TryParse(Get(key), out var value) ? value : defaultValue;

    public static void SetBoolean(string key, bool value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public static int GetInt32(string key, int defaultValue = 0) =>
        int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : defaultValue;

    public static void SetInt32(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public static TEnum GetEnum<TEnum>(string key, TEnum defaultValue)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(Get(key), ignoreCase: true, out var value) ? value : defaultValue;

    public static void SetEnum<TEnum>(string key, TEnum value)
        where TEnum : struct, Enum => Set(key, value.ToString());
}

[SupportedOSPlatform("browser")]
internal static partial class BrowserStorageInterop
{
    [JSImport("initialize", "BrowserStorage")]
    internal static partial Task<string> InitializeAsync(string prefix);

    [JSImport("setItem", "BrowserStorage")]
    internal static partial Task<bool> SetItemAsync(string key, string value);

    [JSImport("removeItem", "BrowserStorage")]
    internal static partial Task<bool> RemoveItemAsync(string key);

}

/// <summary>Exposes <see cref="BrowserStorage"/> as the store the remote services persist into.</summary>
[SupportedOSPlatform("browser")]
internal sealed class BrowserKeyValueStore : LightStudio.RemoteServices.IAsyncKeyValueStore
{
    public string? Get(string key) => BrowserStorage.Get(key);

    public void Set(string key, string value) => BrowserStorage.Set(key, value);

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
        BrowserStorage.SetAsync(key, value, cancellationToken);

    public void Remove(string key) => BrowserStorage.Remove(key);
}