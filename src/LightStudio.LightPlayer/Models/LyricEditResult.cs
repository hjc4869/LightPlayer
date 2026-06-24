using LightStudio.MediaLibraryCore.Lyrics;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// Result of the lyric search dialog. <see cref="Changed"/> is false when the
/// user cancelled without altering the current lyrics; when true,
/// <see cref="Lyrics"/> holds the new lyrics (or null when the user cleared them).
/// </summary>
public sealed record LyricEditResult(bool Changed, ParsedLrc? Lyrics);
