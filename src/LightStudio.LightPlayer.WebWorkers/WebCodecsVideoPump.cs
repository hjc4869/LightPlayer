using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Media;
using LightStudio.NativeBindings;

namespace LightStudio.LightPlayer.WebWorkers;

/// <summary>
/// Feeds a video stream's packets to the browser's WebCodecs decoder that the worker script runs.
/// The script owns the decoded frames and hands them to the page itself, so this pump only paces the
/// packets: it stops while the decoder is saturated and tells the script where each run starts.
/// </summary>
[SupportedOSPlatform("browser")]
[Include("libavformat/avformat.h")]
[Include("libavutil/hwcontext.h")]
internal sealed partial class WebCodecsVideoPump(int decoder, IPacketSource source, int streamIndex, PacketLink input)
{
    private int activeSerial = -1;
    private bool ended;
    private bool faulted;

    public PacketLink Input => input;

    public int StreamIndex => streamIndex;

    public void Fail() => faulted = true;

    public PumpResult Step()
    {
        if (faulted)
            return PumpResult.Finished;
        if (!CanDecode(decoder))
            return PumpResult.Idle;
        if (!input.Queue.TryDequeueNow(out MediaPacket packet))
            return PumpResult.Idle;

        try
        {
            if (packet.Serial != input.Queue.Serial)
                return PumpResult.Progressed;

            if (packet.Serial != activeSerial)
            {
                bool continues = input.Continues(activeSerial, packet.Serial);
                activeSerial = packet.Serial;
                ended = false;
                double target = input.GetTarget(packet.Serial);
                Reset(decoder, activeSerial, double.IsNaN(target) ? 0 : target, continues);
            }

            if (ended)
                return PumpResult.Progressed;

            if (packet.IsEndOfStream)
            {
                ended = true;
                End(decoder, activeSerial);
                return PumpResult.Progressed;
            }

            long stamp = packet.Pts != AV_NOPTS_VALUE ? packet.Pts : packet.Dts;
            double pts = source.ToSeconds(streamIndex, stamp);
            double duration = packet.Duration > 0
                ? source.ToSeconds(streamIndex, stamp + packet.Duration) - pts
                : 0;
            Decode(decoder, activeSerial, (int)packet.Data, packet.SizeInBytes, pts, duration, packet.IsKeyFrame);
            return PumpResult.Progressed;
        }
        finally
        {
            packet.Recycle();
        }
    }

    public void Dispose() => input.Close();

    [JSImport("webCodecsCanDecode", WorkerModule.Name)]
    private static partial bool CanDecode(int decoder);

    [JSImport("webCodecsReset", WorkerModule.Name)]
    private static partial void Reset(int decoder, int serial, double minimumPts, bool continues);

    [JSImport("webCodecsDecode", WorkerModule.Name)]
    private static partial void Decode(int decoder, int serial, int data, int length, double pts, double duration, bool key);

    [JSImport("webCodecsEnd", WorkerModule.Name)]
    private static partial void End(int decoder, int serial);
}
