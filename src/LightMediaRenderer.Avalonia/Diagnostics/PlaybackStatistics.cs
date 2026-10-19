using System;
using System.Text;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Decoding;
using LightMediaRenderer.Avalonia.Rendering;

namespace LightMediaRenderer.Avalonia.Diagnostics;

/// <summary>Immutable snapshot of the pipeline, cheap enough to poll a few times per second.</summary>
public sealed record PlaybackStatistics
{
    public static PlaybackStatistics Empty { get; } = new();

    public PlaybackState State { get; init; } = PlaybackState.Stopped;

    public TimeSpan Position { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>Which timeline is currently driving presentation.</summary>
    public ClockSource MasterClock { get; init; } = ClockSource.External;

    // Queue depths
    public int VideoPacketCount { get; init; }

    public int AudioPacketCount { get; init; }

    public int VideoFrameCount { get; init; }

    public int AudioFrameCount { get; init; }

    public long VideoPacketBytes { get; init; }

    public long AudioPacketBytes { get; init; }

    /// <summary>0..1 fill level of the busiest queue: what "buffering" actually means here.</summary>
    public double BufferOccupancy { get; init; }

    // Throughput
    public double DecodeFps { get; init; }

    public double RenderFps { get; init; }

    public long DecodedVideoFrames { get; init; }

    public long PresentedVideoFrames { get; init; }

    public long DroppedVideoFrames { get; init; }

    public long LateVideoFrames { get; init; }

    // Latency and sync
    /// <summary>Audio currently queued by the output device, not measured speaker latency.</summary>
    public TimeSpan AudioLatency { get; init; }

    /// <summary>Lead time between choosing a frame and its presentation deadline.</summary>
    public TimeSpan VideoLatency { get; init; }

    /// <summary>Video PTS minus master clock at the last presentation. Positive means video is ahead.</summary>
    public TimeSpan AudioVideoDrift { get; init; }

    public int AudioUnderruns { get; init; }

    public long DecodeErrors { get; init; }

    public double Speed { get; init; } = 1.0;

    /// <summary>The video decoder's configuration, which is what most playback reports turn on.</summary>
    public VideoDecoderDiagnostics Decoder { get; init; } = VideoDecoderDiagnostics.None;

    /// <summary>Codec of the audio being heard, empty when the media has none.</summary>
    public string AudioCodecName { get; init; } = string.Empty;

    /// <summary>Format the audio device was opened with, empty when there is no audio.</summary>
    public string AudioOutputFormat { get; init; } = string.Empty;

    /// <summary>Where the packets come from, which is the first thing a playback report needs.</summary>
    public string MediaSource { get; init; } = string.Empty;

    public bool HasBufferedPacketSource { get; init; }

    /// <summary>Fetched media ahead of the current playback position.</summary>
    public TimeSpan SourceBufferRemaining { get; init; }

    /// <summary>Retained media behind the current playback position.</summary>
    public TimeSpan SourceBufferHistory { get; init; }

    public double EstimatedBandwidthBitsPerSecond { get; init; }

    public string ToDisplayString()
    {
        VideoOutputStatus output = VideoOutputDiagnostics.Current;
        var text = new DiagnosticsText();

        string decodeLatency = Decoder.EstimatedDecodeLatencyMilliseconds > 0
            ? $" latency ~{Decoder.EstimatedDecodeLatencyMilliseconds:0.0} ms"
            : string.Empty;
        text.Add("State", $"{State}  {Speed:0.##}x  {Position:hh\\:mm\\:ss\\.ff} / {Duration:hh\\:mm\\:ss\\.ff}  clock {MasterClock}");
        text.Add("Rate", $"decode {DecodeFps:0.0} fps{decodeLatency}  present {RenderFps:0.0} fps");

        string decodeCost = Decoder.DecodeFrameTimeMilliseconds > 0
            ? $"decode {Decoder.DecodeFrameTimeMilliseconds:0.00} ms/f"
            : "decode measuring";
        string drawCost = output.RenderCpuFrameTimeMilliseconds > 0
            ? $"draw {output.RenderCpuFrameTimeMilliseconds:0.00} ms/f"
            : "draw measuring";
        text.Add("Cost", $"{decodeCost}  {drawCost}");

        text.Add("Frames", $"dec {DecodedVideoFrames}  shown {PresentedVideoFrames}  drop {DroppedVideoFrames}  late {LateVideoFrames}  err {DecodeErrors}");

        text.Add("Sync", $"A/V {AudioVideoDrift.TotalMilliseconds:+0.0;-0.0;0.0} ms lead {VideoLatency.TotalMilliseconds:0.0} ms  audio buffer {AudioLatency.TotalMilliseconds:0.0} ms  under {AudioUnderruns}");

        if (!string.IsNullOrEmpty(MediaSource))
            text.Add("Source", MediaSource);
        if (HasBufferedPacketSource)
        {
            string bandwidth = EstimatedBandwidthBitsPerSecond > 0
                ? $"~{EstimatedBandwidthBitsPerSecond / 1_000_000.0:0.0} Mbps"
                : "measuring";
            text.Add(
                "Net",
                $"buffer {SourceBufferRemaining.TotalSeconds:0.0} s ahead  " +
                $"{SourceBufferHistory.TotalSeconds:0.0} s back  bw {bandwidth}");
        }

        text.Add("Packet q", $"video {VideoPacketCount} / {VideoPacketBytes / 1024} KiB  audio {AudioPacketCount} / {AudioPacketBytes / 1024} KiB");
        text.AddBlock(Decoder.ToDisplayString());
        text.Add("Frame q", $"video {VideoFrameCount}  audio {AudioFrameCount}  fill {BufferOccupancy:P0}");

        if (!string.IsNullOrEmpty(AudioCodecName))
            text.Add("Audio", $"{AudioCodecName}  out {AudioOutputFormat}");

        text.AddBlock(output.ToDisplayString());
        return text.ToString();
    }
}

/// <summary>
/// Mutable counters written by the pipeline threads. Every field is either interlocked or a
/// tolerated-stale double, so collecting statistics never synchronizes with playback.
/// </summary>
public sealed class StatisticsCollector
{
    private long decodedVideoFrames;
    private long presentedVideoFrames;
    private long droppedVideoFrames;
    private long lateVideoFrames;
    private long decodeErrors;

