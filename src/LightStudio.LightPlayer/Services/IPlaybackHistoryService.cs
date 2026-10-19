using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Services;

public interface IPlaybackHistoryService
{
    const int HistoryEntryLimit = 20;

    event EventHandler<MusicPlaybackItemModel>? NewEntryAdded;

    Task<IReadOnlyList<MusicPlaybackItemModel>> GetHistoryAsync(int count);
    Task AddHistoryAsync(MusicPlaybackItemModel item);
    Task ClearHistoryAsync();
}

public interface IPlaybackHistoryServiceFactory
{
    IPlaybackHistoryService Create(Func<bool> isIndexing);
}