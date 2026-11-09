using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace LightStudio.LightPlayer.Services;

/// <summary>The side of a top level that hosts a vertical bar.</summary>
public enum DeviceBarEdge
{
    Left,
    Right,
}

/// <summary>
/// Regions of a top level, in its DIPs, that the system reserves or offers to bars, as the iOS head
/// reports them through <see cref="PlatformServices.ObserveDeviceRegions"/>.
/// </summary>
public sealed class DeviceRegions : IEquatable<DeviceRegions>
{
    public static DeviceRegions Empty { get; } = new();

    /// <summary>
    /// The window controls over the top-left corner, inside the safe area: Left is the width they take
    /// from a row along the top, Top the height they take from a column along the left.
    /// </summary>
    public Thickness WindowControls { get; init; }

    /// <summary>
    /// The edge of the band beside the safe area where the system places vertical bars, or null when
    /// bars stay horizontal.
    /// </summary>
    public DeviceBarEdge? VerticalBarEdge { get; init; }

    /// <summary>The band a vertical bar takes on <see cref="VerticalBarEdge"/>, clear of occlusions.</summary>
    public Rect VerticalBar { get; init; }

    /// <summary>
    /// The band along the top where the system lays out a horizontal bar beside the status bar, within
    /// the top safe area inset, or empty where the band lies below the status bar.
    /// </summary>
    public Rect HorizontalBar { get; init; }

    /// <summary>Active areas covered by hardware or system elements, such as cameras and the status bar.</summary>
    public IReadOnlyList<Rect> Occlusions { get; init; } = [];

    /// <summary>Active areas, such as a fold, that divide the top level; controls stay on either side.</summary>
    public IReadOnlyList<Rect> Divisions { get; init; } = [];

    public bool Equals(DeviceRegions? other) =>
        other is not null &&
        WindowControls == other.WindowControls &&
        VerticalBarEdge == other.VerticalBarEdge &&
        VerticalBar == other.VerticalBar &&
        HorizontalBar == other.HorizontalBar &&
        Occlusions.SequenceEqual(other.Occlusions) &&
        Divisions.SequenceEqual(other.Divisions);

    public override bool Equals(object? obj) => Equals(obj as DeviceRegions);

    public override int GetHashCode() =>
        HashCode.Combine(WindowControls, VerticalBarEdge, VerticalBar, HorizontalBar, Occlusions.Count, Divisions.Count);
}
