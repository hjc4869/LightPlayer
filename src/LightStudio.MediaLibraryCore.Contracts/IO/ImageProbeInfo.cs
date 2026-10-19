using System;

namespace LightStudio.MediaLibraryCore.IO;

public sealed record ImageProbeInfo(int Width, int Height, DateTimeOffset? DateTaken,
    double? Latitude = null, double? Longitude = null);
