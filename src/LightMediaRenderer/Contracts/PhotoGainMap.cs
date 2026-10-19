using System;
using System.Numerics;
using System.Threading;

namespace LightMediaRenderer.Contracts;

/// <summary>Encoded RGB gain samples and the metadata used to adapt an SDR photo to display headroom.</summary>
public sealed class PhotoGainMap
{
    private int references = 1;

    public PhotoGainMap(int width, int height, byte[] pixels,
        Vector3 minimum, Vector3 maximum, Vector3 gamma, Vector3 offsetSdr, Vector3 offsetHdr,
        float capacityMin, float capacityMax, float[] toBase, float[] toBt2020)
        : this(width, height, pixels, minimum, maximum, gamma, offsetSdr, offsetHdr,
            capacityMin, capacityMax, toBase, toBt2020, 8)
    {
    }

    public PhotoGainMap(int width, int height, byte[] pixels,
        Vector3 minimum, Vector3 maximum, Vector3 gamma, Vector3 offsetSdr, Vector3 offsetHdr,
        float capacityMin, float capacityMax, float[] toBase, float[] toBt2020, int bitsPerComponent)
        : this(width, height, pixels, null, minimum, maximum, gamma, offsetSdr, offsetHdr,
            capacityMin, capacityMax, toBase, toBt2020, bitsPerComponent)
    {
    }

    public PhotoGainMap(VideoFrame samples,
        Vector3 minimum, Vector3 maximum, Vector3 gamma, Vector3 offsetSdr, Vector3 offsetHdr,
        float capacityMin, float capacityMax, float[] toBase, float[] toBt2020)
        : this(samples.Width, samples.Height, [], samples, minimum, maximum, gamma, offsetSdr, offsetHdr,
            capacityMin, capacityMax, toBase, toBt2020, 16)
    {
    }

    private PhotoGainMap(int width, int height, byte[] pixels, VideoFrame? samples,
        Vector3 minimum, Vector3 maximum, Vector3 gamma, Vector3 offsetSdr, Vector3 offsetHdr,
        float capacityMin, float capacityMax, float[] toBase, float[] toBt2020, int bitsPerComponent)
    {
        if (bitsPerComponent is not (8 or 16) || width <= 0 || height <= 0 ||
            samples is null && pixels.Length != checked((long)width * height * 4 * (bitsPerComponent / 8)) ||
            toBase.Length != 9 || toBt2020.Length != 9 || !float.IsFinite(capacityMin) ||
            !float.IsFinite(capacityMax) || capacityMin < 0 || capacityMax <= capacityMin)
            throw new ArgumentException("Invalid photo gain-map geometry or capacity.");

        Width = width;
        Height = height;
        Pixels = pixels;
        Samples = samples?.AddRef();
        BitsPerComponent = bitsPerComponent;
        Minimum = minimum;
        Maximum = maximum;
        Gamma = gamma;
        OffsetSdr = offsetSdr;
        OffsetHdr = offsetHdr;
        CapacityMin = capacityMin;
        CapacityMax = capacityMax;
        ToBase = toBase;
        ToBt2020 = toBt2020;
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Pixels { get; }
    public VideoFrame? Samples { get; }
    public int SizeInBytes => Samples?.SizeInBytes ?? Pixels.Length;
    public int BitsPerComponent { get; }
    public bool IsApple { get; init; }
    public bool BaseIsHdr { get; init; }
    public Vector3 Minimum { get; }
    public Vector3 Maximum { get; }
    public Vector3 Gamma { get; }
    public Vector3 OffsetSdr { get; }
    public Vector3 OffsetHdr { get; }
    public float CapacityMin { get; }
    public float CapacityMax { get; }
    public ReadOnlyMemory<float> ToBase { get; }
    public ReadOnlyMemory<float> ToBt2020 { get; }

    public PhotoGainMap AddRef()
    {
        Interlocked.Increment(ref references);
        return this;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref references) == 0)
            Samples?.Release();
    }

    public float Weight(double headroom) => double.IsFinite(headroom) && headroom > 1
        ? (float)Math.Clamp((Math.Log2(headroom) - CapacityMin) / (CapacityMax - CapacityMin), 0, 1)
        : 0;
}