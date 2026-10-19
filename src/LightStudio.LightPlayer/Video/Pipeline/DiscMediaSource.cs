#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Avalonia.Pipeline;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Interop;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.NativeBindings;
using LightStudio.NativeBindings.Types;

namespace LightStudio.LightPlayer.Video.Pipeline;

/// <summary>Opens optical-disc structures through libavformat's disc-aware inputs.</summary>
[Include("libavformat/avformat.h")]
[Include("libavutil/hwcontext.h")]
public static partial class DiscMediaSource
{
    private const int MaximumDvdTitle = 99;

    public static unsafe bool IsSupported(VideoMediaKind mediaKind)
    {
        try
        {
            return mediaKind switch
            {
                VideoMediaKind.Dvd => av_find_input_format("dvdvideo") != null,
                VideoMediaKind.BluRay => avio_find_protocol_name("bluray:") != null,
                _ => false,
            };
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    public static FfmpegDemuxer Open(string path, VideoMediaKind mediaKind, int discTitle = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return mediaKind switch
        {
            VideoMediaKind.Dvd => discTitle > 0
                ? OpenDvdTitle(path, discTitle)
                : SelectLongestDvdTitle(path).Demuxer,
            VideoMediaKind.BluRay => OpenBluRay(path),
            _ => throw new ArgumentOutOfRangeException(
                nameof(mediaKind),
                mediaKind,
                "A regular media file is not an optical disc source."),
        };
    }

    /// <summary>
    /// Probes an ISO or disc root. Blu-ray is attempted first because libbluray validates its disc
    /// structure; DVD probing then selects the longest playable title.
    /// </summary>
    public static VideoProbeInfo? Probe(string path, bool withPreview = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using FfmpegDemuxer? demuxer = TryOpenDetected(path, out VideoMediaKind mediaKind, out int discTitle);
        return demuxer is null ? null : CreateProbe(demuxer, mediaKind, discTitle, withPreview);
    }

    /// <summary>Opens the preferred playable title from a disc whose kind is not yet known.</summary>
    public static FfmpegDemuxer? TryOpenDetected(
        string path,
        out VideoMediaKind mediaKind,
        out int discTitle)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        mediaKind = VideoMediaKind.File;
        discTitle = 0;

        try
        {
            FfmpegDemuxer bluRay = Open(path, VideoMediaKind.BluRay);
            if (bluRay.Info.HasVideo)
            {
                mediaKind = VideoMediaKind.BluRay;
                return bluRay;
            }

            bluRay.Dispose();
        }
        catch (Exception exception) when (exception is FfmpegException or NotSupportedException)
        {
        }

        try
        {
            DvdSelection dvd = SelectLongestDvdTitle(path);
            mediaKind = VideoMediaKind.Dvd;
            discTitle = dvd.Title;
            return dvd.Demuxer;
        }
        catch (Exception exception) when (exception is FfmpegException or NotSupportedException)
        {
            return null;
        }
    }

    public static VideoProbeInfo? TryProbe(
        string path,
        VideoMediaKind mediaKind,
        bool withPreview = false)
    {
        try
        {
            DvdSelection? dvd = mediaKind == VideoMediaKind.Dvd
                ? SelectLongestDvdTitle(path)
                : null;
            using FfmpegDemuxer demuxer = dvd?.Demuxer ?? Open(path, mediaKind);
            return CreateProbe(demuxer, mediaKind, dvd?.Title ?? 0, withPreview);
        }
        catch (Exception exception) when (exception is FfmpegException or NotSupportedException)
        {
            return null;
        }
    }

    private static VideoProbeInfo? CreateProbe(
        FfmpegDemuxer demuxer,
        VideoMediaKind mediaKind,
        int discTitle,
        bool withPreview)
    {
        MediaStreamInfo? video = demuxer.Info.DefaultVideoStream;
        if (video is null)
        {
            return null;
        }

        (double displayWidth, double displayHeight) = video.DisplaySize;
        demuxer.Info.Metadata.TryGetValue("title", out string? title);
        VideoProbeInfo? preview = withPreview
            ? VideoPreviewFactory.Probe(demuxer, withPreview: true)
            : null;
        return new VideoProbeInfo
        {
            MediaKind = mediaKind,
            DiscTitle = discTitle,
            Duration = demuxer.Info.Duration > TimeSpan.Zero
                ? demuxer.Info.Duration
                : video.Duration,
            Width = ToDimension(displayWidth, video.Width),
            Height = ToDimension(displayHeight, video.Height),
            FrameRate = video.FrameRate,
            BitRate = demuxer.Info.Bitrate,
            VideoCodec = video.CodecName,
            VideoStreamIndex = video.Index,
            Title = string.IsNullOrWhiteSpace(title) ? null : title,
            AudioStreams = demuxer.Info.Streams
                .Where(stream => stream.Kind == MediaStreamKind.Audio)
                .Select(stream => new AudioStreamProbeInfo
                {
                    StreamIndex = stream.Index,
                    CodecName = stream.CodecName,
                })
                .ToArray(),
            Preview = preview?.Preview,
            PreviewWidth = preview?.PreviewWidth ?? 0,
            PreviewHeight = preview?.PreviewHeight ?? 0,
            PreviewPosition = preview?.PreviewPosition ?? TimeSpan.Zero,
        };
    }

    private static int ToDimension(double displayDimension, int codedDimension) =>
        double.IsFinite(displayDimension) && displayDimension > 0 && displayDimension <= int.MaxValue
            ? (int)Math.Round(displayDimension)
            : codedDimension;

    private static DvdSelection SelectLongestDvdTitle(string path)
    {
        DvdSelection? best = null;
        try
        {
            for (int title = 1; title <= MaximumDvdTitle; title++)
            {
                FfmpegDemuxer candidate;
                try
                {
                    candidate = OpenDvdTitle(path, title);
                }
                catch (FfmpegException) when (best is not null)
                {
                    break;
                }

                if (!candidate.Info.HasVideo)
                {
                    candidate.Dispose();
                    continue;
                }

                if (best is null || PlaybackDuration(candidate) > PlaybackDuration(best.Demuxer))
                {
                    best?.Demuxer.Dispose();
                    best = new DvdSelection(title, candidate);
                }
                else
                {
                    candidate.Dispose();
                }
            }

            return best ?? throw new FfmpegException($"No playable DVD title was found in '{path}'.", 0);
        }
        catch
        {
            best?.Demuxer.Dispose();
            throw;
        }
    }

    private static TimeSpan PlaybackDuration(FfmpegDemuxer demuxer) =>
        demuxer.Info.Duration > TimeSpan.Zero
            ? demuxer.Info.Duration
            : demuxer.Info.DefaultVideoStream?.Duration ?? TimeSpan.Zero;

    private static FfmpegDemuxer OpenBluRay(string path)
    {
        Dictionary<string, string>? options = null;
        MediaChapter[]? chapters = null;
        if (BlurayChapterReader.TryRead(path, out int playlist, out MediaChapter[] detectedChapters))
        {
            options = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["playlist"] = playlist.ToString(CultureInfo.InvariantCulture),
            };
            chapters = detectedChapters.Length == 0 ? null : detectedChapters;
        }

        return FfmpegDemuxer.OpenUrl(
            "bluray:" + path,
            inputFormatName: null,
            options: options,
            chapterOverride: chapters);
    }

