#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Subtitles;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>Resolves an HTTP Live Streaming session entirely through a managed HTTP client.</summary>
public sealed class HlsMediaSourceProvider : IMediaSourceProvider, IStreamingMediaSourceProvider
{
    private readonly HttpMediaResourceResolver resolver;
    private readonly string manifestUrl;
    private StreamingLoadStage loadStage = StreamingLoadStage.Connecting;
    private bool disposed;

    public HlsMediaSourceProvider(string manifestUrl)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out Uri? uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("An absolute HTTP or HTTPS URL is required.", nameof(manifestUrl));
        }

        this.manifestUrl = uri.AbsoluteUri;
        resolver = new HttpMediaResourceResolver();
    }

    public string Description => "HTTP Live Streaming";

    public StreamingTimeline Timeline => resolver.Timeline;

    public event EventHandler? TimelineChanged
    {
        add => resolver.TimelineChanged += value;
        remove => resolver.TimelineChanged -= value;
    }

    public StreamingLoadStage LoadStage => loadStage;

    public event EventHandler? LoadStageChanged;

    public void CancelPendingOperations() => resolver.CancelPendingOperations();

    public IPacketSource OpenMain()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        SetLoadStage(StreamingLoadStage.LoadingManifest);
        MediaResource root = resolver.OpenHlsManifest(manifestUrl);
        SetLoadStage(StreamingLoadStage.ReadingStreamInformation);
        FfmpegDemuxer demuxer = FfmpegDemuxer.OpenHls(root, resolver, -HttpMediaResourceResolver.HlsLiveDelaySegmentCount);
        if (resolver.Timeline is { IsLive: true } timeline)
            demuxer.AlignFirstPacketTo(timeline.SuggestedStartSeconds);
        return demuxer;
    }

    private void SetLoadStage(StreamingLoadStage value)
    {
        if (loadStage == value)
            return;

        loadStage = value;
        LoadStageChanged?.Invoke(this, EventArgs.Empty);
    }

    public IPacketSource OpenSidecar(string path) =>
        throw new NotSupportedException("External sidecars are not available for an HLS link.");

    public string ReadSubtitleScript(int streamIndex, string? sidecarPath, CancellationToken token = default)
    {
        if (sidecarPath is not null)
            throw new NotSupportedException("External sidecars are not available for an HLS link.");

        using var reader = (FfmpegDemuxer)OpenMain();
        return FfmpegSubtitleReader.Read(reader, streamIndex, token);
    }

    public BitmapSubtitleDocument ReadBitmapSubtitles(
        int streamIndex,
        string? sidecarPath,
        CancellationToken token = default)
    {
        if (sidecarPath is not null)
            throw new NotSupportedException("External sidecars are not available for an HLS link.");

        using var reader = (FfmpegDemuxer)OpenMain();
        return FfmpegSubtitleReader.ReadBitmaps(reader, streamIndex, token);
    }

    public Stream OpenFile(string path) =>
        throw new NotSupportedException("External files are not available for an HLS link.");

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        resolver.Dispose();
    }
}

/// <summary>Application-owned adaptive-streaming transport used synchronously by FFmpeg's AVIO callbacks.</summary>
internal sealed class HttpMediaResourceResolver : IMediaResourceResolver, IDisposable
{
    private sealed class BufferedResponseStream(byte[] buffer) : MemoryStream(buffer, writable: false)
    {
        public override bool CanSeek => false;

        public override long Position
        {
            get => base.Position;
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }

    private sealed class PlaylistClock
    {
        private readonly Dictionary<long, double> durations = [];
        private long? checkpointSequence;
        private double checkpointOffset;
        private double targetDuration;

