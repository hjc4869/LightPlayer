using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightStudio.RemoteServices;

/// <summary>
/// <see cref="IKeyValueStore"/> over a JSON file, for heads that persist a remote session on the
/// local filesystem.
/// </summary>
/// <remarks>
/// The whole document is kept in memory and rewritten on every change; the state stored here is a
/// handful of kilobytes (a playback queue and twenty history entries at most). Writes are
/// best-effort so a read-only or full disk degrades to an in-memory store rather than failing
/// playback.
/// </remarks>
public sealed class FileKeyValueStore : IKeyValueStore
{
    private readonly string path;
    private readonly bool ownerOnly;
    private readonly object gate = new();
    private readonly Dictionary<string, string> values;

    /// <param name="ownerOnly">
    /// Restricts the file to the current user, for documents that hold saved credentials.
    /// </param>
    public FileKeyValueStore(string path, bool ownerOnly = false)
    {
        this.path = path;
        this.ownerOnly = ownerOnly;
        values = Load(path);
    }

    public string? Get(string key)
    {
        lock (gate)
        {
            return values.GetValueOrDefault(key);
        }
    }

    public void Set(string key, string value)
    {
        lock (gate)
        {
            if (values.TryGetValue(key, out var existing) && string.Equals(existing, value, StringComparison.Ordinal))
            {
                return;
            }

            values[key] = value;
            Save();
        }
    }

    public void Remove(string key)
    {
        lock (gate)
        {
            if (values.Remove(key))
            {
                Save();
            }
        }
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize(
                    File.ReadAllText(path),
                    KeyValueJsonContext.Default.DictionaryStringString);
                if (loaded is not null)
                {
                    return new Dictionary<string, string>(loaded, StringComparer.Ordinal);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(values, KeyValueJsonContext.Default.DictionaryStringString));
            if (ownerOnly && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class KeyValueJsonContext : JsonSerializerContext;
