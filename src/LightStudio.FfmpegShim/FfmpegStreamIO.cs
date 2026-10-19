using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using LightStudio.NativeBindings.Types;
using LightStudio.Logging;
using static LightStudio.FfmpegShim.Ffmpeg;

/// <summary>Marks a managed method that is called from native code; required by the Mono AOT compilers.</summary>
[AttributeUsage(AttributeTargets.Method)]
sealed class MonoPInvokeCallbackAttribute : Attribute
{
    public MonoPInvokeCallbackAttribute(Type t) { }
}

namespace LightStudio.FfmpegShim
{
    /// <summary>
    /// Bridges a <see cref="Stream"/> to libavformat through a custom <c>AVIOContext</c>, using
    /// native callbacks for browser source clients.
    /// </summary>
    /// <remarks>
    /// The context is owned by this object, not by the format context: libavformat never frees a
    /// caller supplied <c>pb</c> (it sets <c>AVFMT_FLAG_CUSTOM_IO</c>), and a failed
    /// <c>avformat_open_input</c> frees the format context, which would otherwise strand the buffer.
    /// Dispose this only after the format context has been closed.
    /// </remarks>
    public sealed unsafe partial class FfmpegStreamIO : IDisposable
    {
        public const int DefaultBufferSize = 0x10000;

        private sealed class StreamState
        {
            public Stream Stream;
            public ExceptionDispatchInfo ReadFailure;
        }

        private readonly SourceBridgeContext nativeSource;
        private GCHandle stateHandle;
        private AVIOContext* context;
        private bool disposed;

        public FfmpegStreamIO(Stream stream, int bufferSize = DefaultBufferSize)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bufferSize, 0);

            void* opaque;
            delegate* unmanaged[Cdecl]<void*, byte*, int, int> readCallback;
            delegate* unmanaged[Cdecl]<void*, long, int, long> seekCallback;
            if (OperatingSystem.IsBrowser() && stream is SourceBridgeContext bridge)
            {
                nativeSource = bridge;
                bridge.ResetReadFailure();
                opaque = (void*)bridge.Handle;
                readCallback = (delegate* unmanaged[Cdecl]<void*, byte*, int, int>)bridge.ReadCallback;
                seekCallback = (delegate* unmanaged[Cdecl]<void*, long, int, long>)bridge.SeekCallback;
            }
            else
            {
                stateHandle = GCHandle.Alloc(new StreamState { Stream = stream });
                opaque = (void*)GCHandle.ToIntPtr(stateHandle);
                readCallback = &StreamRead;
                seekCallback = stream.CanSeek ? &StreamSeek : null;
            }

            byte* buffer = (byte*)av_malloc((nuint)bufferSize);
            if (buffer == null)
            {
                ReleaseHandles();
                throw new OutOfMemoryException("Failed to allocate the AVIO buffer.");
            }

            // A non-null seek callback tells libavformat the source is seekable, so it must be
            // omitted for forward-only streams rather than left to fail at runtime.
            context = avio_alloc_context(
                buffer,
                bufferSize,
                0,
                opaque,
                readCallback,
                null,
                seekCallback);
            if (context == null)
            {
                av_free(buffer);
                ReleaseHandles();
                throw new OutOfMemoryException("Failed to allocate the AVIO context.");
            }
        }

        public AVIOContext* Context => context;

        internal void ThrowIfReadFailed()
        {
            if (OperatingSystem.IsBrowser() && nativeSource is not null)
                nativeSource.ThrowIfReadFailed();
            else
                ((StreamState)stateHandle.Target).ReadFailure?.Throw();
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;

            if (context != null)
            {
                // libavformat may have replaced the original buffer, so free the current one.
                av_freep(&context->buffer);
                av_free(context);
                context = null;
            }

            ReleaseHandles();
        }

        private void ReleaseHandles()
        {
            if (stateHandle.IsAllocated)
                stateHandle.Free();
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int StreamRead(void* opaque, byte* buffer, int bufferSize)
        {
            var state = (StreamState)GCHandle.FromIntPtr((IntPtr)opaque).Target;
            try
            {
                int read = state.Stream.Read(new Span<byte>(buffer, bufferSize));
                if (read == 0)
                {
                    // Must be AVERROR_EOF: any other negative value is recorded by avio as a
                    // hard I/O error instead of a clean end of stream.
                    return AVERROR_EOF;
                }

                return read;
            }
            catch (OperationCanceledException exception)
            {
                state.ReadFailure ??= ExceptionDispatchInfo.Capture(exception);
                AppLog.Debug("FFmpeg.StreamIO", $"Managed stream read was canceled: {exception.Message}");
                return Interop.FfmpegError.InputOutput;
            }
            catch (Exception exception)
            {
                state.ReadFailure ??= ExceptionDispatchInfo.Capture(exception);
                AppLog.Error("FFmpeg.StreamIO", "Managed stream read failed.", exception);
                return Interop.FfmpegError.InputOutput;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static long StreamSeek(void* opaque, long pos, int whence)
        {
            var state = (StreamState)GCHandle.FromIntPtr((IntPtr)opaque).Target;
            try
            {
                if (whence == AVSEEK_SIZE)
                {
                    return state.Stream.Length;
                }

                return state.Stream.Seek(pos, (SeekOrigin)whence);
            }
            catch (Exception ex)
            {
                AppLog.Warning(
                    "FFmpeg.StreamIO",
                    $"Managed stream seek failed: position={pos}; whence={whence}.",
                    ex);
                return Interop.FfmpegError.InputOutput;
            }
        }
    }
}
