using System;

namespace LightStudio.FfmpegShim.Core;

/// <summary>Which component owns the master timeline.</summary>
public enum ClockSource
{
    /// <summary>Audio playout position. The default: audio must never be stretched to match video.</summary>
    Audio,

    /// <summary>Last presented video frame. Used for video-only content.</summary>
    Video,

    /// <summary>Free-running system clock. Fallback when neither renderer is producing.</summary>
    External,
}

/// <summary>
/// A single timeline: a presentation timestamp anchored to a monotonic reading, so the current
/// position can be interpolated between the sparse updates a renderer produces. Its pts drift and
/// speed model degrades gracefully when a renderer stalls.
/// </summary>
public sealed class PlaybackClock
{
    private readonly object sync = new();
    private double pts = MediaTime.NoTimestamp;
    private double ptsDrift;
    private double lastUpdated;
    private double speed = 1.0;
    private int serial = -1;
    private bool paused;

    /// <summary>Serial of the packet run this clock was last set from; stale runs are ignored.</summary>
    public int Serial
    {
        get { lock (sync) return serial; }
    }

    public bool IsPaused
    {
        get { lock (sync) return paused; }
    }

    /// <summary>Playback rate multiplier. 1.0 is real time.</summary>
    public double Speed
    {
        get { lock (sync) return speed; }
        set
        {
            lock (sync)
            {
                // Re-anchor before changing the rate, otherwise the elapsed time since the last
                // update would retroactively be reinterpreted at the new rate.
                SetLocked(GetLocked(), serial);
                speed = value;
            }
        }
    }

    /// <summary>Current position in seconds, or NaN when the clock has not been anchored yet.</summary>
    public double Get()
    {
        lock (sync) return GetLocked();
    }

    public void Set(double timestamp, int packetSerial) =>
        SetAt(timestamp, packetSerial, MediaTime.Now);

    public void SetAt(double timestamp, int packetSerial, double time)
    {
        lock (sync)
        {
            pts = timestamp;
            lastUpdated = time;
            ptsDrift = timestamp - time;
            serial = packetSerial;
        }
    }

    public void Pause()
    {
        lock (sync)
        {
            if (paused)
                return;
            pts = GetLocked();
            lastUpdated = MediaTime.Now;
            ptsDrift = pts - lastUpdated;
            paused = true;
        }
    }

    public void Resume()
    {
        lock (sync)
        {
            if (!paused)
                return;
            // Absorb the paused interval so the timeline does not jump forward on resume.
            lastUpdated = MediaTime.Now;
            ptsDrift = pts - lastUpdated;
            paused = false;
        }
    }

    /// <summary>Invalidates the clock: <see cref="Get"/> returns NaN until it is anchored again.</summary>
    public void Reset(int packetSerial)
    {
        lock (sync)
        {
            pts = MediaTime.NoTimestamp;
            ptsDrift = 0;
            lastUpdated = MediaTime.Now;
            serial = packetSerial;
        }
    }

    private double GetLocked()
    {
        if (double.IsNaN(pts))
            return MediaTime.NoTimestamp;
        if (paused)
            return pts;

        double now = MediaTime.Now;
        return ptsDrift + now - (now - lastUpdated) * (1.0 - speed);
    }

    private void SetLocked(double timestamp, int packetSerial)
    {
        double now = MediaTime.Now;
        pts = timestamp;
        lastUpdated = now;
        ptsDrift = timestamp - now;
        serial = packetSerial;
    }
}

/// <summary>
/// Chooses the master timeline and exposes it to the synchronizer. Audio is preferred whenever an
/// audio renderer is running: the audio device consumes samples at its own fixed rate and cannot be
/// delayed, so everything else follows it.
/// </summary>
public sealed class MasterClock
{
    public PlaybackClock Audio { get; } = new();

    public PlaybackClock Video { get; } = new();

    public PlaybackClock External { get; } = new();

    /// <summary>Preferred source; falls through to the next usable clock when it is unset.</summary>
    public ClockSource Preferred { get; set; } = ClockSource.Audio;

    /// <summary>The source actually driving playback right now.</summary>
    public ClockSource Active { get; private set; } = ClockSource.External;

    public double Speed
    {
        get => External.Speed;
        set
        {
            Audio.Speed = value;
            Video.Speed = value;
            External.Speed = value;
        }
    }

    public double Get()
    {
        if (Preferred is ClockSource.Audio or ClockSource.Video)
        {
            PlaybackClock preferredClock = Preferred == ClockSource.Audio ? Audio : Video;
            double preferred = preferredClock.Get();
            if (MediaTime.IsValid(preferred))
            {
                Active = Preferred;
                return preferred;
            }

            // Video can start before the audio device publishes its first playout position (and
            // vice versa). Follow real output from the other renderer rather than inventing time.
            ClockSource alternateSource = Preferred == ClockSource.Audio ? ClockSource.Video : ClockSource.Audio;
            PlaybackClock alternateClock = alternateSource == ClockSource.Audio ? Audio : Video;
            double alternate = alternateClock.Get();
            if (MediaTime.IsValid(alternate))
            {
                Active = alternateSource;
                return alternate;
            }
        }

        double external = External.Get();
        Active = ClockSource.External;
        return external;
    }

    public void Pause()
    {
        Audio.Pause();
        Video.Pause();
        External.Pause();
    }

    public void Resume()
    {
        Audio.Resume();
        Video.Resume();

        // A renderer clock is invalid until its first frame reaches the output. Letting the
        // external fallback run in that interval invents progress which is then discarded when
        // the renderer publishes its first timestamp (most visible while opening a slow source).
        bool preferredClockIsReady = Preferred switch
        {
            ClockSource.Audio => MediaTime.IsValid(Audio.Get()),
            ClockSource.Video => MediaTime.IsValid(Video.Get()),
            _ => true,
        };

        if (preferredClockIsReady)
            External.Resume();
        else
        {
            // An invalid master makes the video scheduler present its first frame immediately;
            // MediaPlayer retains the requested position separately for UI reporting meanwhile.
            External.Pause();
            External.Reset(External.Serial);
        }
    }

    /// <summary>Invalidates every timeline; used when seeking or stopping.</summary>
    public void Reset(int serial)
    {
        Audio.Reset(serial);
        Video.Reset(serial);
        External.Reset(serial);
    }

    /// <summary>
    /// Anchors the external clock so a paused seek or an audio-device stall still reports a
    /// sensible position to the UI and to the video scheduler.
    /// </summary>
    public void SetExternal(double timestamp, int serial) => External.Set(timestamp, serial);
}
