using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.WebWorkers.Sources;
using LightStudio.Logging;
using LightStudio.NativeBindings;
using LightStudio.NativeBindings.Types;

namespace LightStudio.LightPlayer.WebWorkers;

/// <summary>
/// The demuxers and decoders the page created in this worker, stepped cooperatively on the runtime's
/// managed thread. FFmpeg's own threads provide the parallelism; this class never waits on a queue.
/// </summary>
[SupportedOSPlatform("browser")]
[Include("libavformat/avformat.h")]
[Include("libavcodec/codec_desc.h")]
[Include("libavutil/hwcontext.h")]
internal static partial class WorkerGraph
{
    private const int PlaybackWindowBytes = 1024 * 1024;
    private const int PlaybackWindows = 6;
    private const int PlaybackPrefetch = 2;
    private const int ProbeWindowBytes = 256 * 1024;
    private const int ProbeWindows = 4;

    // A relayed source reports what it consumed at this granularity, or as soon as it runs dry.
    private const long RelayReportBytes = 1024 * 1024;

    private static readonly Dictionary<int, Demuxer> demuxers = [];
    private static readonly Dictionary<int, VideoDecoder> videoDecoders = [];
    private static readonly Dictionary<int, AudioDecoder> audioDecoders = [];
    private static readonly MediaPacketPool pool = new();
    private static readonly FrameBatch batch = new();
    private static int nextId;

