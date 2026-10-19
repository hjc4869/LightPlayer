using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LocalServices;

/// <summary>
/// Reads and writes the Windows Media Playlist format (<c>.wpl</c>), a SMIL XML
/// dialect. Implemented with <see cref="System.Xml.Linq"/>.
/// </summary>
public static class WplPlaylistFormat
{
    /// <summary>Parses <c>.wpl</c> content into entries. Unknown nodes are skipped.</summary>
    public static IReadOnlyList<PlaylistItemModel> Parse(string content, string? baseDirectory = null)
    {
        var items = new List<PlaylistItemModel>();

        var document = XDocument.Parse(content);
        var body = document.Root?.Element("body");
        var seq = body?.Element("seq");
        if (seq is null)
        {
            return items;
        }

        foreach (var media in seq.Elements("media"))
        {
            var src = media.Attribute("src")?.Value;
            if (string.IsNullOrWhiteSpace(src))
            {
                continue;
            }

            var mediaItem = MediaReferenceUri.TryParse(src, out var parsed)
                ? parsed
                : new MediaItemKey(MediaReference.Local(ResolvePath(baseDirectory, src)), null);
            var resolvedPath = mediaItem.Media.Path;
            items.Add(new PlaylistItemModel(
                Path.GetFileNameWithoutExtension(resolvedPath),
                string.Empty,
                string.Empty,
                resolvedPath,
                TimeSpan.Zero,
                mediaItem.StartTimeMs,
                mediaItem.Media.SourceId));
        }

        return items;
    }

    /// <summary>Creates <c>.wpl</c> content.</summary>
    public static string Write(string title, IReadOnlyList<PlaylistItemModel> items)
    {
        var seq = new XElement("seq");
        foreach (var item in items)
        {
            var source = item.SourceId == LibrarySourceId.Local
                ? item.FilePath
                : MediaReferenceUri.Format(item.Media, item.StartTimeMs);
            seq.Add(new XElement("media", new XAttribute("src", source)));
        }

        var smil = new XElement("smil",
            new XElement("head",
                new XElement("meta",
                    new XAttribute("name", "Generator"),
                    new XAttribute("content", "LightStudio.LightPlayer")),
                new XElement("title", title ?? string.Empty)),
            new XElement("body", seq));

        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = true,
        };

        var builder = new StringBuilder();
        using var writer = XmlWriter.Create(builder, settings);
        // WPL uses a custom processing instruction in place of the XML declaration.
        writer.WriteProcessingInstruction("wpl", "version=\"1.0\"");
        smil.WriteTo(writer);
        writer.Flush();
        return builder.ToString();
    }

    private static string ResolvePath(string? baseDirectory, string entry)
    {
        if (Path.IsPathRooted(entry) || string.IsNullOrEmpty(baseDirectory))
        {
            return entry;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(baseDirectory, entry));
        }
        catch (ArgumentException)
        {
            return entry;
        }
    }
}
