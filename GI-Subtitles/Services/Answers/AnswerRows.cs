using System;
using System.Collections.Generic;
using System.Drawing;

namespace GI_Subtitles.Services.Answers
{
    /// <summary>One text box the read found, in screen pixels.</summary>
    public readonly record struct AnswerReadBox(string Text, Rectangle Bounds);

    /// <summary>A band of letter ink in the menu's text column (screen rows, bottom exclusive).</summary>
    public readonly record struct AnswerInkBand(int Top, int Bottom);

    /// <summary>One option as Kaption sees it. <see cref="Text"/> is null when ink is there but the read missed it.</summary>
    public readonly record struct AnswerRow(Rectangle Bounds, string Text)
    {
        public bool IsUnread => Text == null;
    }

    /// <summary>The options top to bottom, and whether their count is certain enough to show numerals.</summary>
    public sealed record AnswerRowSet(IReadOnlyList<AnswerRow> Rows, bool Numbered)
    {
        public static readonly AnswerRowSet Empty = new(Array.Empty<AnswerRow>(), false);
    }

    /// <summary>
    /// Read boxes and ink bands to option rows (design section 5). Screen order is the truth; a band no read covers
    /// becomes a dash row in its place, so a missed option never shifts the numbering. Pure.
    /// </summary>
    public static class AnswerRows
    {
        // Lines of one option are 4-10 px apart, neighbouring options 22 px and more (Genshin, design px).
        private const double WrapGapLines = 0.6;

        // Icons the read turns into glyphs in front of an option (the old service's list). '.' stays: "...Fine."
        private static readonly char[] LeadingArtefacts =
        {
            '☐', '▸', '●', '○', '■', '□', '•', '‣', '►', '▶',
            '>', '|', '*', '#', '+', '-',
        };

        /// <param name="box">The answers box in screen pixels (AnswerGeometry.TryPlace).</param>
        /// <param name="lineHeight">desktop.answers ink.lineHeight times the interface scale.</param>
        /// <param name="pillPad">desktop.answers pillPad times the interface scale.</param>
        /// <param name="ownPanels">Kaption's own windows on screen, blacked out of every read.</param>
        public static AnswerRowSet Build(IReadOnlyList<AnswerReadBox> boxes, IReadOnlyList<AnswerInkBand> bands,
            Rectangle box, double lineHeight, double pillPad, IReadOnlyList<Rectangle> ownPanels)
        {
            if (lineHeight <= 0) return AnswerRowSet.Empty;
            double wrapGap = WrapGapLines * lineHeight;

            var lines = SameLines(boxes);
            AddUnreadBands(lines, bands, box);
            lines.Sort((a, b) => a.Bounds.Top.CompareTo(b.Bounds.Top));
            var rows = JoinWrapped(lines, wrapGap, lineHeight);
            if (rows.Count == 0) return AnswerRowSet.Empty;

            return new AnswerRowSet(rows, IsCountCertain(rows, bands, box, lineHeight, pillPad, ownPanels));
        }

