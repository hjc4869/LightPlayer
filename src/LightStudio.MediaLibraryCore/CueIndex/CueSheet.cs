using System;
using System.Text;

namespace LightStudio.MediaLibraryCore.CueIndex
{
    /// <summary>Parses and serializes CUE sheets.</summary>
    class CueSheet
    {
        #region Private Variables
        string[] cueLines;

        private string m_Catalog = "";
        private string m_CDTextFile = "";
        private string[] m_Comments = new string[0];
        // Unrecognized global CUE lines are preserved when the sheet is serialized.
        private string[] m_Garbage = new string[0];
        private string m_Performer = "";
        private string m_Songwriter = "";
        private string m_Title = "";
        private Track[] m_Tracks = new Track[0];

        #endregion

        #region Properties


        /// <summary>Gets or sets a track by its zero-based position.</summary>
        public Track this[int tracknumber]
        {
            get
            {
                return m_Tracks[tracknumber];
            }
            set
            {
                m_Tracks[tracknumber] = value;
            }
        }


        /// <summary>
        /// Gets or sets the 13-digit UPC/EAN catalog number.
        /// </summary>
        public string Catalog
        {
            get { return m_Catalog; }
            set { m_Catalog = value; }
        }

        /// <summary>Gets or sets the file containing encoded CD-TEXT data.</summary>
        public string CDTextFile
        {
            get { return m_CDTextFile; }
            set { m_CDTextFile = value; }
        }

        /// <summary>Gets or sets global REM comment values.</summary>
        public string[] Comments
        {
            get { return m_Comments; }
            set { m_Comments = value; }
        }

        /// <summary>Gets unrecognized global CUE lines.</summary>
        public string[] Garbage
        {
            get { return m_Garbage; }
        }

        /// <summary>Gets or sets the disc performer.</summary>
        public string Performer
        {
            get { return m_Performer; }
            set { m_Performer = value; }
        }

        /// <summary>Gets or sets the disc songwriter.</summary>
        public string Songwriter
        {
            get { return m_Songwriter; }
            set { m_Songwriter = value; }
        }

        /// <summary>Gets or sets the disc title.</summary>
        public string Title
        {
            get { return m_Title; }
            set { m_Title = value; }
        }

        /// <summary>Gets or sets the tracks in the sheet.</summary>
        public Track[] Tracks
        {
            get { return m_Tracks; }
            set { m_Tracks = value; }
        }

        #endregion

        #region Constructors

        public CueSheet()
        { }

        public CueSheet(string cueString, char[] lineDelims)
        {
            if (lineDelims == null)
            {
                lineDelims = new char[] { '\n' };
            }

            cueLines = cueString.Split(lineDelims);
            RemoveEmptyLines(ref cueLines);
            ParseCue(cueLines);
        }

        #endregion

        #region Methods

        private void RemoveEmptyLines(ref string[] file)
        {
            int itemsRemoved = 0;

            for (int i = 0; i < file.Length; i++)
            {
                if (file[i].Trim() != "")
                {
                    file[i - itemsRemoved] = file[i];
                }
                else if (file[i].Trim() == "")
                {
                    itemsRemoved++;
                }
            }

            if (itemsRemoved > 0)
            {
                file = (string[])ResizeArray(file, file.Length - itemsRemoved);
            }
        }

        private void ParseCue(string[] file)
        {
            // -1 selects global directives; nonnegative values select a track.
            int trackOn = -1;
            AudioFile currentFile = new AudioFile();

            for (int i = 0; i < file.Length; i++)
            {
                file[i] = file[i].Trim();
                var propLen = file[i].IndexOf(' ');

                if (propLen < 0)
                {
                    continue;
                }

                switch (file[i].Substring(0, propLen).ToUpper())
                {
                    case "CATALOG":
                        ParseString(file[i], trackOn);
                        break;
                    case "CDTEXTFILE":
                        ParseString(file[i], trackOn);
                        break;
                    case "FILE":
                        currentFile = ParseFile(file[i], trackOn);
                        break;
                    case "FLAGS":
                        ParseFlags(file[i], trackOn);
                        break;
                    case "INDEX":
                        ParseIndex(file[i], trackOn);
                        break;
                    case "ISRC":
                        ParseString(file[i], trackOn);
                        break;
                    case "PERFORMER":
                        ParseString(file[i], trackOn);
                        break;
                    case "POSTGAP":
                        ParseIndex(file[i], trackOn);
                        break;
                    case "PREGAP":
                        ParseIndex(file[i], trackOn);
                        break;
                    case "REM":
                        ParseComment(file[i], trackOn);
                        break;
                    case "SONGWRITER":
                        ParseString(file[i], trackOn);
                        break;
                    case "TITLE":
                        ParseString(file[i], trackOn);
                        break;
                    case "TRACK":
                        trackOn++;
                        ParseTrack(file[i], trackOn);
                        if (currentFile.Filename != "")
                        {
                            m_Tracks[trackOn].DataFile = currentFile;
                            currentFile = new AudioFile();
                        }
                        break;
                    default:
                        ParseGarbage(file[i], trackOn);
                        break;
                }
            }

        }

