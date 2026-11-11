using System.Collections.Generic;
using System.Drawing;

namespace GI_Subtitles.Services.Capture
{
    /// <summary>
    /// Blacks Kaption's own windows out of a frame the app is about to read, so it never reads its own Polish line
    /// (black is below the reader's threshold). Everything is in screen pixels, like the capture itself. A frame can be
    /// one composition older than the layout, so the areas of the previous read are covered too: a panel that just
    /// moved, shrank or finished fading is still blacked out where the frame shows it.
    /// </summary>
    public static class OverlayMask
    {
        /// <summary>
        /// The rectangles to fill, relative to the frame: every overlay area, now and at the previous read, cut to the
        /// captured rectangle. Empty when nothing of Kaption's is inside it.
        /// </summary>
        public static List<Rectangle> Plan(Rectangle capture, IReadOnlyList<Rectangle> now, IReadOnlyList<Rectangle> before = null)
        {
            var fills = new List<Rectangle>();
            Add(fills, capture, now);
            Add(fills, capture, before);
            return fills;
        }

        private static void Add(List<Rectangle> fills, Rectangle capture, IReadOnlyList<Rectangle> areas)
        {
            if (areas == null) return;
            for (int i = 0; i < areas.Count; i++)
            {
                var hit = Rectangle.Intersect(capture, areas[i]);
                if (hit.Width <= 0 || hit.Height <= 0) continue;
                hit.Offset(-capture.X, -capture.Y);
                if (!fills.Contains(hit)) fills.Add(hit);
            }
        }

        /// <summary>Fills the planned rectangles with black; returns how many were filled.</summary>
        public static int Apply(Bitmap frame, Rectangle capture, IReadOnlyList<Rectangle> now, IReadOnlyList<Rectangle> before = null)
        {
            if (frame == null) return 0;
            var fills = Plan(capture, now, before);
            if (fills.Count == 0) return 0;
            using (Graphics g = Graphics.FromImage(frame))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                foreach (var fill in fills) g.FillRectangle(Brushes.Black, fill);
            }
            return fills.Count;
        }
    }
}
