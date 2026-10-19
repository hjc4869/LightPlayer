using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;

namespace LightStudio.RemoteServices;

/// <summary>
/// Persistent backing store for the blocks <see cref="RemoteMediaBlockCache"/> streams. Supplied by
/// the head, because where disposable bytes may be kept differs sharply between hosts: the browser
/// uses the Origin Private File System, installed heads a cache directory.
/// </summary>
/// <remarks>
/// Every operation is best-effort. A store that is unavailable, denied or full returns
/// <see langword="null"/> from <see cref="ReadAsync"/> and silently drops writes, which degrades to
/// fetching the block from the network again.
/// </remarks>
public interface IMediaBlockStore : IStorageCache, IStorageCacheLimit
{
    /// <summary>
    /// Reads a persisted block, or returns <see langword="null"/> when it is absent, unreadable or
    /// does not have <paramref name="expectedLength"/> (a truncated leftover from an interrupted write).
    /// </summary>
    Task<byte[]?> ReadAsync(string name, int expectedLength);

    /// <summary>Persists a block, evicting the least recently used ones to stay within budget.</summary>
    Task WriteAsync(string name, byte[] data);

    /// <summary>Whether the store's index lists a persisted block, without reading it.</summary>
    Task<bool> ContainsAsync(string name);
}
