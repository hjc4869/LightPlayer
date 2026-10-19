using LightStudio.LightPlayer.Video.Pipeline;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Interop;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;
using LightStudio.FfmpegShim.Transcoding;
using LightStudio.LightPlayer.Remote;
using LightMediaRenderer.Avalonia.Pipeline;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.RemoteApi.Streaming;

/// <summary>
/// One socket's view of one video: the containers it needs open, the tracks they add up to, and the
/// runs of packets those tracks are delivered through.
/// </summary>
/// <remarks>
/// <para>
/// The point of demuxing here rather than on the client is that a container with several sound
/// tracks only has to give up the one being listened to. Unselected streams are discarded inside
/// libavformat, so they are never read off the disk, let alone sent.
/// </para>
/// <para>
/// A track may also be re-encoded on the way out, when the client asked for that and this server
/// can do it. What that produces is settled here, as the session opens, so the manifest describes
/// what will actually arrive rather than what the container happens to hold.
/// </para>
/// <para>
/// Nothing outlives the socket that opened it, and only one run may use the containers at a time.
/// A seek is a new run, which the connection starts once the one it replaces has let go.
/// </para>
/// </remarks>
internal sealed class StreamSession : IDisposable
{
    /// <summary>
    /// Packets a track may be given before it is expected to have configured itself. A frame
    /// threaded decoder holds tens of them before the first picture comes out.
    /// </summary>
    private const int MaximumPrimePackets = 512;

    private readonly SourceEntry[] sources;
    private readonly TrackEntry[] tracks;
    private bool disposed;

    private StreamSession(SourceEntry[] sources, TrackEntry[] tracks, RemoteStreamManifest manifest)
    {
        this.sources = sources;
        this.tracks = tracks;
        Manifest = manifest;
    }

    public RemoteStreamManifest Manifest { get; }

    private sealed record SourceEntry(FfmpegDemuxer Demuxer, string Path, string EntityTag);

    private sealed record TrackEntry(int Id, int SourceIndex, int StreamIndex, TranscodeRecipe? Recipe);

    private readonly record struct StreamIdentity(string? SourcePath, int StreamIndex);

    /// <summary>
    /// Everything needed to stand the same pipeline up again. A run builds its own, because an
    /// encoder that has already been fed cannot be moved to another position in the media.
    /// </summary>
    private sealed record TranscodeRecipe(
        CodecParameters Source,
        TranscodeVideoTarget? Video,
        TranscodeAudioTarget? Audio,
        TranscodeCapabilities Capabilities,
        TranscodeOptions Options)
    {
        public FfmpegTranscoder Create() =>
            Video is not null
                ? FfmpegTranscoder.CreateVideo(Source, Video, Capabilities, Options)
                : FfmpegTranscoder.CreateAudio(Source, Audio!, Capabilities, Options);
    }

    /// <summary>
    /// Opens the video according to its media kind and every sidecar the library recorded for it,
    /// then folds their streams into one table. <paramref name="openSource"/> resolves regular files
    /// and sidecars to readable bytes.
    /// </summary>
    /// <param name="transcode">
    /// Exact tracks the client asked to have re-encoded, or null to send every track as it is.
    /// </param>
    public static StreamSession Create(
        string mainPath,
        IReadOnlyList<string> sidecarPaths,
        string revision,
        Func<string, Stream> openSource,
        VideoMediaKind mainMediaKind = VideoMediaKind.File,
        int discTitle = 0,
        RemoteTranscodeRequest? transcode = null,
        Func<TranscodeCapabilities>? resolveCapabilities = null,
        TranscodeOptions? transcodeOptions = null,
        string? expectedRevision = null)
    {
        var opened = new List<SourceEntry>();
        AppLog.Information(
            "RemoteApi.Stream",
            $"Opening media session: main='{mainPath}'; sidecars={sidecarPaths.Count}; " +
            $"requestedTranscodes={transcode?.Tracks?.Length ?? 0}.");
        try
        {
            opened.Add(new SourceEntry(
                OpenMainDemuxer(mainPath, mainMediaKind, discTitle, openSource),
                mainPath,
                LiveSourceStamp(mainPath, revision)));

            foreach (var sidecar in sidecarPaths)
            {
                try
                {
                    opened.Add(new SourceEntry(OpenDemuxer(sidecar, openSource), sidecar, LiveSourceStamp(sidecar, revision)));
                }
                catch (Exception exception)
                {
                    // A sidecar the library recorded but that has since moved or gone bad must not
                    // stop the video it belongs to from playing.
                    AppLog.Warning("RemoteApi.Stream", $"Could not open sidecar '{sidecar}'.", exception);
                }
            }

            string sourceRevision = SourceRevision(mainPath, opened);
            if (expectedRevision is not null && expectedRevision != sourceRevision)
                throw new RemoteStreamNegotiationException("source_changed", "The media changed. Obtain a fresh stream description.");

            var (trackEntries, manifest) = BuildManifest(
                mainPath,
                opened,
                transcode,
                resolveCapabilities?.Invoke() ?? TranscodeCapabilities.None,
                transcodeOptions ?? TranscodeOptions.Disabled);
            return new StreamSession(opened.ToArray(), trackEntries, manifest with { SourceRevision = sourceRevision });
        }
        catch
        {
            foreach (var entry in opened)
            {
                entry.Demuxer.Dispose();
            }

            throw;
        }
    }

