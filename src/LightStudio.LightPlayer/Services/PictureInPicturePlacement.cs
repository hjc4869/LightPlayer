namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Where the detached picture-in-picture window last was, in device-independent units. Kept apart
/// from <see cref="WindowPlacement"/> because this one is placed as well as sized.
/// </summary>
public sealed record PictureInPicturePlacement(double X, double Y, double Width, double Height);
