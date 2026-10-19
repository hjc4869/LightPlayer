namespace LightStudio.MediaLibraryCore.CueIndex
{
    /// <summary>
    /// Represents an INDEX entry in a CUE track.
    /// </summary>
    struct Index
    {
        int m_number;

        int m_minutes;
        int m_seconds;
        int m_frames;

        /// <summary>Gets or sets the index number, clamped to 0-99.</summary>
        public int Number
        {
            get { return m_number; }
            set
            {
                if (value > 99)
                {
                    m_number = 99;
                }
                else if (value < 0)
                {
                    m_number = 0;
                }
                else
                {
                    m_number = value;
                }
            }
        }

        /// <summary>Gets or sets the minute component, clamped to 0-99.</summary>
        public int Minutes
        {
            get { return m_minutes; }
            set
            {
                if (value > 99)
                {
                    m_minutes = 99;
                }
                else if (value < 0)
                {
                    m_minutes = 0;
                }
                else
                {
                    m_minutes = value;
                }
            }
        }

        /// <summary>Gets or sets the second component, clamped to 0-59.</summary>
        public int Seconds
        {
            get { return m_seconds; }
            set
            {
                if (value >= 60)
                {
                    m_seconds = 59;
                }
                else if (value < 0)
                {
                    m_seconds = 0;
                }
                else
                {
                    m_seconds = value;
                }
            }
        }

        /// <summary>Gets or sets the frame component, clamped to 0-74.</summary>
        public int Frames
        {
            get { return m_frames; }
            set
            {
                if (value >= 75)
                {
                    m_frames = 74;
                }
                else if (value < 0)
                {
                    m_frames = 0;
                }
                else
                {
                    m_frames = value;
                }
            }
        }

        public Index(int number, int minutes, int seconds, int frames)
        {
            m_number = number;

            m_minutes = minutes;
            m_seconds = seconds;
            m_frames = frames;
        }
    }
}
