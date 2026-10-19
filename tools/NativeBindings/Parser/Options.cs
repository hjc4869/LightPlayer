using System.Text;

namespace NativeBindings.Parser;

/// <summary>Key/value settings written by the MSBuild integration, one <c>key=value</c> pair per line.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    public static async Task<Options> LoadAsync(string path)
    {
        var options = new Options();
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;
            options.values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return options;
    }

    public string Get(string key, string fallback = "") =>
        values.TryGetValue(key, out var value) && value.Length != 0 ? value : fallback;

    public bool Flag(string key) => string.Equals(Get(key), "true", StringComparison.OrdinalIgnoreCase);

    public string[] List(string key) => Get(key).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string Describe()
    {
        var builder = new StringBuilder();
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
        return builder.ToString();
    }
}
