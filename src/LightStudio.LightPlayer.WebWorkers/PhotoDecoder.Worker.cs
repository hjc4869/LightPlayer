using System.IO;
using System.Numerics;
using System.Threading;
using LightStudio.FfmpegShim.Engine;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Where a photo keeps its gain map, found by the same parsers the desktop photo decoder uses. The images
/// are either items of an ISO base media container, encoded bytes, or JPEG byte ranges of the file.
/// </summary>
internal sealed record PhotoGainMapLocation(
    WireGainMap Parameters,
    uint? BaseItem,
    uint? GainItem,
    byte[]? EncodedGain,
    byte[]? BaseProfile,
    byte[]? MathProfile,
    int MathPrimaries,
    (long Offset, int Length)? BaseRange,
    (long Offset, int Length)? GainRange);

internal static partial class PhotoDecoder
{
    internal static PhotoGainMapLocation? FindGainMap(Stream stream, CancellationToken cancellationToken, uint? imageItem = null)
    {
        long origin = stream.Position;
        try
        {
            if (ReadContainer(stream, cancellationToken, imageItem) is { } source)
            {
                return new PhotoGainMapLocation(Describe(source.Metadata), source.BaseItem, source.GainItem, source.EncodedGain,
                    source.BaseProfile, source.MathProfile, source.MathPrimaries, null, null);
            }

            stream.Position = origin;
            if (!TryFindJpegGainMap(stream, out ImageRange primary, out ImageRange gain, out GainMap metadata))
                return null;
            primary = TrimJpegRange(stream, primary);
            gain = TrimJpegRange(stream, gain);
            return new PhotoGainMapLocation(Describe(metadata), null, null, null, null, null, 0,
                (primary.Offset, primary.Length), (gain.Offset, gain.Length));
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

    private static WireGainMap Describe(GainMap metadata) => new(
        Values(metadata.Minimum), Values(metadata.Maximum), Values(metadata.Gamma),
        Values(metadata.OffsetSdr), Values(metadata.OffsetHdr), metadata.CapacityMin, metadata.CapacityMax,
        metadata.BaseIsHdr, metadata.UseBaseColorSpace, metadata.IsApple);

    private static float[] Values(Vector3 vector) => [vector.X, vector.Y, vector.Z];
}
