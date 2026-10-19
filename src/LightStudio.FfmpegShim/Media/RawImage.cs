using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using LightStudio.NativeBindings.Types;

namespace LightStudio.FfmpegShim.Media;

internal static unsafe class RawImage
{
    private const string Library = "libraw";
    private const long MaximumPixels = 100_000_000;
    private const long MaximumFileBytes = 512 * 1024 * 1024;
    private static readonly object DecodeLock = new();

    internal delegate T Decoded<out T>(AVFrame* frame) where T : class;

    internal static bool IsDng(Stream stream)
    {
        if (!stream.CanSeek)
            return false;

        long origin = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[8];
            if (stream.ReadAtLeast(header, header.Length, false) != header.Length ||
                (!header.StartsWith("II*\0"u8) && !header.StartsWith("MM\0*"u8)))
                return false;

            bool littleEndian = header[0] == 'I';
            uint directory = ReadUInt32(header[4..], littleEndian);
            if (directory < 8 || directory > stream.Length - origin - 2)
                return false;

            stream.Position = origin + directory;
            stream.ReadExactly(header[..2]);
            int entries = ReadUInt16(header, littleEndian);
            if (entries > (stream.Length - stream.Position) / 12)
                return false;

            Span<byte> entry = stackalloc byte[12];
            for (int index = 0; index < entries; index++)
            {
                stream.ReadExactly(entry);
                if (ReadUInt16(entry, littleEndian) == 50706 &&
                    ReadUInt16(entry[2..], littleEndian) == 1 &&
                    ReadUInt32(entry[4..], littleEndian) == 4)
                    return true;
            }

            return false;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    internal static T Open<T>(Stream stream, bool wideGamut, Decoded<T> use, CancellationToken token)
        where T : class
    {
        token.ThrowIfCancellationRequested();
        lock (DecodeLock)
        {
            token.ThrowIfCancellationRequested();
            long length = stream.Length - stream.Position;
            if (length <= 0 || length > MaximumFileBytes)
                throw new InvalidDataException($"DNG input exceeds the {MaximumFileBytes} byte limit.");

            byte* input = (byte*)NativeMemory.Alloc((nuint)length);
            IntPtr decoder = IntPtr.Zero;
            ProcessedImage* image = null;
            GCHandle state = default;
            try
            {
                for (int position = 0; position < length;)
                {
                    token.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(64 * 1024, length - position);
                    stream.ReadExactly(new Span<byte>(input + position, count));
                    position += count;
                }

                decoder = libraw_init(0);
                if (decoder == IntPtr.Zero)
                    throw new OutOfMemoryException("LibRaw could not allocate a decoder.");

                var decodeState = new DecodeState(token);
                state = GCHandle.Alloc(decodeState);
                libraw_set_progress_handler(decoder, &Progress, GCHandle.ToIntPtr(state));
                libraw_set_dataerror_handler(decoder, &DataError, GCHandle.ToIntPtr(state));
                Check(libraw_open_buffer(decoder, input, (nuint)length), "open", decodeState);

                int width = libraw_get_raw_width(decoder);
                int height = libraw_get_raw_height(decoder);
                if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels)
                    throw new InvalidDataException($"DNG dimensions {width}x{height} exceed the supported pixel limit.");

                libraw_set_output_color(decoder, wideGamut ? 8 : 1);
                libraw_set_output_bps(decoder, wideGamut ? 16 : 8);
                libraw_set_gamma(decoder, 0, wideGamut ? 1 : 1f / 2.4f);
                libraw_set_gamma(decoder, 1, wideGamut ? 1 : 12.92f);
                libraw_set_no_auto_bright(decoder, 1);
                libraw_set_adjust_maximum_thr(decoder, 0);
                libraw_set_demosaic(decoder, 3);

                float red = libraw_get_cam_mul(decoder, 0);
                float green = libraw_get_cam_mul(decoder, 1);
                float blue = libraw_get_cam_mul(decoder, 2);
                if (float.IsFinite(red) && red > 0 && float.IsFinite(green) && green > 0 &&
                    float.IsFinite(blue) && blue > 0)
                {
                    libraw_set_user_mul(decoder, 0, red);
                    libraw_set_user_mul(decoder, 1, green);
                    libraw_set_user_mul(decoder, 2, blue);
                    float secondGreen = libraw_get_cam_mul(decoder, 3);
                    libraw_set_user_mul(decoder, 3,
                        float.IsFinite(secondGreen) && secondGreen > 0 ? secondGreen : green);
                }

                Check(libraw_unpack(decoder), "unpack", decodeState);
                Check(libraw_dcraw_process(decoder), "process", decodeState);
                int error;
                image = libraw_dcraw_make_mem_image(decoder, &error);
                Check(error, "copy pixels", decodeState);
                if (image == null || image->Type != 2 || image->Colors is not (1 or 3) ||
                    image->Bits != (wideGamut ? 16 : 8) || image->Width == 0 || image->Height == 0 ||
                    (long)image->Width * image->Height > MaximumPixels ||
                    image->DataSize != (long)image->Width * image->Height * image->Colors * image->Bits / 8)
                    throw new InvalidDataException("LibRaw returned an unsupported bitmap layout.");

                AVFrame frame = default;
                frame.width = image->Width;
                frame.height = image->Height;
                frame.format = (int)(image->Colors == 1
                    ? wideGamut ? AVPixelFormat.AV_PIX_FMT_GRAY16LE : AVPixelFormat.AV_PIX_FMT_GRAY8
                    : wideGamut ? AVPixelFormat.AV_PIX_FMT_RGB48LE : AVPixelFormat.AV_PIX_FMT_RGB24);
                frame.data[0] = (byte*)image + sizeof(ProcessedImage);
                frame.linesize[0] = image->Width * image->Colors * image->Bits / 8;
                frame.color_primaries = wideGamut ? AVColorPrimaries.AVCOL_PRI_BT2020 : AVColorPrimaries.AVCOL_PRI_BT709;
                frame.color_trc = wideGamut ? AVColorTransferCharacteristic.AVCOL_TRC_LINEAR : AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1;
                frame.colorspace = AVColorSpace.AVCOL_SPC_RGB;
                frame.color_range = AVColorRange.AVCOL_RANGE_JPEG;
                token.ThrowIfCancellationRequested();
                return use(&frame);
            }
            finally
            {
                if (image != null)
                    libraw_dcraw_clear_mem(image);
                if (decoder != IntPtr.Zero)
                    libraw_close(decoder);
                if (state.IsAllocated)
                    state.Free();
                NativeMemory.Free(input);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Progress(IntPtr state, int stage, int iteration, int expected)
    {
        var decodeState = (DecodeState)GCHandle.FromIntPtr(state).Target;
        return decodeState.Token.IsCancellationRequested ||
            Interlocked.Read(ref decodeState.ErrorOffset) != long.MinValue ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DataError(IntPtr state, IntPtr file, long offset)
    {
        var decodeState = (DecodeState)GCHandle.FromIntPtr(state).Target;
        Interlocked.Exchange(ref decodeState.ErrorOffset, offset);
    }

    private static void Check(int error, string operation, DecodeState state)
    {
        state.Token.ThrowIfCancellationRequested();
        long offset = Interlocked.Read(ref state.ErrorOffset);
        if (error != 0 || offset != long.MinValue)
        {
            string detail = offset == long.MinValue ? string.Empty : $" Data error at offset {offset}.";
            throw new InvalidDataException($"LibRaw {operation} failed ({error}): {Marshal.PtrToStringUTF8(libraw_strerror(error))}.{detail}");
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private sealed class DecodeState(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public long ErrorOffset = long.MinValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessedImage
    {
        public int Type;
        public ushort Height;
        public ushort Width;
        public ushort Colors;
        public ushort Bits;
        public uint DataSize;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr libraw_init(uint flags);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libraw_close(IntPtr decoder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int libraw_versionNumber();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr libraw_strerror(int error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int libraw_open_buffer(IntPtr decoder, byte* buffer, nuint length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int libraw_unpack(IntPtr decoder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int libraw_dcraw_process(IntPtr decoder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern ProcessedImage* libraw_dcraw_make_mem_image(IntPtr decoder, int* error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libraw_dcraw_clear_mem(ProcessedImage* image);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int libraw_get_raw_width(IntPtr decoder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int libraw_get_raw_height(IntPtr decoder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern float libraw_get_cam_mul(IntPtr decoder, int index);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libraw_set_user_mul(IntPtr decoder, int index, float value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libraw_set_output_color(IntPtr decoder, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libraw_set_output_bps(IntPtr decoder, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libraw_set_gamma(IntPtr decoder, int index, float value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libraw_set_no_auto_bright(IntPtr decoder, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libraw_set_adjust_maximum_thr(IntPtr decoder, float value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libraw_set_demosaic(IntPtr decoder, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libraw_set_progress_handler(IntPtr decoder,
        delegate* unmanaged[Cdecl]<IntPtr, int, int, int, int> callback, IntPtr state);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libraw_set_dataerror_handler(IntPtr decoder,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, long, void> callback, IntPtr state);
}