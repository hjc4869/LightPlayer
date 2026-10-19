using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.RemoteServices;

/// <summary>
/// Small string key/value store a head supplies for a remote session's persisted state. The
/// browser backs it with <c>localStorage</c>, installed heads with a JSON file; nothing here
/// depends on either.
/// </summary>
public interface IKeyValueStore
{
    string? Get(string key);

    void Set(string key, string value);

    void Remove(string key);
}

/// <summary>A key/value store whose durable write completion can be awaited when required.</summary>
public interface IAsyncKeyValueStore : IKeyValueStore
{
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
}

/// <summary>Convenience readers and writers shared by every <see cref="IKeyValueStore"/>.</summary>
public static class KeyValueStoreExtensions
{
    public static bool GetBoolean(this IKeyValueStore store, string key, bool defaultValue = false) =>
        bool.TryParse(store.Get(key), out var value) ? value : defaultValue;

    public static void SetBoolean(this IKeyValueStore store, string key, bool value) =>
        store.Set(key, value.ToString(CultureInfo.InvariantCulture));

    public static int GetInt32(this IKeyValueStore store, string key, int defaultValue = 0) =>
        int.TryParse(store.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;

    public static void SetInt32(this IKeyValueStore store, string key, int value) =>
        store.Set(key, value.ToString(CultureInfo.InvariantCulture));

    public static double GetDouble(this IKeyValueStore store, string key, double defaultValue = 0d) =>
        double.TryParse(store.Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;

    public static void SetDouble(this IKeyValueStore store, string key, double value) =>
        store.Set(key, value.ToString("R", CultureInfo.InvariantCulture));

    public static TEnum GetEnum<TEnum>(this IKeyValueStore store, string key, TEnum defaultValue)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(store.Get(key), ignoreCase: true, out var value) ? value : defaultValue;

    public static void SetEnum<TEnum>(this IKeyValueStore store, string key, TEnum value)
        where TEnum : struct, Enum => store.Set(key, value.ToString());
}

/// <summary>
/// A view over another store whose keys are all namespaced. Used to scope the state that belongs
/// to one server (playback queue, playback history) so several servers can be used side by side.
/// </summary>
public sealed class PrefixedKeyValueStore(IKeyValueStore inner, string prefix) : IKeyValueStore
{
    public string? Get(string key) => inner.Get(prefix + key);

    public void Set(string key, string value) => inner.Set(prefix + key, value);

    public void Remove(string key) => inner.Remove(prefix + key);
}

/// <summary>Non-persistent store, used when a head has nowhere to keep a session's state.</summary>
public sealed class MemoryKeyValueStore : IKeyValueStore
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    public string? Get(string key)
    {
        lock (values)
        {
            return values.GetValueOrDefault(key);
        }
    }

    public void Set(string key, string value)
    {
        lock (values)
        {
            values[key] = value;
        }
    }

    public void Remove(string key)
    {
        lock (values)
        {
            values.Remove(key);
        }
    }
}
