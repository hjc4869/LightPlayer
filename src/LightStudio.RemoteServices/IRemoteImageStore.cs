using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;

namespace LightStudio.RemoteServices;

public interface IRemoteImageStore : IStorageCache, IStorageCacheLimit
{
    Task<byte[]?> ReadAsync(string name);

    Task WriteAsync(string name, byte[] data);
}