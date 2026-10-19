using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.MediaLibraryCore.Lyrics;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

namespace LightStudio.LightPlayer.Services;

public sealed record LyricsSearchResult(ParsedLrc? Lyrics, IReadOnlyList<ExternalLrcInfo> Candidates);

public interface ILyricsService
{
    bool HasSources { get; }

    Task<ParsedLrc?> LoadAsync(string title, string artist, MediaReference? media);
    Task<LyricsSearchResult> AutoSearchAsync(
        string title,
        string artist,
        MediaReference? media,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExternalLrcInfo>> SearchAsync(string title, string artist, CancellationToken cancellationToken = default);
    Task<ParsedLrc?> DownloadAsync(ExternalLrcInfo candidate, string title, string artist, CancellationToken cancellationToken = default);
    Task<ParsedLrc?> ImportAsync(string title, string artist, string content);
    Task SaveAsync(ParsedLrc lyrics);
    Task ClearAsync(string title, string artist);
}

public interface IMediaResolverAwareLyricsService
{
    IClientMediaResolver? MediaResolver { get; set; }
}