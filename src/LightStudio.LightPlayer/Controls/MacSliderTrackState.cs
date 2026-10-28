using System.Linq;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;

namespace LightStudio.LightPlayer.Controls;

/// <summary>The end of its range that a slider value rests at, if any.</summary>
public enum MacSliderRangeEnd
{
    None,
    Minimum,
    Maximum,
}

/// <summary>
/// Range state for the AppKit slider track. Beneath a pressed thumb, the value fill is empty at the
/// minimum and fills the track at the maximum, rather than reaching the thumb center.
/// </summary>
public static class MacSliderTrackState
{
    public static readonly AttachedProperty<MacSliderRangeEnd> RangeEndProperty =
        AvaloniaProperty.RegisterAttached<Track, MacSliderRangeEnd>("RangeEnd", typeof(MacSliderTrackState));

    /// <summary>Converts a value, minimum, and maximum to the end of the range the value rests at.</summary>
    public static readonly IMultiValueConverter RangeEndConverter =
        new FuncMultiValueConverter<double, MacSliderRangeEnd>(values =>
        {
            var range = values.ToArray();
            if (range.Length != 3)
            {
                return MacSliderRangeEnd.None;
            }

            // Pointer input computes the far end as minimum plus range, which can round short of the maximum.
            var tolerance = (range[2] - range[1]) * 1e-9;
            if (range[0] <= range[1] + tolerance)
            {
                return MacSliderRangeEnd.Minimum;
            }

            return range[0] >= range[2] - tolerance ? MacSliderRangeEnd.Maximum : MacSliderRangeEnd.None;
        });

    public static MacSliderRangeEnd GetRangeEnd(Track track) => track.GetValue(RangeEndProperty);

    public static void SetRangeEnd(Track track, MacSliderRangeEnd value) => track.SetValue(RangeEndProperty, value);
}
