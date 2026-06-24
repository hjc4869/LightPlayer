using LightStudio.MediaLibraryCore.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace LightStudio.MediaLibraryCore.Database;

/// <summary>
/// Library context.
/// </summary>
public class MediaLibraryDbContext : DbContext
{
    /// <summary>
    /// Set of media files.
    /// </summary>
    public DbSet<DbMediaFile> MediaFiles { get; set; }

    /// <summary>
    /// Set of playback history.
    /// </summary>
    public DbSet<DbPlaybackHistory> PlaybackHistory { get; set; }

    /// <summary>
    /// Class constructor that creates instance of <see cref="MediaLibraryDbContext"/> without caching.
    /// </summary>
    /// <param name="options">Instance of <see cref="DbContextOptions"/>.</param>
    public MediaLibraryDbContext(DbContextOptions<MediaLibraryDbContext> options) : base(options)
    {
    }

#if EFCORE_MIGRATION    
    public MediaLibraryDbContext()
    {

    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite($"Data source=MigrationStub.sqlite");
    }
#else
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
    }
#endif

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DbMediaFile>()
            .Ignore(p => p.IsExternal);

        modelBuilder.Entity<DbMediaFile>()
            .Ignore(p => p.ExternalFileId);

        modelBuilder.Entity<DbPlaybackHistory>()
            .HasOne(p => p.RelatedMediaFile)
            .WithMany()
            .HasForeignKey(p => p.RelatedMediaFileId)
            .IsRequired();

        base.OnModelCreating(modelBuilder);
    }
}
