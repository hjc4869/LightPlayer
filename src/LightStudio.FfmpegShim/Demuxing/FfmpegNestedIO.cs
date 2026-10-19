#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using LightStudio.NativeBindings.Types;
using LightStudio.Logging;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Demuxing;

/// <summary>Owns the managed resources opened through an adaptive demuxer's child I/O callbacks.</summary>
internal sealed unsafe partial class FfmpegNestedIO : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int IoOpenCallback(
        AVFormatContext* context,
        AVIOContext** child,
        byte* url,
        int flags,
        AVDictionary** options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InterruptCallback(void* opaque);

    private sealed class ChildResource : IDisposable
    {
        private readonly FfmpegStreamIO io;
        private readonly MediaResource resource;

        public ChildResource(FfmpegStreamIO io, MediaResource resource)
        {
            this.io = io;
            this.resource = resource;
        }

        public void Dispose()
        {
            io.Dispose();
            resource.Dispose();
        }
    }

    private static readonly IoOpenCallback OpenCallback = OpenChild;
    private static readonly InterruptCallback Interrupt = IsInterrupted;

    private readonly IMediaResourceResolver resolver;
    private readonly Dictionary<nint, ChildResource> children = [];
    private GCHandle stateHandle;
    private int interruptRequested;
    private bool disposed;

    public FfmpegNestedIO(IMediaResourceResolver resolver)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        stateHandle = GCHandle.Alloc(this);
    }

    public void Attach(AVFormatContext* context)
    {
        ArgumentNullException.ThrowIfNull(context);
        void* state = (void*)GCHandle.ToIntPtr(stateHandle);
        context->opaque = state;
        context->io_open = (delegate* unmanaged[Cdecl]<AVFormatContext*, AVIOContext**, byte*, int, AVDictionary**, int>)Marshal.GetFunctionPointerForDelegate(OpenCallback);
        context->io_close2 = &CloseChild;
        context->interrupt_callback.callback = (delegate* unmanaged[Cdecl]<void*, int>)Marshal.GetFunctionPointerForDelegate(Interrupt);
        context->interrupt_callback.opaque = state;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Volatile.Write(ref interruptRequested, 1);

        foreach (ChildResource child in children.Values)
            child.Dispose();
        children.Clear();

        if (stateHandle.IsAllocated)
            stateHandle.Free();
    }

    private int Open(string url, int flags, AVDictionary** options, AVIOContext** output)
    {
        if (disposed || string.IsNullOrWhiteSpace(url) || (flags & AVIO_FLAG_WRITE) != 0)
            return AVERROR_PROTOCOL_NOT_FOUND;

        MediaResourceRange? range = ReadRange(options);
        MediaResource? resource = null;
        FfmpegStreamIO? io = null;
        try
        {
            resource = url.StartsWith("crypto+", StringComparison.OrdinalIgnoreCase)
                ? OpenEncrypted(url["crypto+".Length..], range, options)
                : resolver.Open(url, range);
            io = new FfmpegStreamIO(resource.Stream);
            if (range is { Offset: > 0 } requested)
                io.Context->pos = requested.Offset;
            *output = io.Context;
            children.Add((nint)io.Context, new ChildResource(io, resource));
            return 0;
        }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref interruptRequested, 1);
            io?.Dispose();
            resource?.Dispose();
            return AVERROR_EXIT;
        }
        catch (Exception exception)
        {
            io?.Dispose();
            resource?.Dispose();
            AppLog.Error("FFmpeg.NestedIO", $"Managed open failed for '{url}'.", exception);
            return AVERROR_EXTERNAL;
        }
    }

    private MediaResource OpenEncrypted(
        string url,
        MediaResourceRange? range,
        AVDictionary** options)
    {
        byte[] key = ReadHex(options, "key");
        byte[] iv = ReadHex(options, "iv");
        if (key.Length != 16 || iv.Length != 16)
            throw new InvalidDataException("An encrypted HLS segment requires a 16-byte key and IV.");

        MediaResource encrypted = resolver.Open(url, range);
        Aes? aes = null;
        CryptoStream? decrypted = null;
        try
        {
            aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = iv;
            decrypted = new CryptoStream(
                encrypted.Stream,
                aes.CreateDecryptor(),
                CryptoStreamMode.Read,
                leaveOpen: true);
            return new MediaResource(decrypted, encrypted.ResolvedUrl, () =>
            {
                decrypted.Dispose();
                aes.Dispose();
                encrypted.Dispose();
            });
        }
        catch
        {
            decrypted?.Dispose();
            aes?.Dispose();
            encrypted.Dispose();
            throw;
        }
    }

    private int Close(AVIOContext* context)
    {
        if (context == null || !children.Remove((nint)context, out ChildResource? child))
            return 0;

        try
        {
            child.Dispose();
            return 0;
        }
        catch (Exception exception)
        {
            AppLog.Error("FFmpeg.NestedIO", "Managed child close failed.", exception);
            return AVERROR_EXTERNAL;
        }
    }

    private static MediaResourceRange? ReadRange(AVDictionary** options)
    {
        long? offset = ReadInt64(options, "offset");
        long? endOffset = ReadInt64(options, "end_offset");
        if (offset is null && endOffset is null)
            return null;

        long start = Math.Max(0, offset ?? 0);
        return endOffset > start
            ? new MediaResourceRange(start, endOffset)
            : new MediaResourceRange(start, null);
    }

    private static long? ReadInt64(AVDictionary** options, string key)
    {
        string? value = ReadString(options, key);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : null;
    }

    private static byte[] ReadHex(AVDictionary** options, string key)
    {
        string? value = ReadString(options, key);
        if (string.IsNullOrEmpty(value))
            throw new InvalidDataException($"The HLS crypto option '{key}' is missing.");

        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"The HLS crypto option '{key}' is not hexadecimal.", exception);
        }
    }

    private static string? ReadString(AVDictionary** options, string key)
    {
        if (options == null || *options == null)
            return null;

        AVDictionaryEntry* entry = av_dict_get(*options, key, null, 0);
        return entry == null ? null : Marshal.PtrToStringUTF8((nint)entry->value);
    }

    private static FfmpegNestedIO? From(void* opaque)
    {
        if (opaque == null)
            return null;

        return GCHandle.FromIntPtr((nint)opaque).Target as FfmpegNestedIO;
    }

    private static FfmpegNestedIO? From(AVFormatContext* context) =>
        context == null ? null : From(context->opaque);

    [MonoPInvokeCallback(typeof(InterruptCallback))]
    private static int IsInterrupted(void* opaque) =>
        From(opaque) is { } owner && Volatile.Read(ref owner.interruptRequested) != 0 ? 1 : 0;

    [MonoPInvokeCallback(typeof(IoOpenCallback))]
    private static int OpenChild(
        AVFormatContext* context,
        AVIOContext** child,
        byte* url,
        int flags,
        AVDictionary** options) =>
        From(context)?.Open(Marshal.PtrToStringUTF8((nint)url) ?? string.Empty, flags, options, child)
            ?? AVERROR_EXTERNAL;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CloseChild(AVFormatContext* context, AVIOContext* child) =>
        From(context)?.Close(child) ?? 0;
}