using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightMediaRenderer.Contracts;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.MediaLibraryCore.Lyrics;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

namespace LightStudio.LightPlayer.Remote;

public static class RemoteApiDefaults
{
    public const string ListenerPrefix = "http://127.0.0.1:47832/";
    public const string ApiBaseAddress = ListenerPrefix + "api/v1/";

    /// <summary>Relative path of the unauthenticated capability probe the browser client calls first.</summary>
    public const string ServerInfoPath = "server";

    /// <summary>Relative path that exchanges credentials for a bearer token.</summary>
    public const string LoginPath = "auth/login";

    /// <summary>Relative path that exchanges a short-lived pairing code for a bearer token.</summary>
    public const string PairingPath = "auth/pair";

    /// <summary>Relative path that exchanges a valid bearer token for a new one with a fresh expiry.</summary>
    public const string RefreshPath = "auth/refresh";

    /// <summary>
    /// Advertised by servers whose browse route can list the videos in a folder, and not only the
    /// subtitle files. A client without it has nothing to browse but the index.
    /// </summary>
    public const string MediaBrowseCapability = "media-browse/1";

    public const string PhotoSearchCapability = "photo-search/1";

    /// <summary>Value of the browse route's <c>kind</c> parameter that asks for videos.</summary>
    public const string MediaBrowseKind = "media";
}

/// <param name="AuthenticationRequired">
/// Whether every route other than the probe and login needs an <c>Authorization</c> header.
/// </param>
/// <param name="Capabilities">
/// Optional features this server has. Absent on servers built before a feature existed, which is
/// what a client checks before using one.
/// </param>
/// <param name="Transcoding">
/// What this server can re-encode to, or null when it will not re-encode anything. A client offers
/// the user nothing it cannot actually be given.
/// </param>
/// <param name="ProxyOnly">
/// Whether this host has no library of its own and only relays calls to the target a client names.
/// A browser client asks its own origin this before offering it as a library.
/// </param>
/// <param name="ApiImplemented">Whether this host implements the server API.</param>
public sealed record RemoteServerInfo(
    string Name,
    string ApiVersion,
    bool AuthenticationRequired = false,
    string[]? Capabilities = null,
    RemoteTranscodeCapabilities? Transcoding = null,
    bool ProxyOnly = false,
    bool ApiImplemented = true);

public sealed record RemoteLoginRequest(string Username, string Password);

public sealed record RemotePairingRequest(string Token);

/// <param name="ExpiresAtUnixSeconds">Absolute expiry; the client re-authenticates once it passes.</param>
public sealed record RemoteLoginResult(string Token, long ExpiresAtUnixSeconds);

public sealed record RemoteProblem(string Code, string Title, int Status, string? Detail = null);

public sealed record RemotePathRequest(string Path);

/// <param name="IsFolder">Whether the entry is browsed into rather than chosen.</param>
/// <param name="ExternalAudioPaths">Sidecar audio tracks beside a video, absent for anything else.</param>
/// <param name="ExternalSubtitlePaths">Sidecar subtitle files beside a video.</param>
/// <param name="SizeInBytes">File size, which is all a picture's tile has to say about it.</param>
/// <param name="MediaKind">How a video entry is opened; folders and pictures use <see cref="VideoMediaKind.File"/>.</param>
/// <param name="DiscTitle">One-based DVD title selected during indexing; zero otherwise.</param>
/// <param name="PreviewPaths">Up to nine descendant media paths for a folder collage.</param>
/// <param name="ItemCount">Number of directly listed items in a folder, when available.</param>
public sealed record RemoteFileEntry(
    string Name,
    string Path,
    bool IsFolder,
    string[]? ExternalAudioPaths = null,
    string[]? ExternalSubtitlePaths = null,
    long SizeInBytes = 0,
    VideoMediaKind MediaKind = VideoMediaKind.File,
    int DiscTitle = 0,
    string[]? PreviewPaths = null,
    int? ItemCount = null,
    DateTimeOffset LastModifiedUtc = default,
    DateTimeOffset? CreatedUtc = null);

/// <param name="Path">The folder listed, or null for the video library roots themselves.</param>
/// <param name="Ancestors">
/// The chain from the containing library root down to <paramref name="Path"/>, so a client can draw
/// a breadcrumb without guessing where the library begins.
/// </param>
/// <param name="PreviewPaths">Up to nine media paths for a collage of the listed folder or library roots.</param>
public sealed record RemoteBrowseResult(
    string? Path,
    RemoteFileEntry[] Ancestors,
    RemoteFileEntry[] Entries,
    string[]? PreviewPaths = null);

public sealed record RemoteMediaInfo(
    string Album,
    string AlbumArtist,
    string Artist,
    string Comments,
    string Composer,
    string Copyright,
    string Date,
    string Description,
    string DiscNumber,
    long DurationMilliseconds,
    string Genre,
    string Grouping,
    string Performer,
    string Title,
    string TotalDiscs,
    string TotalTracks,
    string TrackNumber,
    Dictionary<string, string> AllProperties)
{
    public static RemoteMediaInfo FromModel(MediaInfo value) => new(
        value.Album,
        value.AlbumArtist,
        value.Artist,
        value.Comments,
        value.Composer,
        value.Copyright,
        value.Date,
        value.Description,
        value.DiscNumber,
        (long)value.Duration.TotalMilliseconds,
        value.Genre,
        value.Grouping,
        value.Performer,
        value.Title,
        value.TotalDiscs,
        value.TotalTracks,
        value.TrackNumber,
        value.AllProperties is null
            ? []
            : new Dictionary<string, string>(value.AllProperties, StringComparer.Ordinal));

    public MediaInfo ToModel() => new()
    {
        Album = Album,
        AlbumArtist = AlbumArtist,
        Artist = Artist,
        Comments = Comments,
        Composer = Composer,
        Copyright = Copyright,
        Date = Date,
        Description = Description,
        DiscNumber = DiscNumber,
        Duration = TimeSpan.FromMilliseconds(DurationMilliseconds),
        Genre = Genre,
        Grouping = Grouping,
        Performer = Performer,
        Title = Title,
        TotalDiscs = TotalDiscs,
        TotalTracks = TotalTracks,
        TrackNumber = TrackNumber,
        AllProperties = AllProperties,
    };
}

