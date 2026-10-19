using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.Remote;

namespace LightStudio.RemoteApi.Streaming;

internal sealed class PrimedPacketStreamWriter(
    IPacketStreamWriter destination,
    PacketCreditGate credit,
    Func<bool> isReady,
    Func<RemoteStreamManifest> describe,
    CancellationToken cancellationToken) : IPacketStreamWriter, IDisposable
{
    private const int MaximumBufferedPackets = 4096;
    private const long MaximumBufferedBytes = 64L * 1024 * 1024;
    private readonly Queue<(MediaPacket? Packet, int TrackId)> pending = new();
    private readonly MediaPacketPool pool = new(4);
    private long bufferedBytes;
    private bool published;

    public Task WriteManifestAsync(ReadOnlyMemory<byte> json) => destination.WriteManifestAsync(json);

    public async Task WritePacketAsync(
        int trackId, long pts, long dts, long duration, bool isKeyFrame,
        IntPtr bitstream, int length, ReadOnlyMemory<byte> hdr10PlusMetadata = default)
    {
        if (await TryPublishAsync().ConfigureAwait(false))
        {
            await ForwardAsync(trackId, pts, dts, duration, isKeyFrame, bitstream, length, hdr10PlusMetadata)
                .ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (pending.Count >= MaximumBufferedPackets || bufferedBytes + length + hdr10PlusMetadata.Length > MaximumBufferedBytes)
        {
            throw new InvalidDataException("The transcode run exceeded its output initialization budget.");
        }

        MediaPacket packet = Capture(trackId, pts, dts, duration, isKeyFrame, bitstream, length, hdr10PlusMetadata.Span);
        pending.Enqueue((packet, trackId));
        bufferedBytes += length + hdr10PlusMetadata.Length;
    }

    public async Task WriteEndOfTrackAsync(int trackId)
    {
        if (await TryPublishAsync().ConfigureAwait(false))
            await destination.WriteEndOfTrackAsync(trackId).ConfigureAwait(false);
        else
            pending.Enqueue((null, trackId));
    }

    public async Task WriteEndOfStreamAsync()
    {
        if (!await TryPublishAsync().ConfigureAwait(false))
            throw new InvalidDataException("The transcode run ended before its output was initialized.");
        await destination.WriteEndOfStreamAsync().ConfigureAwait(false);
    }

    public Task WriteErrorAsync(string code, string message) => destination.WriteErrorAsync(code, message);

    private async Task<bool> TryPublishAsync()
    {
        if (published)
            return true;
        if (!isReady())
            return false;

        await destination.WriteManifestAsync(JsonSerializer.SerializeToUtf8Bytes(
            describe(), RemoteApiJsonContext.Default.RemoteStreamManifest)).ConfigureAwait(false);
        published = true;
        while (pending.TryDequeue(out var entry))
        {
            if (entry.Packet is not { } packet)
            {
                await destination.WriteEndOfTrackAsync(entry.TrackId).ConfigureAwait(false);
                continue;
            }

            try
            {
                await ForwardAsync(entry.TrackId, packet.Pts, packet.Dts,
                    (int)packet.Duration, packet.IsKeyFrame, packet.Data, packet.SizeInBytes,
                    packet.CopyHdr10PlusMetadata() ?? ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
            }
            finally
            {
                packet.Recycle();
            }
        }

        bufferedBytes = 0;
        return true;
    }

    private async Task ForwardAsync(
        int trackId, long pts, long dts, long duration, bool isKeyFrame,
        IntPtr bitstream, int length, ReadOnlyMemory<byte> metadata)
    {
        await credit.WaitAsync(cancellationToken).ConfigureAwait(false);
        credit.Take(PacketStreamProtocol.PacketCost(length));
        await destination.WritePacketAsync(trackId, pts, dts, duration, isKeyFrame, bitstream, length, metadata)
            .ConfigureAwait(false);
    }

    private unsafe MediaPacket Capture(
        int trackId, long pts, long dts, long duration, bool isKeyFrame,
        IntPtr bitstream, int length, ReadOnlySpan<byte> metadata)
    {
        MediaPacket packet = pool.Rent();
        try
        {
            packet.FillFromWire(trackId, pts, dts, duration, isKeyFrame,
                new ReadOnlySpan<byte>(bitstream.ToPointer(), length), metadata);
            return packet;
        }
        catch
        {
            packet.Recycle();
            throw;
        }
    }

    public void Dispose()
    {
        while (pending.TryDequeue(out var entry))
            entry.Packet?.Recycle();
        pool.Clear();
    }
}