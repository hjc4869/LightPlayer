using System;

namespace LightMediaRenderer.Contracts;

/// <summary>A complete bitmap subtitle stream on its native composition canvas.</summary>
public sealed record BitmapSubtitleDocument(
    int CanvasWidth,
    int CanvasHeight,
    BitmapSubtitleCue[] Cues)
{
    /// <summary>Returns the composition visible at <paramref name="time"/>, or null between cues.</summary>
    public BitmapSubtitleCue? CueAt(TimeSpan time)
    {
        double seconds = time.TotalSeconds;
        int low = 0;
        int high = Cues.Length - 1;
        int candidate = -1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (Cues[middle].StartSeconds <= seconds)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (candidate < 0)
            return null;

        BitmapSubtitleCue cue = Cues[candidate];
        return seconds < cue.EndSeconds ? cue : null;
    }
}

/// <summary>One complete bitmap composition and the interval for which it is visible.</summary>
public sealed record BitmapSubtitleCue(
    double StartSeconds,
    double EndSeconds,
    BitmapSubtitleRectangle[] Rectangles);

/// <summary>One positioned, indexed-color image within a bitmap subtitle composition.</summary>
public sealed record BitmapSubtitleRectangle(
    int X,
    int Y,
    int Width,
    int Height,
    byte[] Pixels,
    uint[] Palette);