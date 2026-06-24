using System.Diagnostics;

namespace LightStudio.LightPlayer.Tools.AudioProbe;

internal static class AudioProbePlayer
{
    private static readonly TimeSpan DefaultBufferTarget = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan LoopDelay = TimeSpan.FromMilliseconds(15);

    public static async Task<PlaybackResult> PlayAsync(
        string mediaFile,
        PlaybackOptions options,
        TextWriter log,
        CancellationToken cancellationToken = default)
    {
        using var source = new FfmpegPcmFrameSource(mediaFile, options.Start);
        using var output = new OpenAlAudioOutputDevice(source.Format)
        {
            Volume = options.Volume
        };

        output.BackendError += (_, message) => log.WriteLine($"Backend error: {message}");
        output.Underrun += (_, message) => log.WriteLine($"Underrun: {message}");

        log.WriteLine($"Input: {source.MediaFile}");
        log.WriteLine($"Title: {source.Metadata.Title ?? Path.GetFileName(source.MediaFile)}");
        log.WriteLine($"Duration: {source.Duration}");
        log.WriteLine($"Start: {source.Position}");
        log.WriteLine($"Format: {source.Format.SampleRate} Hz, {source.Format.ChannelCount} channel(s), {source.Format.BitsPerSample}-bit PCM");

        var stopwatch = Stopwatch.StartNew();
        var decodedDuration = TimeSpan.Zero;
        var decodedBytes = 0L;
        var frameCount = 0;
        var sourceEnded = false;
        var started = false;
        var pauseExercised = false;
        var seekExercised = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            output.Update();

            if (!pauseExercised && options.PauseAfter.HasValue && output.PlayedDuration >= options.PauseAfter.Value)
            {
                log.WriteLine($"Pausing at estimated output position {output.PlayedDuration} for {options.PauseDuration}.");
                output.Pause();
                await Task.Delay(options.PauseDuration, cancellationToken);
                output.Resume();
                log.WriteLine($"Resumed at estimated output position {output.PlayedDuration}.");
                pauseExercised = true;
            }

            if (!seekExercised && options.SeekAt.HasValue && options.SeekTo.HasValue && output.PlayedDuration >= options.SeekAt.Value)
            {
                output.ResetAfterSeek();
                var actual = source.Seek(options.SeekTo.Value);
                decodedDuration = TimeSpan.Zero;
                sourceEnded = false;
                started = false;
                seekExercised = true;
                log.WriteLine($"Seek reset requested {options.SeekTo.Value}, actual {actual}.");
            }

            while (!sourceEnded && decodedDuration < options.Duration && output.BufferedDuration < options.BufferTarget)
            {
                var frame = source.ReadFrame(options.Duration - decodedDuration);
                if (frame is null)
                {
                    sourceEnded = true;
                    break;
                }

                output.QueueBuffer(frame.Data);
                decodedDuration += frame.Duration;
                decodedBytes += frame.Data.Length;
                frameCount++;
            }

            if (!started && output.QueuedBufferCount > 0)
            {
                output.Start();
                started = true;
                log.WriteLine("Playback started.");
            }

            if ((sourceEnded || decodedDuration >= options.Duration) && output.QueuedBufferCount == 0)
            {
                break;
            }

            if (!started && sourceEnded)
            {
                break;
            }

            await Task.Delay(LoopDelay, cancellationToken);
        }

        output.Update();
        var played = output.PlayedDuration;
        output.Stop();
        stopwatch.Stop();

        return new PlaybackResult(
            Path.GetFullPath(mediaFile),
            decodedBytes,
            frameCount,
            decodedDuration,
            played,
            stopwatch.Elapsed,
            output.UnderrunCount,
            source.LastFfmpegError);
    }

    public static PlaybackOptions CreateOptions(
        TimeSpan duration,
        TimeSpan start,
        float volume,
        TimeSpan? pauseAfter,
        TimeSpan pauseDuration,
        TimeSpan? seekAt,
        TimeSpan? seekTo)
    {
        return new PlaybackOptions(
            duration,
            start,
            volume,
            DefaultBufferTarget,
            pauseAfter,
            pauseDuration,
            seekAt,
            seekTo);
    }
}

internal sealed record PlaybackOptions(
    TimeSpan Duration,
    TimeSpan Start,
    float Volume,
    TimeSpan BufferTarget,
    TimeSpan? PauseAfter,
    TimeSpan PauseDuration,
    TimeSpan? SeekAt,
    TimeSpan? SeekTo);

internal sealed record PlaybackResult(
    string MediaFile,
    long DecodedBytes,
    int FrameCount,
    TimeSpan DecodedDuration,
    TimeSpan PlayedDuration,
    TimeSpan Elapsed,
    int UnderrunCount,
    int LastFfmpegError);