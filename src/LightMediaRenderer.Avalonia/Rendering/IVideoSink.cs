using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;

namespace LightMediaRenderer.Avalonia.Rendering;

/// <summary>
/// Receives frames that are due for display. Implemented by the Avalonia control, but nothing in
/// the pipeline depends on Avalonia: an off-screen encoder or a test double implements the same
/// two methods.
/// </summary>
public interface IVideoSink
{
    /// <summary>
    /// Called from the video scheduler thread when <paramref name="frame"/> reaches its
    /// presentation time. The sink must call <see cref="VideoFrame.AddRef"/> if it keeps the frame
    /// past the call and <see cref="VideoFrame.Release"/> when done; the scheduler releases its own
    /// reference immediately after this returns.
    /// </summary>
    void Present(VideoFrame frame);

    /// <summary>Drops any retained frame, e.g. when playback stops.</summary>
    void ClearVideo();
}

/// <summary>A sink that tells when it has taken the frame presented to it last for drawing.</summary>
public interface IVideoDrawFeedback
{
    /// <summary>Whether the most recently presented frame has not been taken for drawing yet.</summary>
    bool IsDrawPending { get; }

    /// <summary>Completes when the sink next takes a frame for drawing, or at once when none is waiting.</summary>
    Task WaitForDrawAsync(CancellationToken cancellationToken);
}
