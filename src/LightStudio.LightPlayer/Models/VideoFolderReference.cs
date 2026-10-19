namespace LightStudio.LightPlayer.Models;

public sealed record VideoFolderReference(
    LibrarySourceId SourceId,
    string? Path,
    string SourceName)
{
    /// <summary>
    /// True for the synthetic recently viewed folder, which lists history entries from every source
    /// rather than the contents of one folder. <see cref="SourceId"/> and <see cref="Path"/> carry
    /// nothing for it.
    /// </summary>
    public bool IsRecent { get; init; }

    public static VideoFolderReference Recent(string name) =>
        new(LibrarySourceId.Local, null, name) { IsRecent = true };
}