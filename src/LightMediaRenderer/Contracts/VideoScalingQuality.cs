namespace LightMediaRenderer.Contracts;

/// <summary>
/// Filter the renderer uses when the decoded picture is resized to fit the control. Only matters
/// when the control is not showing the video at its native pixel size.
/// </summary>
public enum VideoScalingQuality
{
    /// <summary>Nearest neighbor: blocky, cheapest, and the honest choice for pixel art.</summary>
    NearestNeighbour,

    /// <summary>Bilinear. The default; good enough for the small scale factors of a resized window.</summary>
    Bilinear,

    /// <summary>
    /// Bilinear with a full mipmap chain, which removes the shimmering of a heavy downscale. The
    /// chain is rebuilt for every frame, so this is by far the most expensive option.
    /// </summary>
    Trilinear,

    /// <summary>Mitchell-Netravali cubic: softer than bilinear with very little ringing.</summary>
    Mitchell,

    /// <summary>Catmull-Rom cubic: the sharpest upscale, at the cost of mild ringing on edges.</summary>
    CatmullRom,

    /// <summary>
    /// Lanczos over three lobes each side: the closest a separable filter gets to the detail the
    /// samples still carry, for thirty-six taps a pixel and frank ringing on hard edges.
    /// </summary>
    Lanczos3,

    /// <summary>
    /// Jinc windowed by jinc over three lobes, weighted by distance from the sample rather than per
    /// axis, so a diagonal edge is treated no worse than an upright one. The most expensive option.
    /// </summary>
    Jinc,
}
