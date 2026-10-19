using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Artist detail page: artist header (name, album count, total duration) with a
/// set of playback, queue, and playlist actions, followed by one block per album (cover, title, actions
/// and the album's ordered track rows).
/// </summary>
public sealed partial class ArtistDetailPageViewModel : SelectionPageViewModelBase, INowPlayingHighlightTarget, IInlineDetailPage, IDisposable
{
    private const int HeaderBackdropWidth = 512;
    private const float HeaderBackdropBlurSigma = 32f;

    private readonly LibraryItemReference identifier;
    private readonly LibraryCommands commands;
    private readonly AlbumArtService albumArt;
    private readonly IUnifiedLibraryQueryService libraryQuery;
    private MediaItemKey? nowPlaying;

    // All of the artist's tracks in album-then-track order.
    private IReadOnlyList<TrackRowModel> allTracks = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    private bool isLoading = true;

    // A failed load leaves the page with nothing to show, which is indistinguishable from an artist
    // that has no albums, so failures get their own state carrying the reason and a way to retry.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    private bool hasLoadError;

    [ObservableProperty]
    private string loadErrorText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPartialFailures))]
    private string partialFailureText = string.Empty;

    // Its own header back button. Shown only when the page is on its own route and the shell has no
    // back button of its own. The narrow layout, the Apple layout, and hosts with their own back
    // navigation (Android, the browser) hide it in favor of a single global back affordance. When
    // hosted inline in the artist list page, the host drives this instead (see ListItemsPageViewModel).
    [ObservableProperty]
    private bool showBackButton = !PlatformServices.HostProvidesBackNavigation;

    [ObservableProperty]
    private string artistName = string.Empty;

    [ObservableProperty]
    private string metaLine = string.Empty;

    [ObservableProperty]
    private Bitmap? headerBackground;

    [ObservableProperty]
    private IReadOnlyList<ArtistAlbumSectionModel> albumSections = [];

    public ArtistDetailPageViewModel(
        MediaLibraryItemIdentifier identifier,
        LibraryCommands commands,
        AlbumArtService albumArt,
        IUnifiedLibraryQueryService libraryQuery)
        : this(identifier, commands, albumArt, libraryQuery, onBack: null)
    {
    }

    public ArtistDetailPageViewModel(
        MediaLibraryItemIdentifier identifier,
        LibraryCommands commands,
        AlbumArtService albumArt,
        IUnifiedLibraryQueryService libraryQuery,
        Action? onBack)
    {
        this.identifier = LibraryItemReference.Normalize(identifier);
        this.commands = commands;
        this.albumArt = albumArt;
        this.libraryQuery = libraryQuery;
        TitleKey = nameof(Strings.Resources.Common_ColumnArtist);

        // When hosted inline (artist list page) back returns to the list; on its own route it
        // falls back to the shell's navigation stack.
        BackCommand = new RelayCommand(() =>
        {
            if (!TryHandleBack())
            {
                (onBack ?? (() => this.commands.GoBack?.Invoke())).Invoke();
            }
        });
        PlayCommand = new RelayCommand(() => this.commands.PlayTracks?.Invoke(allTracks), () => allTracks.Count > 0);
        ShuffleCommand = new RelayCommand(() => this.commands.ShuffleTracks?.Invoke(allTracks), () => allTracks.Count > 0);
        AddToQueueCommand = new RelayCommand(() => this.commands.AddTracksToQueue?.Invoke(allTracks), () => allTracks.Count > 0);
        AddToPlaylistCommand = new RelayCommand(() => this.commands.AddTracksToPlaylist?.Invoke(allTracks), () => allTracks.Count > 0);
        RetryCommand = new AsyncRelayCommand(LoadAsync);
        MultiSelectionActions.AddMusicActions(Selection, commands);
        PageSearch.SetSource(new PageSearchSource(GetSearchEntries, [], this,
            isLoading: () => IsLoading, hasPendingItems: () => HasLoadError || HasPartialFailures));
    }

    private IEnumerable<PageSearchEntry> GetSearchEntries()
    {
        if (!ShowContent)
        {
            yield break;
        }

        yield return new PageSearchEntry(this, string.Join('\n', ArtistName, MetaLine));
        foreach (var section in AlbumSections)
        {
            yield return new PageSearchEntry(section, string.Join('\n', section.Title, section.Year));
            foreach (var entry in PageSearchSource.Entries(section.Tracks, [section]))
            {
                yield return entry;
            }
        }
    }

    // A standalone (route-hosted) detail page hides its own back button while the shell shows one.
    // Inline-hosted copies never receive shell layout changes (their host owns ShowBackButton), so
    // this stays inert there.
    protected override void OnHasShellBackButtonChanged(bool value) =>
        ShowBackButton = !PlatformServices.HostProvidesBackNavigation && !value;

    public bool ShowContent => !IsLoading && !HasLoadError;

    public bool HasPartialFailures => PartialFailureText.Length > 0;

    /// <inheritdoc />
    public void SetNowPlaying(MediaItemKey? mediaItem)
    {
        nowPlaying = mediaItem;
        NowPlayingHighlight.Apply(allTracks, mediaItem);
    }

    public IRelayCommand BackCommand { get; }

    public IRelayCommand PlayCommand { get; }

    public IRelayCommand ShuffleCommand { get; }

    public IRelayCommand AddToQueueCommand { get; }

    public IRelayCommand AddToPlaylistCommand { get; }

    public IAsyncRelayCommand RetryCommand { get; }

    public async Task LoadAsync()
    {
        Selection.Exit();
        IsLoading = true;
        HasLoadError = false;
        try
        {
            var artist = identifier.UnifiedArtist ?? new UnifiedArtistReference(
                identifier.ArtistName ?? string.Empty,
                identifier.ArtistName ?? string.Empty,
                [identifier.SourceId],
                new Dictionary<LibrarySourceId, string>
                {
                    [identifier.SourceId] = identifier.ArtistName ?? string.Empty,
                });
            ArtistName = LibrarySortKeys.Fallback(artist.DisplayName, Strings.Resources.Common_UnknownArtist);

            var albumEntities = await libraryQuery.GetArtistAlbumsAsync(artist);
            var files = await libraryQuery.GetArtistTracksAsync(artist);
            PartialFailureText = FormatFailures(
                albumEntities.SourceFailures.Concat(files.SourceFailures));

            var sections = BuildSections(albumEntities.Items, files.Items);
            var discarded = AlbumSections;
            AlbumSections = sections;
            foreach (var previous in discarded)
            {
                previous.ReleaseArt();
            }
            allTracks = sections.SelectMany(section => section.Tracks).ToList();
            NowPlayingHighlight.Apply(allTracks, nowPlaying);

            var total = files.Items.Aggregate(TimeSpan.Zero, (sum, file) => sum + file.Value.Duration);
            MetaLine = BuildMetaLine(sections.Count, total);
            NotifyCommands();

            foreach (var section in sections)
            {
                section.RequestArt();
            }
        }
        catch (Exception ex)
        {
            AlbumSections = [];
            allTracks = [];
            NotifyCommands();
            LoadErrorText = AppErrorReporter.LogAndGetMessage("Library", "Could not load the artist page.", ex);
            HasLoadError = true;
            PartialFailureText = string.Empty;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void NotifyCommands()
    {
        PlayCommand.NotifyCanExecuteChanged();
        ShuffleCommand.NotifyCanExecuteChanged();
        AddToQueueCommand.NotifyCanExecuteChanged();
        AddToPlaylistCommand.NotifyCanExecuteChanged();
    }

    // Groups the artist's tracks into one section per album, ordered newest first.
    private List<ArtistAlbumSectionModel> BuildSections(
        IReadOnlyList<Sourced<MediaLibraryAlbum>> albumEntities,
        IReadOnlyList<Sourced<DbMediaFile>> files)
    {
        var entityByAlbum = new Dictionary<(LibrarySourceId SourceId, string Title, string Artist, string Group), MediaLibraryAlbum>();
        foreach (var entity in albumEntities)
        {
            entityByAlbum[(entity.SourceId, entity.Value.Title ?? string.Empty,
                entity.Value.ArtistForQuery ?? string.Empty, entity.Value.AlbumGroupingKey ?? string.Empty)] = entity.Value;
        }

        var sections = new List<ArtistAlbumSectionModel>();
        foreach (var group in files.GroupBy(file => (
            file.SourceId,
            Title: file.Value.Album ?? string.Empty,
            Artist: file.Value.Album is null ? string.Empty : file.Value.AlbumArtist ?? string.Empty,
            Group: file.Value.Album is null ? string.Empty : file.Value.AlbumGroupingKey ?? string.Empty)))
        {
            entityByAlbum.TryGetValue(group.Key, out var entity);
            var first = group.First();

            var rows = group
                .Select(file => TrackRowFactory.CreateRow(file, commands.TrackActions))
                .OrderBy(row => row.DiscNumber)
                .ThenBy(row => row.TrackNumber)
                .ToList();
            IReadOnlyList<TrackRowModel> sectionTracks = rows;
            foreach (var row in rows)
            {
                row.HideArtist = true;
                row.PlaybackContext = () => sectionTracks;
            }

            var title = LibrarySortKeys.Fallback(entity?.Title ?? first.Value.Album, Strings.Resources.Common_UnknownAlbum);
            var year = AlbumTileFactory.YearLabel(entity?.Date ?? first.Value.Date);
            var section = new ArtistAlbumSectionModel(
                title,
                year,
                rows,
                new RelayCommand(() => commands.PlayTracks?.Invoke(sectionTracks)),
                new RelayCommand(() => commands.ShuffleTracks?.Invoke(sectionTracks)),
                new RelayCommand(() => commands.AddTracksToQueue?.Invoke(sectionTracks)),
                new RelayCommand(() => commands.AddTracksToPlaylist?.Invoke(sectionTracks)));

            var firstFile = entity?.FirstFileInAlbum ?? first.Value.Path;
            var media = new MediaReference(first.SourceId, firstFile ?? string.Empty);
            section.ArtLoader = (target, token) => LoadSectionArtAsync(target, media, token);

            sections.Add(section);
        }

        return sections
            .OrderByDescending(section => LibrarySortKeys.ParseLeadingInt(section.Year))
            .ThenBy(section => section.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task LoadSectionArtAsync(
        ArtistAlbumSectionModel section,
        MediaReference media,
        CancellationToken cancellationToken)
    {
        // The first album's artwork also backs the header, so its backdrop comes from the same read of the picture.
        Bitmap? bitmap;
        Bitmap? backdrop = null;
        if (ReferenceEquals(AlbumSections.FirstOrDefault(), section))
        {
            var artwork = await albumArt.GetHighQualityTrackArtWithBackdropAsync(
                media, HeaderBackdropWidth, HeaderBackdropBlurSigma, cancellationToken).ConfigureAwait(false);
            bitmap = artwork?.Cover;
            backdrop = artwork?.Backdrop;
        }
        else
        {
            bitmap = await albumArt.GetHighQualityTrackArtAsync(media, cancellationToken).ConfigureAwait(false);
        }

        if (backdrop is null)
        {
            if (bitmap is not null)
            {
                AlbumArtService.ApplyOnUiThread(bitmap, art => section.CoverImage = art, cancellationToken);
            }

            return;
        }

        if (bitmap is null)
        {
            AlbumArtService.ApplyOnUiThread(backdrop, ApplyHeaderBackground, cancellationToken);
            return;
        }

        AlbumArtService.ApplyOnUiThread([bitmap, backdrop], images =>
        {
            section.CoverImage = images[0];
            ApplyHeaderBackground((Bitmap)images[1]);
        }, cancellationToken);

        void ApplyHeaderBackground(Bitmap image)
        {
            if (ReferenceEquals(AlbumSections.FirstOrDefault(), section))
            {
                HeaderBackground = image;
            }
            else
            {
                image.Dispose();
            }
        }
    }

    private static string BuildMetaLine(int albumCount, TimeSpan total)
    {
        var parts = new List<string>
        {
            albumCount == 1 ? Strings.Resources.Common_OneAlbum : string.Format(Strings.Resources.Common_AlbumsFormat, albumCount),
        };

        if (total > TimeSpan.Zero)
        {
            parts.Add(LibrarySortKeys.FormatDuration(total));
        }

        return string.Join("  ·  ", parts);
    }

    partial void OnAlbumSectionsChanged(IReadOnlyList<ArtistAlbumSectionModel> value)
    {
        HeaderBackground = null;
        Selection.SetItems(value.SelectMany(section => section.Tracks));
    }

    partial void OnHeaderBackgroundChanged(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    public void Dispose()
    {
        PageSearch.Close();
        Selection.Dispose();
        HeaderBackground = null;
        foreach (var section in AlbumSections)
        {
            section.ReleaseArt();
        }
    }

    private static string FormatFailures(IEnumerable<UnifiedSourceFailure> failures) =>
        string.Join(Environment.NewLine, failures
            .GroupBy(failure => failure.SourceId)
            .Select(group => $"{group.First().SourceName}: {group.First().Message}"));
}
