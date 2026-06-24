using System.IO;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.IO;

public interface IMultiMediaPlatformIO
{
    public static IMultiMediaPlatformIO Instance { get; protected set; }
    int OptimalIoThreadCount { get; }
    string LocalDataPath { get; }
    Task<Stream> OpenMediaFileAsync(string path);
    Task<IFile[]> ListFilesFullNameInPathAsync(string path);
    Task<string[]> ListFoldersFullNameInPathAsync(string path);
    Task<MediaInfo> GetMediaInfoAsync(string mediaFilePath);
    Task<byte[]> GetAlbumCoverAsync(string mediaFilePath);
    T GetConfigurationValue<T>(string key);
    void SetConfigurationValue<T>(string key, T value);
}
