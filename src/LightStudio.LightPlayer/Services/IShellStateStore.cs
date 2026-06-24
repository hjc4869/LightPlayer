namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Persists small pieces of shell layout state across launches, such as whether
/// the now-playing queue pane is open. Later migration tasks fold this into a
/// fuller settings store; for the shell it only needs to round-trip a few flags.
/// </summary>
public interface IShellStateStore
{
    bool IsQueuePaneOpen { get; set; }

    bool IsNavigationExpanded { get; set; }

    /// <summary>The persisted now-playing queue, or <c>null</c> when none was saved.</summary>
    PlaybackQueuePersistedState? PlaybackQueue { get; set; }
}
