using CommunityToolkit.Mvvm.ComponentModel;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// A configured library source folder, used by the settings row sample list.
/// </summary>
public sealed partial class LibraryFolderModel : ObservableObject
{
    /// <summary>
    /// Whether the platform has to ask the user for this folder again before it can be read.
    /// A browser drops the grant on a picked directory at the end of every session.
    /// </summary>
    [ObservableProperty]
    private bool needsAccess;

    public LibraryFolderModel(string name, string path, bool needsAccess = false)
    {
        Name = name;
        Path = path;
        this.needsAccess = needsAccess;
    }

    public string Name { get; }

    public string Path { get; }
}
