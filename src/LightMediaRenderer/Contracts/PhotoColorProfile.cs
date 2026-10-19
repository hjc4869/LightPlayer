using System;
using SkiaSharp;

namespace LightMediaRenderer.Contracts;

/// <summary>A parametric sample-to-linear transfer and a row-major linear RGB-to-BT.2020 matrix.</summary>
public sealed class PhotoColorProfile
{
    public PhotoColorProfile(float[] transfer, float[] toBt2020, int transferCode = 0,
        float linearScale = 1, bool hasAlpha = false)
    {
        if (transfer.Length != 7 || toBt2020.Length != 9 || !float.IsFinite(linearScale) || linearScale <= 0)
            throw new ArgumentException("Invalid photo color transform.");
        foreach (float value in transfer)
            if (!float.IsFinite(value))
                throw new ArgumentException("Invalid photo transfer function.");
        foreach (float value in toBt2020)
            if (!float.IsFinite(value))
                throw new ArgumentException("Invalid photo color matrix.");

        Transfer = transfer;
        ToBt2020 = toBt2020;
        TransferCode = transferCode;
        LinearScale = linearScale;
        HasAlpha = hasAlpha;
    }

    public ReadOnlyMemory<float> Transfer { get; }
    public ReadOnlyMemory<float> ToBt2020 { get; }
    public int TransferCode { get; }
    public float LinearScale { get; }
    public bool HasAlpha { get; }

    public static SKColorSpace? CreateColorSpace(byte[] profile, int primaries, int transfer)
    {
        if (profile.Length != 0)
            return SKColorSpace.CreateIcc(profile);

        SKColorSpaceXyz gamut;
        if (primaries == 9)
            gamut = SKColorSpaceXyz.Rec2020;
        else
        {
            SKColorSpacePrimaries coordinates = primaries switch
            {
                12 => new(0.68f, 0.32f, 0.265f, 0.69f, 0.15f, 0.06f, 0.3127f, 0.3290f),
                11 => new(0.68f, 0.32f, 0.265f, 0.69f, 0.15f, 0.06f, 0.314f, 0.351f),
                5 => new(0.64f, 0.33f, 0.29f, 0.60f, 0.15f, 0.06f, 0.3127f, 0.3290f),
                6 or 7 => new(0.63f, 0.34f, 0.31f, 0.595f, 0.155f, 0.07f, 0.3127f, 0.3290f),
                _ => new(0.64f, 0.33f, 0.30f, 0.60f, 0.15f, 0.06f, 0.3127f, 0.3290f),
            };
            if (!coordinates.ToColorSpaceXyz(out gamut))
                return null;
        }

        SKColorSpaceTransferFn curve = transfer switch
        {
            8 => SKColorSpaceTransferFn.Linear,
            16 => SKColorSpaceTransferFn.Pq,
            18 => SKColorSpaceTransferFn.Hlg,
            4 => new(2.2f, 1, 0, 0, 0, 0, 0),
            5 => new(2.8f, 1, 0, 0, 0, 0, 0),
            1 or 6 or 14 or 15 => new(1 / 0.45f, 1 / 1.099f, 0.099f / 1.099f, 1 / 4.5f, 0.081f, 0, 0),
            _ => SKColorSpaceTransferFn.Srgb,
        };
        return SKColorSpace.CreateRgb(curve, gamut);
    }

    public static PhotoColorProfile? Create(SKColorSpace space, bool relativeHdr = false, bool hasAlpha = false)
    {
        if (!space.GetNumericalTransferFunction(out SKColorSpaceTransferFn curve) ||
            !space.ToColorSpaceXyz(out SKColorSpaceXyz gamut))
            return null;
        int transfer = curve.G == SKColorSpaceTransferFn.Pq.G ? 16 : curve.G == SKColorSpaceTransferFn.Hlg.G ? 18 : 0;
        return new PhotoColorProfile([curve.G, curve.A, curve.B, curve.C, curve.D, curve.E, curve.F],
            SKColorSpaceXyz.Concat(SKColorSpaceXyz.Rec2020.Invert(), gamut).Values,
            transfer, relativeHdr && transfer is 16 or 18 ? 1f / 203 : 1, hasAlpha);
    }
}