        private void ParseComment(string line, int trackOn)
        {
            line = line.Substring(line.IndexOf(' '), line.Length - line.IndexOf(' ')).Trim();

            if (trackOn == -1)
            {
                if (line.Trim() != "")
                {
                    m_Comments = (string[])ResizeArray(m_Comments, m_Comments.Length + 1);
                    m_Comments[m_Comments.Length - 1] = line;
                }
            }
            else
            {
                m_Tracks[trackOn].AddComment(line);
            }
        }

        private AudioFile ParseFile(string line, int trackOn)
        {
            string fileType;

            line = line.Substring(line.IndexOf(' '), line.Length - line.IndexOf(' ')).Trim();

            fileType = line.Substring(line.LastIndexOf(' '), line.Length - line.LastIndexOf(' ')).Trim();

            line = line.Substring(0, line.LastIndexOf(' ')).Trim();

            if (line[0] == '"')
            {
                line = line.Substring(1, line.LastIndexOf('"') - 1);
            }

            return new AudioFile(line, fileType);
        }

        private void ParseFlags(string line, int trackOn)
        {
            string temp;

            if (trackOn != -1)
            {
                line = line.Trim();
                if (line != "")
                {
                    try
                    {
                        temp = line.Substring(0, line.IndexOf(' ')).ToUpper();
                    }
                    catch (Exception)
                    {
                        temp = line.ToUpper();

                    }

                    switch (temp)
                    {
                        case "FLAGS":
                            m_Tracks[trackOn].AddFlag(temp);
                            break;
                        case "DATA":
                            m_Tracks[trackOn].AddFlag(temp);
                            break;
                        case "DCP":
                            m_Tracks[trackOn].AddFlag(temp);
                            break;
                        case "4CH":
                            m_Tracks[trackOn].AddFlag(temp);
                            break;
                        case "PRE":
                            m_Tracks[trackOn].AddFlag(temp);
                            break;
                        case "SCMS":
                            m_Tracks[trackOn].AddFlag(temp);
                            break;
                        default:
                            break;
                    }

                    // A final flag has no following space.
                    try
                    {
                        temp = line.Substring(line.IndexOf(' '), line.Length - line.IndexOf(' '));
                    }
                    catch (Exception)
                    {
                        temp = line.Substring(0, line.Length);
                    }

                    if (temp.ToUpper().Trim() != line.ToUpper().Trim())
                    {
                        ParseFlags(temp, trackOn);
                    }
                }
            }
        }

        private void ParseGarbage(string line, int trackOn)
        {
            if (trackOn == -1)
            {
                if (line.Trim() != "")
                {
                    m_Garbage = (string[])ResizeArray(m_Garbage, m_Garbage.Length + 1);
                    m_Garbage[m_Garbage.Length - 1] = line;
                }
            }
            else
            {
                m_Tracks[trackOn].AddGarbage(line);
            }
        }

