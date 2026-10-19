using System;
using System.Text;

namespace LightStudio.MediaLibraryCore.CueIndex
{
    /// <summary>Represents one data or audio track in a CUE sheet.</summary>
    struct Track
    {
        #region Private Variables
        private string[] m_Comments;
        private AudioFile m_DataFile;
        private string[] m_Garbage;
        private Index[] m_Indices;
        private string m_ISRC;

        private string m_Performer;
        private Index m_PostGap;
        private Index m_PreGap;
        private string m_Songwriter;
        private string m_Title;
        private Flags[] m_TrackFlags;
        private DataType m_TrackDataType;
        private int m_TrackNumber;
        #endregion

        #region Properties

        /// <summary>Gets or sets an index by its zero-based position.</summary>
        public Index this[int indexnumber]
        {
            get
            {
                return m_Indices[indexnumber];
            }
            set
            {
                m_Indices[indexnumber] = value;
            }
        }


        public string[] Comments
        {
            get { return m_Comments; }
            set { m_Comments = value; }
        }


        public AudioFile DataFile
        {
            get { return m_DataFile; }
            set { m_DataFile = value; }
        }

        /// <summary>Gets or sets unrecognized lines associated with this track.</summary>
        public string[] Garbage
        {
            get { return m_Garbage; }
            set { m_Garbage = value; }
        }

        public Index[] Indices
        {
            get { return m_Indices; }
            set { m_Indices = value; }
        }

        public string ISRC
        {
            get { return m_ISRC; }
            set { m_ISRC = value; }
        }

        public string Performer
        {
            get { return m_Performer; }
            set { m_Performer = value; }
        }

        public Index PostGap
        {
            get { return m_PostGap; }
            set { m_PostGap = value; }
        }

        public Index PreGap
        {
            get { return m_PreGap; }
            set { m_PreGap = value; }
        }

        public string Songwriter
        {
            get { return m_Songwriter; }
            set { m_Songwriter = value; }
        }

        public string Title
        {
            get { return m_Title; }
            set { m_Title = value; }
        }

        public DataType TrackDataType
        {
            get { return m_TrackDataType; }
            set { m_TrackDataType = value; }
        }

        public Flags[] TrackFlags
        {
            get { return m_TrackFlags; }
            set { m_TrackFlags = value; }
        }

        public int TrackNumber
        {
            get { return m_TrackNumber; }
            set { m_TrackNumber = value; }
        }
        #endregion

        #region Constructors

        public Track(int tracknumber, string datatype)
        {
            m_TrackNumber = tracknumber;

            switch (datatype.Trim().ToUpper())
            {
                case "AUDIO":
                    m_TrackDataType = DataType.AUDIO;
                    break;
                case "CDG":
                    m_TrackDataType = DataType.CDG;
                    break;
                case "MODE1/2048":
                    m_TrackDataType = DataType.MODE1_2048;
                    break;
                case "MODE1/2352":
                    m_TrackDataType = DataType.MODE1_2352;
                    break;
                case "MODE2/2336":
                    m_TrackDataType = DataType.MODE2_2336;
                    break;
                case "MODE2/2352":
                    m_TrackDataType = DataType.MODE2_2352;
                    break;
                case "CDI/2336":
                    m_TrackDataType = DataType.CDI_2336;
                    break;
                case "CDI/2352":
                    m_TrackDataType = DataType.CDI_2352;
                    break;
                default:
                    m_TrackDataType = DataType.AUDIO;
                    break;
            }

            m_TrackFlags = new Flags[0];
            m_Songwriter = "";
            m_Title = "";
            m_ISRC = "";
            m_Performer = "";
            m_Indices = new Index[0];
            m_Garbage = new string[0];
            m_Comments = new string[0];
            m_PreGap = new Index(-1, 0, 0, 0);
            m_PostGap = new Index(-1, 0, 0, 0);
            m_DataFile = new AudioFile();
        }

        public Track(int tracknumber, DataType datatype)
        {
            m_TrackNumber = tracknumber;
            m_TrackDataType = datatype;

            m_TrackFlags = new Flags[0];
            m_Songwriter = "";
            m_Title = "";
            m_ISRC = "";
            m_Performer = "";
            m_Indices = new Index[0];
            m_Garbage = new string[0];
            m_Comments = new string[0];
            m_PreGap = new Index(-1, 0, 0, 0);
            m_PostGap = new Index(-1, 0, 0, 0);
            m_DataFile = new AudioFile();
        }

        #endregion

        #region Methods
        public void AddFlag(Flags flag)
        {
            if (flag != Flags.NONE && NewFlag(flag) == true)
            {
                m_TrackFlags = (Flags[])CueSheet.ResizeArray(m_TrackFlags, m_TrackFlags.Length + 1);
                m_TrackFlags[m_TrackFlags.Length - 1] = flag;
            }
        }

