using System;

namespace LightMediaRenderer.Avalonia.Audio;

/// <summary>
/// A point on the source's media timeline, tagged with the seek generation it belongs to so a
/// consumer can discard readings that predate its own seek.
/// </summary>
public readonly record struct AudioTimelinePosition(TimeSpan MediaPosition, int Serial, bool HasValue)
{
    public static AudioTimelinePosition None => default;
}

/// <summary>
/// Maps a backend's playout cursor back onto the media timeline, so the position that is audible
/// right now can be recovered rather than the position that was most recently decoded. This is what
/// turns the pull callback into a clock A/V synchronization can follow.
/// </summary>
/// <remarks>
/// Offsets are in whatever unit the backend already counts its playout cursor in -- bytes for the
/// native backends, frames for WebAudio. The map only requires that <see cref="Append"/> and
/// <see cref="Resolve"/> agree on that unit and share an origin.
/// </remarks>
public sealed class AudioTimelineMap
{
    // Covers the deepest buffer target even when the decoder emits very short frames; the oldest
    // anchor is dropped on overflow, which is exactly the one the cursor has already passed.
    private const int Capacity = 512;

    private readonly Anchor[] anchors = new Anchor[Capacity];
    private readonly object gate = new();
    private int start;
    private int count;

    /// <summary>Records where a pulled block lands, ignoring blocks the producer left unstamped.</summary>
    public void Append(long startOffset, int length, in AudioOutputReadResult result)
    {
        if (result.MediaPosition is { } mediaPosition)
        {
            Append(startOffset, length, mediaPosition, result.Duration, result.Serial);
        }
    }

    public void Append(long startOffset, int length, TimeSpan mediaPosition, TimeSpan mediaDuration, int serial)
    {
        if (length <= 0)
        {
            return;
        }

        lock (gate)
        {
            if (count == Capacity)
            {
                start = (start + 1) % Capacity;
                count--;
            }

            anchors[(start + count) % Capacity] =
                new Anchor(startOffset, length, mediaPosition, mediaDuration, serial);
            count++;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            start = 0;
            count = 0;
        }
    }

    /// <summary>
    /// Interpolates the media position of <paramref name="playedOffset"/>. A cursor past the newest
    /// anchor clamps to the end of that block, which is where the device ran out of stamped audio.
    /// </summary>
    public AudioTimelinePosition Resolve(long playedOffset)
    {
        lock (gate)
        {
            // Newest first: the cursor normally sits inside one of the last blocks handed over.
            for (var i = count - 1; i >= 0; i--)
            {
                ref readonly var anchor = ref anchors[(start + i) % Capacity];
                if (playedOffset < anchor.StartOffset && i > 0)
                {
                    continue;
                }

                var progress = Math.Clamp(
                    (double)(playedOffset - anchor.StartOffset) / anchor.Length,
                    0.0,
                    1.0);
                return new AudioTimelinePosition(
                    anchor.MediaPosition + anchor.MediaDuration * progress,
                    anchor.Serial,
                    true);
            }

            return AudioTimelinePosition.None;
        }
    }

    private readonly record struct Anchor(
        long StartOffset,
        int Length,
        TimeSpan MediaPosition,
        TimeSpan MediaDuration,
        int Serial);
}