        /// <summary>The option's text without the icon glyphs a read puts in front of it.</summary>
        public static string CleanText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            int i = 0;
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || Array.IndexOf(LeadingArtefacts, text[i]) >= 0)) i++;
            return text[i..].TrimEnd();
        }

        // Boxes on one screen line (more than half the smaller box's height shared) are one piece of text, left to right.
        private static List<AnswerRow> SameLines(IReadOnlyList<AnswerReadBox> boxes)
        {
            var items = new List<AnswerReadBox>(boxes?.Count ?? 0);
            if (boxes != null)
            {
                foreach (var b in boxes)
                {
                    string text = CleanText(b.Text);
                    if (text.Length > 0 && b.Bounds.Height > 0) items.Add(b with { Text = text });
                }
            }
            items.Sort((a, b) => a.Bounds.Top.CompareTo(b.Bounds.Top));

            var lines = new List<AnswerRow>(items.Count);
            var parts = new List<AnswerReadBox>();
            for (int i = 0; i < items.Count;)
            {
                parts.Clear();
                parts.Add(items[i]);
                Rectangle bounds = items[i].Bounds;
                int j = i + 1;
                for (; j < items.Count; j++)
                {
                    var next = items[j].Bounds;
                    int shared = Math.Min(bounds.Bottom, next.Bottom) - Math.Max(bounds.Top, next.Top);
                    if (shared * 2 <= Math.Min(bounds.Height, next.Height)) break;
                    parts.Add(items[j]);
                    bounds = Rectangle.Union(bounds, next);
                }
                parts.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));
                lines.Add(new AnswerRow(bounds, parts.Count == 1 ? parts[0].Text : string.Join(" ", parts.ConvertAll(p => p.Text))));
                i = j;
            }
            return lines;
        }

        // A line closer than the wrap gap below the previous one continues that option: a read line when it starts
        // where the option starts, an unread one always. An option with any line unread is unread as a whole, so a
        // half-read option stays one row (one numeral) and never shows a translation of half its text.
        private static List<AnswerRow> JoinWrapped(List<AnswerRow> lines, double wrapGap, double lineHeight)
        {
            var rows = new List<AnswerRow>(lines.Count);
            foreach (var line in lines)
            {
                if (rows.Count > 0)
                {
                    var last = rows[^1];
                    if (line.Bounds.Top - last.Bounds.Bottom < wrapGap &&
                        (last.IsUnread || line.IsUnread || Math.Abs(line.Bounds.Left - last.Bounds.Left) <= lineHeight))
                    {
                        string text = last.IsUnread || line.IsUnread ? null : last.Text + " " + line.Text;
                        rows[^1] = new AnswerRow(Rectangle.Union(last.Bounds, line.Bounds), text);
                        continue;
                    }
                }
                rows.Add(line);
            }
            return rows;
        }

        // Ink no read line covers is a line the read missed, kept in its place as an unread line across the column.
        private static void AddUnreadBands(List<AnswerRow> lines, IReadOnlyList<AnswerInkBand> bands, Rectangle box)
        {
            if (bands == null || bands.Count == 0) return;
            var (left, right) = Column(lines, box);
            int readCount = lines.Count;
            foreach (var band in bands)
            {
                if (band.Bottom <= band.Top || Covered(band, lines, readCount)) continue;
                lines.Add(new AnswerRow(Rectangle.FromLTRB(left, band.Top, right, band.Bottom), null));
            }
        }

        // A read line covers a band when it holds at least half of it: a box's padding reaching a neighbouring line's
        // band by a few pixels does not make that line read.
        private static bool Covered(AnswerInkBand band, List<AnswerRow> lines, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int shared = Math.Min(lines[i].Bounds.Bottom, band.Bottom) - Math.Max(lines[i].Bounds.Top, band.Top);
                if (shared * 2 >= band.Bottom - band.Top) return true;
            }
            return false;
        }

        // The options' text column: where the read rows are, or the whole box when nothing was read.
        private static (int left, int right) Column(IReadOnlyList<AnswerRow> read, Rectangle box)
        {
            int left = int.MaxValue, right = int.MinValue;
            foreach (var row in read)
            {
                if (row.IsUnread) continue;
                left = Math.Min(left, row.Bounds.Left);
                right = Math.Max(right, row.Bounds.Right);
            }
            return left <= right ? (left, right) : (box.Left, box.Right);
        }

        /// <summary>
        /// Numerals only when the count is certain: the box shows blank space above the top row taller than any gap
        /// between options (else a taller stack may go on above it, cut away cleanly in such a gap), no gap could hold
        /// a missed option, and none of Kaption's panels crosses the column from that blank space down (a panel there
        /// may hide an option).
        /// </summary>
        private static bool IsCountCertain(List<AnswerRow> rows, IReadOnlyList<AnswerInkBand> bands, Rectangle box,
            double lineHeight, double pillPad, IReadOnlyList<Rectangle> ownPanels)
        {
            // Measured text gaps between neighbouring options: at most 53 px (Genshin) and about 52 (WuWa); with one
            // option missing, at least 98 (Genshin) and 130 (WuWa). 2 lines + a pad is 78 and 64 there. The design's
            // "taller than the tallest row" fired on every menu of one-line options (rows are 26 to 30 px).
            double maxGap = 2 * lineHeight + pillPad;

            int top = rows[0].Bounds.Top;
            if (bands != null)
                foreach (var band in bands)
                    if (band.Bottom > band.Top) top = Math.Min(top, band.Top);
            if (top - box.Top <= maxGap) return false;

            for (int i = 1; i < rows.Count; i++)
                if (rows[i].Bounds.Top - rows[i - 1].Bounds.Bottom > maxGap) return false;

            if (ownPanels != null && ownPanels.Count > 0)
            {
                var (left, right) = Column(rows, box);
                var column = Rectangle.FromLTRB(left, top - (int)Math.Ceiling(maxGap), right, box.Bottom);
                foreach (var panel in ownPanels)
                    if (panel.IntersectsWith(column)) return false;
            }
            return true;
        }
    }
}
