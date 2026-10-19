using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using SkiaSharp;

namespace LightStudio.LightPlayer.Services;

internal static partial class PhotoDecoder
{
    private static VideoFrame? DecodeAnimation(Stream stream, CancellationToken cancellationToken)
    {
        StillImage.AnimationSource? native = StillImage.OpenAnimation(stream, out StillImageNative first, cancellationToken);
        if (native is null)
            return null;
        using (first)
        {
            try
            {
                ApplyAnimationColor(first);
                first.Frame.PhotoAnimation = new AnimationSource(native);
                return first.Frame.AddRef();
            }
            catch { native.Dispose(); throw; }
        }
    }

    private static void ApplyAnimationColor(StillImageNative image)
    {
        if (image.Frame.PhotoColor is not null)
            return;
        using SKColorSpace? space = CreateColorSpace(image.IccProfile, image.ColorPrimaries,
            image.ColorTransfer is 0 or 2 ? 13 : image.ColorTransfer);
        if (space is not null && CreateGpuProfile(space, relativeHdr: false, image.Frame.HasAlpha) is { } profile)
            ApplyGpuProfile(image.Frame, profile, hasGainMap: false);
    }

    private sealed class AnimationSource(StillImage.AnimationSource source) : IPhotoAnimationSource
    {
        public long EncodedBytes => source.EncodedBytes;
        public IPhotoAnimation Open() => new AnimationSession(source.AddRef());
        public void Dispose() => source.Dispose();
    }

    private sealed class AnimationSession(StillImage.AnimationSource source) : IPhotoAnimation
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly CancellationTokenSource lifetime = new();
        private readonly object lifecycle = new();
        private StillImage.AnimationDecoder? reader;
        private PhotoColorProfile? profile;
        private bool colorInitialized;
        private Task? closing;

        public async ValueTask<VideoFrame?> ReadNextAsync(CancellationToken cancellationToken)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            await gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    reader ??= source.CreateDecoder();
                    using StillImageNative? image = reader.Read(cancellation.Token);
                    if (image is null)
                        return null;
                    if (!colorInitialized)
                    {
                        ApplyAnimationColor(image);
                        profile = image.Frame.PhotoColor;
                        colorInitialized = true;
                    }
                    else if (profile is not null)
                        ApplyGpuProfile(image.Frame, profile, hasGainMap: false);
                    cancellation.Token.ThrowIfCancellationRequested();
                    return image.Frame.AddRef();
                }, cancellation.Token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        public ValueTask DisposeAsync()
        {
            lock (lifecycle)
                return new ValueTask(closing ??= CloseAsync());
        }

        private async Task CloseAsync()
        {
            lifetime.Cancel();
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    try { reader?.Dispose(); }
                    finally { source.Dispose(); }
                }).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
    }

}