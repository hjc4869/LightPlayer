using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Threading;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>
/// Registers the system font at the optical size and tracking AppKit uses for regular control text.
/// </summary>
/// <remarks>
/// Avalonia shapes variable fonts at their default instance, which for SF is the display optical
/// size; HarfBuzz pins the text instance into a static face that Skia and shaping both read.
/// Avalonia's shaper also never sets a point size, so the font's size-specific tracking is published
/// separately as letter spacing.
/// </remarks>
internal static partial class MacSystemTextFont
{
    private const string HarfBuzz = "libHarfBuzzSharp";
    private const string SystemFontPath = "/System/Library/Fonts/SFNS.ttf";
    private const string ResourceKey = "AppMacControlFontFamily";
    private const string LetterSpacingKey = "AppMacControlLetterSpacing";
    private const float ControlTextOpticalSize = 17;
    private const double ControlTextSize = 13;
    private static readonly float[] Weights = [400, 600, 700];

    private static readonly Uri CollectionKey = new("fonts:LightPlayerMacText", UriKind.Absolute);

    public static void Register() => _ = RegisterAsync();

    private static async Task RegisterAsync()
    {
        try
        {
            var faces = await Task.Run(() => Array.ConvertAll(Weights, CreateTextInstance));
            if (Array.IndexOf(faces, null) >= 0)
            {
                AppLog.Warning("Desktop.Fonts", "The macOS control text font instances could not be created.");
                return;
            }

            var letterSpacing = TrackingTable.Read(faces[0]!) is { } tracking
                ? tracking.At(ControlTextSize) - tracking.At(TrackingTable.ShapingSize)
                : 0;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var collection = new TextFontCollection(CollectionKey);
                string? familyName = null;
                foreach (var face in faces)
                {
                    using var stream = new MemoryStream(face!, writable: false);
                    if (!collection.TryAddGlyphTypeface(stream, out var typeface))
                    {
                        AppLog.Warning("Desktop.Fonts", "A macOS control text font instance could not be read.");
                        return;
                    }

                    familyName = typeface.FamilyName;
                }

                FontManager.Current.AddFontCollection(collection);
                if (Application.Current is { } app)
                {
                    app.Resources[LetterSpacingKey] = letterSpacing;
                    app.Resources[ResourceKey] = new FontFamily($"{CollectionKey}#{familyName}");
                }
            });
        }
        catch (Exception exception)
        {
            AppLog.Warning("Desktop.Fonts", "The macOS control text font could not be registered.", exception);
        }
    }

    private static byte[]? CreateTextInstance(float weight)
    {
        var blob = hb_blob_create_from_file_or_fail(SystemFontPath);
        if (blob == IntPtr.Zero)
        {
            return null;
        }

        var face = hb_face_create(blob, 0);
        var input = hb_subset_input_create_or_fail();
        var instance = IntPtr.Zero;
        var output = IntPtr.Zero;
        try
        {
            if (input == IntPtr.Zero)
            {
                return null;
            }

            hb_subset_input_keep_everything(input);
            foreach (var axis in new[] { Tag('w', 'd', 't', 'h'), Tag('G', 'R', 'A', 'D') })
            {
                hb_subset_input_pin_axis_to_default(input, face, axis);
            }

            if (hb_subset_input_pin_axis_location(input, face, Tag('w', 'g', 'h', 't'), weight) == 0 ||
                hb_subset_input_pin_axis_location(input, face, Tag('o', 'p', 's', 'z'), ControlTextOpticalSize) == 0)
            {
                return null;
            }

            instance = hb_subset_or_fail(face, input);
            if (instance == IntPtr.Zero)
            {
                return null;
            }

            output = hb_face_reference_blob(instance);
            var data = hb_blob_get_data(output, out var length);
            if (data == IntPtr.Zero || length == 0)
            {
                return null;
            }

            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);
            return bytes;
        }
        finally
        {
            if (output != IntPtr.Zero)
            {
                hb_blob_destroy(output);
            }

            if (instance != IntPtr.Zero)
            {
                hb_face_destroy(instance);
            }

            if (input != IntPtr.Zero)
            {
                hb_subset_input_destroy(input);
            }

            hb_face_destroy(face);
            hb_blob_destroy(blob);
        }
    }

    private static uint Tag(char a, char b, char c, char d) => (uint)(a << 24 | b << 16 | c << 8 | d);

    /// <summary>The normal horizontal track of an AAT <c>trak</c> table, in points per glyph.</summary>
    private sealed class TrackingTable(double unitsPerEm, double[] sizes, short[] values)
    {
        /// <summary>HarfBuzz applies tracking for this size when no point size is set.</summary>
        public const double ShapingSize = 12;

        public double At(double size)
        {
            var index = Array.FindIndex(sizes, candidate => candidate >= size);
            double units = index switch
            {
                < 0 => values[^1],
                0 => values[0],
                _ => values[index - 1] + (values[index] - values[index - 1]) *
                    (size - sizes[index - 1]) / (sizes[index] - sizes[index - 1]),
            };
            return units * size / unitsPerEm;
        }

        public static TrackingTable? Read(byte[] font)
        {
            var data = font.AsSpan();
            if (!TryFindTable(data, Tag('t', 'r', 'a', 'k'), out var trak) ||
                !TryFindTable(data, Tag('h', 'e', 'a', 'd'), out var head) || head.Length < 20 || trak.Length < 12)
            {
                return null;
            }

            var horizontal = U16(trak, 6);
            if (horizontal == 0 || horizontal + 8 > trak.Length)
            {
                return null;
            }

            var trackCount = U16(trak, horizontal);
            var sizeCount = U16(trak, horizontal + 2);
            var sizeTable = (int)BinaryPrimitives.ReadUInt32BigEndian(trak[(horizontal + 4)..]);
            if (sizeCount == 0 || sizeTable < 0 || sizeTable + sizeCount * 4 > trak.Length)
            {
                return null;
            }

            var sizes = new double[sizeCount];
            for (var index = 0; index < sizeCount; index++)
            {
                sizes[index] = BinaryPrimitives.ReadInt32BigEndian(trak[(sizeTable + index * 4)..]) / 65536.0;
            }

            for (var track = 0; track < trackCount; track++)
            {
                var entry = horizontal + 8 + track * 8;
                if (entry + 8 > trak.Length || BinaryPrimitives.ReadInt32BigEndian(trak[entry..]) != 0)
                {
                    continue;
                }

                var offset = U16(trak, entry + 6);
                if (offset + sizeCount * 2 > trak.Length)
                {
                    return null;
                }

                var values = new short[sizeCount];
                for (var index = 0; index < sizeCount; index++)
                {
                    values[index] = BinaryPrimitives.ReadInt16BigEndian(trak[(offset + index * 2)..]);
                }

                return new TrackingTable(U16(head, 18), sizes, values);
            }

            return null;
        }

        private static int U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

        private static bool TryFindTable(ReadOnlySpan<byte> font, uint tag, out ReadOnlySpan<byte> table)
        {
            table = default;
            var count = font.Length >= 12 ? U16(font, 4) : 0;
            for (var index = 0; index < count; index++)
            {
                var record = 12 + index * 16;
                if (record + 16 > font.Length)
                {
                    break;
                }

                if (BinaryPrimitives.ReadUInt32BigEndian(font[record..]) == tag)
                {
                    var offset = BinaryPrimitives.ReadUInt32BigEndian(font[(record + 8)..]);
                    var length = BinaryPrimitives.ReadUInt32BigEndian(font[(record + 12)..]);
                    if ((ulong)offset + length > (ulong)font.Length)
                    {
                        return false;
                    }

                    table = font.Slice((int)offset, (int)length);
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class TextFontCollection(Uri key) : FontCollectionBase
    {
        public override Uri Key { get; } = key;
    }

    [LibraryImport(HarfBuzz, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr hb_blob_create_from_file_or_fail(string fileName);

    [LibraryImport(HarfBuzz)]
    private static partial void hb_blob_destroy(IntPtr blob);

    [LibraryImport(HarfBuzz)]
    private static partial IntPtr hb_blob_get_data(IntPtr blob, out uint length);

    [LibraryImport(HarfBuzz)]
    private static partial IntPtr hb_face_create(IntPtr blob, uint index);

    [LibraryImport(HarfBuzz)]
    private static partial void hb_face_destroy(IntPtr face);

    [LibraryImport(HarfBuzz)]
    private static partial IntPtr hb_face_reference_blob(IntPtr face);

    [LibraryImport(HarfBuzz)]
    private static partial IntPtr hb_subset_input_create_or_fail();

    [LibraryImport(HarfBuzz)]
    private static partial void hb_subset_input_destroy(IntPtr input);

    [LibraryImport(HarfBuzz)]
    private static partial void hb_subset_input_keep_everything(IntPtr input);

    [LibraryImport(HarfBuzz)]
    private static partial int hb_subset_input_pin_axis_to_default(IntPtr input, IntPtr face, uint axisTag);

    [LibraryImport(HarfBuzz)]
    private static partial int hb_subset_input_pin_axis_location(IntPtr input, IntPtr face, uint axisTag, float axisValue);

    [LibraryImport(HarfBuzz)]
    private static partial IntPtr hb_subset_or_fail(IntPtr source, IntPtr input);
}