    public static WireOpenResult Open(WireSource source)
    {
        bool readAhead = source.ReadAhead;
        var stream = new SourceBridgeContext(source.Id, (long)source.Length,
            readAhead ? PlaybackWindowBytes : ProbeWindowBytes,
            readAhead ? PlaybackWindows : ProbeWindows,
            readAhead ? PlaybackPrefetch : 0);
        FfmpegDemuxer container;
        try
        {
            container = FfmpegDemuxer.Open(stream, source.Name, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        int id = NextId();
        var demuxer = new Demuxer(id, container);
        demuxers.Add(id, demuxer);
        return new WireOpenResult(id, container.Info, container.VideoStreamIndex, container.AudioStreamIndex, container.CanSeek,
            (container as IAdaptiveVideoPacketSource)?.VideoRenditions ?? []);
    }

    /// <summary>Opens a demuxer over packets the page reads and relays; see <see cref="Relay"/>.</summary>
    public static WireOpenResult OpenRelay(WireRelaySource source)
    {
        var relayed = new RelayedPacketSource(source);
        int id = NextId();
        demuxers.Add(id, new Demuxer(id, relayed));
        return new WireOpenResult(id, relayed.Info, relayed.VideoStreamIndex, relayed.AudioStreamIndex, relayed.CanSeek, []);
    }

    public static void Relay(int demuxer, int generation, byte[] batch)
    {
        if (demuxers.TryGetValue(demuxer, out Demuxer? target) && target.Pump.Source is RelayedPacketSource relayed)
            relayed.Accept(generation, batch);
    }

    public static WireVideoStream DescribeVideo(int demuxer)
    {
        IPacketSource source = Get(demuxers, demuxer).Pump.Source;
        int index = source.VideoStreamIndex;
        if (index < 0)
            throw new InvalidOperationException("The media has no video stream.");
        CodecParameters codec = source.GetCodecParameters(index);
        return new WireVideoStream(index, codec.CodecName, codec.Extradata, codec.Width, codec.Height, codec.Profile,
            BitDepth(codec), codec.ColorPrimaries, codec.ColorTransfer, codec.ColorSpace, codec.ColorRange, codec.ChromaLocation,
            codec.FieldOrder, codec.PixelAspectRatio, codec.FrameRate, codec.Rotation, codec.MirrorHorizontally,
            codec.DolbyVisionProfile, codec.HdrPeakLuminance, codec.HdrMasteringLuminance?.MinimumNits ?? 0,
            codec.HdrMasteringLuminance?.MaximumNits ?? 0);
    }

    public static WireDecoderResult CreateVideoDecoder(WireVideoDecoderRequest request)
    {
        Demuxer demuxer = Get(demuxers, request.Demuxer);
        IPacketSource source = demuxer.Pump.Source;
        var options = new VideoDecoderOptions
        {
            ThreadCount = Math.Clamp(request.ThreadCount > 0 ? request.ThreadCount : Environment.ProcessorCount,
                1, Math.Max(1, Math.Min(Environment.ProcessorCount, 10) - 2)),
            EnableHardwareDecoding = false,
            SoftwareFallbackReason = request.SoftwareFallbackReason,
            AllowDecoderOutputWindow = false,
            FramePoolSize = request.FramePoolSize,
            FramePoolByteCapacity = request.FramePoolByteCapacity,
        };
        int id = NextId();
        var input = new PacketLink("video-packets", request.PacketCapacity, request.PacketByteCapacity);
        var decoder = new VideoDecoder(id, demuxer, input, options);
        if (request.WebCodecs is not null)
        {
            CodecParameters codec = source.GetCodecParameters(source.VideoStreamIndex);
            decoder.WebCodecs = new WebCodecsVideoPump(id, source, source.VideoStreamIndex, input);
            decoder.Codec = codec.CodecName;
            decoder.WebCodecsDiagnostics = VideoDecoderDiagnostics.None with
            {
                Implementation = "WebCodecs worker", CodecName = codec.CodecName, DecoderName = codec.CodecName,
                Profile = ProfileName(codec), Level = codec.Level,
            };
        }
        else
        {
            decoder.UseFfmpeg(new FfmpegVideoDecoder(source, options));
            if (request.Hardware && string.IsNullOrEmpty(request.SoftwareFallbackReason))
                decoder.SoftwareStatus = "WebCodecs cannot decode this stream";
        }

        videoDecoders.Add(id, decoder);
        return new WireDecoderResult(id, source.VideoStreamIndex, decoder.Codec, decoder.Diagnostics, default);
    }

    public static WireDecoderResult CreateAudioDecoder(WireAudioDecoderRequest request)
    {
        Demuxer demuxer = Get(demuxers, request.Demuxer);
        var decoder = new FfmpegAudioDecoder(demuxer.Pump.Source, request.Stream, request.Format);
        try
        {
            decoder.Configure(request.Format, request.Speed);
        }
        catch
        {
            decoder.Dispose();
            throw;
        }

        int id = NextId();
        var options = new AudioDecoderStageOptions(request.Format, request.Speed, request.PacketCapacity, request.PacketByteCapacity);
        var pump = new AudioDecoderPump(decoder, new PacketLink("audio-packets", request.PacketCapacity, request.PacketByteCapacity),
            PumpMode.Cooperative, options, fault => NotifyFault(id, fault));
        audioDecoders.Add(id, new AudioDecoder(id, demuxer, pump));
        return new WireDecoderResult(id, request.Stream, decoder.CodecName, VideoDecoderDiagnostics.None, decoder.OutputFormat);
    }

    public static void Link(WireLink link)
    {
        PacketLink? input = null;
        if (link.Decoder != 0)
        {
            input = videoDecoders.TryGetValue(link.Decoder, out VideoDecoder? video) ? video.Input
                : Get(audioDecoders, link.Decoder).Pump.Input;
        }

        Get(demuxers, link.Demuxer).Pump.Link(link.Stream, input);
    }

    public static void SetOutput(WireOutput output)
    {
        if (videoDecoders.TryGetValue(output.Decoder, out VideoDecoder? video))
        {
            video.Output = new FrameExport<VideoFrame>(video.Id, batch, () => video.Source.Serial) { Allowance = output.Capacity };
            video.Ffmpeg?.SetOutput(video.Output);
            return;
        }

        AudioDecoder audio = Get(audioDecoders, output.Decoder);
        audio.Output = new FrameExport<AudioFrame>(audio.Id, batch, () => audio.Source.Serial) { Allowance = output.Capacity };
        audio.Pump.SetOutput(audio.Output);
    }

    public static void Start(int demuxer) => Get(demuxers, demuxer).Running = true;

    public static WireSeekResult Seek(WireSeek seek)
    {
        Demuxer demuxer = Get(demuxers, seek.Demuxer);
        bool succeeded = demuxer.Pump.Seek(seek.Request);
        if (succeeded)
            demuxer.Serial = seek.Request.Serial;
        return new WireSeekResult(succeeded, (demuxer.Pump.Source as RelayedPacketSource)?.Generation ?? 0);
    }

    public static void SelectRendition(WireRendition rendition) =>
        Get(demuxers, rendition.Demuxer).Pump.SelectRendition(rendition.Video, rendition.Audio);

    public static bool Recover(WireRecover recover)
    {
        VideoDecoder decoder = Get(videoDecoders, recover.Decoder);
        if (decoder.WebCodecs is not null)
        {
            if (recover.Recovery != VideoDecoderRecovery.Software)
                return false;

            // The packets keep flowing through the same link; only the decoder behind it changes.
            CloseWebCodecs(decoder.Id);
            decoder.UseFfmpeg(new FfmpegVideoDecoder(decoder.Source.Pump.Source, decoder.Options with
            {
                SoftwareFallbackReason = recover.Reason,
            }));
            decoder.WebCodecs = null;
            if (decoder.Output is not null)
            {
                decoder.Output.Sent = recover.Sent;
                decoder.Ffmpeg!.SetOutput(decoder.Output);
            }

            return true;
        }

        return decoder.Ffmpeg!.Recover(recover.Recovery, recover.Reason);
    }

    public static void SetSpeed(int decoder, double speed)
    {
        if (audioDecoders.TryGetValue(decoder, out AudioDecoder? audio))
            audio.Pump.SetSpeed(speed);
    }

    public static void Dispose(int id)
    {
        if (demuxers.Remove(id, out Demuxer? demuxer))
            demuxer.Pump.Dispose();
        else if (videoDecoders.Remove(id, out VideoDecoder? video))
            video.Dispose();
        else if (audioDecoders.Remove(id, out AudioDecoder? audio))
            audio.Pump.Dispose();
    }

    /// <summary>
    /// Steps every component until none makes progress or the budget is spent, then describes what was
    /// produced. Busy means there is more to do right away.
    /// </summary>
    public static string Pump(double budgetMilliseconds, WirePumpUpdates updates)
    {
        Apply(updates);
        long started = Stopwatch.GetTimestamp();
        bool busy = false;
        while (true)
        {
            bool progressed = false;
            foreach (VideoDecoder decoder in videoDecoders.Values)
                progressed |= Step(decoder);
            foreach (AudioDecoder decoder in audioDecoders.Values)
                progressed |= Step(decoder);
            foreach (Demuxer demuxer in demuxers.Values)
                progressed |= Step(demuxer);
            if (!progressed)
                break;
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= budgetMilliseconds)
            {
                busy = true;
                break;
            }
        }

        // Relayed sources are woken by the page's batches rather than by polling.
        bool waiting = demuxers.Values.Any(demuxer =>
            demuxer.Running && demuxer.Pump.IsWaitingForSource && demuxer.Pump.Source is not RelayedPacketSource);
        return batch.Finish(
            demuxers.Values.Select(demuxer => new WireDemuxerStatus(demuxer.Id, demuxer.Pump.GetStatus())),
            videoDecoders.Values.Where(decoder => decoder.TakeChangedDiagnostics())
                .Select(decoder => new WireDecoderStatus(decoder.Id, decoder.Diagnostics)),
            demuxers.Values.Where(demuxer => demuxer.TakeRelayReport())
                .Select(demuxer => new WireRelayStatus(demuxer.Id, demuxer.RelayReported)),
            busy,
            waiting ? 5 : 0);
    }

    private static void Apply(WirePumpUpdates updates)
    {
        foreach (int frame in updates.Releases)
            FrameRegistry.Release(frame);
        foreach (WireAllowance allowance in updates.Allowances)
        {
            if (videoDecoders.TryGetValue(allowance.Decoder, out VideoDecoder? video) && video.Output is { } videoOutput)
                videoOutput.Allowance = Math.Max(videoOutput.Allowance, allowance.Allowance);
            else if (audioDecoders.TryGetValue(allowance.Decoder, out AudioDecoder? audio) && audio.Output is { } audioOutput)
                audioOutput.Allowance = Math.Max(audioOutput.Allowance, allowance.Allowance);
        }

        foreach (WirePosition position in updates.Positions)
        {
            if (demuxers.TryGetValue(position.Demuxer, out Demuxer? demuxer))
                demuxer.Pump.ReportPlaybackPosition(position.Seconds);
        }

        foreach (WireSpeed speed in updates.Speeds)
            SetSpeed(speed.Decoder, speed.Speed);

        foreach (WireScriptFault fault in updates.Faults)
        {
            if (!videoDecoders.TryGetValue(fault.Decoder, out VideoDecoder? decoder) || decoder.WebCodecs is null)
                continue;
            decoder.WebCodecs.Fail();
            batch.Notify(new WireNotification(decoder.Id, WireNotificationKind.DecoderFaulted, fault.Message)
            {
                Fault = new WireFault(fault.Message, decoder.Source.Serial, decoder.Diagnostics, fault.RequiredSoftwareFallbackReason,
                    false, false, false, false),
            });
        }
    }

    private static bool Step(Demuxer demuxer)
    {
        if (!demuxer.Running || demuxer.Failed)
            return false;
        try
        {
            return demuxer.Pump.Step() == PumpResult.Progressed;
        }
        catch (Exception exception)
        {
            demuxer.Failed = true;
            AppLog.Error("Worker.Media", "The demuxer failed.", exception);
            batch.Notify(new WireNotification(demuxer.Id, WireNotificationKind.DemuxerFaulted, exception.Message));
            return false;
        }
    }

    private static bool Step(VideoDecoder decoder)
    {
        if (decoder.Failed)
            return false;
        try
        {
            PumpResult result = decoder.WebCodecs is { } webCodecs ? webCodecs.Step() : decoder.Ffmpeg!.Step();
            return result == PumpResult.Progressed;
        }
        catch (Exception exception)
        {
            decoder.Failed = true;
            AppLog.Error("Worker.Media", "The video decoder failed.", exception);
            NotifyFatal(decoder.Id, decoder.Diagnostics, exception);
            return false;
        }
    }

    private static bool Step(AudioDecoder decoder)
    {
        if (decoder.Failed)
            return false;
        try
        {
            return decoder.Pump.Step() == PumpResult.Progressed;
        }
        catch (Exception exception)
        {
            decoder.Failed = true;
            AppLog.Error("Worker.Media", "The audio decoder failed.", exception);
            NotifyFatal(decoder.Id, VideoDecoderDiagnostics.None, exception);
            return false;
        }
    }

    private static void NotifyFault(int decoder, DecoderFault fault) =>
        batch.Notify(new WireNotification(decoder, WireNotificationKind.DecoderFaulted, fault.Message)
        {
            Fault = new WireFault(fault.Message, fault.Serial, fault.Diagnostics, fault.RequiredSoftwareFallbackReason,
                fault.CanRestart, fault.EverDecoded, fault.IsSoftware, false),
        });

    private static void NotifyFatal(int decoder, VideoDecoderDiagnostics diagnostics, Exception exception) =>
        batch.Notify(new WireNotification(decoder, WireNotificationKind.DecoderFaulted, exception.Message)
        {
            Fault = new WireFault(exception.Message, -1, diagnostics, string.Empty, false, false, true, true),
        });

    private static unsafe string ProfileName(CodecParameters codec)
    {
        AVCodecDescriptor* descriptor = avcodec_descriptor_get_by_name(codec.CodecName);
        return descriptor is null ? string.Empty : avcodec_profile_name(descriptor->id, codec.Profile) ?? string.Empty;
    }

    private static int BitDepth(CodecParameters codec)
    {
        if (codec.BitDepth is > 0 and int depth)
            return depth;
        return codec.BitsPerRawSample > 8 ? codec.BitsPerRawSample : 8;
    }

    private static T Get<T>(Dictionary<int, T> components, int id) =>
        components.TryGetValue(id, out T? component) ? component
            : throw new InvalidOperationException($"Media component {id} is closed.");

    private static int NextId()
    {
        do
            nextId = nextId == int.MaxValue ? 1 : nextId + 1;
        while (demuxers.ContainsKey(nextId) || videoDecoders.ContainsKey(nextId) || audioDecoders.ContainsKey(nextId));
        return nextId;
    }

    private sealed class Demuxer(int id, IPacketSource source)
    {
        public int Id { get; } = id;

        public DemuxerPump Pump { get; } = new(source, PumpMode.Cooperative, pool, notification =>
            batch.Notify(new WireNotification(id,
                notification.Kind == DemuxerNotificationKind.Reconfigured
                    ? WireNotificationKind.Reconfigured
                    : WireNotificationKind.DemuxerFaulted,
                notification.Message)
            {
                Info = notification.Info,
                Position = notification.Position,
            }));

        public bool Running { get; set; }

        public bool Failed { get; set; }

        /// <summary>Run of the latest seek; frames of older runs are not handed over.</summary>
        public int Serial { get; set; }

        /// <summary>Relayed bytes last reported as consumed to the page.</summary>
        public long RelayReported { get; private set; }

        /// <summary>Whether the page should hear how far a relayed source has consumed its batches.</summary>
        public bool TakeRelayReport()
        {
            if (Pump.Source is not RelayedPacketSource relayed || relayed.Consumed == RelayReported ||
                (relayed.Consumed - RelayReported < RelayReportBytes && !relayed.IsEmpty))
            {
                return false;
            }

            RelayReported = relayed.Consumed;
            return true;
        }
    }

    private sealed class VideoDecoder(int id, Demuxer source, PacketLink input, VideoDecoderOptions options)
    {
        private VideoDecoderDiagnostics? reported;

        public int Id { get; } = id;
        public Demuxer Source { get; } = source;
        public PacketLink Input { get; } = input;
        public VideoDecoderOptions Options { get; } = options;
        public VideoDecoderPump? Ffmpeg { get; private set; }
        public WebCodecsVideoPump? WebCodecs { get; set; }
        public FrameExport<VideoFrame>? Output { get; set; }
        public string Codec { get; set; } = string.Empty;
        public VideoDecoderDiagnostics WebCodecsDiagnostics { get; set; } = VideoDecoderDiagnostics.None;

        /// <summary>Why FFmpeg decodes in software although hardware decoding was asked for.</summary>
        public string? SoftwareStatus { get; set; }

        public bool Failed { get; set; }

        public VideoDecoderDiagnostics Diagnostics => Ffmpeg?.Diagnostics is { } diagnostics
            ? SoftwareStatus is null ? diagnostics : diagnostics with { HardwareStatus = SoftwareStatus }
            : WebCodecsDiagnostics;

        public void UseFfmpeg(IVideoDecoder decoder)
        {
            Ffmpeg = new VideoDecoderPump(decoder, Source.Pump.Source, Input, PumpMode.Cooperative,
                new VideoDecoderStageOptions(Options, 0, 0), fault => NotifyFault(Id, fault));
            Codec = decoder.CodecName;
            reported = null;
        }

        public bool TakeChangedDiagnostics()
        {
            VideoDecoderDiagnostics current = Diagnostics;
            if (ReferenceEquals(current, reported) || current.Equals(reported))
                return false;
            reported = current;
            return true;
        }

        public void Dispose()
        {
            if (WebCodecs is not null)
            {
                CloseWebCodecs(Id);
                WebCodecs.Dispose();
            }

            Ffmpeg?.Dispose();
            Input.Close();
        }
    }

    private sealed class AudioDecoder(int id, Demuxer source, AudioDecoderPump pump)
    {
        public int Id { get; } = id;
        public Demuxer Source { get; } = source;
        public AudioDecoderPump Pump { get; } = pump;
        public FrameExport<AudioFrame>? Output { get; set; }
        public bool Failed { get; set; }
    }

    [JSImport("webCodecsClose", WorkerModule.Name)]
    private static partial void CloseWebCodecs(int decoder);
}
