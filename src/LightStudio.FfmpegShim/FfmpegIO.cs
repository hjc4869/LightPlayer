using FFmpeg.AutoGen;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LightStudio.FfmpegShim
{
    public unsafe class FfmpegIO
    {
        byte[] streamReadBuffer;
        Stream _stream;

        static FfmpegIO()
        {
            FfmpegNativeInitializer.Initialize();
        }

        public FfmpegIO(Stream stream)
        {
            streamReadBuffer = new byte[4096];
            _stream = stream;
        }
        public void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream?.Dispose();
            }
        }
        public static int StreamRead(void* opaque, byte* buffer, int bufferSize)
        {
            var handle = (GCHandle*)opaque;
            var obj = handle->Target as FfmpegIO;
            try
            {
                int read = 0;
                if (obj.streamReadBuffer.Length < bufferSize)
                {
                    obj.streamReadBuffer = new byte[2 * bufferSize];
                }
                read = obj._stream.Read(obj.streamReadBuffer, 0, bufferSize);

                if (read == 0)
                {
                    return -0x5fb9b0bb; // {'E', 'O', 'F', ' '}
                }
                Marshal.Copy(obj.streamReadBuffer, 0, (IntPtr)buffer, read);
                return read;
            }
            catch
            {
                return -1;
            }
        }
        public static long StreamSeek(void* opaque, long pos, int whence)
        {
            var handle = (GCHandle*)opaque;
            var obj = handle->Target as FfmpegIO;
            try
            {
                if (whence == ffmpeg.AVSEEK_SIZE)
                {
                    return obj._stream.Length;
                }

                return obj._stream.Seek(pos, (SeekOrigin)whence);
            }
            catch
            {
                return -1;
            }
        }
    }
}