        public StreamingTimeline? Observe(string text)
        {
            string[] lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
            long mediaSequence = 0;
            double pendingDuration = 0;
            var segmentDurations = new List<double>();
            bool endList = false;
            bool isVod = false;
            bool isEvent = false;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                {
                    long.TryParse(
                        line.AsSpan("#EXT-X-MEDIA-SEQUENCE:".Length),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out mediaSequence);
                }
                else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
                {
                    double.TryParse(
                        line.AsSpan("#EXT-X-TARGETDURATION:".Length),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out targetDuration);
                }
                else if (line.StartsWith("#EXT-X-PLAYLIST-TYPE:VOD", StringComparison.OrdinalIgnoreCase))
                {
                    isVod = true;
                }
                else if (line.StartsWith("#EXT-X-PLAYLIST-TYPE:EVENT", StringComparison.OrdinalIgnoreCase))
                {
                    isEvent = true;
                }
                else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    ReadOnlySpan<char> value = line.AsSpan("#EXTINF:".Length);
                    int comma = value.IndexOf(',');
                    if (comma >= 0)
                        value = value[..comma];
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out pendingDuration);
                }
                else if (line.Equals("#EXT-X-ENDLIST", StringComparison.Ordinal))
                {
                    endList = true;
                }
                else if (line.Length > 0 && line[0] != '#' && pendingDuration > 0)
                {
                    segmentDurations.Add(pendingDuration);
                    pendingDuration = 0;
                }
            }

            if (segmentDurations.Count == 0)
                return null;

            bool isLive = !endList && !isVod;
            checkpointSequence ??= isLive && !isEvent
                ? mediaSequence + segmentDurations.Count - 1
                : mediaSequence;
            for (int index = 0; index < segmentDurations.Count; index++)
                durations[mediaSequence + index] = segmentDurations[index];

            double start = OffsetOf(mediaSequence);
            double end = start;
            foreach (double duration in segmentDurations)
                end += duration;

            double suggestedStart = start;
            int suggestedIndex = isLive
                ? Math.Max(0, segmentDurations.Count - HlsLiveDelaySegmentCount)
                : 0;
            for (int index = 0; index < suggestedIndex; index++)
                suggestedStart += segmentDurations[index];
            PruneBefore(mediaSequence, start);

