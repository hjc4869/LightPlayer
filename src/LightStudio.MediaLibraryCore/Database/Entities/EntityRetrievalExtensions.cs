using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.Database.Entities;

public static class EntityRetrievalExtensions
{
    public static DbMediaFile GetFileById(this int id)
    {
        DbMediaFile file = null;

        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider
            .GetRequiredService<MediaLibraryDbContext>())
        {
            file = context.MediaFiles.Find(id);
        }

        return file;
    }

    public static async Task<DbMediaFile> GetFileByIdAsync(this int id)
    {
        DbMediaFile file = null;

        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider
            .GetRequiredService<MediaLibraryDbContext>())
        {
            file = await context.MediaFiles.FindAsync(id);
        }

        return file;
    }
}
