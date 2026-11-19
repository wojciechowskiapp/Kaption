using System.Drawing;

namespace GI_Subtitles.Services.Detection
{
    /// <summary>How the dialogue area is kept (<c>Config["Region.Mode"]</c>).</summary>
    public enum DialogueAreaMode
    {
        /// <summary>The game's measured block on its window, placed again when the window moves or changes size.</summary>
        Auto,
        /// <summary>Drawn by the player: a hint Kaption widens until it holds the game's text, never shrinks or moves.</summary>
        Hand,
        /// <summary>Exactly as drawn or typed: never changed by Kaption.</summary>
        Exact,
    }

    /// <summary>How the saved area came to be: the store's callers say it, the counters count it.</summary>
    public enum AreaOrigin
    {
        /// <summary>Kaption placed the block (setup, the watch, the Start gate, "Set automatically").</summary>
        Placed,
        /// <summary>Placed again because the game's window moved or changed size.</summary>
        Followed,
        /// <summary>A drawing that cut the game's text, grown to hold it.</summary>
        Widened,
        /// <summary>Drawn with the picker.</summary>
        Drawn,
        /// <summary>Typed as pixels in Settings.</summary>
        Typed,
        /// <summary>The player chose how the frame is kept ("Keep exactly my frame" on or off).</summary>
        Chosen,
        /// <summary>"Keep exactly my frame" on a widened frame: the smaller drawing the widen replaced is back. Counted apart from <see cref="Chosen"/>.</summary>
        Undone,
    }

    /// <summary>Why reads are held in automatic mode while the game is in front: its window cannot carry the frame.</summary>
    public enum AreaHold
    {
        None,
        /// <summary>The window is under the size of a game's window, or has no client area.</summary>
        TooSmall,
        /// <summary>The window hangs off the screen: the game's dialogue would be cut.</summary>
        OffScreen,
    }

    /// <summary>Why the Start gate could not place the frame on a game it looked for.</summary>
    public enum AreaPlaceFailure { None, NoWindow, Minimised, TooSmall, OffScreen }

    /// <summary>The inputs of "may Kaption place or change the area right now", shared by the watch, the tick and the gate.</summary>
    public readonly record struct PlaceContext(
        DialogueAreaMode Mode, bool KnownGame, bool HandDecided, bool DevSkipGameGate, bool CloudSession, bool Busy);

    /// <summary>Small shared rules of the dialogue area: the mode an install has, and when the area may be touched.</summary>
    public static class DialogueArea
    {
        /// <summary>
        /// The stored mode, or for an install that has none yet: automatic when nothing is saved or the area is the one
        /// Kaption filled itself, by hand otherwise (a drawing, or what an older "Detect" saved, which is why it is
        /// checked against the game's text and not trusted).
        /// </summary>
        public static DialogueAreaMode ModeOf(string stored, string region, string filledByKaption)
        {
            switch (stored?.Trim().ToLowerInvariant())
            {
                case "auto": return DialogueAreaMode.Auto;
                case "hand": return DialogueAreaMode.Hand;
                case "exact": return DialogueAreaMode.Exact;
            }
            if (!CaptureRegionValidator.TryParse(region, out _, out _, out _, out _)) return DialogueAreaMode.Auto;
            return !string.IsNullOrEmpty(filledByKaption) && region == filledByKaption ? DialogueAreaMode.Auto : DialogueAreaMode.Hand;
        }

        public static string Name(DialogueAreaMode mode) => mode switch
        {
            DialogueAreaMode.Hand => "hand",
            DialogueAreaMode.Exact => "exact",
            _ => "auto",
        };

        /// <summary>
        /// Whether Kaption may place or change the area. Never under the dev switch or in a cloud session (the window in
        /// front is a video player or a stream, not the game's), never for a game without a measured block, never while
        /// a picker or Ctrl+Q is open, never over "exactly as drawn"; a drawing is checked once a session.
        /// </summary>
        public static bool MayPlace(in PlaceContext c) =>
            c.KnownGame && !c.DevSkipGameGate && !c.CloudSession && !c.Busy && c.Mode switch
            {
                DialogueAreaMode.Auto => true,
                DialogueAreaMode.Hand => !c.HandDecided,
                _ => false,
            };

        /// <summary>
        /// What the follow and watch caches must know after a save, whoever made it. A placement carries the window it
        /// was made for (Detect, setup, the watch): the caches now hold exactly that window, so a frame placed for
        /// another one is never taken for current. A drawing, typed pixels or a choice is for no window: both look
        /// again at the next sight.
        /// </summary>
        public static void SyncCaches(in AreaChange c, DialogueAreaFollow follow, DialogueAreaWatch watch)
        {
            if (c.Origin is AreaOrigin.Placed or AreaOrigin.Followed)
            {
                if (!c.Client.IsEmpty)
                {
                    follow.MarkPlaced(c.Client);
                    watch.MarkDecided(c.Client);
                    return;
                }
            }
            else if (c.Origin == AreaOrigin.Widened) return;
            follow.Reset();
            watch.Forget();
        }

        public static string Format(Rectangle r) => $"{r.X},{r.Y},{r.Width},{r.Height}";

        public static Rectangle? Parse(string region) =>
            CaptureRegionValidator.TryParse(region, out int x, out int y, out int w, out int h) ? new Rectangle(x, y, w, h) : null;

        public static Rectangle? Parse(string[] region) =>
            CaptureRegionValidator.TryParse(region, out int x, out int y, out int w, out int h) ? new Rectangle(x, y, w, h) : null;
    }
}
