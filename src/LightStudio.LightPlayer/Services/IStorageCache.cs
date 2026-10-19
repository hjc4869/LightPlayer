using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Services;

/// <summary>Number of entries a cache currently holds and the bytes they occupy.</summary>
public readonly record struct StorageCacheUsage(int ItemCount, long TotalBytes);

/// <summary>
/// A cache of disposable data kept on this device, which the user may inspect and empty from the
/// storage settings. A service implements it only when it owns such a cache locally, so the
/// settings list is assembled by probing for this interface rather than by testing the platform.
/// </summary>
public interface IStorageCache
{
    /// <summary>Reads the cache's current item count and total size.</summary>
    Task<StorageCacheUsage> MeasureAsync();

    /// <summary>Removes every entry. Cached content is produced again on demand.</summary>
    Task ClearAsync();
}

/// <summary>
/// A cache whose size budget the user may change. Implemented alongside <see cref="IStorageCache"/>
/// only where a budget exists and can be persisted, so the settings UI offers the control by
/// probing for this interface rather than by testing the platform.
/// </summary>
public interface IStorageCacheLimit
{
    /// <summary>Bytes the cache may currently occupy.</summary>
    long MaxBytes { get; }

    /// <summary>Smallest budget the cache accepts.</summary>
    long MinimumBytes { get; }

    /// <summary>Largest budget the cache accepts.</summary>
    long MaximumBytes { get; }

    /// <summary>Persists a new budget, clamped to the accepted range, and evicts what no longer fits.</summary>
    Task SetMaxBytesAsync(long maxBytes);
}
