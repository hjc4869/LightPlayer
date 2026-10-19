using System;
using System.Collections.Generic;
using System.Globalization;
using LightMediaRenderer.Contracts;
using SkiaSharp;

namespace LightMediaRenderer.Subtitles.Layout;

/// <summary>
/// Turns the command string of a <c>\p</c> block into a path.
/// </summary>
/// <remarks>
/// Coordinates stay in the drawing's own units; the caller applies the <c>\p</c> scale, the font
/// scale and the <c>\pbo</c> shift. Malformed commands stop the parse rather than failing it, which
/// is what every other renderer does with the half-written shapes typesetting tools leave behind.
/// </remarks>
internal static class DrawingParser
{
    /// <summary>Returns the shape, or <c>null</c> when nothing drawable was found.</summary>
    public static SKPath? Parse(string commands)
    {
        if (string.IsNullOrWhiteSpace(commands))
            return null;

        using var path = new SKPathBuilder();
        var spline = new List<SKPoint>();
        char command = '\0';
        var pending = new List<double>(6);
        bool open = false;
        SKPoint current = default;

        void FlushSpline()
        {
            // A uniform cubic B-spline: every four consecutive controls become one Bezier segment.
            for (int i = 0; i + 3 < spline.Count; i++)
            {
                SKPoint p0 = spline[i], p1 = spline[i + 1], p2 = spline[i + 2], p3 = spline[i + 3];
                var b0 = new SKPoint((p0.X + (4 * p1.X) + p2.X) / 6f, (p0.Y + (4 * p1.Y) + p2.Y) / 6f);
                var b1 = new SKPoint(((2 * p1.X) + p2.X) / 3f, ((2 * p1.Y) + p2.Y) / 3f);
                var b2 = new SKPoint((p1.X + (2 * p2.X)) / 3f, (p1.Y + (2 * p2.Y)) / 3f);
                var b3 = new SKPoint((p1.X + (4 * p2.X) + p3.X) / 6f, (p1.Y + (4 * p2.Y) + p3.Y) / 6f);

                if (!open)
                {
                    path.MoveTo(b0);
                    open = true;
                }
                else
                {
                    path.LineTo(b0);
                }

                path.CubicTo(b1, b2, b3);
                current = b3;
            }

            spline.Clear();
        }

        void Apply()
        {
            switch (command)
            {
                case 'm':
                case 'n':
                    for (int i = 0; i + 1 < pending.Count; i += 2)
                    {
                        current = new SKPoint((float)pending[i], (float)pending[i + 1]);
                        if (command == 'm' && open)
                            path.Close();
                        path.MoveTo(current);
                        open = true;
                    }

                    break;

                case 'l':
                    for (int i = 0; i + 1 < pending.Count; i += 2)
                    {
                        current = new SKPoint((float)pending[i], (float)pending[i + 1]);
                        if (!open)
                        {
                            path.MoveTo(current);
                            open = true;
                        }
                        else
                        {
                            path.LineTo(current);
                        }
                    }

                    break;

                case 'b':
                    for (int i = 0; i + 5 < pending.Count; i += 6)
                    {
                        if (!open)
                        {
                            path.MoveTo(current);
                            open = true;
                        }

                        path.CubicTo(
                            (float)pending[i], (float)pending[i + 1],
                            (float)pending[i + 2], (float)pending[i + 3],
                            (float)pending[i + 4], (float)pending[i + 5]);
                        current = new SKPoint((float)pending[i + 4], (float)pending[i + 5]);
                    }

                    break;

                case 's':
                    spline.Clear();
                    spline.Add(current);
                    for (int i = 0; i + 1 < pending.Count; i += 2)
                        spline.Add(new SKPoint((float)pending[i], (float)pending[i + 1]));
                    break;

                case 'p':
                    for (int i = 0; i + 1 < pending.Count; i += 2)
                        spline.Add(new SKPoint((float)pending[i], (float)pending[i + 1]));
                    break;

                case 'c':
                    // Closing a spline wraps the first three controls around to the end.
                    if (spline.Count >= 3)
                    {
                        spline.Add(spline[0]);
                        spline.Add(spline[1]);
                        spline.Add(spline[2]);
                    }

                    FlushSpline();
                    break;
            }

            pending.Clear();
        }

        int index = 0;
        while (index < commands.Length)
        {
            char c = commands[index];
            if (char.IsWhiteSpace(c))
            {
                index++;
                continue;
            }

            if (char.IsAsciiLetter(c))
            {
                Apply();
                char next = char.ToLowerInvariant(c);

                // A spline stays open across the p commands that extend it.
                if (command is 's' or 'p' && next is not ('p' or 'c'))
                    FlushSpline();

                command = next;
                index++;
                continue;
            }

            int begin = index;
            if (c is '-' or '+')
                index++;
            while (index < commands.Length && (char.IsAsciiDigit(commands[index]) || commands[index] == '.'))
                index++;

            if (index == begin)
            {
                index++;
                continue;
            }

            if (double.TryParse(commands.AsSpan(begin, index - begin), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                pending.Add(number);
        }

        Apply();
        FlushSpline();

        if (open)
            path.Close();

        SKPath result = path.Detach();
        if (result.PointCount == 0)
        {
            result.Dispose();
            return null;
        }

        return result;
    }
}
