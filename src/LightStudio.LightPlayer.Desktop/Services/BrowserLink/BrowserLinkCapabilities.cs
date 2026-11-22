using System.Collections.Generic;
using System.Linq;
using LightMediaRenderer.Avalonia.Pipeline;

namespace LightStudio.LightPlayer.Desktop.Services.BrowserLink;

/// <param name="Codec">Codec family as Media Source names it: <c>avc1</c>, <c>hvc1</c>, <c>av01</c>, <c>vp09</c> or <c>vp08</c>.</param>
internal sealed record BrowserLinkVideoCodec(string Codec, int MaxWidth, int MaxHeight, IReadOnlyList<int> BitDepths, bool Hardware);

/// <summary>What the application tells the extension it can decode, in the <c>ready</c> message.</summary>
internal sealed record BrowserLinkCapabilities(
    long BudgetBytes,
    IReadOnlyList<string> Containers,
    IReadOnlyList<BrowserLinkVideoCodec> Video,
    IReadOnlyList<string> Audio,
    bool Hdr)
{
    /// <summary>
    /// What the append-driven source accepts: its byte stream formats and the codec families the
    /// FFmpeg build decodes, with hardware decoding where a device opens. High dynamic range video is
    /// reported as playable on every output: the renderer shows it as such where the output can and
    /// tone-maps it otherwise.
    /// </summary>
    public static BrowserLinkCapabilities Detect() => new(
        AppendMediaSourceProvider.DefaultBudgetBytes,
        AppendSourceCapabilities.Containers,
        AppendSourceCapabilities.VideoCodecs
            .Select(codec => new BrowserLinkVideoCodec(codec.Codec, codec.MaxWidth, codec.MaxHeight, codec.BitDepths, codec.Hardware))
            .ToArray(),
        AppendSourceCapabilities.AudioCodecs.Select(codec => codec.Codec).ToArray(),
        Hdr: true);

    public BrowserLinkCapabilities WithoutHardware() =>
        this with { Video = Video.Select(codec => codec with { Hardware = false }).ToArray() };
}
