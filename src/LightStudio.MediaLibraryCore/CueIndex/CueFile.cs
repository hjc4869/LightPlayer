using LightStudio.MediaLibraryCore.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.CueIndex
{
    public class ManagedAudioIndexCue
    {
        public TimeSpan StartTime { get; set; }
        public TimeSpan Duration { get; set; }
        public MediaInfo TrackInfo { get; set; }
    }
    public class CueFile
    {
        const int MaxCueFileSize = 1048576;
        public string FileName { get; set; }
        public IList<ManagedAudioIndexCue> Indices { get; set; }
        public CueFile(string fileName, List<ManagedAudioIndexCue> indices)
        {
            FileName = fileName;
            Indices = indices;
        }

        private static void AddIfNotNullOrWhiteSpace(IDictionary<string, string> dict, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key) ||
                string.IsNullOrWhiteSpace(value))
                return;
            dict.Add(key.ToLower(), value);
        }

        public static async Task<CueFile> CreateFromStreamAsync(Stream stream)
        {
            using (stream)
            {
                if (stream.Length > MaxCueFileSize)
                {
                    // Return empty list when file size exceeds the limit.
                    return new CueFile("", new List<ManagedAudioIndexCue>());
                }

                using var sr = new StreamReader(stream);
                return CreateFromString(await sr.ReadToEndAsync());
            }
        }
        public static CueFile CreateFromString(string content)
        {
            string fileName = null;

            List<ManagedAudioIndexCue> cues = new List<ManagedAudioIndexCue>();
            var cue = new CueSheet(content, null);
            var cueComments = new Dictionary<string, string>();
            foreach (var comment in cue.Comments)
            {
                var s_index = comment.IndexOf(' ');
                if (s_index == -1)
                    continue;
                AddIfNotNullOrWhiteSpace(cueComments, comment.Substring(0, s_index),
                    comment.Substring(s_index + 1, comment.Length - s_index - 1).Trim('\"')); // Naive approach
            }
            var tracks = cue.Tracks;
            for (int i = 0; i < tracks.Length; i++)
            {
                var track = tracks[i];
                var next = i == tracks.Length - 1 ? (Track?)null : tracks[i + 1];
                if (track.TrackDataType != DataType.AUDIO)
                    continue; //just ignore that
                else if (track.DataFile.Filename != null &&
                    track.DataFile.Filename.Trim() != string.Empty)
                {
                    //file
                    fileName = track.DataFile.Filename;
                }

                ManagedAudioIndexCue item = null;
                var trackComments = new Dictionary<string, string>();
                var duration = TimeSpan.Zero;
                if (track.Indices.Length == 0)
                    continue;//ignore track without indices
                var beginTime = track.Indices[track.Indices.Length - 1];
                var beginTimespan = new TimeSpan(0, 0, beginTime.Minutes, beginTime.Seconds, beginTime.Frames * 40 / 3);
                var endTimespan = TimeSpan.Zero;
                if (next != null && next.Value.Indices.Length > 0)
                {
                    endTimespan = new TimeSpan(0, 0, next.Value.Indices[0].Minutes, next.Value.Indices[0].Seconds, next.Value.Indices[0].Frames * 40 / 3);
                    duration = endTimespan - beginTimespan;
                }//else duration=0, which means play until end.

                //track
                foreach (var comment in track.Comments)
                {
                    var s_index = comment.IndexOf(' ');
                    if (s_index == -1)
                        continue;
                    AddIfNotNullOrWhiteSpace(trackComments, comment.Substring(0, s_index),
                        comment.Substring(s_index + 1, comment.Length - s_index - 1));
                }
                var mediaInfo = new MediaInfo();
                IDictionary<string, string> allProps = new Dictionary<string, string>();

                mediaInfo.Album = cue.Title;
                AddIfNotNullOrWhiteSpace(allProps, "album", cue.Title);

                mediaInfo.AlbumArtist = cue.Performer;
                AddIfNotNullOrWhiteSpace(allProps, "album_artist", mediaInfo.AlbumArtist);

                mediaInfo.Artist = string.IsNullOrWhiteSpace(track.Performer) ? cue.Performer : track.Performer;
                AddIfNotNullOrWhiteSpace(allProps, "artist", mediaInfo.Artist);

                if (trackComments.ContainsKey("comment"))
                    mediaInfo.Comments = trackComments["comment"];
                else if (cueComments.ContainsKey("comment"))
                    mediaInfo.Comments = cueComments["comment"];

                mediaInfo.Composer = string.IsNullOrWhiteSpace(track.Songwriter) ? cue.Songwriter : track.Songwriter;
                AddIfNotNullOrWhiteSpace(allProps, "composer", mediaInfo.Composer);

                if (trackComments.ContainsKey("date"))
                    mediaInfo.Date = trackComments["date"];
                else if (cueComments.ContainsKey("date"))
                    mediaInfo.Date = cueComments["date"];

                if (cueComments.TryGetValue("disc", out var discStr))
                {
                    var split = discStr.Split('/');
                    if (split.Length == 2)
                    {
                        mediaInfo.DiscNumber = split[0];
                        mediaInfo.TotalDiscs = split[1];
                    }
                    else
                    {
                        mediaInfo.DiscNumber = discStr;
                        mediaInfo.TotalDiscs = discStr;
                    }
                }
                else
                {
                    mediaInfo.DiscNumber = "1";
                    mediaInfo.TotalDiscs = "1";
                }

                mediaInfo.Duration = duration;

                if (trackComments.ContainsKey("genre"))
                    mediaInfo.Genre = trackComments["genre"];
                else if (cueComments.ContainsKey("genre"))
                    mediaInfo.Genre = cueComments["genre"];

                mediaInfo.Performer = string.IsNullOrWhiteSpace(track.Performer) ? cue.Performer : track.Performer;
                AddIfNotNullOrWhiteSpace(allProps, "performer", mediaInfo.Performer);

                mediaInfo.Title = track.Title;
                AddIfNotNullOrWhiteSpace(allProps, "title", mediaInfo.Title);

                mediaInfo.TrackNumber = (i + 1).ToString();
                mediaInfo.TotalTracks = tracks.Length.ToString();
                AddIfNotNullOrWhiteSpace(allProps, "track", $"{i}/{tracks.Length}");

                foreach (var kvp in trackComments)
                {
                    if (allProps.ContainsKey(kvp.Key))
                        continue;
                    allProps.Add(kvp);
                }
                foreach (var kvp in cueComments)
                {
                    if (allProps.ContainsKey(kvp.Key))
                        continue;
                    allProps.Add(kvp);
                }

                mediaInfo.AllProperties = allProps as IReadOnlyDictionary<string, string>;


                item = new ManagedAudioIndexCue() { Duration = duration, StartTime = beginTimespan, TrackInfo = mediaInfo };
                cues.Add(item);
            }
            return new CueFile(fileName ?? "", cues);
        }
    }
}
