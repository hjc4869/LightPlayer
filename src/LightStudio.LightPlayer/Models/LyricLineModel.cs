using CommunityToolkit.Mvvm.ComponentModel;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// One displayable lyric line. <see cref="IsCurrent"/> is flipped by the
/// now-playing page as playback crosses each timestamp so the view can highlight
/// the active line.
/// </summary>
public sealed partial class LyricLineModel : ObservableObject
{
    [ObservableProperty]
    private bool isCurrent;

    public LyricLineModel(long timeMs, string text)
    {
        TimeMs = timeMs;
        Text = text;
    }

    public long TimeMs { get; }

    public string Text { get; }
}