    private double decodeFpsWindowStart;
    private long decodeFpsWindowBase;
    private double renderFpsWindowStart;
    private long renderFpsWindowBase;

    public double DecodeFps { get; private set; }

    public double RenderFps { get; private set; }

    public double AudioVideoDrift { get; set; }

    public double VideoLatency { get; set; }

    public long DecodedVideoFrames => System.Threading.Interlocked.Read(ref decodedVideoFrames);

    public long PresentedVideoFrames => System.Threading.Interlocked.Read(ref presentedVideoFrames);

    public long DroppedVideoFrames => System.Threading.Interlocked.Read(ref droppedVideoFrames);

    public long LateVideoFrames => System.Threading.Interlocked.Read(ref lateVideoFrames);

    public long DecodeErrors => System.Threading.Interlocked.Read(ref decodeErrors);

    public void CountDecodedVideoFrame() => System.Threading.Interlocked.Increment(ref decodedVideoFrames);

    public void CountPresentedVideoFrame() => System.Threading.Interlocked.Increment(ref presentedVideoFrames);

    public void CountDroppedVideoFrame() => System.Threading.Interlocked.Increment(ref droppedVideoFrames);

    public void CountLateVideoFrame() => System.Threading.Interlocked.Increment(ref lateVideoFrames);

    public void CountDecodeError() => System.Threading.Interlocked.Increment(ref decodeErrors);

    /// <summary>Recomputes the sliding rates. Called from the statistics poller, not from a hot path.</summary>
    public void Sample()
    {
        double now = MediaTime.Now;

        if (decodeFpsWindowStart == 0)
        {
            decodeFpsWindowStart = now;
            renderFpsWindowStart = now;
            decodeFpsWindowBase = DecodedVideoFrames;
            renderFpsWindowBase = PresentedVideoFrames;
            return;
        }

        double decodeElapsed = now - decodeFpsWindowStart;
        if (decodeElapsed >= 0.5)
        {
            long decoded = DecodedVideoFrames;
            DecodeFps = (decoded - decodeFpsWindowBase) / decodeElapsed;
            decodeFpsWindowBase = decoded;
            decodeFpsWindowStart = now;
        }

        double renderElapsed = now - renderFpsWindowStart;
        if (renderElapsed >= 0.5)
        {
            long presented = PresentedVideoFrames;
            RenderFps = (presented - renderFpsWindowBase) / renderElapsed;
            renderFpsWindowBase = presented;
            renderFpsWindowStart = now;
        }
    }

    public void Reset()
    {
        System.Threading.Interlocked.Exchange(ref decodedVideoFrames, 0);
        System.Threading.Interlocked.Exchange(ref presentedVideoFrames, 0);
        System.Threading.Interlocked.Exchange(ref droppedVideoFrames, 0);
        System.Threading.Interlocked.Exchange(ref lateVideoFrames, 0);
        System.Threading.Interlocked.Exchange(ref decodeErrors, 0);
        DecodeFps = 0;
        RenderFps = 0;
        AudioVideoDrift = 0;
        VideoLatency = 0;
        decodeFpsWindowStart = 0;
        renderFpsWindowStart = 0;
    }
}
