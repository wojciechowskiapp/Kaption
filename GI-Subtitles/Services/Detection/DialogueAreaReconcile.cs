using System.Drawing;

namespace GI_Subtitles.Services.Detection
{
    public enum AreaAction
    {
        /// <summary>The saved area stays.</summary>
        Keep,
        /// <summary>Save the block: automatic mode, or nothing saved yet.</summary>
        Place,
        /// <summary>Save the drawing grown until it holds the game's text; it stays the player's.</summary>
        Widen,
        /// <summary>No game window yet: decide at the first sight of it, before the first read.</summary>
        Wait,
    }

    /// <summary>What Kaption found about the area, for the log, the counters and (PR 3) what the player is told.</summary>
    public enum AreaVerdict { None, Moved, Fits, CutsText, TooBig, Elsewhere, OutsideGame, Exact, NoWindow }

    public readonly record struct AreaDecision(AreaAction Action, Rectangle Area, AreaVerdict Verdict);

    /// <summary>
    /// The player's box is a hint (design 2.4). Runs when the picker closes, at the first sight of the game's window in
    /// a session and when the window changes. Pure: the caller places the block and the text on the client first.
    /// </summary>
    public static class DialogueAreaReconcile
    {
        /// <summary>A drawing holding the block and this many times its size covers far more than the dialogue.</summary>
        public const double TooBigBlockFactor = 2.5;

        /// <summary>A drawing over this share of the game's window covers far more than the dialogue.</summary>
        public const double TooBigClientShare = 0.6;

        /// <param name="saved">The saved or just-drawn area; null when there is none.</param>
        /// <param name="client">The game's client area; null when no usable window is known (closed, minimised, off screen).</param>
        /// <param name="block">The block placed on <paramref name="client"/> (<see cref="DialogueAreaPlacement.Place"/>).</param>
        /// <param name="text">The game's measured text placed the same way; it lies inside the block.</param>
        public static AreaDecision Decide(Rectangle? saved, DialogueAreaMode mode, Rectangle? client, Rectangle block, Rectangle text)
        {
            if (saved is not Rectangle s)
                return client == null ? new(AreaAction.Wait, default, AreaVerdict.NoWindow) : new(AreaAction.Place, block, AreaVerdict.None);
            if (mode == DialogueAreaMode.Exact) return new(AreaAction.Keep, s, AreaVerdict.Exact);
            if (client is not Rectangle c) return new(AreaAction.Wait, s, AreaVerdict.NoWindow);

            if (mode == DialogueAreaMode.Auto)
                return s == block ? new(AreaAction.Keep, s, AreaVerdict.Fits) : new(AreaAction.Place, block, AreaVerdict.Moved);

            // By hand. Holding the text decides first: a box over half the screen holds it whatever its centre.
            if (s.Contains(text))
            {
                long area = (long)s.Width * s.Height;
                bool tooBig = (s.Contains(block) && area > TooBigBlockFactor * block.Width * block.Height)
                              || area > TooBigClientShare * c.Width * c.Height;
                return new(AreaAction.Keep, s, tooBig ? AreaVerdict.TooBig : AreaVerdict.Fits);
            }
            if (!block.Contains(Centre(s)))
                return new(AreaAction.Keep, s, s.IntersectsWith(c) ? AreaVerdict.Elsewhere : AreaVerdict.OutsideGame);

            // Grow only, and only to the text: edges that cut nothing stay where the player put them, so the subtitle
            // panel (which hangs from the top edge) moves only when the top cut the speaker's name.
            return new(AreaAction.Widen, Rectangle.Union(s, text), AreaVerdict.CutsText);
        }

        private static Point Centre(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
    }
}
