using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.IO
{
    public class MediaInfo
    {
        public string Album { get; set; } = string.Empty;
        public string AlbumArtist { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Comments { get; set; } = string.Empty;
        public string Composer { get; set; } = string.Empty;
        public string Copyright { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string DiscNumber { get; set; } = string.Empty;
        public TimeSpan Duration { get; set; }
        public string Genre { get; set; } = string.Empty;
        public string Grouping { get; set; } = string.Empty;
        public string Performer { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string TotalDiscs { get; set; } = string.Empty;
        public string TotalTracks { get; set; } = string.Empty;
        public string TrackNumber { get; set; } = string.Empty;
        public IReadOnlyDictionary<string, string> AllProperties { get; set; }
    }
}