            return new StreamingTimeline(
                isLive,
                start,
                end,
                targetDuration,
                isLive && isEvent && segmentDurations.Count > 1,
                suggestedStart);
        }

        private double OffsetOf(long sequence)
        {
            long checkpoint = checkpointSequence ?? sequence;
            long distance = sequence - checkpoint;
            if (distance == 0)
                return checkpointOffset;
            if (Math.Abs(distance) > 10_000)
                return checkpointOffset + distance * targetDuration;

            double offset = checkpointOffset;
            if (distance > 0)
            {
                for (long current = checkpoint; current < sequence; current++)
                    offset += DurationOf(current);
            }
            else
            {
                for (long current = sequence; current < checkpoint; current++)
                    offset -= DurationOf(current);
            }
            return offset;
        }

        private void PruneBefore(long sequence, double offset)
        {
            if (checkpointSequence is null || sequence <= checkpointSequence)
                return;

            checkpointSequence = sequence;
            checkpointOffset = offset;
            var expired = new List<long>();
            foreach (long knownSequence in durations.Keys)
            {
                if (knownSequence < sequence)
                    expired.Add(knownSequence);
            }
            foreach (long expiredSequence in expired)
                durations.Remove(expiredSequence);
        }

        private double DurationOf(long sequence) =>
            durations.TryGetValue(sequence, out double duration) ? duration : targetDuration;
    }

    private static readonly HttpRequestOptionsKey<bool> BrowserResponseStreaming =
        new("WebAssemblyEnableStreamingResponse");
    private const string ManagedUrlPrefix = "file:";
    private const int FfmpegDashTimelineLookbackSeconds = 60;
    internal const int HlsLiveDelaySegmentCount = 3;
    private const int DashLiveDelaySegmentCount = 4;

    private readonly HttpClient client = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private readonly CancellationTokenSource lifetime = new();
    private readonly object timelineSync = new();
    private readonly Dictionary<string, PlaylistClock> playlistClocks = new(StringComparer.Ordinal);
    private StreamingTimeline timeline;
    private bool disposed;

    public StreamingTimeline Timeline
    {
        get
        {
            lock (timelineSync)
                return timeline;
        }
    }

    public event EventHandler? TimelineChanged;

    public MediaResource OpenHlsManifest(string url) =>
        OpenCore(url, range: null, forceHlsManifest: true, forceDashManifest: false);

    public MediaResource OpenDashManifest(string url) =>
        OpenCore(url, range: null, forceHlsManifest: false, forceDashManifest: true);

    public MediaResource Open(string url, MediaResourceRange? range = null) =>
        OpenCore(url, range, forceHlsManifest: false, forceDashManifest: false);

    private MediaResource OpenCore(
        string url,
        MediaResourceRange? range,
        bool forceHlsManifest,
        bool forceDashManifest)
    {
        if (disposed)
            throw new OperationCanceledException("The streaming session is closed.");
        string transportUrl = url.StartsWith(ManagedUrlPrefix, StringComparison.OrdinalIgnoreCase)
            ? url[ManagedUrlPrefix.Length..]
            : url;
        if (!Uri.TryCreate(transportUrl, UriKind.Absolute, out Uri? uri) ||
            !IsHttp(uri))
        {
            throw new NotSupportedException($"Streaming resource URL '{url}' is not HTTP or HTTPS.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Options.Set(BrowserResponseStreaming, true);
        if (range is { } requested)
        {
            if (requested.Offset < 0 || requested.EndOffset <= requested.Offset)
                throw new ArgumentOutOfRangeException(nameof(range));

            request.Headers.Range = new RangeHeaderValue(
                requested.Offset,
                requested.EndOffset is { } endOffset ? endOffset - 1 : null);
        }

        HttpResponseMessage response = client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token)
            .GetAwaiter()
            .GetResult();
        CancellationTokenRegistration responseCancellation = lifetime.Token.Register(
            static state => ((HttpResponseMessage)state!).Dispose(),
            response);
        try
        {
            response.EnsureSuccessStatusCode();
            ValidateRange(response, range);

            Uri resolvedUri = response.RequestMessage?.RequestUri ?? uri;
            if (!IsHttp(resolvedUri))
                throw new NotSupportedException($"Streaming resource URL '{url}' redirected outside HTTP or HTTPS.");

            string resolvedUrl = resolvedUri.AbsoluteUri;
            Stream stream;
            if (range is null && (forceHlsManifest || IsHlsManifest(response, resolvedUrl)))
            {
                byte[] content = response.Content.ReadAsByteArrayAsync(lifetime.Token).GetAwaiter().GetResult();
                string text = Encoding.UTF8.GetString(content);
                ObservePlaylist(resolvedUrl, text);
                stream = new MemoryStream(
                    Encoding.UTF8.GetBytes(RewritePlaylistUris(resolvedUri, text)),
                    writable: false);
            }
            else if (range is null && (forceDashManifest || IsDashManifest(response, resolvedUrl)))
            {
                byte[] content = response.Content.ReadAsByteArrayAsync(lifetime.Token).GetAwaiter().GetResult();
                string text = RewriteDashManifest(resolvedUri, Encoding.UTF8.GetString(content));
                stream = new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false);
            }
            else
            {
                byte[] content = response.Content.ReadAsByteArrayAsync(lifetime.Token).GetAwaiter().GetResult();
                stream = new BufferedResponseStream(content);
            }

            return new MediaResource(stream, resolvedUrl, () =>
            {
                responseCancellation.Dispose();
                stream.Dispose();
                response.Dispose();
            });
        }
        catch
        {
            responseCancellation.Dispose();
            response.Dispose();
            throw;
        }
    }

    public void CancelPendingOperations()
    {
        if (!disposed)
            lifetime.Cancel();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        lifetime.Cancel();
        client.Dispose();
        lifetime.Dispose();
    }

    private static void ValidateRange(HttpResponseMessage response, MediaResourceRange? range)
    {
        if (range is not { } requested ||
            requested is { Offset: 0, EndOffset: null })
            return;

        ContentRangeHeaderValue? contentRange = response.Content.Headers.ContentRange;
        long? expectedEnd = requested.EndOffset - 1;
        if (response.StatusCode != HttpStatusCode.PartialContent ||
            contentRange?.From != requested.Offset ||
            expectedEnd is not null && contentRange.To != expectedEnd)
        {
            throw new NotSupportedException(
                $"The streaming server did not honor the requested byte range " +
                $"{requested.Offset}-{expectedEnd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}.");
        }
    }

    private void ObservePlaylist(string url, string content)
    {
        if (!content.StartsWith("#EXTM3U", StringComparison.Ordinal))
            return;

        bool changed = false;
        lock (timelineSync)
        {
            if (!playlistClocks.TryGetValue(url, out PlaylistClock? clock))
            {
                clock = new PlaylistClock();
                playlistClocks.Add(url, clock);
            }

            if (clock.Observe(content) is { } observed && observed != timeline)
            {
                timeline = observed;
                changed = true;
            }
        }

        if (changed)
            TimelineChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateTimeline(StreamingTimeline observed)
    {
        bool changed;
        lock (timelineSync)
        {
            changed = observed != timeline;
            if (changed)
                timeline = observed;
        }

        if (changed)
            TimelineChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string RewritePlaylistUris(Uri baseUri, string content)
    {
        string[] lines = content.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var rewritten = new StringBuilder(content.Length);
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length > 0)
            {
                line = line[0] == '#'
                    ? RewriteUriAttributes(baseUri, line)
                    : ToManagedUrl(baseUri, line);
            }

            rewritten.Append(line);
            if (index < lines.Length - 1)
                rewritten.Append('\n');
        }

        return rewritten.ToString();
    }

    private static string RewriteUriAttributes(Uri baseUri, string line)
    {
        StringBuilder? rewritten = null;
        int copied = 0;
        int position = 0;
        while ((position = line.IndexOf("URI=", position, StringComparison.Ordinal)) >= 0)
        {
            if (position > 0 && line[position - 1] is not (':' or ','))
            {
                position += "URI=".Length;
                continue;
            }

            int start = position + "URI=".Length;
            bool quoted = start < line.Length && line[start] == '"';
            if (quoted)
                start++;
            int end = quoted ? line.IndexOf('"', start) : line.IndexOf(',', start);
            if (end < 0)
            {
                if (quoted)
                    break;
                end = line.Length;
            }
            if (end == start)
            {
                position = end + (quoted ? 1 : 0);
                continue;
            }

            rewritten ??= new StringBuilder(line.Length + 32);
            rewritten.Append(line, copied, start - copied);
            rewritten.Append(ToManagedUrl(baseUri, line[start..end]));
            copied = end;
            position = end + (quoted ? 1 : 0);
        }

        return rewritten?.Append(line, copied, line.Length - copied).ToString() ?? line;
    }

    private static string ToManagedUrl(Uri baseUri, string reference)
    {
        if (reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return reference;
        if (!Uri.TryCreate(baseUri, reference, out Uri? resolved) || !IsHttp(resolved))
            throw new NotSupportedException($"HLS resource URL '{reference}' is not HTTP or HTTPS.");

        return ManagedUrlPrefix + resolved.AbsoluteUri;
    }

    private string RewriteDashManifest(Uri manifestUri, string content)
    {
        XDocument document = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        XElement root = document.Root ?? throw new InvalidDataException("The DASH manifest has no root element.");
        if (!string.Equals(root.Name.LocalName, "MPD", StringComparison.Ordinal))
            throw new InvalidDataException("The DASH manifest root element is not MPD.");

        double segmentDuration = ReadDashSegmentDuration(root);
        NormalizeDashTimelineStartNumbers(root, segmentDuration);
        ApplyDashLiveDelay(root, segmentDuration);
        ObserveDashManifest(root, segmentDuration);
        RewriteDashUrls(root, new Uri(manifestUri, "."), ensureBaseUrl: true);

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static void RewriteDashUrls(XElement element, Uri inheritedBase, bool ensureBaseUrl = false)
    {
        Uri effectiveBase = inheritedBase;
        bool hasBaseUrl = false;
        foreach (XElement child in element.Elements())
        {
            if (!string.Equals(child.Name.LocalName, "BaseURL", StringComparison.Ordinal))
                continue;

            Uri resolved = ResolveDashUrl(inheritedBase, child.Value, "BaseURL");
            child.Value = ManagedUrlPrefix + resolved.AbsoluteUri;
            if (!hasBaseUrl)
                effectiveBase = resolved;
            hasBaseUrl = true;
        }

        if (ensureBaseUrl && !hasBaseUrl)
        {
            element.AddFirst(new XElement(
                element.Name.Namespace + "BaseURL",
                ManagedUrlPrefix + inheritedBase.AbsoluteUri));
        }

        foreach (XAttribute attribute in element.Attributes())
        {
            if (IsDashUrlAttribute(element, attribute) &&
                !attribute.Value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                attribute.Value = ManagedUrlPrefix + ResolveDashUrl(
                    effectiveBase,
                    attribute.Value,
                    $"{element.Name.LocalName} {attribute.Name.LocalName}").AbsoluteUri;
            }
        }

        foreach (XElement child in element.Elements())
        {
            if (string.Equals(child.Name.LocalName, "BaseURL", StringComparison.Ordinal))
                continue;

            if (child.Name.LocalName is "Location" or "PatchLocation")
            {
                child.Value = ManagedUrlPrefix + ResolveDashUrl(
                    effectiveBase,
                    child.Value,
                    child.Name.LocalName).AbsoluteUri;
                continue;
            }

            RewriteDashUrls(child, effectiveBase);
        }
    }

    private static Uri ResolveDashUrl(Uri baseUri, string reference, string description)
    {
        string value = reference.Trim();
        if (!Uri.TryCreate(baseUri, string.IsNullOrEmpty(value) ? "." : value, out Uri? resolved) ||
            !IsHttp(resolved))
        {
            throw new NotSupportedException($"DASH {description} URL '{value}' is not HTTP or HTTPS.");
        }

        return resolved;
    }

    private static bool IsDashUrlAttribute(XElement element, XAttribute attribute)
    {
        string attributeName = attribute.Name.LocalName;
        if (attributeName == "href" &&
            attribute.Name.NamespaceName == "http://www.w3.org/1999/xlink")
        {
            return true;
        }

        return element.Name.LocalName switch
        {
            "SegmentTemplate" => attributeName is "media" or "initialization",
            "SegmentURL" => attributeName is "media" or "index",
            "Initialization" or "RepresentationIndex" or "BitstreamSwitching" =>
                attributeName == "sourceURL",
            _ => false,
        };
    }

    private static void NormalizeDashTimelineStartNumbers(XElement root, double segmentDuration)
    {
        if (!string.Equals((string?)root.Attribute("type"), "dynamic", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (XElement template in root.Descendants())
        {
            string? media = (string?)template.Attribute("media");
            if (!string.Equals(template.Name.LocalName, "SegmentTemplate", StringComparison.Ordinal) ||
                media?.Contains("$Time$", StringComparison.Ordinal) != true)
            {
                continue;
            }

            foreach (XElement child in template.Elements())
            {
                if (string.Equals(child.Name.LocalName, "SegmentTimeline", StringComparison.Ordinal))
                {
                    // FFmpeg adds startNumber to a SegmentTimeline index after applying its fixed
                    // 60-second lookback. $Time$ URLs do not use that number, so keep it index-sized.
                    long indexOffset = segmentDuration > 0 &&
                        ReadDashTimelineDuration(template) > FfmpegDashTimelineLookbackSeconds
                            ? Math.Max(
                                0,
                                (long)Math.Ceiling(FfmpegDashTimelineLookbackSeconds / segmentDuration) -
                                DashLiveDelaySegmentCount)
                            : 0;
                    template.SetAttributeValue(
                        "startNumber",
                        indexOffset > 0 ? indexOffset.ToString(CultureInfo.InvariantCulture) : null);
                    break;
                }
            }
        }
    }

    private static double ReadDashTimelineDuration(XElement template)
    {
        if (!long.TryParse(
                (string?)template.Attribute("timescale"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long timescale) || timescale <= 0)
        {
            timescale = 1;
        }

        double duration = 0;
        foreach (XElement element in template.Descendants())
        {
            if (!string.Equals(element.Name.LocalName, "S", StringComparison.Ordinal) ||
                !long.TryParse(
                    (string?)element.Attribute("d"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long segmentUnits) || segmentUnits <= 0)
            {
                continue;
            }

            long.TryParse(
                (string?)element.Attribute("r"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long repeat);
            if (repeat < 0)
                return double.PositiveInfinity;

            duration += segmentUnits / (double)timescale * (repeat + 1);
        }

        return duration;
    }

    private static void ApplyDashLiveDelay(XElement root, double segmentDuration)
    {
        if (!(segmentDuration > 0) ||
            !string.Equals((string?)root.Attribute("type"), "dynamic", StringComparison.OrdinalIgnoreCase) ||
            root.Attribute("suggestedPresentationDelay") is not null)
        {
            return;
        }

        double delaySeconds = Math.Ceiling(segmentDuration * DashLiveDelaySegmentCount);
        if (double.IsFinite(delaySeconds))
        {
            root.SetAttributeValue(
                "suggestedPresentationDelay",
                "PT" + delaySeconds.ToString("0", CultureInfo.InvariantCulture) + "S");
        }
    }

    private void ObserveDashManifest(XElement root, double segmentDuration)
    {
        if (!string.Equals((string?)root.Attribute("type"), "dynamic", StringComparison.OrdinalIgnoreCase))
        {
            UpdateTimeline(default);
            return;
        }

        UpdateTimeline(new StreamingTimeline(
            IsLive: true,
            StartSeconds: 0,
            EndSeconds: 0,
            TargetDurationSeconds: segmentDuration > 0 ? segmentDuration : 1,
            CanSeekBack: false));
    }

    private static double ReadDashSegmentDuration(XElement root)
    {
        foreach (XElement template in root.Descendants())
        {
            if (!string.Equals(template.Name.LocalName, "SegmentTemplate", StringComparison.Ordinal))
                continue;

            if (!long.TryParse(
                    (string?)template.Attribute("timescale"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long timescale) || timescale <= 0)
            {
                timescale = 1;
            }

            if (long.TryParse(
                    (string?)template.Attribute("duration"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long duration) && duration > 0)
            {
                return duration / (double)timescale;
            }

            foreach (XElement timelineChild in template.Descendants())
            {
                if (string.Equals(timelineChild.Name.LocalName, "S", StringComparison.Ordinal) &&
                    long.TryParse(
                        (string?)timelineChild.Attribute("d"),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out duration) && duration > 0)
                {
                    return duration / (double)timescale;
                }
            }
        }

        return 0;
    }

    private static bool IsHlsManifest(HttpResponseMessage response, string url)
    {
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        return HasPathExtension(url, ".m3u8") ||
            string.Equals(mediaType, "application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mediaType, "application/x-mpegurl", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDashManifest(HttpResponseMessage response, string url)
    {
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        return HasPathExtension(url, ".mpd") ||
            string.Equals(mediaType, "application/dash+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasPathExtension(string url, string extension) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
        uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static bool IsHttp(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}