using Avalonia.Controls;
using LightStudio.LightPlayer.Behaviors;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// One cell of the video library grid. The caption sits over either the preview fade or the
/// theme-aware placeholder surface.
/// </summary>
/// <remarks>
/// No drag handling and no queue buttons: videos are never mixed into the music queue, so the tile
/// only opens what it represents.
/// </remarks>
public partial class VideoTile : UserControl
{
    public VideoTile()
    {
        InitializeComponent();
        VideoTilePresenter.Track(this, PlaceholderGlyph, TypeGlyph);
    }
}
