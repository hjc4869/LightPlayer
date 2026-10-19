using Avalonia.Controls;
using LightStudio.LightPlayer.Behaviors;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// One row of the video library list, the narrow layout's stand-in for <see cref="VideoTile"/>.
/// A phone-width grid of tiles leaves the caption barely legible, so narrow layouts drop to this
/// list instead of shrinking the tiles further.
/// </summary>
public partial class VideoListRow : UserControl
{
    public VideoListRow()
    {
        InitializeComponent();
        VideoTilePresenter.Track(this, PlaceholderGlyph);
    }
}
