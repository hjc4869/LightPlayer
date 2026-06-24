using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Artist detail page: artist header (name, album count, total duration) with a
/// shuffle-all action, followed by one block per album (cover, title, actions
/// and the album's ordered track rows).
/// </summary>
public sealed partial class ArtistDetailPageViewModel : PageViewModelBase
{
    private readonly MediaLibraryItemIdentifier identifier;
    private readonly LibraryCommands commands;
    private readonly AlbumArtService albumArt;

    // All of the artist's tracks in album-then-track order, used by shuffle all.
    private IReadOnlyList<TrackRowModel> allTracks = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    private bool isLoading = true;

    [ObservableProperty]
    private string artistName = string.Empty;

    [ObservableProperty]
    private string metaLine = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ArtistAlbumSectionModel> albumSections = [];

    public ArtistDetailPageViewModel(MediaLibraryItemIdentifier identifier, LibraryCommands commands, AlbumArtService albumArt)
    {
        this.identifier = identifier;
        this.commands = commands;
        this.albumArt = albumArt;
        Title = "Artist";

        BackCommand = new RelayCommand(() => this.commands.GoBack?.Invoke());
        ShuffleCommand = new RelayCommand(() => this.commands.ShuffleTracks?.Invoke(allTracks), () => allTracks.Count > 0);
    }

    public bool ShowContent => !IsLoading;

    public IRelayCommand BackCommand { get; }

    public IRelayCommand ShuffleCommand { get; }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var artist = await Task.Run(() => LibraryHelper.GetArtistByNameAsync(identifier.ArtistName));
            ArtistName = LibrarySortKeys.Fallback(artist.Name, "Unknown Artist");
            Title = ArtistName;

            var albumEntities = await Task.Run(() => LibraryHelper.ListAlbumsAsync(artist.Name));
            var files = await Task.Run(() => LibraryHelper.GetMediaFileByArtistAsync(artist));

            var sections = BuildSections(albumEntities, files);
            AlbumSections = sections;
            allTracks = sections.SelectMany(section => section.Tracks).ToList();

            var total = files.Aggregate(TimeSpan.Zero, (sum, file) => sum + file.Duration);
            MetaLine = BuildMetaLine(sections.Count, total);
            ShuffleCommand.NotifyCanExecuteChanged();

            foreach (var section in sections)
            {
                section.RequestArt();
            }
        }
        catch (Exception)
        {
            AlbumSections = [];
            allTracks = [];
            ShuffleCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Groups the artist's tracks into one section per album, ordered newest first.
    private List<ArtistAlbumSectionModel> BuildSections(
        IReadOnlyList<MediaLibraryAlbum> albumEntities,
        IReadOnlyList<DbMediaFile> files)
    {
        var entityByTitle = new Dictionary<string, MediaLibraryAlbum>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var entity in albumEntities)
        {
            entityByTitle[entity.Title ?? string.Empty] = entity;
        }

        var sections = new List<ArtistAlbumSectionModel>();
        foreach (var group in files.GroupBy(file => file.Album ?? string.Empty, StringComparer.CurrentCultureIgnoreCase))
        {
            entityByTitle.TryGetValue(group.Key, out var entity);
            var first = group.First();

            var rows = group
                .Select(file => TrackRowFactory.CreateRow(file, commands.TrackActions))
                .OrderBy(row => row.DiscNumber)
                .ThenBy(row => row.TrackNumber)
                .ToList();
            IReadOnlyList<TrackRowModel> sectionTracks = rows;
            foreach (var row in rows)
            {
                row.PlaybackContext = () => sectionTracks;
            }

            var title = LibrarySortKeys.Fallback(entity?.Title ?? group.Key, "Unknown Album");
            var year = AlbumTileFactory.YearLabel(entity?.Date ?? first.Date);
            var section = new ArtistAlbumSectionModel(
                title,
                year,
                rows,
                new RelayCommand(() => commands.PlayTracks?.Invoke(sectionTracks)),
                new RelayCommand(() => commands.AddTracksToQueue?.Invoke(sectionTracks)));

            var artistKey = entity?.ArtistForQuery ?? entity?.Artist ?? first.AlbumArtist ?? first.Artist ?? string.Empty;
            var albumKey = entity?.Title ?? group.Key;
            var firstFile = entity?.FirstFileInAlbum ?? first.Path;
            section.ArtLoader = target => LoadSectionArtAsync(target, artistKey, albumKey, firstFile);

            sections.Add(section);
        }

        return sections
            .OrderByDescending(section => LibrarySortKeys.ParseLeadingInt(section.Year))
            .ThenBy(section => section.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task LoadSectionArtAsync(ArtistAlbumSectionModel section, string artistKey, string albumKey, string? firstFile)
    {
        Bitmap? bitmap = await albumArt.GetAlbumArtAsync(artistKey, albumKey, firstFile);
        if (bitmap is null)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            section.CoverImage = bitmap;
        }
        else
        {
            Dispatcher.UIThread.Post(() => section.CoverImage = bitmap);
        }
    }

    private static string BuildMetaLine(int albumCount, TimeSpan total)
    {
        var parts = new List<string>
        {
            albumCount == 1 ? "1 album" : $"{albumCount} albums",
        };

        if (total > TimeSpan.Zero)
        {
            parts.Add(LibrarySortKeys.FormatDuration(total));
        }

        return string.Join("  ·  ", parts);
    }
}
