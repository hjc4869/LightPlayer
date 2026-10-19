namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Persists shell layout state across launches.
/// </summary>
public interface IShellStateStore
{
    bool IsQueuePaneOpen { get; set; }

    /// <summary>The user's preferred width for the minimized video card, or 0 when none was saved.</summary>
    double MinimizedVideoWidth { get; set; }

    /// <summary>The persisted now-playing queue, or <c>null</c> when none was saved.</summary>
    PlaybackQueuePersistedState? PlaybackQueue { get; set; }

    /// <summary>The persisted desktop main-window placement, or <c>null</c> when none was saved.</summary>
    WindowPlacement? WindowPlacement { get; set; }

    /// <summary>Where the picture-in-picture window last was, or <c>null</c> when it has never been opened.</summary>
    PictureInPicturePlacement? PictureInPicturePlacement { get; set; }
}
