namespace LightMediaRenderer.Subtitles;

/// <summary>
/// One row of the <c>[V4+ Styles]</c> section.
/// </summary>
internal sealed class AssStyle
{
    public string Name { get; set; } = "Default";

    public string FontName { get; set; } = "Arial";

    public double FontSize { get; set; } = 40;

    public AssColor PrimaryColour { get; set; } = AssColor.White;

    public AssColor SecondaryColour { get; set; } = new(255, 0, 0);

    public AssColor OutlineColour { get; set; } = AssColor.Black;

    public AssColor BackColour { get; set; } = AssColor.Black;

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public bool Underline { get; set; }

    public bool StrikeOut { get; set; }

    /// <summary>Horizontal scale in percent.</summary>
    public double ScaleX { get; set; } = 100;

    /// <summary>Vertical scale in percent.</summary>
    public double ScaleY { get; set; } = 100;

    /// <summary>Extra advance inserted after every glyph, in script pixels.</summary>
    public double Spacing { get; set; }

    /// <summary>Rotation around Z in degrees, counter-clockwise.</summary>
    public double Angle { get; set; }

    /// <summary>1 = outline and drop shadow, 3 = opaque box.</summary>
    public int BorderStyle { get; set; } = 1;

    public double Outline { get; set; } = 2;

    public double Shadow { get; set; } = 2;

    /// <summary>Numpad-style alignment, 1-9.</summary>
    public int Alignment { get; set; } = 2;

    public int MarginL { get; set; } = 10;

    public int MarginR { get; set; } = 10;

    public int MarginV { get; set; } = 10;

    /// <summary>
    /// GDI charset the script was authored against; 128 is Shift-JIS, 134 GB2312, 136 Big5.
    /// </summary>
    /// <remarks>
    /// Only used to guess which regional Han forms the line wants, and only when nothing more
    /// trustworthy is available: <c>0</c> and <c>1</c> mean nothing in particular and most scripts
    /// leave every style on one of them whatever language they are in.
    /// </remarks>
    public int Encoding { get; set; } = 1;

    public AssStyle Clone() => (AssStyle)MemberwiseClone();
}
