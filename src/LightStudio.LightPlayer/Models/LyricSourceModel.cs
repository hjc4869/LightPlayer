namespace LightStudio.LightPlayer.Models;

/// <summary>A registered third-party lyric source script, identified by a unique name.</summary>
public sealed class LyricSourceModel
{
    public LyricSourceModel(string name)
    {
        Name = name;
    }

    public string Name { get; }
}
