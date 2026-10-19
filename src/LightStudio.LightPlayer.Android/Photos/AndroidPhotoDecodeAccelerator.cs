#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;
using SkiaSharp;

namespace LightStudio.LightPlayer.Android.Photos;

internal sealed class AndroidPhotoDecodeAccelerator(Context context) : IPhotoDecodeAccelerator
{
    private const long MaximumEncodedBytes = 128L * 1024 * 1024;
    private static readonly object budgetSync = new();
    private static long reservedBytes;

    public VideoFrame? TryDecode(Stream stream, Func<Stream, CancellationToken, VideoFrame?> softwareFallback,
        CancellationToken cancellationToken)
    {
        if (!stream.CanSeek || AppContext.TryGetSwitch("LightStudio.FfmpegShim.DisablePhotoHardwareDecoding", out bool disabled) && disabled)
            return null;
        long origin = stream.Position;
        long length = stream.Length - origin;
        if (length is <= 0 or > MaximumEncodedBytes)
            return null;
        long started = Stopwatch.GetTimestamp();
        Bitmap? bitmap = null;
        Bitmap? gainPixels = null;
        Reservation? reservation = null;
        AndroidPhotoStorage? storage = null;
        VideoFrame? frame = null;
        try
        {
            using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true, InScaled = false };
            BitmapFactory.DecodeStream(stream, null, bounds)?.Dispose();
            stream.Position = origin;
            if (bounds.OutMimeType is not ("image/jpeg" or "image/heif" or "image/heic" or "image/avif") ||
                bounds.OutWidth <= 0 || bounds.OutHeight <= 0 ||
                Math.Max(bounds.OutWidth, bounds.OutHeight) > AndroidPhotoSurface.MaximumDimension)
                return null;
            int bytesPerPixel = bounds.OutConfig?.Equals(Bitmap.Config.RgbaF16) == true ? 8 : 4;
            long pixels = checked((long)bounds.OutWidth * bounds.OutHeight * bytesPerPixel);
            reservation = TryReserve(checked(pixels + pixels / 4 + length));
            if (reservation is null)
            {
                AppLog.Information("Photo.Decoding", $"Platform photo memory budget declined {bounds.OutWidth}x{bounds.OutHeight}.");
                return null;
            }

            byte[] encoded = new byte[(int)length];
            int read = 0;
            while (read < encoded.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = stream.Read(encoded, read, Math.Min(64 * 1024, encoded.Length - read));
                if (count == 0)
                    throw new EndOfStreamException("The photo ended before its declared length.");
                read += count;
            }
            using var options = new BitmapFactory.Options { InPreferredConfig = Bitmap.Config.Hardware, InScaled = false };
            cancellationToken.ThrowIfCancellationRequested();
            bitmap = BitmapFactory.DecodeByteArray(encoded, 0, encoded.Length, options);
            cancellationToken.ThrowIfCancellationRequested();
            if (bitmap is null || bitmap.HasAlpha || bitmap.GetConfig()?.Equals(Bitmap.Config.Hardware) != true ||
                bitmap.Width != bounds.OutWidth || bitmap.Height != bounds.OutHeight)
                return null;

            using Gainmap? gain = bitmap.HasGainmap ? bitmap.Gainmap : null;
            gainPixels = gain?.GainmapContents;
            if (gainPixels is not null && gainPixels.GetConfig()?.Equals(Bitmap.Config.Hardware) != true)
                return null;
            long actualBytes = bitmap.AllocationByteCount + (long)(gainPixels?.AllocationByteCount ?? 0);
            if (actualBytes + encoded.Length > reservation.Bytes)
                return null;
            using ColorSpace? baseColor = bitmap.ColorSpace;
            PhotoColorProfile? profile = CreateProfile(baseColor, out SKColorSpaceXyz gamut);
            if (profile is null)
                return null;
            storage = new AndroidPhotoStorage(bitmap, gainPixels, reservation);
            bitmap = null;
            gainPixels = null;
            reservation = null;
            frame = CreateFrame(storage, storage.Bitmap, profile);
            frame.DownloadPhotoSurface = () =>
            {
                using var input = new MemoryStream(encoded, writable: false);
                return softwareFallback(input, CancellationToken.None);
            };
            if (bounds.OutMimeType == "image/jpeg" && ExifOrientation.TryRead(encoded, out int rotation, out bool mirror))
            {
                frame.Rotation = rotation;
                frame.MirrorHorizontally = mirror;
            }
            using (var input = new MemoryStream(encoded, writable: false))
            {
                if (bounds.OutMimeType == "image/jpeg")
                    frame.PixelAspectRatio = StillImage.ReadPixelAspectRatio(input);
                else
                {
                    var geometry = StillImage.ReadGeometry(input);
                    if (geometry.Width != frame.Width || geometry.Height != frame.Height)
                        throw new NotSupportedException("The platform photo did not preserve the primary image geometry.");
                    frame.Rotation = geometry.Rotation;
                    frame.MirrorHorizontally = geometry.MirrorHorizontally;
                    frame.PixelAspectRatio = geometry.PixelAspectRatio;
                }
            }

            if (gain is not null && storage.GainBitmap is { } gainBitmap)
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(36))
                    throw new NotSupportedException("The platform cannot expose the complete photo gain-map metadata.");
                using ColorSpace? alternate = gain.AlternativeImagePrimaries;
                if (alternate is not null && !TryReadGamut(alternate, out gamut))
                    throw new NotSupportedException("The platform gain-map gamut is not supported.");
                var samples = VideoFrame.CreateHosted(gainBitmap.Width, gainBitmap.Height, VideoPixelFormat.Bgra32);
                try
                {
                    samples.PhotoSurface = new AndroidPhotoSurface(storage, gainBitmap);
                    frame.GainMap = new PhotoGainMap(samples,
                        ReadVector(gain.GetRatioMin(), logarithm: true), ReadVector(gain.GetRatioMax(), logarithm: true),
                        ReadVector(gain.GetGamma()), ReadVector(gain.GetEpsilonSdr()), ReadVector(gain.GetEpsilonHdr()),
                        MathF.Log2(gain.MinDisplayRatioForHdrTransition), MathF.Log2(gain.DisplayRatioForFullHdr),
                        SKColorSpaceXyz.Concat(gamut.Invert(), SKColorSpaceXyz.Rec2020).Values,
                        SKColorSpaceXyz.Concat(SKColorSpaceXyz.Rec2020.Invert(), gamut).Values)
                    {
                        BaseIsHdr = (int)gain.GainmapDirection == 1,
                    };
                    frame.ColorTransfer = VideoColorTransfer.Linear;
                }
                finally { samples.Release(); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            AppLog.Information("Photo.Decoding", $"Platform decoded {frame.Width}x{frame.Height} {bounds.OutMimeType}: " +
                $"bytes={actualBytes}, gainMap={frame.GainMap is not null}, elapsed={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms.");
            VideoFrame result = frame;
            frame = null;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            AppLog.Warning("Photo.Decoding", "Platform photo decoding failed; using software.", exception);
            return null;
        }
        finally
        {
            frame?.Release();
            storage?.Release();
            gainPixels?.Recycle();
            gainPixels?.Dispose();
            bitmap?.Recycle();
            bitmap?.Dispose();
            reservation?.Dispose();
            stream.Position = origin;
        }
    }

    private Reservation? TryReserve(long bytes)
    {
        using var manager = context.GetSystemService(Context.ActivityService) as ActivityManager;
        using var memory = new ActivityManager.MemoryInfo();
        manager?.GetMemoryInfo(memory);
        long budget = Math.Min(1536L * 1024 * 1024, memory.TotalMem / 8);
        if (memory.LowMemory || bytes > memory.AvailMem / 2 || bytes > budget)
            return null;
        lock (budgetSync)
        {
            if (reservedBytes > budget - bytes)
                return null;
            reservedBytes += bytes;
            return new Reservation(bytes);
        }
    }

    private static VideoFrame CreateFrame(AndroidPhotoStorage owner, Bitmap bitmap, PhotoColorProfile profile)
    {
        VideoFrame frame = VideoFrame.CreateHosted(bitmap.Width, bitmap.Height, VideoPixelFormat.Bgra32);
        try
        {
            var surface = new AndroidPhotoSurface(owner, bitmap);
            frame.PhotoSurface = surface;
            if (profile.TransferCode is 16 or 18 && surface.BitDepth < 10)
                throw new NotSupportedException("The platform photo surface did not preserve HDR precision.");
            frame.PhotoColor = profile;
            frame.ColorRange = VideoColorRange.Full;
            frame.ColorPrimaries = VideoColorPrimaries.Bt2020;
            frame.ColorTransfer = profile.TransferCode switch
            {
                16 => VideoColorTransfer.Pq,
                18 => VideoColorTransfer.Hlg,
                _ => VideoColorTransfer.Linear,
            };
            frame.FieldOrder = VideoFieldOrder.Progressive;
            return frame;
        }
        catch
        {
            frame.Release();
            throw;
        }
    }

    private static PhotoColorProfile? CreateProfile(ColorSpace? color, out SKColorSpaceXyz gamut)
    {
        if (!TryReadGamut(color, out gamut) || color is not ColorSpace.Rgb rgb)
            return null;
        SKColorSpaceTransferFn curve;
        int transfer = 0;
        using ColorSpace.Rgb.TransferParameters? parameters = rgb.GetTransferParameters();
        if (parameters is not null)
            curve = new SKColorSpaceTransferFn((float)parameters.G, (float)parameters.A, (float)parameters.B,
                (float)parameters.C, (float)parameters.D, (float)parameters.E, (float)parameters.F);
        else if (color.Equals(ColorSpace.Get(ColorSpace.Named.Bt2020Pq!)))
        {
            curve = SKColorSpaceTransferFn.Pq;
            transfer = 16;
        }
        else if (color.Equals(ColorSpace.Get(ColorSpace.Named.Bt2020Hlg!)))
        {
            curve = SKColorSpaceTransferFn.Hlg;
            transfer = 18;
        }
        else
            return null;
        return new PhotoColorProfile([curve.G, curve.A, curve.B, curve.C, curve.D, curve.E, curve.F],
            SKColorSpaceXyz.Concat(SKColorSpaceXyz.Rec2020.Invert(), gamut).Values, transfer);
    }

    private static bool TryReadGamut(ColorSpace? color, out SKColorSpaceXyz gamut)
    {
        gamut = default;
        if (color is not ColorSpace.Rgb)
            return false;
        ColorSpace adapted = ColorSpace.Adapt(color, [0.34567f, 0.35850f]);
        try
        {
            if (adapted is not ColorSpace.Rgb rgb || rgb.GetTransform() is not { Length: 9 } matrix)
                return false;
            gamut = new SKColorSpaceXyz(matrix[0], matrix[3], matrix[6], matrix[1], matrix[4], matrix[7], matrix[2], matrix[5], matrix[8]);
            return true;
        }
        finally
        {
            if (!ReferenceEquals(adapted, color))
                adapted.Dispose();
        }
    }

    private static Vector3 ReadVector(float[]? values, bool logarithm = false)
    {
        if (values is not { Length: 3 })
            throw new InvalidDataException("The platform gain map has invalid channel metadata.");
        var result = new Vector3(values[0], values[1], values[2]);
        return logarithm ? new Vector3(MathF.Log2(result.X), MathF.Log2(result.Y), MathF.Log2(result.Z)) : result;
    }

    private sealed class Reservation(long bytes) : IDisposable
    {
        private long retained = bytes;
        public long Bytes { get; } = bytes;
        public void Dispose()
        {
            long released = Interlocked.Exchange(ref retained, 0);
            lock (budgetSync)
                reservedBytes -= released;
        }
    }
}

internal sealed class AndroidPhotoStorage(Bitmap bitmap, Bitmap? gainBitmap, IDisposable reservation)
{
    private int references = 1;
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Bitmap Bitmap { get; } = bitmap;
    public Bitmap? GainBitmap { get; } = gainBitmap;
    public Task WhenReleased => released.Task;
    public void AddRef() => Interlocked.Increment(ref references);
    public void Release()
    {
        if (Interlocked.Decrement(ref references) != 0)
            return;
        try
        {
            GainBitmap?.Recycle();
            GainBitmap?.Dispose();
            Bitmap.Recycle();
            Bitmap.Dispose();
        }
        finally
        {
            reservation.Dispose();
            released.TrySetResult();
        }
    }
}
