using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// The CJK faces Avalonia and the subtitle renderer fall back on when a glyph is missing.
/// </summary>
/// <remarks>
/// The browser exposes no system fonts. The LightStudio.Fonts package supplies separate static
/// files under <c>wwwroot/fonts</c> that <c>browser-fonts.js</c> downloads while the runtime starts.
/// </remarks>
[SupportedOSPlatform("browser")]
internal static partial class BrowserFonts
{
    /// <summary>Collection key; <see cref="FontManager"/> only accepts the <c>fonts:</c> scheme.</summary>
    private static readonly Uri CollectionKey = new("fonts:LightPlayerFallback", UriKind.Absolute);

    /// <summary>Family to name in <see cref="FontManagerOptions.FontFallbacks"/>.</summary>
    internal const string CjkFontFamily = "fonts:LightPlayerFallback#Noto Sans CJK SC";

    private static byte[][] faces = [];

    /// <summary>The downloaded font files, empty when the download failed.</summary>
    internal static IReadOnlyList<byte[]> Faces => faces;

    /// <summary>Waits for the download JavaScript started at boot and copies it into managed memory.</summary>
    internal static async Task LoadAsync()
    {
        try
        {
            int count = await Interop.BeginLoadAsync();
            var loaded = new byte[count][];
            for (int index = 0; index < count; index++)
            {
                loaded[index] = new byte[Interop.Size(index)];
                Interop.Copy(index, loaded[index]);
            }

            Interop.Release();
            faces = loaded;
        }
        catch (Exception exception)
        {
            AppLog.Warning(
                "Web.Fonts",
                "The fallback fonts could not be downloaded; text outside the Latin range will not render.",
                exception);
        }
    }

    /// <summary>
    /// Publishes the downloaded faces to Avalonia. Must run after the platform is set up, because
    /// the collection resolves the font manager as it is constructed.
    /// </summary>
    internal static void Register()
    {
        if (faces.Length == 0)
        {
            return;
        }

        var collection = new DownloadedFontCollection(CollectionKey);
        foreach (byte[] face in faces)
        {
            using var stream = new MemoryStream(face, writable: false);
            if (!collection.TryAddGlyphTypeface(stream, out _))
            {
                AppLog.Warning("Web.Fonts", "A downloaded fallback font could not be read.");
            }
        }

        FontManager.Current.AddFontCollection(collection);
    }

    private sealed class DownloadedFontCollection(Uri key) : FontCollectionBase
    {
        public override Uri Key { get; } = key;
    }

    private static partial class Interop
    {
        /// <summary>Awaits the in-flight download and returns the number of faces.</summary>
        [JSImport("beginLoad", "BrowserFonts")]
        internal static partial Task<int> BeginLoadAsync();

        [JSImport("size", "BrowserFonts")]
        internal static partial int Size(int index);

        [JSImport("copy", "BrowserFonts")]
        internal static partial void Copy(int index, [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> buffer);

        [JSImport("release", "BrowserFonts")]
        internal static partial void Release();
    }
}
