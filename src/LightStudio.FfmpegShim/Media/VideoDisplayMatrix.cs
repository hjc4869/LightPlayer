using System;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

/// <summary>
/// Reads the container's display matrix, which is how a camera says the picture it recorded
/// sideways has to be turned before anyone looks at it.
/// </summary>
/// <remarks>
/// <para>
/// The matrix is a 3x3 affine transform in 16.16 fixed point (2.30 for the bottom row) laid out as
/// <c>| a b u | c d v | x y w |</c>, applied to a row vector, so the source x axis lands on
/// <c>(a, b)</c> and the source y axis on <c>(c, d)</c>. Every matrix a real container carries is a
/// signed permutation of those axes, i.e. one of the eight symmetries of a rectangle, which
/// decomposes uniquely into a mirror followed by a quarter turn.
/// </para>
/// <para>
/// Decoders do not apply any of this: FFmpeg hands the picture over exactly as it was coded and
/// leaves the turning to whoever displays it.
/// </para>
/// </remarks>
public static unsafe partial class VideoDisplayMatrix
{
    private const int ElementCount = 9;

    /// <summary>Rotation and mirroring of the video stream, both zero when it declares none.</summary>
    public static void Read(AVCodecParameters* parameters, out int rotation, out bool mirrorHorizontally)
    {
        rotation = 0;
        mirrorHorizontally = false;
        if (parameters == null || parameters->coded_side_data == null || parameters->nb_coded_side_data <= 0)
            return;

        AVPacketSideData* sideData = av_packet_side_data_get(
            parameters->coded_side_data, parameters->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
        if (sideData == null || sideData->data == null || sideData->size < ElementCount * sizeof(int))
        {
            return;
        }

        TryDecode(new ReadOnlySpan<int>(sideData->data, ElementCount), out rotation, out mirrorHorizontally);
    }

    /// <summary>The same for a decoded frame, which carries the matrix only where a packet did.</summary>
    public static bool TryRead(AVFrame* frame, out int rotation, out bool mirrorHorizontally)
    {
        rotation = 0;
        mirrorHorizontally = false;
        if (frame == null)
            return false;

        AVFrameSideData* sideData = av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DISPLAYMATRIX);
        if (sideData == null || sideData->data == null || sideData->size < ElementCount * sizeof(int))
        {
            return false;
        }

        return TryDecode(new ReadOnlySpan<int>(sideData->data, ElementCount), out rotation, out mirrorHorizontally);
    }

    /// <summary>
    /// Splits a display matrix into a left-to-right mirror and a clockwise quarter turn applied
    /// after it, in that order. False when the matrix is degenerate and describes nothing.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<int> matrix, out int rotation, out bool mirrorHorizontally)
    {
        rotation = 0;
        mirrorHorizontally = false;
        if (matrix.Length < ElementCount)
            return false;

        double a = matrix[0];
        double b = matrix[1];
        double c = matrix[3];
        double d = matrix[4];

        double determinant = (a * d) - (b * c);
        if (determinant == 0)
            return false;

        // A mirror is the only thing that can turn the axes from right-handed to left-handed, and
        // it commutes out to the front of any rotation, so its sign alone identifies it.
        mirrorHorizontally = determinant < 0;

        // Where the source y axis ends up gives the turn whether or not the picture was mirrored
        // first, because a left-to-right mirror leaves that axis alone.
        double degrees = Math.Atan2(-c, d) * 180 / Math.PI;
        rotation = (((int)Math.Round(degrees / 90) * 90) % 360 + 360) % 360;
        return true;
    }
}
