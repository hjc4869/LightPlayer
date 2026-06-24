using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.Tools;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.Database;

public static class GlobalLibraryCache
{
    private static readonly IServiceScopeFactory _scopeFactory;
    public static MediaLibraryAlbum[] CachedDbAlbum;
    public static MediaLibraryArtist[] CachedDbArtist;
    public static DbMediaFile[] CachedDbMediaFile;
    public static TrieTreeNode<char, MediaLibraryAlbum> AlbumSearchTree;
    public static TrieTreeNode<char, MediaLibraryArtist> ArtistSearchTree;
    public static TrieTreeNode<char, DbMediaFile> FileSearchTree;

    static GlobalLibraryCache()
    {
        _scopeFactory = ApplicationServiceBase.App
            .ApplicationServices.GetRequiredService<IServiceScopeFactory>();
    }

    public static void Invalidate()
    {
        CachedDbAlbum = null;
        AlbumSearchTree = null;
        CachedDbArtist = null;
        ArtistSearchTree = null;
        CachedDbMediaFile = null;
        FileSearchTree = null;
    }

    public static async Task LoadAlbumAsync()
    {
        CachedDbAlbum = (await LibraryHelper.ListAlbumsAsync()).ToArray();
        AlbumSearchTree = CachedDbAlbum.ToTrieTree(x => x.Title, new CaseInsensitiveCharComparer());
    }

    public static async Task LoadArtistAsync()
    {
        CachedDbArtist = (await LibraryHelper.ListArtistsAsync()).ToArray();
        ArtistSearchTree = CachedDbArtist.ToTrieTree(x => x.Name, new CaseInsensitiveCharComparer());
    }

    public static async Task LoadMediaAsync()
    {
        using (var scope = _scopeFactory.CreateScope())
        using (var context = scope.ServiceProvider
            .GetRequiredService<MediaLibraryDbContext>())
        {
            await Task.Run(() =>
            {
                var files = context.MediaFiles.ToArray();
                FileSearchTree = files.ToTrieTree(x => x.Title, new CaseInsensitiveCharComparer());
                CachedDbMediaFile = files;
            });
        }
    }
}
