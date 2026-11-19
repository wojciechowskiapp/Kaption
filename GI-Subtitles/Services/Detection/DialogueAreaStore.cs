using System;
using System.Drawing;
using Settings = GI_Subtitles.Core.Config.Config;

namespace GI_Subtitles.Services.Detection
{
    /// <summary>A change of the saved dialogue area, as the store announces it.</summary>
    /// <param name="Quiet">Kaption changed it by itself: no dialog may open over the game.</param>
    /// <param name="Client">The game's client area it was placed for; empty when no window was looked at.</param>
    public readonly record struct AreaChange(string Region, DialogueAreaMode Mode, AreaOrigin Origin, bool Quiet, Rectangle Client);

    /// <summary>
    /// The frame, how it is kept and the game picked, taken before a lookup that runs off the UI thread. A lookup that
    /// comes back to a snapshot that is no longer current (a drawing, typed pixels, another game picked meanwhile) is
    /// dropped: its answer is older than what the player has done since.
    /// </summary>
    public readonly record struct AreaSnapshot(string Region, DialogueAreaMode Mode, string Game)
    {
        public static AreaSnapshot Take() => new(DialogueAreaStore.Region, DialogueAreaStore.Mode, Settings.Get<string>("Game", "Genshin"));

        public bool IsCurrent => this == Take();
    }

    /// <summary>
    /// The one place the dialogue area is saved. Setup (before the main window exists), the picker, Settings, the
    /// watch, the capture tick and the Start gate all come here, so <c>Config["Region"]</c>, its mode, Kaption's own
    /// fill and the way back after a widen always agree. The running app listens to <see cref="Changed"/> to update
    /// the reader's copy, the pipeline and the subtitle panel. Call on the UI thread.
    /// </summary>
    public static class DialogueAreaStore
    {
        public const string RegionKey = "Region";
        public const string ModeKey = "Region.Mode";
        /// <summary>What a widen replaced: the player's drawing, for the way back (<see cref="KeepExact"/>).</summary>
        public const string PreviousKey = "Region.Previous";
        /// <summary>The last area Kaption saved by itself: an older install's area equal to it is automatic, not drawn.</summary>
        public const string FilledKey = "RegionAutoFilled";
        /// <summary>The last area a widen saved, so the Start card can say Kaption adjusted the frame while it is still that one.</summary>
        public const string WidenedKey = "Region.Widened";
        /// <summary>The frame the player kept after a too-big or outside-the-game warning ("Keep mine"): its note stays away.</summary>
        public const string AcceptedKey = "Region.Accepted";

        /// <summary>Raised after every save, on the saving thread. Setup's first run saves with nobody listening.</summary>
        public static event Action<AreaChange> Changed;

        /// <summary>Where saves are counted (<c>area.json</c>); tests swap it for one in a temp folder or none.</summary>
        internal static DialogueAreaCounters Counters { get; set; } = DialogueAreaCounters.Default;

        public static string Region => Settings.Get<string>(RegionKey, "");

        public static DialogueAreaMode Mode =>
            DialogueArea.ModeOf(Settings.Get<string>(ModeKey, null), Region, Settings.Get<string>(FilledKey, null));

        public static void Commit(Rectangle area, DialogueAreaMode mode, AreaOrigin origin, bool quiet, Rectangle client = default) =>
            Commit(DialogueArea.Format(area), mode, origin, quiet, client);

