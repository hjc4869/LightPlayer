using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

namespace LightStudio.MediaLibraryCore.Lyrics;

public interface IJsDownloadSource : IDisposable
{
    string Name { get; set; }

    IReadOnlyList<LyricSourceInfoItem> GetSourceInfo();

    Task<ExternalLrcInfo[]> LookupLrcAsync(
        string title,
        string artist,
        CancellationToken cancellationToken = default);

    Task<LyricDownload> DownloadLrcAsync(
        ExternalLrcInfo result,
        CancellationToken cancellationToken = default);
}