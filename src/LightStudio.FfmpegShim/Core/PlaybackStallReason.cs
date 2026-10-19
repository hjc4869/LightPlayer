namespace LightStudio.FfmpegShim.Core;

/// <summary>
/// Why the pipeline is currently unable to hand the renderers anything. Reported separately from
/// <see cref="PlaybackState"/> because a stall is not a state the user asked for: playback is still
/// nominally playing (or paused on a picture that is being decoded) and resumes on its own.
/// </summary>
public enum PlaybackStallReason
{
    /// <summary>Playback is progressing normally.</summary>
    None,

    /// <summary>
    /// A seek is decoding its way from the keyframe to the requested position. Applies whether or
    /// not playback was running: the picture only changes once that frame exists.
    /// </summary>
    Seeking,

    /// <summary>The queues ran dry -- a slow source, a slow network or a decoder that fell behind.</summary>
    Buffering,
}
