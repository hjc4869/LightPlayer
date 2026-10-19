namespace LightStudio.FfmpegShim.Core;

/// <summary>
/// Explicit playback states. Every player operation is a transition between these; components
/// never infer "is playing" from queue or device state.
/// </summary>
public enum PlaybackState
{
    Stopped,
    Opening,
    Buffering,
    Playing,
    Paused,
    Seeking,
    EndOfStream,
    Error,
}
