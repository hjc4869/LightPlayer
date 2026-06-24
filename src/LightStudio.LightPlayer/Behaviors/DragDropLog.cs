using System;
using System.Linq;
using Avalonia.Input;

namespace LightStudio.LightPlayer.Behaviors;

/// <summary>
/// Lightweight console diagnostics for drag-and-drop troubleshooting (especially
/// external file drops, which vary by platform / desktop). Opt-in: set the
/// environment variable <c>LIGHTPLAYER_DND_DEBUG=1</c> to enable the output.
/// </summary>
internal static class DragDropLog
{
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("LIGHTPLAYER_DND_DEBUG") is { Length: > 0 } value
        && !string.Equals(value, "0", StringComparison.Ordinal);

    public static void Write(string message)
    {
        if (Enabled)
        {
            Console.WriteLine($"[DnD] {message}");
        }
    }

    /// <summary>Summarizes the advertised formats of a data transfer (kind:identifier list).</summary>
    public static string DescribeFormats(IDataTransfer? data)
    {
        if (data is null)
        {
            return "<null transfer>";
        }

        try
        {
            var formats = data.Formats;
            if (formats.Count == 0)
            {
                return "<no formats>";
            }

            return string.Join(", ", formats.Select(f => $"{f.Kind}:{f.Identifier}"));
        }
        catch (Exception ex)
        {
            return $"<error reading formats: {ex.GetType().Name}: {ex.Message}>";
        }
    }
}
