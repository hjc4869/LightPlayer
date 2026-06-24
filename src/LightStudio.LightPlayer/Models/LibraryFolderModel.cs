namespace LightStudio.LightPlayer.Models;

/// <summary>
/// A configured library source folder, used by the settings row sample list.
/// </summary>
public sealed class LibraryFolderModel
{
    public LibraryFolderModel(string name, string path)
    {
        Name = name;
        Path = path;
    }

    public string Name { get; }

    public string Path { get; }
}