        private void ParseIndex(string line, int trackOn)
        {
            string indexType;
            string tempString;

            int number = 0;
            int minutes;
            int seconds;
            int frames;

            indexType = line.Substring(0, line.IndexOf(' ')).ToUpper();

            tempString = line.Substring(line.IndexOf(' '), line.Length - line.IndexOf(' ')).Trim();

            if (indexType == "INDEX")
            {
                number = Convert.ToInt32(tempString.Substring(0, tempString.IndexOf(' ')));
                tempString = tempString.Substring(tempString.IndexOf(' '), tempString.Length - tempString.IndexOf(' ')).Trim();
            }

            minutes = Convert.ToInt32(tempString.Substring(0, tempString.IndexOf(':')));
            seconds = Convert.ToInt32(tempString.Substring(tempString.IndexOf(':') + 1, tempString.LastIndexOf(':') - tempString.IndexOf(':') - 1));
            frames = Convert.ToInt32(tempString.Substring(tempString.LastIndexOf(':') + 1, tempString.Length - tempString.LastIndexOf(':') - 1));

            if (indexType == "INDEX")
            {
                m_Tracks[trackOn].AddIndex(number, minutes, seconds, frames);
            }
            else if (indexType == "PREGAP")
            {
                m_Tracks[trackOn].PreGap = new Index(0, minutes, seconds, frames);
            }
            else if (indexType == "POSTGAP")
            {
                m_Tracks[trackOn].PostGap = new Index(0, minutes, seconds, frames);
            }
        }

        private void ParseString(string line, int trackOn)
        {
            string category = line.Substring(0, line.IndexOf(' ')).ToUpper();

            line = line.Substring(line.IndexOf(' '), line.Length - line.IndexOf(' ')).Trim();

            if (line[0] == '"')
            {
                line = line.Substring(1, line.LastIndexOf('"') - 1);
            }

            switch (category)
            {
                case "CATALOG":
                    if (trackOn == -1)
                    {
                        this.m_Catalog = line;
                    }
                    break;
                case "CDTEXTFILE":
                    if (trackOn == -1)
                    {
                        this.m_CDTextFile = line;
                    }
                    break;
                case "ISRC":
                    if (trackOn != -1)
                    {
                        m_Tracks[trackOn].ISRC = line;
                    }
                    break;
                case "PERFORMER":
                    if (trackOn == -1)
                    {
                        this.m_Performer = line;
                    }
                    else
                    {
                        m_Tracks[trackOn].Performer = line;
                    }
                    break;
                case "SONGWRITER":
                    if (trackOn == -1)
                    {
                        this.m_Songwriter = line;
                    }
                    else
                    {
                        m_Tracks[trackOn].Songwriter = line;
                    }
                    break;
                case "TITLE":
                    if (trackOn == -1)
                    {
                        this.m_Title = line;
                    }
                    else
                    {
                        m_Tracks[trackOn].Title = line;
                    }
                    break;
                default:
                    break;
            }
        }

        private void ParseTrack(string line, int trackOn)
        {
            string tempString;
            int trackNumber;

            tempString = line.Substring(line.IndexOf(' '), line.Length - line.IndexOf(' ')).Trim();

            try
            {
                trackNumber = Convert.ToInt32(tempString.Substring(0, tempString.IndexOf(' ')));
            }
            catch (Exception)
            { throw; }

            tempString = tempString.Substring(tempString.IndexOf(' '), tempString.Length - tempString.IndexOf(' ')).Trim();

            AddTrack(trackNumber, tempString);
        }

        /// <summary>Returns a copy of an array with the requested size.</summary>
        public static System.Array ResizeArray(System.Array oldArray, int newSize)
        {
            int oldSize = oldArray.Length;
            System.Type elementType = oldArray.GetType().GetElementType();
            System.Array newArray = System.Array.CreateInstance(elementType, newSize);
            int preserveLength = System.Math.Min(oldSize, newSize);
            if (preserveLength > 0)
                System.Array.Copy(oldArray, newArray, preserveLength);
            return newArray;
        }

        private void AddTrack(int tracknumber, string datatype)
        {
            m_Tracks = (Track[])ResizeArray(m_Tracks, m_Tracks.Length + 1);
            m_Tracks[m_Tracks.Length - 1] = new Track(tracknumber, datatype);
        }

        public void AddTrack(string title, string performer)
        {
            m_Tracks = (Track[])ResizeArray(m_Tracks, m_Tracks.Length + 1);
            m_Tracks[m_Tracks.Length - 1] = new Track(m_Tracks.Length, "");

            m_Tracks[m_Tracks.Length - 1].Performer = performer;
            m_Tracks[m_Tracks.Length - 1].Title = title;
        }


        public void AddTrack(string title, string performer, string filename, FileType fType)
        {
            m_Tracks = (Track[])ResizeArray(m_Tracks, m_Tracks.Length + 1);
            m_Tracks[m_Tracks.Length - 1] = new Track(m_Tracks.Length, "");

            m_Tracks[m_Tracks.Length - 1].Performer = performer;
            m_Tracks[m_Tracks.Length - 1].Title = title;
            m_Tracks[m_Tracks.Length - 1].DataFile = new AudioFile(filename, fType);
        }

