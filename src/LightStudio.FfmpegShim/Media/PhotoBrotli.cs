#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace LightStudio.FfmpegShim.Media;

public static unsafe class PhotoBrotli
{
    public static byte[]? Decode(byte[] compressed, int maximumLength, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsBrowser())
            return DecodeBrowser(compressed, maximumLength, cancellationToken);
        try
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] buffer = new byte[16384];
            int count;
            while ((count = brotli.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (output.Length + count > maximumLength)
                    return null;
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("browser")]
    private static byte[]? DecodeBrowser(byte[] compressed, int maximumLength, CancellationToken cancellationToken)
    {
        IntPtr decoder = BrotliDecoderCreateInstance(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (decoder == IntPtr.Zero)
            throw new OutOfMemoryException("Could not allocate the photo Brotli decoder.");
        try
        {
            using var output = new MemoryStream();
            byte[] buffer = new byte[16384];
            fixed (byte* inputStart = compressed)
            fixed (byte* outputStart = buffer)
            {
                byte* input = inputStart;
                nuint availableInput = (nuint)compressed.Length;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte* target = outputStart;
                    nuint availableOutput = (nuint)buffer.Length;
                    int result = BrotliDecoderDecompressStream(decoder, &availableInput, &input, &availableOutput, &target, null);
                    int count = buffer.Length - (int)availableOutput;
                    if (output.Length + count > maximumLength)
                        return null;
                    output.Write(buffer, 0, count);
                    if (result == 1)
                        return output.ToArray();
                    if (result == 2)
                        return null;
                    if (result == 0)
                    {
                        int error = BrotliDecoderGetErrorCode(decoder);
                        if (error is >= -19 and <= -1)
                            return null;
                        throw new InvalidOperationException($"Photo Brotli decoding failed with native error {error}.");
                    }
                }
            }
        }
        finally
        {
            BrotliDecoderDestroyInstance(decoder);
        }
    }

    [DllImport("libbrotlidec", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr BrotliDecoderCreateInstance(IntPtr allocate, IntPtr free, IntPtr opaque);

    [DllImport("libbrotlidec", CallingConvention = CallingConvention.Cdecl)]
    private static extern void BrotliDecoderDestroyInstance(IntPtr decoder);

    [DllImport("libbrotlidec", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BrotliDecoderDecompressStream(IntPtr decoder, nuint* availableInput, byte** input,
        nuint* availableOutput, byte** output, nuint* totalOutput);

    [DllImport("libbrotlidec", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BrotliDecoderGetErrorCode(IntPtr decoder);
}