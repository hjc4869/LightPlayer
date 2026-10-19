using System.Collections.Generic;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Services;

public interface IFileQueueExpander
{
    Task<CueExpansionResult> ExpandAsync(IReadOnlyList<string> paths);
}

public sealed record CueExpansionResult(
    IReadOnlyList<MusicPlaybackItemModel> Items,
    IReadOnlyList<CueResolutionFailure> Failures);

public sealed record CueResolutionFailure(string CueFilePath, string ReferencedFileName);