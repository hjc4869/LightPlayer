using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.Models;

public interface IVideoSelectionCallbacks
{
    Action<Sourced<DbVideoFile>, IReadOnlyList<Sourced<DbVideoFile>>, bool> OpenVideo { get; }

    Func<IReadOnlyList<Sourced<DbVideoFile>>, Task>? AddVideosToPlaylistAsync { get; }

    Action<IReadOnlyList<Sourced<DbVideoFile>>> ClearPlaybackProgress { get; }

    Action<IReadOnlyList<Sourced<DbVideoFile>>> MarkWatched { get; }
}