    /// <summary>Reads one subtitle track out as an ASS script.</summary>
    /// <remarks>
    /// This walks the container it reads to the end, which is why it may only be asked of a session
    /// that is not also streaming packets.
    /// </remarks>
    public string ReadSubtitleScript(int trackId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        TrackEntry track = tracks.FirstOrDefault(entry => entry.Id == trackId)
            ?? throw new ArgumentOutOfRangeException(nameof(trackId), "No such track in this media.");

        return FfmpegSubtitleReader.Read(sources[track.SourceIndex].Demuxer, track.StreamIndex, cancellationToken);
    }

    /// <summary>
    /// Sends <paramref name="selectedTrackIds"/> from <paramref name="startSeconds"/> until the media
    /// ends, the client asks for something else, or it goes away. Never sends more than
    /// <paramref name="credit"/> allows.
    /// </summary>
    public async Task WritePacketsAsync(
        IPacketStreamWriter writer,
        PacketCreditGate credit,
        IReadOnlyList<int> selectedTrackIds,
        double startSeconds,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        TrackEntry[] selected = ResolveSelection(selectedTrackIds);
        var readers = new List<SourceReader>();
        var transcoders = new Dictionary<int, FfmpegTranscoder>();
        var pool = new MediaPacketPool(8);
        using var output = new PrimedPacketStreamWriter(writer, credit,
            () => transcoders.Values.All(transcoder => transcoder.IsReady),
            () => DescribeRun(transcoders), cancellationToken);

        try
        {
            foreach (TrackEntry track in selected)
            {
                if (track.Recipe is not null)
                {
                    AppLog.Information(
                        "RemoteApi.Stream",
                        $"Creating transcode pipeline for track={track.Id}; sourceIndex={track.SourceIndex}; " +
                        $"streamIndex={track.StreamIndex}.");
                    transcoders[track.Id] = track.Recipe.Create();
                }
            }

            foreach (var group in selected.GroupBy(track => track.SourceIndex))
            {
                var streams = group.ToDictionary(track => track.StreamIndex, track => track.Id);
                SourceEntry source = sources[group.Key];
                source.Demuxer.SelectStreams([.. streams.Keys]);

                // Always seek, even to zero: the containers are shared by every run of this socket,
                // so a run must not inherit wherever the last one left them.
                if (source.Demuxer.CanSeek)
                {
                    source.Demuxer.Seek(startSeconds);
                }

                AppLog.Information(
                    "RemoteApi.Stream",
                    $"Prepared source='{source.Path}'; streams=[{string.Join(",", streams.Keys)}]; " +
                    $"startSeconds={startSeconds:F3}; seekable={source.Demuxer.CanSeek}.");

                readers.Add(new SourceReader(source.Demuxer, streams, pool));
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                SourceReader? next = null;
                double earliest = double.MaxValue;
                bool anyLive = false;

                foreach (SourceReader reader in readers)
                {
                    if (!reader.EnsurePending(out double time))
                    {
                        continue;
                    }

                    anyLive = true;
                    if (time < earliest)
                    {
                        earliest = time;
                        next = reader;
                    }
                }

                foreach (SourceReader reader in readers)
                {
                    foreach (int endedTrack in reader.TakeEndedTracks())
                    {
                        if (transcoders.TryGetValue(endedTrack, out FfmpegTranscoder? ending))
                        {
                            // Whatever the pipeline is still holding has to come out before the
                            // client is told the track is over.
                            ending.Send(null);
                            await DrainAsync(output, ending, pool, endedTrack, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        await output.WriteEndOfTrackAsync(endedTrack).ConfigureAwait(false);
                    }
                }

                if (!anyLive)
                {
                    await output.WriteEndOfStreamAsync().ConfigureAwait(false);
                    return;
                }

                MediaPacket packet = next!.TakePending(out int trackId);
                try
                {
                    if (transcoders.TryGetValue(trackId, out FfmpegTranscoder? transcoder))
                    {
                        transcoder.Send(packet);
                        await DrainAsync(output, transcoder, pool, trackId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        byte[] hdr10Plus = packet.CopyHdr10PlusMetadata();
                        ReadOnlyMemory<byte> hdr10PlusMetadata = hdr10Plus is null
                            ? ReadOnlyMemory<byte>.Empty
                            : hdr10Plus;

                        // The bitstream crosses untouched: the client configures its decoder from
                        // the same codec parameters this container reported, so any reshaping here
                        // would leave the two disagreeing.
                        await output.WritePacketAsync(
                            trackId,
                            packet.Pts,
                            packet.Dts,
                            packet.Duration,
                            packet.IsKeyFrame,
                            packet.Data,
                                packet.SizeInBytes,
                                hdr10PlusMetadata).ConfigureAwait(false);
                    }
                }
                finally
                {
                    packet.Recycle();
                }
            }
        }
        finally
        {
            foreach (FfmpegTranscoder transcoder in transcoders.Values)
            {
                transcoder.Dispose();
            }

            foreach (SourceReader reader in readers)
            {
                reader.Dispose();
            }

            pool.Clear();
        }
    }

    private RemoteStreamManifest DescribeRun(IReadOnlyDictionary<int, FfmpegTranscoder> transcoders) =>
        Manifest with
        {
            Tracks = Manifest.Tracks.Select(track =>
            {
                if (!transcoders.TryGetValue(track.Id, out var transcoder))
                    return track;
                CodecParameters parameters = transcoder.OutputParameters;
                TrackEntry entry = tracks.First(value => value.Id == track.Id);
                return track with
                {
                    CodecName = parameters.CodecName,
                    Parameters = parameters,
                    Width = parameters.Width,
                    Height = parameters.Height,
                    FrameRate = parameters.FrameRate,
                    SampleRate = parameters.SampleRate,
                    ChannelCount = parameters.ChannelCount,
                    VariantKey = VariantKey(sources[entry.SourceIndex], entry.StreamIndex, track.DeliveryMode, parameters),
                };
            }).ToArray(),
        };

    private static async Task DrainAsync(
        IPacketStreamWriter writer,
        FfmpegTranscoder transcoder,
        MediaPacketPool pool,
        int trackId,
        CancellationToken cancellationToken)
    {
        MediaPacket output = pool.Rent();
        try
        {
            while (transcoder.TryReceive(output, trackId))
            {
                try
                {
                    byte[] hdr10Plus = output.CopyHdr10PlusMetadata();
                    ReadOnlyMemory<byte> hdr10PlusMetadata = hdr10Plus is null
                        ? ReadOnlyMemory<byte>.Empty
                        : hdr10Plus;
                    cancellationToken.ThrowIfCancellationRequested();
                    await writer.WritePacketAsync(
                        trackId,
                        output.Pts,
                        output.Dts,
                        output.Duration,
                        output.IsKeyFrame,
                        output.Data,
                        output.SizeInBytes,
                        hdr10PlusMetadata).ConfigureAwait(false);
                }
                finally
                {
                    output.Reset();
                }
            }
        }
        finally
        {
            output.Recycle();
        }
    }

    private TrackEntry[] ResolveSelection(IReadOnlyList<int> selectedTrackIds)
    {
        if (selectedTrackIds.Count == 0)
        {
            throw new ArgumentException("A packet stream needs at least one track.", nameof(selectedTrackIds));
        }

        var selected = new List<TrackEntry>(selectedTrackIds.Count);
        foreach (int id in selectedTrackIds)
        {
            TrackEntry entry = tracks.FirstOrDefault(track => track.Id == id)
                ?? throw new ArgumentOutOfRangeException(nameof(selectedTrackIds), $"No track {id} in this media.");
            if (!selected.Contains(entry))
            {
                selected.Add(entry);
            }
        }

        return selected.ToArray();
    }

    private static FfmpegDemuxer OpenDemuxer(string path, Func<string, Stream> openSource)
    {
        AppLog.Information("RemoteApi.Stream", $"Opening indexed media source '{path}'.");
        Stream stream = openSource(path)
            ?? throw new InvalidOperationException($"'{path}' could not be opened.");
        try
        {
            return FfmpegDemuxer.Open(stream, path, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static FfmpegDemuxer OpenMainDemuxer(
        string path,
        VideoMediaKind mediaKind,
        int discTitle,
        Func<string, Stream> openSource)
    {
        if (mediaKind == VideoMediaKind.File)
        {
            return OpenDemuxer(path, openSource);
        }

        AppLog.Information("RemoteApi.Stream", $"Opening indexed {mediaKind} source '{path}'.");
        return DiscMediaSource.Open(path, mediaKind, discTitle);
    }

    private static (TrackEntry[] Tracks, RemoteStreamManifest Manifest) BuildManifest(
        string mainPath,
        List<SourceEntry> opened,
        RemoteTranscodeRequest? transcode,
        TranscodeCapabilities capabilities,
        TranscodeOptions transcodeOptions)
    {
        var entries = new List<TrackEntry>();
        var manifestTracks = new List<RemoteStreamTrack>();
        var requests = new Dictionary<StreamIdentity, RemoteTrackTranscodeRequest>();
        foreach (RemoteTrackTranscodeRequest request in transcode?.Tracks ?? [])
        {
            var identity = new StreamIdentity(request.SourcePath, request.StreamIndex);
            if (!requests.TryAdd(identity, request))
            {
                throw new RemoteStreamNegotiationException("invalid_transcode", "The transcode request names the same stream more than once.");
            }
        }

        var matchedRequests = new HashSet<StreamIdentity>();
        int nextId = 0;

        for (int sourceIndex = 0; sourceIndex < opened.Count; sourceIndex++)
        {
            SourceEntry source = opened[sourceIndex];
            foreach (MediaStreamInfo stream in source.Demuxer.Info.Streams)
            {
                // The media's own streams keep their container index, so anything a client remembered
                // by stream index still names the same track.
                int trackId = sourceIndex == 0 ? stream.Index : nextId;
                nextId = Math.Max(nextId, trackId + 1);

                CodecParameters parameters = source.Demuxer.GetCodecParameters(stream.Index);
                AppLog.Information(
                    "RemoteApi.Stream",
                    $"Discovered stream: source='{source.Path}'; index={stream.Index}; kind={stream.Kind}; " +
                    $"codec={stream.CodecName}; size={stream.Width}x{stream.Height}; frameRate={stream.FrameRate:F3}; " +
                    $"sampleRate={stream.SampleRate}; channels={stream.ChannelCount}; bitRate={parameters.BitRate}.");
                var identity = new StreamIdentity(sourceIndex == 0 ? null : source.Path, stream.Index);
                requests.TryGetValue(identity, out RemoteTrackTranscodeRequest? request);
                if (request is not null)
                {
                    matchedRequests.Add(identity);
                }

                TranscodeRecipe? recipe = null;

                CodecParameters delivered = parameters;
                string delivery = RemoteStreamDefaults.PassthroughDelivery;
                string codecName = stream.CodecName ?? string.Empty;
                int width = stream.Width;
                int height = stream.Height;
                int sampleRate = stream.SampleRate;
                int channels = stream.ChannelCount;

                if (request is not null)
                {
                    (recipe, delivered) = NegotiateTrack(source, stream, parameters, request, capabilities, transcodeOptions);
                    delivery = RemoteStreamDefaults.TranscodeDelivery;
                    codecName = delivered.CodecName;
                    width = delivered.Width;
                    height = delivered.Height;
                    sampleRate = delivered.SampleRate;
                    channels = delivered.ChannelCount;
                }

                entries.Add(new TrackEntry(trackId, sourceIndex, stream.Index, recipe));
                manifestTracks.Add(new RemoteStreamTrack(
                    trackId,
                    stream.Kind.ToString().ToLowerInvariant(),
                    codecName,
                    stream.Language ?? string.Empty,
                    stream.Title ?? string.Empty,
                    stream.IsDefault,
                    sourceIndex == 0 ? null : source.Path,
                    (long)stream.Duration.TotalMilliseconds,
                    source.Demuxer.StartTime,
                    delivery,
                    VariantKey(source, stream.Index, delivery, delivered),
                    delivered,
                    width,
                    height,
                    delivered.FrameRate,
                    sampleRate,
                    channels,
                    stream.Index));
            }
        }

        if (matchedRequests.Count != requests.Count)
        {
            StreamIdentity missing = requests.Keys.First(identity => !matchedRequests.Contains(identity));
            throw new RemoteStreamNegotiationException("invalid_transcode",
                $"Requested transcode stream {missing.StreamIndex} in '{missing.SourcePath ?? mainPath}' was not found.");
        }

        FfmpegDemuxer main = opened[0].Demuxer;
        var manifest = new RemoteStreamManifest(
            mainPath,
            main.Info.Source ?? opened[0].Path,
            main.Info.FormatName ?? string.Empty,
            (long)main.Info.Duration.TotalMilliseconds,
            main.CanSeek,
            main.VideoStreamIndex,
            main.AudioStreamIndex,
            manifestTracks.ToArray(),
            main.Info.Chapters
                .Select(chapter => new RemoteStreamChapter(
                    chapter.Index,
                    (long)chapter.Start.TotalMilliseconds,
                    (long)chapter.End.TotalMilliseconds,
                    chapter.Title ?? string.Empty))
                .ToArray(),
            new Dictionary<string, string>(main.Info.Metadata, StringComparer.OrdinalIgnoreCase))
        {
            Bitrate = main.Info.Bitrate,
        };

        return (entries.ToArray(), manifest);
    }

    private static (TranscodeRecipe Recipe, CodecParameters Parameters) NegotiateTrack(
        SourceEntry source,
        MediaStreamInfo stream,
        CodecParameters parameters,
        RemoteTrackTranscodeRequest request,
        TranscodeCapabilities capabilities,
        TranscodeOptions options)
    {
        string[] candidates = string.IsNullOrWhiteSpace(request.CodecName)
            ? request.CodecCandidates ?? []
            : [request.CodecName];
        if (candidates.Length == 0 || candidates.Any(string.IsNullOrWhiteSpace))
            throw new RemoteStreamNegotiationException("invalid_transcode", "Output codec candidates must not be empty.");
        if (request.BitRate < 0 || request.MaxHeight < 0 || stream.Kind is not (MediaStreamKind.Video or MediaStreamKind.Audio))
            throw new RemoteStreamNegotiationException("invalid_transcode", "Transcode targets require audio or video and non-negative bitrate and height.");

        foreach (string codec in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                TranscodeRecipe recipe = CreateRecipe(stream, parameters,
                    request with { CodecName = codec, CodecCandidates = null }, capabilities, options);
                using FfmpegTranscoder probe = recipe.Create();
                if (!Prime(source.Demuxer, stream.Index, probe))
                    throw new RemoteStreamNegotiationException("transcode_unavailable", "The encoder produced no output parameters.");
                AppLog.Information("RemoteApi.Stream",
                    $"Negotiated stream={stream.Index}; source='{source.Path}'; pipeline='{probe.Description}'.");
                return (recipe, probe.OutputParameters);
            }
            catch (Exception exception) when (exception is FfmpegException or RemoteStreamNegotiationException)
            {
                AppLog.Warning("RemoteApi.Stream", $"Cannot encode stream={stream.Index} as '{codec}'.", exception);
            }
        }

        throw new RemoteStreamNegotiationException("transcode_unavailable",
            $"No requested output codec could encode stream {stream.Index}: {string.Join(", ", candidates)}.");
    }

    /// <summary>
    /// Feeds the container's own packets through until the pipeline has configured itself, and puts
    /// the container back where it was.
    /// </summary>
    /// <remarks>
    /// A frame-threaded decoder holds a long run of packets before it emits anything, so the budget
    /// is generous. Whatever the probe encodes is thrown away; a run starts again from its own seek.
    /// </remarks>
    private static bool Prime(FfmpegDemuxer demuxer, int streamIndex, FfmpegTranscoder transcoder)
    {
        if (transcoder.IsReady)
        {
            return true;
        }

        if (!demuxer.CanSeek)
        {
            // Reading ahead would cost the playback run the packets it consumed.
            return false;
        }

        var pool = new MediaPacketPool(4);
        MediaPacket packet = pool.Rent();
        MediaPacket discarded = pool.Rent();
        try
        {
            demuxer.SelectStreams([streamIndex]);
            for (int read = 0; read < MaximumPrimePackets && !transcoder.IsReady; read++)
            {
                PacketReadStatus status = demuxer.Read(packet);
                if (status == PacketReadStatus.Ok)
                {
                    if (packet.StreamIndex == streamIndex)
                    {
                        transcoder.Send(packet);
                        while (transcoder.TryReceive(discarded, streamIndex))
                        {
                            discarded.Reset();
                        }
                    }

                    packet.Reset();
                    continue;
                }

                if (status == PacketReadStatus.Recoverable)
                {
                    packet.Reset();
                    continue;
                }

                break;
            }

            return transcoder.IsReady;
        }
        finally
        {
            packet.Recycle();
            discarded.Recycle();
            pool.Clear();
            demuxer.Seek(0);
        }
    }

    /// <summary>Builds the exact pipeline target named by an already-matched client request.</summary>
    private static TranscodeRecipe CreateRecipe(
        MediaStreamInfo stream,
        CodecParameters parameters,
        RemoteTrackTranscodeRequest request,
        TranscodeCapabilities capabilities,
        TranscodeOptions options)
    {
        if (!options.Enabled || !capabilities.IsAvailable)
        {
            throw new RemoteStreamNegotiationException("transcode_unavailable", "This server cannot fulfill the requested transcode.");
        }

        if (string.IsNullOrWhiteSpace(request.CodecName) && request.CodecCandidates is not { Length: > 0 })
        {
            throw new RemoteStreamNegotiationException("invalid_transcode", "A transcode request must name an output codec or ordered candidates.");
        }

        if (request.BitRate < 0 || request.MaxHeight < 0)
        {
            throw new InvalidOperationException("Transcode bitrate and height cannot be negative.");
        }

        if (stream.Kind == MediaStreamKind.Video)
        {
            TranscodeEncoder encoder = ResolveEncoder(request, capabilities.FindVideo);
            return new TranscodeRecipe(
                parameters,
                new TranscodeVideoTarget
                {
                    CodecName = encoder.CodecName,
                    BitRate = request.BitRate,
                    MaxHeight = request.MaxHeight,
                },
                null,
                capabilities,
                options);
        }

        if (stream.Kind == MediaStreamKind.Audio)
        {
            TranscodeEncoder encoder = ResolveEncoder(request, capabilities.FindAudio);
            return new TranscodeRecipe(
                parameters,
                null,
                new TranscodeAudioTarget
                {
                    CodecName = encoder.CodecName,
                    BitRate = request.BitRate,
                },
                capabilities,
                options);
        }

        throw new InvalidOperationException(
            $"Stream {stream.Index} is not video or audio and cannot be transcoded.");
    }

    private static TranscodeEncoder ResolveEncoder(
        RemoteTrackTranscodeRequest request, Func<string?, TranscodeEncoder?> find)
    {
        string[] candidates = string.IsNullOrWhiteSpace(request.CodecName)
            ? request.CodecCandidates ?? []
            : [request.CodecName];
        return candidates.Where(codec => !string.IsNullOrWhiteSpace(codec)).Select(find).FirstOrDefault(encoder => encoder is not null)
            ?? throw new RemoteStreamNegotiationException("transcode_unavailable",
                $"This server cannot currently encode any requested codec: {string.Join(", ", candidates)}.");
    }

    private static string SourceRevision(string mainPath, IReadOnlyList<SourceEntry> opened)
    {
        var identity = new List<string> { mainPath };
        foreach (SourceEntry source in opened)
        {
            identity.Add(source.EntityTag);
            foreach (MediaStreamInfo stream in source.Demuxer.Info.Streams)
            {
                identity.Add(stream.Index.ToString(CultureInfo.InvariantCulture));
                identity.Add(RemoteStreamIdentity.ParametersKey(source.Demuxer.GetCodecParameters(stream.Index)));
            }
        }
        return RemoteStreamIdentity.Fingerprint(identity.ToArray());
    }

    private static string LiveSourceStamp(string path, string revision)
    {
        var file = new FileInfo(path);
        return RemoteStreamIdentity.Fingerprint(path, revision,
            file.Exists ? file.Length.ToString(CultureInfo.InvariantCulture) : "directory",
            (file.Exists ? file.LastWriteTimeUtc : Directory.GetLastWriteTimeUtc(path)).Ticks.ToString(CultureInfo.InvariantCulture));
    }

    private static string VariantKey(
        SourceEntry source,
        int streamIndex,
        string delivery,
        CodecParameters delivered) => RemoteStreamIdentity.Fingerprint(
            delivery, source.EntityTag, streamIndex.ToString(CultureInfo.InvariantCulture),
            RemoteStreamIdentity.ParametersKey(delivered));

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (SourceEntry source in sources)
        {
            source.Demuxer.Dispose();
        }
    }

    /// <summary>
    /// One container being walked, holding at most one packet back so the merge can compare what
    /// each source would produce next.
    /// </summary>
    private sealed class SourceReader(
        FfmpegDemuxer demuxer,
        Dictionary<int, int> streamToTrack,
        MediaPacketPool pool) : IDisposable
    {
        private MediaPacket? pending;
        private int pendingTrackId = -1;
        private double pendingTime;
        private bool exhausted;
        private bool endReported;

        public bool IsFinished => exhausted && pending is null;

        /// <summary>Fills the held slot if it is empty. False once the container has nothing left.</summary>
        public bool EnsurePending(out double time)
        {
            while (pending is null && !exhausted)
            {
                MediaPacket packet = pool.Rent();
                PacketReadStatus status = demuxer.Read(packet);
                if (status == PacketReadStatus.Ok)
                {
                    if (streamToTrack.TryGetValue(packet.StreamIndex, out int trackId))
                    {
                        pending = packet;
                        pendingTrackId = trackId;
                        pendingTime = NormalizedTime(packet);
                        break;
                    }

                    packet.Recycle();
                    continue;
                }

                packet.Recycle();
                if (status == PacketReadStatus.Recoverable)
                {
                    continue;
                }

                exhausted = true;
                if (status == PacketReadStatus.Fatal)
                {
                    throw new IOException("The media source failed while reading.");
                }
            }

            time = pendingTime;
            return pending is not null;
        }

        public MediaPacket TakePending(out int trackId)
        {
            MediaPacket packet = pending!;
            trackId = pendingTrackId;
            pending = null;
            pendingTrackId = -1;
            return packet;
        }

        /// <summary>Tracks whose end has to be announced, once each.</summary>
        public IEnumerable<int> TakeEndedTracks()
        {
            if (!IsFinished || endReported)
            {
                return [];
            }

            endReported = true;
            return streamToTrack.Values;
        }

        private double NormalizedTime(MediaPacket packet)
        {
            double dts = demuxer.ToSeconds(packet.StreamIndex, packet.Dts);
            if (MediaTime.IsValid(dts))
            {
                return dts;
            }

            double pts = demuxer.ToSeconds(packet.StreamIndex, packet.Pts);

            // A packet with no timing of its own cannot be ordered against the other sources, so it
            // goes out with the one before it rather than being held back.
            return MediaTime.IsValid(pts) ? pts : pendingTime;
        }

        public void Dispose() => pending?.Recycle();
    }
}
