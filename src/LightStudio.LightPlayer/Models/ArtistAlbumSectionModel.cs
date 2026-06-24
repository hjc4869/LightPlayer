using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// One album's worth of tracks rendered as a section on the artist detail page:
/// cover art, title, release year, the album's ordered track rows, and
/// album-level play/queue actions.
/// </summary>
public sealed partial class ArtistAlbumSectionModel : ObservableObject
{
    private bool artRequested;

    [ObservableProperty]
    private IImage? coverImage;

    public ArtistAlbumSectionModel(
        string title,
        string year,
        IReadOnlyList<TrackRowModel> tracks,
        IRelayCommand playCommand,
        IRelayCommand addToQueueCommand)
    {
        Title = title;
        Year = year;
        Tracks = tracks;
        PlayCommand = playCommand;
        AddToQueueCommand = addToQueueCommand;
    }

    public string Title { get; }

    public string Year { get; }

    public bool HasYear => !string.IsNullOrEmpty(Year);

    public IReadOnlyList<TrackRowModel> Tracks { get; }

    public IRelayCommand PlayCommand { get; }

    public IRelayCommand AddToQueueCommand { get; }

    /// <summary>Loads this section's cover art. Invoked once when the section is shown.</summary>
    public Func<ArtistAlbumSectionModel, Task>? ArtLoader { get; set; }

    /// <summary>Triggers a one-time cover load for this section, if a loader is set.</summary>
    public void RequestArt()
    {
        if (artRequested || ArtLoader is null)
        {
            return;
        }

        artRequested = true;
        _ = ArtLoader(this);
    }
}
