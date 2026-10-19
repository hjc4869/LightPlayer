#nullable enable
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.MediaLibraryCore.Library;

/// <summary>
/// Names photo coordinates during one media scan. The embedded GeoNames database is loaded when the
/// first photo needs a location and released when the scan disposes the resolver.
/// </summary>
internal sealed class PhotoLocationResolver(IMultiMediaPlatformIO platform) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private GeoNamesDatabase? database;
    private int languageIndex = -1;
    private bool closed;

    /// <summary>The GeoNames language for the current interface culture.</summary>
    public string Language { get; } = GeoNamesDatabase.LanguageFor(CultureInfo.CurrentUICulture);

    /// <summary>True when stored coordinates have not been named in <see cref="Language"/>.</summary>
    public bool NeedsUpdate(DbImageFile image) =>
        image.Latitude is not null && image.Longitude is not null && image.LocationLanguage != Language;

    /// <summary>
    /// Sets the image's location names from its coordinates and returns true when they were resolved.
    /// Names are left for a later scan to retry when the database cannot be loaded.
    /// </summary>
    public async ValueTask<bool> ApplyAsync(DbImageFile image, CancellationToken cancellationToken)
    {
        if (image.Latitude is not { } latitude || image.Longitude is not { } longitude)
        {
            image.Country = image.State = image.City = image.LocationLanguage = null;
            return true;
        }
        if ((Volatile.Read(ref database) ?? await LoadAsync(cancellationToken).ConfigureAwait(false)) is not { } current)
        {
            image.LocationLanguage = null;
            return false;
        }
        var place = current.Lookup(latitude, longitude, languageIndex);
        image.Country = place?.Country;
        image.State = place?.State;
        image.City = place?.City;
        image.LocationLanguage = Language;
        return true;
    }

    private async Task<GeoNamesDatabase?> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (database is not null || closed)
                return database;
            var budget = new ScanWorkBudget(platform, cancellationToken);
            var loaded = await GeoNamesDatabase.LoadAsync(budget.YieldAsync, cancellationToken).ConfigureAwait(false);
            languageIndex = loaded.LanguageIndex(Language);
            Volatile.Write(ref database, loaded);
            return loaded;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            closed = true;
            AppLog.Warning("Library.Scan", "The photo location database could not be loaded.", exception);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        closed = true;
        Volatile.Write(ref database, null);
    }
}
