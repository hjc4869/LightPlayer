using LightMediaRenderer.Subtitles;
namespace LightMediaRenderer.Contracts;

/// <summary>How subtitle colors are placed into an extended-linear video surface.</summary>
public enum SubtitleBrightnessMode
{
    /// <summary>Keep ASS colors at ordinary SDR surface values.</summary>
    Sdr,

    /// <summary>Match subtitle white to the video peak relative to the surface's diffuse white.</summary>
    FollowVideo,
}