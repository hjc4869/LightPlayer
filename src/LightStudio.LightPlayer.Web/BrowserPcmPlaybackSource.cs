using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using LightMediaRenderer.Avalonia.Audio;
using LightMediaRenderer.Avalonia.Pipeline;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
internal sealed class BrowserPcmPlaybackSource : IPcmPlaybackSource
{
    private readonly PcmPlaybackRequest request;
    private readonly MediaDecodeSession session;
    private AudioFrame? inFlight;

    internal BrowserPcmPlaybackSource(PcmPlaybackRequest request, IClientMediaResolver resolver)
    {
        this.request = request;
        var options = new MediaPlayerOptions
        {
            AudioSampleRate = request.AlwaysResample ? request.PreferredSampleRate : 0,
        };
        var input = new BrowserSessionInput(request.Item.FilePath, async cancellationToken =>
        {
            var platform = await resolver.ResolvePlatformAsync(new(request.Item.SourceId, request.Item.FilePath), cancellationToken);
            return await platform.OpenMediaFileAsync(request.Item.FilePath, cancellationToken);
        });
        session = new MediaDecodeSession(options, input, WorkerMediaEngine.Main, audioOnly: true, audioBitsPerSample: 24);
    }

    public AudioFormat Format { get; private set; }
    public string? Title => session.Info?.Metadata.GetValueOrDefault("title");
    public TimeSpan Duration => session.Info?.Duration ?? TimeSpan.Zero;
    public TimeSpan Position { get; private set; }
    public string? LastError => session.Failure?.Message;
    public bool UsesAudioSeparation => false;
    public bool RequiresRestartOnSeek => false;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await session.OpenAsync(cancellationToken);
        if (!session.HasAudio) throw new InvalidDataException("The media has no playable audio stream.");
        Format = new AudioFormat((uint)session.AudioFormat.SampleRate, (uint)session.AudioFormat.ChannelCount, (uint)session.AudioFormat.BitsPerSample);
        if (request.StartPosition > TimeSpan.Zero) await SeekAsync(request.StartPosition, cancellationToken);
        while (session.AudioFrames.Count == 0)
        {
            int version = session.AudioFrames.Version;
            ThrowIfFailed();
            if (session.AudioFrames.Count == 0) await session.AudioFrames.WaitForChangeAsync(version, cancellationToken);
        }
    }

    public bool TryRead(TimeSpan maximumMediaDuration, out AudioOutputReadResult block)
    {
        inFlight?.Recycle();
        inFlight = null;
        ThrowIfFailed();
        if (request.EndPosition is { } end && end - Position < maximumMediaDuration) maximumMediaDuration = end - Position;
        if (maximumMediaDuration <= TimeSpan.Zero) { block = AudioOutputReadResult.Complete; return true; }
        if (!session.AudioFrames.TryRead(out var frame)) { block = default; return false; }
        if (frame.IsEndOfStream) { frame.Recycle(); block = AudioOutputReadResult.Complete; return true; }
        inFlight = frame;
        int length = frame.Length;
        if (frame.Duration > maximumMediaDuration.TotalSeconds)
            length = (int)Math.Min(length, Math.Floor((maximumMediaDuration.TotalSeconds + 2d / TimeSpan.TicksPerSecond)
                * Format.SampleRate) * Format.BlockAlign);
        if (length <= 0) { block = AudioOutputReadResult.Complete; return true; }
        TimeSpan duration = Format.GetDuration(length);
        Position = TimeSpan.FromSeconds(frame.Pts) + duration;
        block = AudioOutputReadResult.FromBuffer(frame.Buffer[..length], duration, TimeSpan.FromSeconds(frame.Pts), frame.Serial);
        return true;
    }

    public async Task<TimeSpan> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        await session.SeekAsync(position.TotalSeconds, cancellationToken);
        inFlight?.Recycle();
        inFlight = null;
        Position = position;
        return position;
    }

    private void ThrowIfFailed()
    {
        if (session.Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public async ValueTask DisposeAsync()
    {
        await session.DisposeAsync();
        inFlight?.Recycle();
        inFlight = null;
    }
}
