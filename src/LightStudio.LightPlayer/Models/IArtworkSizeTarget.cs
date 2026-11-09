namespace LightStudio.LightPlayer.Models;

/// <summary>
/// Receives the display size of a <c>MediaThumbnail</c> so its artwork is decoded at the
/// resolution it is shown at. Sizes only grow: a larger report reloads the artwork.
/// </summary>
public interface IArtworkSizeTarget
{
    /// <summary>
    /// Called on the UI thread with the device pixels the artwork's shorter side must cover to
    /// fill the thumbnail.
    /// </summary>
    void SetArtworkDisplaySize(int pixelSize);
}
