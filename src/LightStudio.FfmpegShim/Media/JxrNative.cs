using System.Runtime.InteropServices;

namespace LightStudio.FfmpegShim.Media;

internal static unsafe class JxrNative
{
    private const string Library = "libjpegxr";

    [StructLayout(LayoutKind.Sequential)]
    internal struct ImageInfo
    {
        public nuint cWidth, cHeight;
        public int cfColorFormat, bdBitDepth;
        public nuint cBitsPerUnit, cLeadingPadding;
        public int bRGB;
        public byte cChromaCenteringX, cChromaCenteringY;
        public nuint cROILeftX, cROIWidth, cROITopY, cROIHeight;
        public int bSkipFlexbits;
        public nuint cThumbnailWidth, cThumbnailHeight;
        public int oOrientation;
        public byte cPostProcStrength;
        public int fPaddedUserBuffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CodecParameters
    {
        public int bVerbose;
        public byte uiDefaultQPIndex, uiDefaultQPIndexYLP, uiDefaultQPIndexYHP;
        public byte uiDefaultQPIndexU, uiDefaultQPIndexULP, uiDefaultQPIndexUHP;
        public byte uiDefaultQPIndexV, uiDefaultQPIndexVLP, uiDefaultQPIndexVHP, uiDefaultQPIndexAlpha;
        public int cfColorFormat, bdBitDepth, olOverlap, bfBitstreamFormat;
        public nuint cChannel;
        public byte uAlphaMode;
        public int sbSubband;
        public byte uiTrimFlexBits;
        public void* pWStream;
        public nuint cbStream;
        public uint cNumOfSliceMinus1V;
        public fixed uint uiTileX[4096];
        public uint cNumOfSliceMinus1H;
        public fixed uint uiTileY[4096];
        public byte nLenMantissaOrShift;
        public sbyte nExpBias;
        public int bBlackWhite, bUseHardTileBoundaries, bProgressiveMode, bYUVData, bUnscaledArith, fMeasurePerf;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ImageBuffer
    {
        public void* pv;
        public nuint cLine, cbStride;
        public uint uiFirstMBRow, uiLastMBRow;
        public nuint cLinesDecoded;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CreateWS_Memory(void** stream, void* data, nuint length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CloseWS_Memory(void** stream);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ImageStrDecGetInfo(ImageInfo* image, CodecParameters* parameters);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ImageStrDecInit(ImageInfo* image, CodecParameters* parameters, void** decoder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ImageStrDecDecode(void* decoder, ImageBuffer* output, nuint* decodedLines);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ImageStrDecTerm(void* decoder);
}