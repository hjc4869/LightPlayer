using SkiaSharp;

namespace LightMediaRenderer.Contracts;

/// <summary>Supplies a frame's planes as Skia images.</summary>
public interface IVideoPlaneSource
{
    /// <summary>Whether returned images remain owned by the source.</summary>
    bool ImagesAreBorrowed => false;

    /// <summary>Whether 16-bit planes are represented as pairs of bytes.</summary>
    bool UseBytePairPlanes { get; }

    SKImage? CreatePlane(int index, SKImageInfo info);

    SKImage? CreatePlane(int index, SKImageInfo info, GRContext? context) => CreatePlane(index, info);
}