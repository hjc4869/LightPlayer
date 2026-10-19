namespace LightMediaRenderer.Contracts;

public enum VideoOutputTransferFunction
{
    Unknown,
    Srgb,
    Power,
    Bt1886,
}

public enum VideoToneMappingMode
{
    Unknown,
    Client,
    Platform,
}

public enum VideoLuminanceBasis
{
    Unknown,
    DisplayReported,
    Nominal,
}

public readonly record struct VideoLuminance(double? MinimumNits, double? MaximumNits);

/// <summary>Color and luminance metadata supplied by the owner of the output surface.</summary>
public readonly record struct VideoOutputColorVolume(
    VideoOutputTransferFunction Transfer,
    double TransferExponent,
    double? ReferenceWhiteNits,
    double? SurfaceNitsPerUnit,
    VideoLuminance? TargetLuminance)
{
    public double? ReferenceWhiteScale { get; init; }

    public double? HeadroomRatio { get; init; }

    public double? MaximumHeadroomRatio { get; init; }

    public VideoToneMappingMode ToneMapping { get; init; }

    public VideoLuminanceBasis LuminanceBasis { get; init; }
}