        /// <summary>
        /// Saves the area and announces it. The same area in the same mode again (setup's game step revisited, Detect
        /// pressed twice) changes nothing and says nothing: no counter, no reset, no overlap check.
        /// </summary>
        public static void Commit(string region, DialogueAreaMode mode, AreaOrigin origin, bool quiet, Rectangle client = default)
        {
            string before = Region;
            // The player's own frame ends Kaption's widen: no "adjusted" note, and "exactly mine" is this frame, even one
            // drawn at the widen's very coordinates.
            if (origin is AreaOrigin.Drawn or AreaOrigin.Typed)
            {
                Settings.Set(WidenedKey, "");
                Settings.Set(PreviousKey, "");
            }
            if (region == before && mode == Mode && origin != AreaOrigin.Widened) return;
            if (origin == AreaOrigin.Widened)
            {
                // Widening Kaption's own widen again keeps the player's drawing as the way back, not the first widen.
                if (before != Settings.Get<string>(WidenedKey, null)) Settings.Set(PreviousKey, before);
                Settings.Set(WidenedKey, region);
            }
            Settings.Set(RegionKey, region);
            Settings.Set(ModeKey, DialogueArea.Name(mode));
            if (mode == DialogueAreaMode.Auto) Settings.Set(FilledKey, region);
            // Counted here, so setup's first run (before the main window listens) counts too.
            Counters?.CountArea(Settings.Get<string>("Game", "Genshin"), origin, client.Width, client.Height);
            Changed?.Invoke(new AreaChange(region, mode, origin, quiet, client));
        }

        /// <summary>
        /// A frame drawn with the picker. A hint Kaption may widen, unless the player chose "Keep exactly my frame":
        /// then the new drawing is kept exactly too. Every picker saves through here, so they cannot disagree.
        /// </summary>
        public static void CommitDrawing(Rectangle area) => CommitDrawing(DialogueArea.Format(area));

        public static void CommitDrawing(string region) =>
            Commit(region, Mode == DialogueAreaMode.Exact ? DialogueAreaMode.Exact : DialogueAreaMode.Hand, AreaOrigin.Drawn, quiet: false);

        /// <summary>The frame is Kaption's widen of a drawing, and that drawing is still there to bring back.</summary>
        public static bool IsWidened =>
            Mode == DialogueAreaMode.Hand && !string.IsNullOrEmpty(Settings.Get<string>(WidenedKey, null))
            && Region == Settings.Get<string>(WidenedKey, null) && DialogueArea.Parse(Settings.Get<string>(PreviousKey, null)) != null;

        /// <summary>
        /// "Keep exactly my frame" on: the player's frame, never widened. After a widen that is the drawing it replaced
        /// (<see cref="PreviousKey"/>), counted as "undone". Off: the frame stays where it is, as a hint again. False
        /// when nothing changed (no frame saved, or already so).
        /// </summary>
        public static bool KeepExact(bool on)
        {
            string region = Region;
            if (DialogueArea.Parse(region) == null) return false;
            DialogueAreaMode mode = Mode;
            if (on)
            {
                if (mode == DialogueAreaMode.Exact) return false;
                bool undone = IsWidened;
                if (undone) region = Settings.Get<string>(PreviousKey, null);
                Commit(region, DialogueAreaMode.Exact, undone ? AreaOrigin.Undone : AreaOrigin.Chosen, quiet: false);
                return true;
            }
            if (mode != DialogueAreaMode.Exact) return false;
            Commit(region, DialogueAreaMode.Hand, AreaOrigin.Chosen, quiet: false);
            return true;
        }

        /// <summary>
        /// "Keep it" on the note about a widened frame: the player accepts Kaption's widen. The note goes, and so does the
        /// way back to the smaller drawing (a later "exactly mine" keeps this frame). The frame itself stays a hint.
        /// False when there was no widen to accept.
        /// </summary>
        public static bool KeepWidened()
        {
            if (!IsWidened) return false;
            Settings.Set(WidenedKey, "");
            Settings.Set(PreviousKey, "");
            Counters?.CountChoice(Settings.Get<string>("Game", "Genshin"), DialogueAreaCounters.KeptWidened);
            return true;
        }

        /// <summary>"Keep mine" on a too-big or outside-the-game warning: this frame is not warned about again.</summary>
        public static void KeepMine()
        {
            Settings.Set(AcceptedKey, Region);
            Counters?.CountChoice(Settings.Get<string>("Game", "Genshin"), DialogueAreaCounters.KeptMine);
        }

        public static bool IsKeptMine => !string.IsNullOrEmpty(Region) && Region == Settings.Get<string>(AcceptedKey, null);
    }
}
