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
public sealed partial class ArtistAlbumSectionModel : ObservableObject
{
    private readonly object artGate = new();
    private CancellationTokenSource? artRequest;
    private bool artLoaded;

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

    /// <summary>Loads this section's cover art. Invoked once when the section is shown.</summary>
    public Func<ArtistAlbumSectionModel, CancellationToken, Task>? ArtLoader { get; set; }

    /// <summary>Triggers a one-time cover load for this section, if a loader is set.</summary>
    public void RequestArt()
    {
        CancellationTokenSource request;
        lock (artGate)
        {
            if (artLoaded || artRequest is not null || ArtLoader is null)
            {
                return;
            }

            artRequest = request = new CancellationTokenSource();
        }

        // Never run the loader inline: RequestArt is raised from the UI thread after the page loads.
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
                    artLoaded = true;
                    artRequest = null;
                }

                request.Dispose();
            }
        }
    }
}