public sealed record RemoteHistoryItem(
    string Title,
    string Artist,
    string Album,
    long DurationMilliseconds,
    string FilePath,
    int? StartTimeMs)
{
    public static RemoteHistoryItem FromModel(MusicPlaybackItemModel value) => new(
        value.Title,
        value.Artist,
        value.Album,
        (long)value.Duration.TotalMilliseconds,
        value.FilePath,
        value.StartTimeMs);

    public MusicPlaybackItemModel ToModel() => new(
        Title,
        Artist,
        Album,
        TimeSpan.FromMilliseconds(DurationMilliseconds),
        FilePath,
        StartTimeMs);
}

/// <param name="Cursor">Scan event cursor taken before the scan was requested; its <c>completed</c> event follows it.</param>
public sealed record RemoteScanStarted(long Cursor);

public sealed record RemoteScanEvent(
    long Id,
    string Type,
    string[]? Folders = null,
    int? IndexedCount = null,
    LibraryScanWarning? Warning = null,
    int? WarningCount = null)
{
    public bool? HasChanges { get; init; }

    public int? VideoIndexedCount { get; init; }

    public int? ImageIndexedCount { get; init; }

    public bool? Failed { get; init; }
}

/// <param name="Cursor">Value to pass as <c>after</c> on the next request: the newest event ID the server has issued.</param>
public sealed record RemoteScanEvents(RemoteScanEvent[] Events, long Cursor);

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RemoteServerInfo))]
[JsonSerializable(typeof(PhotoSearchPage))]
[JsonSerializable(typeof(RemoteLoginRequest))]
[JsonSerializable(typeof(RemotePairingRequest))]
[JsonSerializable(typeof(RemoteLoginResult))]
[JsonSerializable(typeof(RemoteProblem))]
[JsonSerializable(typeof(RemotePathRequest))]
[JsonSerializable(typeof(RemoteFileEntry))]
[JsonSerializable(typeof(RemoteBrowseResult))]
[JsonSerializable(typeof(RemoteMediaInfo))]
[JsonSerializable(typeof(MediaFileProperties))]
[JsonSerializable(typeof(MediaProbeReport))]
[JsonSerializable(typeof(RemoteHistoryItem))]
[JsonSerializable(typeof(RemoteHistoryItem[]))]
[JsonSerializable(typeof(RemoteScanStarted))]
[JsonSerializable(typeof(RemoteScanEvent))]
[JsonSerializable(typeof(RemoteScanEvents))]
[JsonSerializable(typeof(RemoteStreamHello))]
[JsonSerializable(typeof(RemotePacketStreamRequest))]
[JsonSerializable(typeof(RemoteStreamSubtitleRequest))]
[JsonSerializable(typeof(RemoteStreamSubtitle))]
[JsonSerializable(typeof(RemoteStreamManifest))]
[JsonSerializable(typeof(RemoteStreamTrack))]
[JsonSerializable(typeof(RemoteStreamChapter))]
[JsonSerializable(typeof(RemoteTranscodeRequest))]
[JsonSerializable(typeof(RemoteTrackTranscodeRequest))]
[JsonSerializable(typeof(RemoteTranscodeCapabilities))]
[JsonSerializable(typeof(LightStudio.FfmpegShim.Media.CodecParameters))]
[JsonSerializable(typeof(DbMediaFile))]
[JsonSerializable(typeof(DbMediaFile[]))]
[JsonSerializable(typeof(PagedResult<DbMediaFile>))]
[JsonSerializable(typeof(MusicFolderListing))]
[JsonSerializable(typeof(DbVideoFile))]
[JsonSerializable(typeof(DbVideoFile[]))]
[JsonSerializable(typeof(PagedResult<DbVideoFile>))]
[JsonSerializable(typeof(DbImageFile[]))]
[JsonSerializable(typeof(IndexedMediaFile[]))]
[JsonSerializable(typeof(PagedResult<IndexedMediaFile>))]
[JsonSerializable(typeof(DbVideoAudioStream))]
[JsonSerializable(typeof(VideoFolderCount))]
[JsonSerializable(typeof(VideoFolderCount[]))]
[JsonSerializable(typeof(MediaLibraryAlbum))]
[JsonSerializable(typeof(MediaLibraryAlbum[]))]
[JsonSerializable(typeof(PagedResult<MediaLibraryAlbum>))]
[JsonSerializable(typeof(MediaLibraryArtist))]
[JsonSerializable(typeof(MediaLibraryArtist[]))]
[JsonSerializable(typeof(MediaLibraryArtistBrowseItem))]
[JsonSerializable(typeof(MediaLibraryArtistBrowseItem[]))]
[JsonSerializable(typeof(PagedResult<MediaLibraryArtistBrowseItem>))]
[JsonSerializable(typeof(LibraryBrowseIndex))]
[JsonSerializable(typeof(LibraryBrowseItemsRequest))]
[JsonSerializable(typeof(LibrarySearchResults))]
[JsonSerializable(typeof(LibraryScanWarning))]
[JsonSerializable(typeof(LibraryScanWarning[]))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string[]))]
public sealed partial class RemoteApiJsonContext : JsonSerializerContext;