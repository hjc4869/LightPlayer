namespace LightStudio.LightPlayer.Services;

/// <summary>
/// The persisted desktop main-window placement: either maximized, or a windowed size.
/// <see cref="Width"/>/<see cref="Height"/> are the last windowed (Normal) client size in
/// device-independent units and are retained even while maximized so the window can round-trip.
/// </summary>
public sealed record WindowPlacement(bool Maximized, double Width, double Height);
