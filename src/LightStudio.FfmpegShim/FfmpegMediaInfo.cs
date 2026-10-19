using System;
using System.Collections.Generic;
using LightStudio.NativeBindings.Types;

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
            : this(new TimeSpan(duration), new AVDictionaryWrapper(metadata))
        {
        }

        /// <summary>Describes media from tags read elsewhere. Lookups should ignore case, as FFmpeg's do.</summary>
        public FfmpegMediaInfo(TimeSpan duration, IReadOnlyDictionary<string, string> properties)
        {
            Duration = duration;
            AllProperties = properties;
            Title = Get("title");
            Album = Get("album");
            AlbumArtist = Get("album_artist");
            Artist = Get("artist");
            Composer = Get("composer");
            Date = Get("date");

            var track = Get("track");
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

            var disc = Get("disc");
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

            Genre = Get("genre");
            Performer = Get("performer");
            Grouping = Get("grouping");
            Comments = Get("comment");
            Copyright = Get("copyright");
            Description = Get("description");
        }

        private string Get(string key) => AllProperties.TryGetValue(key, out string value) ? value : null;
    }
}
