namespace LightMediaRenderer.Subtitles;

/// <summary>
/// The <c>[Script Info]</c> values the renderer cares about.
/// </summary>
internal sealed class AssScriptInfo
{
    /// <summary>Width of the coordinate system every position and size in the script is expressed in.</summary>
    /// <remarks>384x288 is what VSFilter assumes when the script does not say; scripts written for it
    /// rely on that default, so it must not be changed to the video size.</remarks>
    public int PlayResX { get; set; } = 384;

    public int PlayResY { get; set; } = 288;

    /// <summary>Resolution perspective and blur are measured in; 0 means "ask the host".</summary>
    /// <remarks>
    /// VSFilter worked in video pixels, so <c>\frx</c>, <c>\fry</c> and <c>\blur</c> came out
    /// stronger on a script written for a small <c>PlayRes</c>. Scripts that want to pin that down
    /// say so with <c>LayoutResX</c> / <c>LayoutResY</c>; otherwise the video's own size is used.
    /// </remarks>
    public int LayoutResX { get; set; }

    public int LayoutResY { get; set; }

    public string? Title { get; set; }

    /// <summary>0 and 3 = smart wrap, 1 = wrap at the end of the line, 2 = no wrapping.</summary>
    public int WrapStyle { get; set; }

    /// <summary>When set, border and shadow widths follow the script-to-screen scale.</summary>
    public bool ScaledBorderAndShadow { get; set; } = true;
}
