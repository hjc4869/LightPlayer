using System;
using System.IO;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.Logging;
using SkiaSharp;

namespace LightStudio.LightPlayer.Services;

internal static unsafe partial class PhotoDecoder
{
    private static VideoFrame? DecodeDngPreview(Stream stream, CancellationToken cancellationToken, bool includeReduced = false)
    {
        long origin = stream.Position;
        try
        {
            foreach (DngPreview preview in FindDngPreviews(stream, cancellationToken, includeReduced))
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Position = preview.Offset;
                if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd8)
                    continue;
                stream.Position = preview.Offset;
                byte[] encoded = new byte[preview.Length];
                for (int position = 0; position < encoded.Length;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(64 * 1024, encoded.Length - position);
                    stream.ReadExactly(encoded.AsSpan(position, count));
                    position += count;
                }
                using SKData data = SKData.CreateCopy(encoded);
                using SKCodec? codec = SKCodec.Create(data);
                if (codec is null || codec.Info.Width != preview.Width || codec.Info.Height != preview.Height ||
                    (long)codec.Info.Width * codec.Info.Height > 100_000_000)
                    continue;

                using var embedded = new MemoryStream(encoded, writable: false);
                VideoFrame? frame;
                try
                {
                    frame = Decode(embedded, cancellationToken);
                }
                catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
                {
                    AppLog.Warning("Photo", "Could not decode the embedded DNG preview.", exception);
                    continue;
                }
                if (frame is null)
                    continue;
                if (codec.EncodedOrigin == SKEncodedOrigin.TopLeft && preview.Orientation is >= 2 and <= 8)
                {
                    frame.Rotation = preview.Orientation switch { 3 or 4 => 180, 5 or 8 => 270, 6 or 7 => 90, _ => 0 };
                    frame.MirrorHorizontally = preview.Orientation is 2 or 4 or 5 or 7;
                }
                frame.IsEmbeddedRawPreview = true;
                return frame;
            }
            return null;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        finally
        {
            stream.Position = origin;
        }
    }
}