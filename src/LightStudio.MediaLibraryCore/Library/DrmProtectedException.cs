using System;

namespace LightStudio.MediaLibraryCore.Library
{
    /// <summary>
    /// Exception indicates that file is DRM-protected.
    /// </summary>
    class DrmProtectedException : Exception
    {
        /// <summary>
        /// Initializes new instance of <see cref="DrmProtectedException"/>.
        /// </summary>
        public DrmProtectedException() : base(Strings.Resources.DrmExceptionMessage)
        {
            // Do nothing
        }
    }
}
