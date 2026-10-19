using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Subtitles;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;
using LightStudio.Logging;

namespace LightMediaRenderer.Avalonia.Pipeline;

public sealed partial class MediaDecodeSession
{
    /// <summary>
    /// Registers every audio stream of <paramref name="path"/> as a selectable track. The file is only
    /// probed here; nothing is decoded until one of the tracks is selected.
    /// </summary>
    public Task<IReadOnlyList<AudioTrack>> AddExternalAudioAsync(string path) => RunAuxiliaryAsync(async token =>
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (trackSync)
        {
            var known = audioTracks.FindAll(track => string.Equals(track.Source, path, StringComparison.Ordinal));
            if (known.Count > 0)
                return (IReadOnlyList<AudioTrack>)known;
        }

        var added = new List<AudioTrack>();
        foreach (MediaStreamInfo stream in await ProbeStreamsAsync(input.Sidecar(path), token).ConfigureAwait(false))
        {
            if (stream.Kind == MediaStreamKind.Audio)
                added.Add(new AudioTrack(stream, path, Info?.Source, DefaultTrackName));
        }

        if (added.Count == 0)
            throw new InvalidOperationException($"'{Path.GetFileName(path)}' contains no audio stream.");

        lock (trackSync)
        {
            audioTracks.AddRange(added);
            PublishAudioTracks();
        }

        AudioTracksChanged?.Invoke(this, EventArgs.Empty);
        return added;
    });

    /// <summary>
    /// Registers the subtitles of <paramref name="path"/> as selectable tracks: the script itself when
    /// the file is one, otherwise every supported subtitle stream of the container.
    /// </summary>
    public Task<IReadOnlyList<SubtitleTrack>> AddExternalSubtitleAsync(string path) => RunAuxiliaryAsync(async token =>
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (trackSync)
        {
            var known = subtitleTracks.FindAll(track => string.Equals(track.Source, path, StringComparison.Ordinal));
            if (known.Count > 0)
                return (IReadOnlyList<SubtitleTrack>)known;
        }

        var added = new List<SubtitleTrack>();
        if (SubtitleLoader.IsScriptFile(path))
        {
            added.Add(new SubtitleTrack(path, Info?.Source, DefaultTrackName));
        }
        else
        {
            foreach (MediaStreamInfo stream in await ProbeStreamsAsync(input.Sidecar(path), token).ConfigureAwait(false))
            {
                if (FfmpegSubtitleReader.IsSupported(stream))
                    added.Add(new SubtitleTrack(stream, path, Info?.Source, DefaultTrackName));
            }
        }

        if (added.Count == 0)
            throw new InvalidOperationException($"'{Path.GetFileName(path)}' contains no supported subtitle.");

        lock (trackSync)
        {
            subtitleTracks.AddRange(added);
            PublishSubtitleTracks();
        }

        SubtitleTracksChanged?.Invoke(this, EventArgs.Empty);
        return added;
    });

    /// <summary>
    /// Registers a subtitle file whose bytes are already in hand, such as one picked on a device or
    /// fetched from another source. Supported streams are decoded now and cached for selection.
    /// </summary>
    public Task<IReadOnlyList<SubtitleTrack>> AddSubtitleFileAsync(string path, byte[] content)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(content);
        IMediaDemuxer session = demuxer ?? throw new InvalidOperationException("No media is open.");
        return RunAuxiliaryAsync(token => AddSubtitleFileAsync(path, content, session, token));
    }

    private async Task<IReadOnlyList<SubtitleTrack>> AddSubtitleFileAsync(
        string path,
        byte[] content,
        IMediaDemuxer session,
        CancellationToken token)
    {
        lock (trackSync)
        {
            EnsureSubtitleSession(session, token);
            var known = subtitleTracks.FindAll(track => string.Equals(track.Source, path, StringComparison.Ordinal));
            if (known.Count > 0)
                return known;
        }

        if (SubtitleLoader.IsScriptFile(path))
        {
            AssDocument document;
            using (var script = new MemoryStream(content, writable: false))
                document = SubtitleLoader.Load(script, path);
            var track = new SubtitleTrack(path, Info?.Source, DefaultTrackName);
            lock (trackSync)
            {
                EnsureSubtitleSession(session, token);
                if (subtitleTracks.Find(candidate => string.Equals(candidate.Source, path, StringComparison.Ordinal)) is { } known)
                    return [known];
                subtitleDocuments[track] = document;
                subtitleTracks.Add(track);
                PublishSubtitleTracks();
            }

            SubtitleTracksChanged?.Invoke(this, EventArgs.Empty);
            return [track];
        }

        var file = new MemoryMediaInput(path, content);
        MediaStreamInfo[] streams = (await ProbeStreamsAsync(file, token).ConfigureAwait(false))
            .Where(FfmpegSubtitleReader.IsSupported).ToArray();
        if (streams.Length == 0)
            throw new InvalidOperationException($"'{Path.GetFileName(path)}' contains no supported subtitle.");

        var added = new List<SubtitleTrack>(streams.Length);
        var textDocuments = new Dictionary<SubtitleTrack, AssDocument>();
        var bitmapDocuments = new Dictionary<SubtitleTrack, BitmapSubtitleDocument>();
        foreach (MediaStreamInfo stream in streams)
        {
            var track = new SubtitleTrack(stream, path, Info?.Source, DefaultTrackName);
            if (track.IsBitmap)
            {
                bitmapDocuments[track] = await engine.ReadBitmapSubtitlesAsync(file, stream.Index, true, token)
                    .ConfigureAwait(false);
            }
            else
            {
                textDocuments[track] = SubtitleLoader.Parse(await engine.ReadSubtitleScriptAsync(file, stream.Index, token)
                    .ConfigureAwait(false));
            }

            added.Add(track);
        }

        lock (trackSync)
        {
            EnsureSubtitleSession(session, token);
            var known = subtitleTracks.FindAll(track => string.Equals(track.Source, path, StringComparison.Ordinal));
            if (known.Count > 0)
                return known;

            subtitleTracks.AddRange(added);
            foreach ((SubtitleTrack track, AssDocument document) in textDocuments)
                subtitleDocuments[track] = document;
            foreach ((SubtitleTrack track, BitmapSubtitleDocument document) in bitmapDocuments)
                bitmapSubtitleDocuments[track] = document;
            PublishSubtitleTracks();
        }

        SubtitleTracksChanged?.Invoke(this, EventArgs.Empty);
        return added;
    }

    private void EnsureSubtitleSession(IMediaDemuxer session, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(demuxer, session))
            throw new InvalidOperationException("The media changed while reading subtitles.");
    }

    /// <summary>
    /// Shows <paramref name="track"/>, or turns subtitles off when it is <c>null</c>. Returns at once:
    /// a track that has not been read yet reaches the screen through <see cref="SubtitlesChanged"/>
    /// when its stream has been gone over.
    /// </summary>
    public void SelectSubtitleTrack(SubtitleTrack? track)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (track is not null)
        {
            lock (trackSync)
            {
                if (!subtitleTracks.Contains(track))
                    throw new ArgumentException("The track does not belong to this player.", nameof(track));
            }
        }

        if (ReferenceEquals(activeSubtitleTrack, track))
            return;

        int generation = Interlocked.Increment(ref subtitleGeneration);
        activeSubtitleTrack = track;
        SubtitleTrackChanged?.Invoke(this, track);

        AssDocument? cachedText = null;
        BitmapSubtitleDocument? cachedBitmaps = null;
        if (track is not null)
        {
            lock (trackSync)
            {
                if (track.IsBitmap)
                    bitmapSubtitleDocuments.TryGetValue(track, out cachedBitmaps);
                else
                    subtitleDocuments.TryGetValue(track, out cachedText);
            }
        }

        if (track is null || cachedText is not null || cachedBitmaps is not null)
        {
            PublishSubtitles(cachedText, cachedBitmaps, generation);
            return;
        }

        // Clear what is on screen while the new track is read, so the old subtitle does not linger
        // over the picture it no longer belongs to.
        PublishSubtitles(null, null, generation);
        _ = RunAuxiliaryAsync(async token =>
        {
            try
            {
                if (track.IsBitmap)
                {
                    BitmapSubtitleDocument document = await input.ReadBitmapSubtitlesAsync(engine, track.StreamIndex,
                        SidecarOf(track), token).ConfigureAwait(false);
                    lock (trackSync)
                    {
                        if (subtitleTracks.Contains(track))
                            bitmapSubtitleDocuments[track] = document;
                    }

                    PublishSubtitles(null, document, generation);
                }
                else
                {
                    AssDocument document = await ReadSubtitlesAsync(track, token).ConfigureAwait(false);
                    lock (trackSync)
                    {
                        if (subtitleTracks.Contains(track))
                            subtitleDocuments[track] = document;
                    }

                    PublishSubtitles(document, null, generation);
                }
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                if (Volatile.Read(ref subtitleGeneration) == generation)
                {
                    string message = $"Cannot read those subtitles: {exception.Message}";
                    AppLog.Warning("Video.Subtitles", message, exception);
                    ErrorOccurred?.Invoke(this, message);
                }
            }

            return true;
        });
    }

    private async Task<AssDocument> ReadSubtitlesAsync(SubtitleTrack track, CancellationToken token)
    {
        if (track.IsScriptFile)
        {
            // Read whole first: browser files only read asynchronously.
            using var buffer = new MemoryStream();
            await using (Stream script = await input.OpenFileAsync(track.Source!, token).ConfigureAwait(false))
                await script.CopyToAsync(buffer, token).ConfigureAwait(false);
            buffer.Position = 0;
            return SubtitleLoader.Load(buffer, track.Source);
        }

        return SubtitleLoader.Parse(await input.ReadSubtitleScriptAsync(engine, track.StreamIndex, SidecarOf(track), token)
            .ConfigureAwait(false));
    }

    private string? SidecarOf(SubtitleTrack track) => IsOwnedByMainSource(track.StreamIndex, track.Source) ? null : track.Source;

    /// <summary>
    /// Whether the session's own source already delivers this track. A remote source merges the
    /// sidecars into one stream table, so a track can be external without needing a second reader.
    /// </summary>
    private bool IsOwnedByMainSource(int streamIndex, string? source)
    {
        if (Info is null)
            return false;
        if (source is null)
            return true;
        foreach (MediaStreamInfo stream in Info.Streams)
        {
            if (stream.Index == streamIndex && string.Equals(stream.SourcePath, source, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private void PublishSubtitles(AssDocument? document, BitmapSubtitleDocument? bitmapDocument, int generation)
    {
        // A read that outlived its selection must not put its subtitles back on screen.
        if (Volatile.Read(ref subtitleGeneration) != generation)
            return;

        subtitles = document;
        bitmapSubtitles = bitmapDocument;
        SubtitlesChanged?.Invoke(this, document);
        BitmapSubtitlesChanged?.Invoke(this, bitmapDocument);
    }

    private void PublishAudioTracks() => Volatile.Write(ref audioTrackSnapshot, audioTracks.ToArray());

    private void PublishSubtitleTracks() => Volatile.Write(ref subtitleTrackSnapshot, subtitleTracks.ToArray());

    private AudioTrack? FindAudioTrack(int streamIndex, string? source)
    {
        lock (trackSync)
            return audioTracks.Find(track => track.StreamIndex == streamIndex && track.Source == source);
    }

    private async Task<IReadOnlyList<MediaStreamInfo>> ProbeStreamsAsync(MediaInput file, CancellationToken token)
    {
        IMediaDemuxer probe = await engine.OpenDemuxerAsync(file, token).ConfigureAwait(false);
        try
        {
            return probe.Info.Streams;
        }
        finally
        {
            await probe.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Runs work outside the command order that the session waits for when it closes.</summary>
    private Task<T> RunAuxiliaryAsync<T>(Func<CancellationToken, Task<T>> operation)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (demuxer is null)
                throw new InvalidOperationException("No media is open.");
        }

        Task<T> task = operation(lifetime.Token);
        lock (auxiliaryOperations)
        {
            auxiliaryOperations.RemoveAll(completed => completed.IsCompleted);
            auxiliaryOperations.Add(task);
        }

        return task;
    }
}
