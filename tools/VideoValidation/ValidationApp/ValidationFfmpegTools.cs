using System;
using System.IO;

namespace LightStudio.VideoValidation;

/// <summary>
/// Resolves the FFmpeg command-line tools used to generate the validation corpus.
/// </summary>
/// <remarks>
/// The build drops a GPL static FFmpeg build into <c>ffmpeg-gpl/</c> next to the validator because
/// several conformance assets require GPL-only encoders. It is only ever spawned as a child
/// process; the validator itself keeps loading the LGPL shared libraries that ship with the player,
/// so the decode path under test stays the shipping one. When the bundle is missing (Linux, macOS,
/// or a hand-assembled corpus) the tools are resolved from <c>PATH</c> as before.
/// </remarks>
public static class ValidationFfmpegTools
{
    /// <summary>Path or command name for <c>ffmpeg</c>.</summary>
    public static string FfmpegPath { get; } = Resolve("ffmpeg");

    /// <summary>Path or command name for <c>ffprobe</c>.</summary>
    public static string FfprobePath { get; } = Resolve("ffprobe");

    private static string Resolve(string tool)
    {
        string fileName = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        string bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg-gpl", fileName);
        return File.Exists(bundled) ? bundled : tool;
    }
}
