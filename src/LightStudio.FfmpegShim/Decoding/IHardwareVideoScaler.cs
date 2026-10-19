using System;
using System.Collections.Generic;
using LightMediaRenderer.Contracts;
using LightStudio.NativeBindings.Types;

namespace LightStudio.FfmpegShim.Decoding;

/// <summary>Resizes hardware frames while preserving their surface kind and color metadata.</summary>
public unsafe interface IHardwareVideoScaler : IDisposable
{
    bool IsAvailable { get; }
    string Status { get; }
    (int Width, int Height) ActiveSize { get; }
    bool CoversOutput { get; }

    /// <summary>The returned frame belongs to the scaler and remains valid until its next call.</summary>
    bool TryScale(AVFrame* input, VideoOutputRequest request, out AVFrame* result);
}

/// <summary>Creates a scaler whose borrowed device context outlives it.</summary>
public unsafe delegate IHardwareVideoScaler CreateHardwareVideoScaler(
    long id, AVBufferRef* device, AVRational timeBase, int surfaceBudget);

/// <summary>Application-head registrations for platform-native video processing.</summary>
public static class HardwareVideoScalerFactory
{
    private static readonly Dictionary<VideoSurfaceKind, CreateHardwareVideoScaler> factories = [];

    public static void Register(VideoSurfaceKind kind, CreateHardwareVideoScaler factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        factories[kind] = factory;
    }

    internal static unsafe IHardwareVideoScaler Create(long id, VideoSurfaceKind kind,
        AVBufferRef* device, AVRational timeBase, int surfaceBudget) =>
        factories.TryGetValue(kind, out CreateHardwareVideoScaler factory)
            ? factory(id, device, timeBase, surfaceBudget)
            : new HardwareVideoScaler(id, kind, device, timeBase, surfaceBudget);
}