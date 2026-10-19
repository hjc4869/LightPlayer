using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// A complete shell location: the route, the parameter it was opened with, and the in-page state
/// layered over it (a master-detail page's open detail, the narrow now-playing sheet, the maximized
/// video sheet). Converts to and from a compact query-like text such as
/// <c>albumdetail?t=Album&amp;al=Kind%20Of%20Blue&amp;ar=Miles%20Davis</c> so an
/// <see cref="Services.INavigationHistory"/> head can keep it in a reloadable address.
/// </summary>
/// <param name="VideoKey">
/// The <see cref="MediaReferenceUri"/> of the video whose sheet covers the page, or null when there
/// is none. Only the maximized sheet is addressed; the corner card plays on beside the page.
/// </param>
public sealed record NavigationLocation(
    AppRouteId RouteId,
    object? Parameter = null,
    string? DetailKey = null,
    bool IsSheetExpanded = false,
    string? VideoKey = null)
{
    public string Format()
    {
        var builder = new StringBuilder(RouteId.ToString().ToLowerInvariant());
        var separator = '?';
        foreach (var (key, value) in ParameterPairs().Concat(OverlayPairs()))
        {
            builder.Append(separator).Append(key).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether both locations address the same page, ignoring the in-page state layered over it.
    /// Separates a navigation from an overlay change when a history entry is applied.
    /// </summary>
    public bool SamePage(NavigationLocation other) =>
        RouteId == other.RouteId && ParameterText() == other.ParameterText();

    /// <summary>
    /// Parses a location produced by <see cref="Format"/>. Returns false for anything unrecognized,
    /// including hand-edited addresses, so the caller can fall back to its normal startup page.
    /// </summary>
    public static bool TryParse(string? text, out NavigationLocation location)
    {
        location = new NavigationLocation(AppRouteId.Home);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim().TrimStart('#');
        var separator = span.IndexOf('?');
        var name = (separator < 0 ? span : span[..separator]).ToString();
        if (!Enum.TryParse<AppRouteId>(name, ignoreCase: true, out var routeId) || !Enum.IsDefined(routeId))
        {
            return false;
        }

        var values = separator < 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : ParseQuery(span[(separator + 1)..].ToString());

        location = new NavigationLocation(
            routeId,
            ParseParameter(routeId, values),
            values.GetValueOrDefault("d"),
            values.GetValueOrDefault("s") == "1",
            values.GetValueOrDefault("v"));
        return true;
    }

    private string ParameterText() =>
        string.Join('&', ParameterPairs().Select(pair => pair.Key + '=' + pair.Value));

    private IEnumerable<(string Key, string Value)> ParameterPairs()
    {
        switch (Parameter)
        {
            case string text when text.Length > 0:
                yield return ("p", text);
                break;

            case MediaReference media when media.Path.Length > 0:
                yield return ("p", MediaReferenceUri.Format(media));
                break;

            case PhotoViewerRequest { Photos.Count: > 0 } request:
                var photo = request.Photos[Math.Clamp(request.Index, 0, request.Photos.Count - 1)];
                if (photo.Path.Length > 0)
                {
                    yield return ("p", MediaReferenceUri.Format(photo));
                }

                break;

            // The playing video is addressed by its path, which is all the shell needs to look the
            // file and its folder up again after a reload.
            case VideoPlaybackRequest request when !string.IsNullOrEmpty(request.Video.Value.Path):
                yield return ("p", MediaReferenceUri.Format(
                    new MediaReference(request.Video.SourceId, request.Video.Value.Path)));
                break;

            case MusicFolderReference folder:
                yield return ("ms", folder.SourceId.Value);
                yield return ("mn", folder.SourceName);
                if (folder.Path is not null)
                {
                    yield return ("mf", folder.Path);
                }
                break;

            case VideoFolderReference folder:
                yield return ("vs", folder.SourceId.Value);
                yield return ("vn", folder.SourceName);
                if (folder.IsRecent)
                {
                    yield return ("vr", "1");
                }

                if (!string.IsNullOrEmpty(folder.Path))
                {
                    yield return ("vf", folder.Path);
                }
                break;

            case LibraryItemReference reference:
                foreach (var pair in IdentifierPairs(reference))
                {
                    yield return pair;
                }

                yield return ("sid", reference.SourceId.Value);
                if (reference.UnifiedArtist is { } artist)
                {
                    yield return ("am", string.Join(',', artist.SourceIds.Select(sourceId =>
                        sourceId.Value + ":" + EncodeText(artist.SourceNames.GetValueOrDefault(sourceId, artist.DisplayName)))));
                }
                break;

            case MediaLibraryItemIdentifier id:
                foreach (var pair in IdentifierPairs(id))
                {
                    yield return pair;
                }

                break;
        }
    }

    private static IEnumerable<(string Key, string Value)> IdentifierPairs(MediaLibraryItemIdentifier id)
    {
        yield return ("t", id.Type.ToString());
        if (id.MediaFileEntityId != 0)
        {
            yield return ("f", id.MediaFileEntityId.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(id.ArtistName))
        {
            yield return ("ar", id.ArtistName);
        }

        if (!string.IsNullOrEmpty(id.AlbumName))
        {
            yield return ("al", id.AlbumName);
        }

        if (id.AlbumGroupingKey is not null)
        {
            yield return ("ag", id.AlbumGroupingKey);
        }
    }

    private IEnumerable<(string Key, string Value)> OverlayPairs()
    {
        if (!string.IsNullOrEmpty(DetailKey))
        {
            yield return ("d", DetailKey);
        }

        if (IsSheetExpanded)
        {
            yield return ("s", "1");
        }

        if (!string.IsNullOrEmpty(VideoKey))
        {
            yield return ("v", VideoKey);
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals > 0)
            {
                values[pair[..equals]] = Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }

        return values;
    }

    private static object? ParseParameter(AppRouteId routeId, Dictionary<string, string> values)
    {
        if (routeId == AppRouteId.PhotoViewer && values.TryGetValue("p", out var photoPath))
        {
            return MediaReferenceUri.TryParse(photoPath, out var mediaItem)
                ? mediaItem.Media
                : MediaReference.Local(photoPath);
        }

        if (routeId == AppRouteId.Songs && values.TryGetValue("ms", out var musicSource))
        {
            return new MusicFolderReference(
                LibrarySourceId.FromPersisted(musicSource),
                values.GetValueOrDefault("mf"),
                values.GetValueOrDefault("mn") ?? musicSource);
        }

        if (values.TryGetValue("vs", out var videoSource))
        {
            return new VideoFolderReference(
                LibrarySourceId.FromPersisted(videoSource),
                values.GetValueOrDefault("vf"),
                values.GetValueOrDefault("vn") ?? videoSource)
            {
                IsRecent = values.GetValueOrDefault("vr") == "1",
            };
        }

        if (!values.TryGetValue("t", out var type))
        {
            return values.TryGetValue("p", out var text) && text.Length > 0 ? text : null;
        }

        if (!Enum.TryParse<MediaLibraryItemIdentifier.ItemType>(type, ignoreCase: true, out var itemType))
        {
            return null;
        }

        var sourceId = LibrarySourceId.FromPersisted(values.GetValueOrDefault("sid"));
        UnifiedArtistReference? unifiedArtist = null;
        if (values.TryGetValue("am", out var contributors))
        {
            var names = contributors.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Split(':', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(
                    parts => LibrarySourceId.FromPersisted(parts[0]),
                    parts => DecodeText(parts[1]));
            if (names.Count > 0)
            {
                var displayName = values.GetValueOrDefault("ar") ?? names.Values.First();
                unifiedArtist = new UnifiedArtistReference(
                    FederatedLibraryOrdering.NormalizeArtistName(displayName),
                    displayName,
                    names.Keys.ToArray(),
                    names);
            }
        }

        return new LibraryItemReference
        {
            Type = itemType,
            SourceId = sourceId,
            UnifiedArtist = unifiedArtist,
            MediaFileEntityId = values.TryGetValue("f", out var fileId)
                && int.TryParse(fileId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
            ArtistName = values.GetValueOrDefault("ar"),
            AlbumName = values.GetValueOrDefault("al"),
            AlbumGroupingKey = values.GetValueOrDefault("ag"),
        };
    }

    private static string EncodeText(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string DecodeText(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}
