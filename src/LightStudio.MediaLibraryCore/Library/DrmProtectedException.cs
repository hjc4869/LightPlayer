using System;

namespace LightStudio.MediaLibraryCore.Library
{
    /// <summary>Represents a DRM-protected media file.</summary>
    class DrmProtectedException : Exception
    {
        public DrmProtectedException() : base(Strings.Resources.DrmExceptionMessage)
        {
        }
    }
}
