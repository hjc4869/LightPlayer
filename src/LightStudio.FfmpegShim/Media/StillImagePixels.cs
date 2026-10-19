namespace LightStudio.FfmpegShim.Media;

public sealed class StillImagePixels
{
    public int Width { get; init; }
    public int Height { get; init; }
    public ushort[] Pixels { get; init; } = [];
    public byte[] IccProfile { get; init; } = [];
    public int ColorPrimaries { get; init; }
    public int ColorTransfer { get; init; }
    public int Rotation { get; init; }
    public bool MirrorHorizontally { get; init; }
    public double PixelAspectRatio { get; init; } = 1;
}