        public void AddFlag(string flag)
        {
            switch (flag.Trim().ToUpper())
            {
                case "DATA":
                    AddFlag(Flags.DATA);
                    break;
                case "DCP":
                    AddFlag(Flags.DCP);
                    break;
                case "4CH":
                    AddFlag(Flags.CH4);
                    break;
                case "PRE":
                    AddFlag(Flags.PRE);
                    break;
                case "SCMS":
                    AddFlag(Flags.SCMS);
                    break;
                default:
                    return;
            }
        }

        public void AddGarbage(string garbage)
        {
            if (garbage.Trim() != "")
            {
                m_Garbage = (string[])CueSheet.ResizeArray(m_Garbage, m_Garbage.Length + 1);
                m_Garbage[m_Garbage.Length - 1] = garbage;
            }
        }

        public void AddComment(string comment)
        {
            if (comment.Trim() != "")
            {
                m_Comments = (string[])CueSheet.ResizeArray(m_Comments, m_Comments.Length + 1);
                m_Comments[m_Comments.Length - 1] = comment;
            }
        }

        public void AddIndex(int number, int minutes, int seconds, int frames)
        {
            m_Indices = (Index[])CueSheet.ResizeArray(m_Indices, m_Indices.Length + 1);

            m_Indices[m_Indices.Length - 1] = new Index(number, minutes, seconds, frames);
        }

        public void RemoveIndex(int indexIndex)
        {
            for (int i = indexIndex; i < m_Indices.Length - 1; i++)
            {
                m_Indices[i] = m_Indices[i + 1];
            }
            m_Indices = (Index[])CueSheet.ResizeArray(m_Indices, m_Indices.Length - 1);
        }

        private bool NewFlag(Flags new_flag)
        {
            foreach (Flags flag in m_TrackFlags)
            {
                if (flag == new_flag)
                {
                    return false;
                }
            }
            return true;
        }

        public override string ToString()
        {
            StringBuilder output = new StringBuilder();

            if (m_DataFile.Filename != null && m_DataFile.Filename.Trim() != "")
            {
                output.Append("FILE \"" + m_DataFile.Filename.Trim() + "\" " + m_DataFile.Filetype.ToString() + Environment.NewLine);
            }

            output.Append("  TRACK " + m_TrackNumber.ToString().PadLeft(2, '0') + " " + m_TrackDataType.ToString().Replace('_', '/'));

            foreach (string comment in m_Comments)
            {
                output.Append(Environment.NewLine + "    REM " + comment);
            }

            if (m_Performer.Trim() != "")
            {
                output.Append(Environment.NewLine + "    PERFORMER \"" + m_Performer + "\"");
            }

            if (m_Songwriter.Trim() != "")
            {
                output.Append(Environment.NewLine + "    SONGWRITER \"" + m_Songwriter + "\"");
            }

            if (m_Title.Trim() != "")
            {
                output.Append(Environment.NewLine + "    TITLE \"" + m_Title + "\"");
            }

            if (m_TrackFlags.Length > 0)
            {
                output.Append(Environment.NewLine + "    FLAGS");
            }

            foreach (Flags flag in m_TrackFlags)
            {
                output.Append(" " + flag.ToString().Replace("CH4", "4CH"));
            }

            if (m_ISRC.Trim() != "")
            {
                output.Append(Environment.NewLine + "    ISRC " + m_ISRC.Trim());
            }

            if (m_PreGap.Number != -1)
            {
                output.Append(Environment.NewLine + "    PREGAP " + m_PreGap.Minutes.ToString().PadLeft(2, '0') + ":" + m_PreGap.Seconds.ToString().PadLeft(2, '0') + ":" + m_PreGap.Frames.ToString().PadLeft(2, '0'));
            }

            for (int j = 0; j < m_Indices.Length; j++)
            {
                output.Append(Environment.NewLine + "    INDEX " + this[j].Number.ToString().PadLeft(2, '0') + " " + this[j].Minutes.ToString().PadLeft(2, '0') + ":" + this[j].Seconds.ToString().PadLeft(2, '0') + ":" + this[j].Frames.ToString().PadLeft(2, '0'));
            }

            if (m_PostGap.Number != -1)
            {
                output.Append(Environment.NewLine + "    POSTGAP " + m_PostGap.Minutes.ToString().PadLeft(2, '0') + ":" + m_PostGap.Seconds.ToString().PadLeft(2, '0') + ":" + m_PostGap.Frames.ToString().PadLeft(2, '0'));
            }

            return output.ToString();
        }

        #endregion Methods
    }
}
