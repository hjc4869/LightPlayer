using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Reads and writes the Windows Media Playlist format (<c>.wpl</c>), a SMIL XML
/// dialect. Implemented with <see cref="System.Xml.Linq"/>.
/// </summary>
public static class WplPlaylistFormat
{
    /// <summary>Parses a <c>.wpl</c> file into entries. Unknown nodes are skipped.</summary>
    public static IReadOnlyList<PlaylistItemModel> Parse(string filePath)
    {
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
        var items = new List<PlaylistItemModel>();

        var document = XDocument.Load(filePath);
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

            var resolvedPath = ResolvePath(baseDirectory, src);
            items.Add(new PlaylistItemModel(
                Path.GetFileNameWithoutExtension(resolvedPath),
                string.Empty,
                string.Empty,
                resolvedPath,
                TimeSpan.Zero));
        }

        return items;
    }

    /// <summary>Writes the entries as a <c>.wpl</c> file.</summary>
    public static void Write(string filePath, string title, IReadOnlyList<PlaylistItemModel> items)
    {
        var seq = new XElement("seq");
        foreach (var item in items)
        {
            seq.Add(new XElement("media", new XAttribute("src", item.FilePath)));
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

        using var writer = XmlWriter.Create(filePath, settings);
        // WPL uses a custom processing instruction in place of the XML declaration.
        writer.WriteProcessingInstruction("wpl", "version=\"1.0\"");
        smil.WriteTo(writer);
    }

    private static string ResolvePath(string baseDirectory, string entry)
    {
        if (Path.IsPathRooted(entry) || baseDirectory.Length == 0)
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
