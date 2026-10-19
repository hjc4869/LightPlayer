using System;

namespace LightStudio.LightPlayer.Services;

/// <summary>How a platform shows the picture outside the page it belongs to.</summary>
public enum PictureInPictureKind
{
    /// <summary>
    /// The host opens a surface of its own and puts a view of ours in it: a small always-on-top
    /// window on the desktop, a document picture-in-picture window in the browser.
    /// </summary>
    DetachedView,

    /// <summary>
    /// The host shrinks the running app itself and nothing is re-hosted, so the page only has to
    /// put its controls away (Android).
    /// </summary>
    HostManaged,
}

/// <param name="DataContext">
/// View model the detached view binds to. Ignored by <see cref="PictureInPictureKind.HostManaged"/>
/// hosts, which show the app's own window rather than a view of ours.
/// </param>
/// <param name="AspectRatio">
/// Picture width over height, which is what the window is sized to. 0 when the video's shape is not
/// known yet, leaving the host to pick.
/// </param>
public sealed record PictureInPictureRequest(object DataContext, double AspectRatio);

/// <summary>
/// Detaches video playback from the page it is on. The pipeline is never told: the detached view
/// takes over as the player's video sink, so the clock, the audio and the decoders carry on
/// untouched and the position moves across with the picture.
/// </summary>
public interface IPictureInPictureHost
{
    PictureInPictureKind Kind { get; }

    /// <summary>Whether this device can do it at all. The page hides its button when it cannot.</summary>
    bool IsAvailable { get; }

    bool IsActive { get; }

    /// <summary>
    /// Raised when the mode is entered or left, including when the user closes the detached window
    /// rather than the app asking for it.
    /// </summary>
    event Action<bool>? ActiveChanged;

    /// <summary>Does nothing when already active, or when the platform cannot detach the picture.</summary>
    void Enter(PictureInPictureRequest request);

    /// <summary>Does nothing when not active.</summary>
    void Exit();

    /// <summary>
    /// Offers the picture to a host that can take it without being asked, so leaving the app while
    /// a video runs shrinks it rather than putting it away; null withdraws the offer. Hosts that
    /// open a window of their own do nothing here: that window is only ever opened on purpose.
    /// </summary>
    void Offer(PictureInPictureRequest? request)
    {
    }
}
