using System;
using System.Collections.Generic;
using FFmpeg.AutoGen;

namespace LightStudio.FfmpegShim
{
    public class FfmpegMediaInfo
    {
        public string Title { get; private set; }
        public string Artist { get; private set; }
        public string Album { get; private set; }
        public string Date { get; private set; }
        public string Composer { get; private set; }
        public string Performer { get; private set; }
        public string AlbumArtist { get; private set; }
        public string TrackNumber { get; private set; }
        public string Genre { get; private set; }
        public string Grouping { get; private set; }
        public string Comments { get; private set; }
        public string Copyright { get; private set; }
        public string Description { get; private set; }
        public string TotalTracks { get; private set; }
        public string DiscNumber { get; private set; }
        public string TotalDiscs { get; private set; }
        public TimeSpan Duration { get; private set; }
        public IReadOnlyDictionary<string, string> AllProperties { get; private set; }
        internal unsafe FfmpegMediaInfo(long duration, AVDictionary* metadata)
        {
            Duration = new TimeSpan(duration);
            AllProperties = new AVDictionaryWrapper(metadata);
            Title = AllProperties["title"];
            Album = AllProperties["album"];
            AlbumArtist = AllProperties["album_artist"];
            Artist = AllProperties["artist"];
            Composer = AllProperties["composer"];
            Date = AllProperties["date"];

            var track = AllProperties["track"];
            if (track == null)
            {
                TrackNumber = null;
                TotalTracks = null;
            }
            else if (track.Contains('/'))
            {
                var p = track.Split('/');
                TrackNumber = p[0];
                TotalTracks = p[1];
            }
            else
            {
                TrackNumber = track;
                TotalTracks = null;
            }

            var disc = AllProperties["disc"];
            if (disc==null)
            {
                DiscNumber = null;
                TotalDiscs = null;
            }
            else if (disc.Contains("/"))
            {
                var p = disc.Split('/');
                DiscNumber = p[0];
                TotalDiscs = p[1];
            }
            else
            {
                DiscNumber = disc;
                TotalDiscs = null;
            }

            Genre = AllProperties["genre"];
            Performer = AllProperties["performer"];
            Grouping = AllProperties["grouping"];
            Comments = AllProperties["comment"];
            Copyright = AllProperties["copyright"];
            Description = AllProperties["description"];
        }
    }
}