    private static FfmpegDemuxer OpenDvdTitle(string path, int title) =>
        FfmpegDemuxer.OpenUrl(
            path,
            "dvdvideo",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = title.ToString(CultureInfo.InvariantCulture),
            });

    private sealed record DvdSelection(int Title, FfmpegDemuxer Demuxer);
}

/// <summary>Feeds one DVD or Blu-ray title to the normal playback pipeline.</summary>
internal sealed class DiscMediaSourceProvider : IMediaSourceProvider
{
    private readonly string path;
    private readonly VideoMediaKind mediaKind;
    private readonly int discTitle;
    private readonly LocalMediaSourceProvider files;

    public DiscMediaSourceProvider(
        string path,
        VideoMediaKind mediaKind,
        int discTitle,
        IMultiMediaPlatformIO platform)
    {
        this.path = path;
        this.mediaKind = mediaKind;
        this.discTitle = discTitle;
        files = MediaSourceProviders.CreateLocal(path, platform);
    }

    public string Description => mediaKind == VideoMediaKind.Dvd
        ? "DVD title (libdvdnav)"
        : "Blu-ray playlist (libbluray)";

    public IPacketSource OpenMain() => DiscMediaSource.Open(path, mediaKind, discTitle);

    public IPacketSource OpenSidecar(string sidecarPath) => files.OpenSidecar(sidecarPath);

    public Stream OpenFile(string filePath) => files.OpenFile(filePath);

    public string ReadSubtitleScript(int streamIndex, string? sidecarPath, CancellationToken token = default)
    {
        if (sidecarPath is not null)
        {
            return files.ReadSubtitleScript(streamIndex, sidecarPath, token);
        }

        using FfmpegDemuxer reader = DiscMediaSource.Open(path, mediaKind, discTitle);
        return FfmpegSubtitleReader.Read(reader, streamIndex, token);
    }

    public BitmapSubtitleDocument ReadBitmapSubtitles(
        int streamIndex,
        string? sidecarPath,
        CancellationToken token = default)
    {
        if (sidecarPath is not null)
        {
            return files.ReadBitmapSubtitles(streamIndex, sidecarPath, token);
        }

        using FfmpegDemuxer reader = DiscMediaSource.Open(path, mediaKind, discTitle);
        return FfmpegSubtitleReader.ReadBitmaps(reader, streamIndex, token, preserveContainerTimestamps: false);
    }

    public void Dispose() => files.Dispose();
}