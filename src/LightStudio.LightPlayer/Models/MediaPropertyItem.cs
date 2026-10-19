namespace LightStudio.LightPlayer.Models;

/// <summary>
/// A single row of the media properties dialog: either a name/value pair, or a section header that
/// introduces the rows below it (the container, one stream, and so on).
/// </summary>
public sealed record MediaPropertyItem(string Key, string Value, bool IsSection = false);
