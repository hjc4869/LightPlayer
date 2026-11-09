using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// One album's worth of tracks rendered as a section on the artist detail page:
/// cover art, title, release year, the album's ordered track rows, and
/// album-level playback, queue, and playlist actions.
/// </summary>
public sealed partial class ArtistAlbumSectionModel : ObservableObject, IArtworkSizeTarget
{
    private readonly object artGate = new();
    private CancellationTokenSource? artRequest;

    [ObservableProperty]
    private IImage? coverImage;

    public ArtistAlbumSectionModel(
        string title,
        string year,
        IReadOnlyList<TrackRowModel> tracks,
        IRelayCommand playCommand,
        IRelayCommand shuffleCommand,
        IRelayCommand addToQueueCommand,
        IRelayCommand addToPlaylistCommand)
    {
        Title = title;
        Year = year;
        Tracks = tracks;
        PlayCommand = playCommand;
        ShuffleCommand = shuffleCommand;
        AddToQueueCommand = addToQueueCommand;
        AddToPlaylistCommand = addToPlaylistCommand;
    }

    public string Title { get; }

    public string Year { get; }

    public bool HasYear => !string.IsNullOrEmpty(Year);

    public IReadOnlyList<TrackRowModel> Tracks { get; }

    public IRelayCommand PlayCommand { get; }

    public IRelayCommand ShuffleCommand { get; }

    public IRelayCommand AddToQueueCommand { get; }

    public IRelayCommand AddToPlaylistCommand { get; }

    /// <summary>Loads this section's cover art. Invoked when the section's view reports its size.</summary>
    public Func<ArtistAlbumSectionModel, CancellationToken, Task>? ArtLoader { get; set; }

    /// <summary>Device pixels covered by the shorter side of the cover where it is displayed.</summary>
    public int ArtPixelSize { get; private set; }

    /// <summary>
    /// Loads the cover for the first view showing this section and reloads it when a view needs
    /// more pixels than the loaded cover has, if a loader is set.
    /// </summary>
    public void SetArtworkDisplaySize(int pixelSize)
    {
        CancellationTokenSource request;
        lock (artGate)
        {
            if (ArtLoader is null || pixelSize <= ArtPixelSize)
            {
                return;
            }

            ArtPixelSize = pixelSize;
            artRequest?.Cancel();
            artRequest = request = new CancellationTokenSource();
        }

        // Never run the loader inline: the size is reported from the UI thread during layout.
        _ = Task.Run(() => RunArtLoaderAsync(request));
    }

    /// <summary>Abandons an in-flight load, e.g. when the page reloads onto a different artist.</summary>
    public void CancelArt()
    {
        lock (artGate)
        {
            artRequest?.Cancel();
            artRequest = null;
        }
    }

    /// <summary>Frees this section's cover once it is no longer displayed anywhere.</summary>
    public void ReleaseArt()
    {
        CancelArt();
        CoverImage = null;
    }

    partial void OnCoverImageChanged(IImage? oldValue, IImage? newValue) =>
        (oldValue as IDisposable)?.Dispose();

    private async Task RunArtLoaderAsync(CancellationTokenSource request)
    {
        try
        {
            if (ArtLoader is { } loader)
            {
                await loader(this, request.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (artGate)
            {
                if (ReferenceEquals(artRequest, request))
                {
                    artRequest = null;
                }

                request.Dispose();
            }
        }
    }
}