        public void AddTrack(string title, string performer, DataType datatype)
        {
            m_Tracks = (Track[])ResizeArray(m_Tracks, m_Tracks.Length + 1);
            m_Tracks[m_Tracks.Length - 1] = new Track(m_Tracks.Length, datatype);

            m_Tracks[m_Tracks.Length - 1].Performer = performer;
            m_Tracks[m_Tracks.Length - 1].Title = title;
        }

        public void AddTrack(Track track)
        {
            m_Tracks = (Track[])ResizeArray(m_Tracks, m_Tracks.Length + 1);
            m_Tracks[m_Tracks.Length - 1] = track;
        }

        public void RemoveTrack(int trackIndex)
        {
            for (int i = trackIndex; i < m_Tracks.Length - 1; i++)
            {
                m_Tracks[i] = m_Tracks[i + 1];
            }
            m_Tracks = (Track[])ResizeArray(m_Tracks, m_Tracks.Length - 1);
        }

        public void AddIndex(int trackIndex, int indexNum, int minutes, int seconds, int frames)
        {
            m_Tracks[trackIndex].AddIndex(indexNum, minutes, seconds, frames);
        }

        /// <summary>Removes an index by array position rather than by its CUE index number.</summary>
        public void RemoveIndex(int trackIndex, int indexIndex)
        {
            m_Tracks[trackIndex].RemoveIndex(indexIndex);
        }

        /// <summary>Returns the serialized CUE sheet content.</summary>
        public string SaveCue() => this.ToString();


        /// <summary>Serializes the CUE sheet.</summary>
        public override string ToString()
        {
            StringBuilder output = new StringBuilder();

            foreach (string comment in m_Comments)
            {
                output.Append("REM " + comment + Environment.NewLine);
            }

            if (m_Catalog.Trim() != "")
            {
                output.Append("CATALOG " + m_Catalog + Environment.NewLine);
            }

            if (m_Performer.Trim() != "")
            {
                output.Append("PERFORMER \"" + m_Performer + "\"" + Environment.NewLine);
            }

            if (m_Songwriter.Trim() != "")
            {
                output.Append("SONGWRITER \"" + m_Songwriter + "\"" + Environment.NewLine);
            }

            if (m_Title.Trim() != "")
            {
                output.Append("TITLE \"" + m_Title + "\"" + Environment.NewLine);
            }

            if (m_CDTextFile.Trim() != "")
            {
                output.Append("CDTEXTFILE \"" + m_CDTextFile.Trim() + "\"" + Environment.NewLine);
            }

            for (int i = 0; i < m_Tracks.Length; i++)
            {
                output.Append(m_Tracks[i].ToString());

                if (i != m_Tracks.Length - 1)
                {
                    output.Append(Environment.NewLine);
                }
            }

            return output.ToString();
        }

        #endregion

        #region CalculateDiscIDs

        public string CalculateCDDBdiscID()
        {
            int i, t = 0, n = 0;

            // Persisted disc IDs require this calculation to remain stable.

            i = 0;

            while (i < m_Tracks.Length)
            {
                n = n + cddb_sum((lastTrackIndex(m_Tracks[i]).Minutes * 60) + lastTrackIndex(m_Tracks[i]).Seconds);
                i++;
            }

            t = ((lastTrackIndex(m_Tracks[m_Tracks.Length - 1]).Minutes * 60) + lastTrackIndex(m_Tracks[m_Tracks.Length - 1]).Seconds) -
                ((lastTrackIndex(m_Tracks[0]).Minutes * 60) + lastTrackIndex(m_Tracks[0]).Seconds);

            ulong lDiscId = (((uint)n % 0xff) << 24 | (uint)t << 8 | (uint)m_Tracks.Length);
            return string.Format("{0:x8}", lDiscId);
        }

        private Index lastTrackIndex(Track track) => track.Indices[track.Indices.Length - 1];

        private int cddb_sum(int n)
        {
            int ret;

            ret = 0;

            while (n > 0)
            {
                ret = ret + (n % 10);
                n = n / 10;
            }

            return (ret);
        }

        #endregion CalculateDiscIDS


    }
}
