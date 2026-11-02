using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>A laid-out block of overlay text: outline geometry in a box whose top-left is (0, 0).</summary>
    public sealed class OverlayTextBlockLayout
    {
        public static readonly OverlayTextBlockLayout Empty = new(Geometry.Empty, 0, 0, 0, Array.Empty<double>());

        public OverlayTextBlockLayout(Geometry geometry, double width, double height, int lines, IReadOnlyList<double> lineWidths)
        {
            Geometry = geometry;
            Width = width;
            Height = height;
            Lines = lines;
            LineWidths = lineWidths;
        }

        public Geometry Geometry { get; }
        /// <summary>The widest line, trailing spaces excluded.</summary>
        public double Width { get; }
        public double Height { get; }
        public int Lines { get; }
        public IReadOnlyList<double> LineWidths { get; }
    }

    /// <summary>
    /// Lays out subtitle text the way the overlay draws it. WPF shapes the text (kerning, ligatures, marks, font fallback
    /// for scripts the face lacks) through <see cref="FormattedText"/>; this class then takes the shaped glyph runs and
    /// <list type="bullet">
    /// <item>adds letter spacing (WPF text has none) by widening each glyph's advance,</item>
    /// <item>balances the lines: a two-line subtitle gets two lines of near equal length instead of a full first line and
    /// a stub, by finding the narrowest wrap width that keeps the same number of lines,</item>
    /// <item>aligns each line itself, so centred text is centred in the box the control reports.</item>
    /// </list>
    /// Pure WPF media calls, no visual tree: safe on any STA thread, and what the layout engine measures is exactly what
    /// the control draws.
    /// </summary>
    public static class OverlayTextLayout
    {
        private const int BalanceSteps = 9;

        public static OverlayTextBlockLayout Build(string text, Typeface typeface, double emSize, double maxWidth,
            double trackingEm, bool balance, TextAlignment alignment, double pixelsPerDip, CultureInfo culture = null)
        {
            if (emSize <= 0 || double.IsNaN(emSize)) emSize = 1;
            culture ??= CultureInfo.CurrentUICulture;
            if (pixelsPerDip <= 0) pixelsPerDip = 1;
            text ??= string.Empty;
            if (text.Length == 0)
            {
                var probe = Make(" ", typeface, emSize, double.PositiveInfinity, pixelsPerDip, culture);
                return new OverlayTextBlockLayout(Geometry.Empty, 0, probe.Height, 0, Array.Empty<double>());
            }

            double tracking = trackingEm * emSize;
            bool bounded = maxWidth > 0 && !double.IsInfinity(maxWidth) && !double.IsNaN(maxWidth);
            var natural = Fit(text, typeface, emSize, bounded ? maxWidth : double.PositiveInfinity, tracking, pixelsPerDip, culture);
            var chosen = natural;
            if (balance && bounded && natural.Lines.Count >= 2)
            {
                // The narrowest width that still needs the same number of lines evens them out.
                double lo = Math.Max(1, natural.MaxWidth / natural.Lines.Count * 0.5), hi = natural.MaxWidth + 0.5;
                for (int i = 0; i < BalanceSteps && hi - lo > 1; i++)
                {
                    double mid = (lo + hi) / 2;
                    var trial = Fit(text, typeface, emSize, mid, tracking, pixelsPerDip, culture);
                    if (trial.Lines.Count <= natural.Lines.Count && trial.MaxWidth <= mid + 0.5) { hi = mid; chosen = trial; }
                    else lo = mid;
                }
            }
            return Draw(chosen, alignment);
        }

        // ── Layout at one wrap width ─────────────────────────────────────────────

        private sealed class Line
        {
            public double Baseline;
            public readonly List<GlyphRun> Runs = new();
            public double Left = double.MaxValue;
            public double Width;      // tracked, trailing spaces excluded
            public int TrailingSpaces;
        }

        private sealed class Laid
        {
            public readonly List<Line> Lines = new();
            public double Height;
            public double MaxWidth;
            public double Tracking;
        }

        /// <summary>Wraps at <paramref name="width"/>; with letter spacing, narrows the wrap until every spaced line fits.</summary>
        private static Laid Fit(string text, Typeface tf, double em, double width, double tracking, double ppd, CultureInfo culture)
        {
            double wrap = width;
            Laid laid = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                laid = Lay(text, tf, em, wrap, tracking, ppd, culture);
                if (double.IsInfinity(width) || laid.MaxWidth <= width + 0.5 || wrap < em) break;
                wrap -= laid.MaxWidth - width + 0.5;
            }
            return laid;
        }

        private static Laid Lay(string text, Typeface tf, double em, double wrap, double tracking, double ppd, CultureInfo culture)
        {
            var ft = Make(text, tf, em, wrap, ppd, culture);
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open()) dc.DrawText(ft, new Point(0, 0));
            var runs = new List<GlyphRun>();
            Collect(drawing, runs);

            var laid = new Laid { Height = ft.Height };
            // Right-to-left or vertical runs keep WPF's own spacing.
            laid.Tracking = runs.Any(r => (r.BidiLevel & 1) == 1 || r.IsSideways) ? 0 : tracking;
            foreach (var run in runs.OrderBy(r => Math.Round(r.BaselineOrigin.Y, 1)).ThenBy(r => r.BaselineOrigin.X))
            {
                var line = laid.Lines.LastOrDefault();
                if (line == null || Math.Abs(line.Baseline - run.BaselineOrigin.Y) > 0.5)
                {
                    line = new Line { Baseline = run.BaselineOrigin.Y };
                    laid.Lines.Add(line);
                }
                line.Runs.Add(run);
            }
            foreach (var line in laid.Lines)
            {
                line.Left = line.Runs.Min(r => r.BaselineOrigin.X);
                double right = line.Left;
                int glyphs = 0;
                foreach (var r in line.Runs)
                {
                    right = Math.Max(right, r.BaselineOrigin.X + r.AdvanceWidths.Sum());
                    glyphs += r.GlyphIndices.Count;
                }
                // Trailing spaces at a wrap do not count towards the line's width.
                var last = line.Runs[^1];
                int spaces = 0;
                if (last.Characters != null)
                    for (int i = last.Characters.Count - 1; i >= 0 && char.IsWhiteSpace(last.Characters[i]); i--) spaces++;
                spaces = Math.Min(spaces, last.AdvanceWidths.Count);
                for (int i = 0; i < spaces; i++) right -= last.AdvanceWidths[last.AdvanceWidths.Count - 1 - i];
                line.TrailingSpaces = spaces;
                int spaced = Math.Max(0, glyphs - spaces - 1);
                line.Width = Math.Max(0, right - line.Left + laid.Tracking * spaced);
                laid.MaxWidth = Math.Max(laid.MaxWidth, line.Width);
            }
            return laid;
        }

        private static OverlayTextBlockLayout Draw(Laid laid, TextAlignment alignment)
        {
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            var widths = new List<double>(laid.Lines.Count);
            foreach (var line in laid.Lines)
            {
                widths.Add(line.Width);
                double dx = alignment switch
                {
                    TextAlignment.Center => (laid.MaxWidth - line.Width) / 2,
                    TextAlignment.Right => laid.MaxWidth - line.Width,
                    _ => 0,
                } - line.Left;
                int before = 0;
                int total = line.Runs.Sum(r => r.GlyphIndices.Count) - line.TrailingSpaces;
                foreach (var run in line.Runs)
                {
                    GlyphRun drawn = laid.Tracking == 0 ? run : Respace(run, laid.Tracking, before, total);
                    var g = drawn.BuildGeometry();
                    if (!g.IsEmpty())
                    {
                        // Runs keep their place on the line; tracking pushes later runs right by the spacing before them.
                        double shift = dx + laid.Tracking * before;
                        g.Transform = new TranslateTransform(shift, 0);
                        group.Children.Add(g);
                    }
                    before += run.GlyphIndices.Count;
                }
            }
            if (group.Children.Count == 0) return new OverlayTextBlockLayout(Geometry.Empty, laid.MaxWidth, laid.Height, laid.Lines.Count, widths);
            group.Freeze();
            return new OverlayTextBlockLayout(group, laid.MaxWidth, laid.Height, laid.Lines.Count, widths);
        }

        /// <summary>The same glyphs with <paramref name="tracking"/> added after every glyph but the line's last visible one.</summary>
        private static GlyphRun Respace(GlyphRun run, double tracking, int glyphsBefore, int visibleOnLine)
        {
            var advances = new double[run.AdvanceWidths.Count];
            for (int i = 0; i < advances.Length; i++)
            {
                int index = glyphsBefore + i;
                advances[i] = run.AdvanceWidths[i] + (index < visibleOnLine - 1 ? tracking : 0);
            }
            return new GlyphRun(run.GlyphTypeface, run.BidiLevel, run.IsSideways, run.FontRenderingEmSize, run.PixelsPerDip,
                run.GlyphIndices, run.BaselineOrigin, advances, run.GlyphOffsets, run.Characters, run.DeviceFontName,
                run.ClusterMap, run.CaretStops, run.Language);
        }

        private static void Collect(Drawing drawing, List<GlyphRun> runs)
        {
            switch (drawing)
            {
                case DrawingGroup group:
                    foreach (var child in group.Children) Collect(child, runs);
                    break;
                case GlyphRunDrawing glyphs when glyphs.GlyphRun != null:
                    runs.Add(glyphs.GlyphRun);
                    break;
            }
        }

        private static FormattedText Make(string text, Typeface tf, double em, double wrap, double ppd, CultureInfo culture)
        {
            var ft = new FormattedText(text, culture, FlowDirection.LeftToRight, tf, em, Brushes.White, ppd)
            {
                TextAlignment = TextAlignment.Left,
                Trimming = TextTrimming.None,
            };
            if (wrap > 0 && !double.IsInfinity(wrap) && !double.IsNaN(wrap)) ft.MaxTextWidth = Math.Max(1, wrap);
            return ft;
        }
    }
}
