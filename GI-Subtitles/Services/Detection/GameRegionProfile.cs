using System;
using System.Collections.Generic;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Detection
{
    /// <summary>
    /// Game-specific data for window detection and fallback ratio-based region calculation.
    /// Ratios are relative to the 16:9 reference area within the game window, so they
    /// remain correct on ultrawide and 16:10 monitors after aspect-ratio correction.
    ///
    /// <para>The profiles come from the game manifests (games/&lt;id&gt;.json) through
    /// <see cref="GameRegistry"/>; <see cref="RegisteredProfiles"/> is what the game pickers,
    /// <c>DictionaryInventoryService.KnownGames</c> and the display-name lookups enumerate.
    /// Behaviour that differs per game (dialogue strategy, upstream mirror, display-mode tip)
    /// is read from the same manifests by the code that owns it.</para>
    /// </summary>
    public class GameRegionProfile
    {
        /// <summary>Unique game identifier used as dictionary key. Also the
        /// <c>%APPDATA%\Kaption\&lt;GameId&gt;\</c> folder name and — after
        /// <c>ToLowerInvariant()</c> — the wire id used by the backend, R2 keys
        /// and <c>GameDialogueContextFactory</c>.</summary>
        public string GameId { get; set; }

        /// <summary>Human-readable game name for every UI surface (Translations
        /// pills, Dashboard "Active translation" strip, pack list headers).
        /// Single source of truth — callers must not keep their own copy.</summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// Alternate spellings of <see cref="GameId"/> that also resolve to this
        /// profile. Exists because a hand-edited <c>Config.json</c> is a real
        /// scenario and because ZZZ in particular went by several names in the
        /// tree before the canonical tag was settled. Resolution is already
        /// case-insensitive, so these only need to cover genuinely different
        /// spellings, not casing variants.
        /// </summary>
        public string[] Aliases { get; set; }

        /// <summary>Win32 process names to search for (without .exe).</summary>
        public string[] ProcessNames { get; set; }

        /// <summary>Window title substrings to match when process lookup fails.</summary>
        public string[] WindowTitles { get; set; }

        /// <summary>
        /// The process name is generic (Unreal's Client-Win64-Shipping), so a process is the game only while one of its
        /// windows has a title from <see cref="WindowTitles"/>. Every detection path asks <see cref="GameProcessMatch"/>.
        /// </summary>
        public bool RequiresWindowTitle { get; set; }

        // ── Fallback dialogue region ratios (% of 16:9 reference area) ──

        /// <summary>Dialogue region X offset as fraction of reference width.</summary>
        public double DialogueXPct { get; set; }

        /// <summary>Dialogue region Y offset as fraction of reference height.</summary>
        public double DialogueYPct { get; set; }

        /// <summary>Dialogue region width as fraction of reference width.</summary>
        public double DialogueWPct { get; set; }

        /// <summary>Dialogue region height as fraction of reference height.</summary>
        public double DialogueHPct { get; set; }

        // ── Fallback answer region ratios ──

        /// <summary>Answer region X offset as fraction of reference width.</summary>
        public double AnswerXPct { get; set; }

        /// <summary>Answer region Y offset as fraction of reference height.</summary>
        public double AnswerYPct { get; set; }

        /// <summary>Answer region width as fraction of reference width.</summary>
        public double AnswerWPct { get; set; }

        /// <summary>Answer region height as fraction of reference height.</summary>
        public double AnswerHPct { get; set; }

        // ── Per-game OCR pacing ──
        //
        // These knobs tune the OCR stability pipeline for how a given game
        // actually renders dialogue text. Null = "use the global default"
        // (i.e. the value baked into the code / the old Config.Get fallback).
        // A non-null value acts as the PER-GAME DEFAULT and applies when the
        // user has NOT explicitly set the matching Config.json key; any user
        // value still wins over the profile value (so power users keep
        // control). See GameOcrTuning.cs for the resolution chain.
        //
        // Why not a single set of global constants? Because Genshin uses a
        // slow typewriter animation (characters appear one-at-a-time over
        // ~1-2s per line) while HSR renders every line instantly and cycles
        // lines faster. Genshin-tuned stability windows (5 frames × 100 ms
        // = 500 ms wait) meant HSR lines often *disappeared* before OCR
        // fired — the engine was still waiting for a stability window that
        // HSR never needs.

        /// <summary>Minimum milliseconds between OCR runs. Tighter = more responsive,
        /// higher CPU/GPU load.</summary>
        public int? OcrIntervalMs { get; set; }

        /// <summary>Number of past ticks to compare against when checking
        /// "stable over window". Lower = faster trigger, higher = more
        /// resistance to typewriter flicker.</summary>
        public int? StabilityWindowFrames { get; set; }

        /// <summary>Consecutive stable frames required when chain prediction is
        /// active (tight path).</summary>
        public int? StableFramesChain { get; set; }

        /// <summary>Consecutive stable frames required when no chain prediction
        /// is active (general path).</summary>
        public int? StableFramesDefault { get; set; }

        /// <summary>How long to wait after the frame changed vs last OCR before
        /// forcing an OCR run, in seconds. Lower for games that change text
        /// quickly.</summary>
        public double? ForceOcrAfterSeconds { get; set; }

        // ── Vision knobs (merged from Services/OCR/GameVisionProfile.cs) ──
        //
        // These were briefly a second per-game profile type, written in parallel
        // by the vision workstream while this file was being reshaped. Two
        // profile tables is the exact duplication the registry consolidation set
        // out to remove — game #4 would have needed an entry in both — so they
        // live here now.
        //
        // Every one carries the accent/Genshin value as a PROPERTY INITIALIZER,
        // not as a per-game assignment. That matters: object initializers run
        // after these, so a future game entry that forgets one of these fields
        // inherits today's shipping behaviour instead of silently getting 0 —
        // which for SubtitlePadVertical would drop the overlay straight onto the
        // capture region and stall OCR via the overlap guard.

        /// <summary>Which speaker/body split to run. See <c>ITextBlockClassifier</c>.
        /// False = the HSV accent-colour gate the shipping games use.</summary>
        public bool UsesGeometryClassifier { get; set; } = false;

        /// <summary>
        /// Whether the speaker name arrives wrapped in ornament that has to be
        /// stripped before it can key the name-to-role index. Selects
        /// <c>ZzzNameNormalizer</c> over the default <c>TrimNameNormalizer</c>.
        /// Kept separate from <see cref="UsesGeometryClassifier"/> even though
        /// only ZZZ sets both — they are different pipeline stages and a future
        /// game could plausibly need one without the other.
        /// </summary>
        public bool StripsSpeakerNameDecoration { get; set; } = false;

        /// <summary>
        /// Default vertical offset, in WPF logical pixels, between the top of the
        /// capture region and the top of the subtitle overlay. Negative lifts the
        /// overlay above the region. Feeds <c>Config.GetPad(default)</c>, so a
        /// user who has touched the Pad slider still wins.
        /// </summary>
        public int SubtitlePadVertical { get; set; } = -140;

        /// <summary>
        /// Fraction of frame width separating "centre" blocks (dialogue) from
        /// "right" blocks (answer choices) during auto region detection.
        /// </summary>
        public double RegionSplitXFraction { get; set; } = 0.55;

        /// <summary>
        /// Minimum dialogue-region width as a fraction of frame width. Auto
        /// detection widens a narrow detected cluster up to this so the region
        /// still fits longer lines that appear later in the conversation.
        /// </summary>
        public double RegionMinWidthFraction { get; set; } = 0.70;

        /// <summary>
        /// Whether auto region detection should apply <c>OcrTextJunkFilter</c> on
        /// top of its own HUD heuristic. Off for the shipping games so their
        /// detection stays unchanged.
        /// </summary>
        public bool StrictRegionJunkFilter { get; set; } = false;

        /// <summary>
        /// Speaker/body split by row: the name is the top row of a centred dialogue column, accent-coloured or in a
        /// larger font (<c>SpeakerRowTextBlockClassifier</c>). Wuthering Waves' free-roam lines have no backing panel,
        /// so sunlit scenery passes body text through the accent gate. Ignored when
        /// <see cref="UsesGeometryClassifier"/> is set.
        /// </summary>
        public bool UsesSpeakerRowClassifier { get; set; } = false;

        /// <summary>
        /// Whether every OCR frame drops <c>OcrTextJunkFilter</c> blocks (HP and level readouts, watermarks, stray
        /// glyphs) before the speaker/body split, on the accent and speaker-row paths. The geometry classifier always
        /// does. Off for Genshin and Star Rail, which have never filtered.
        /// </summary>
        public bool FiltersOcrJunk { get; set; } = false;

        /// <summary>
        /// A matcher hit is painted only when the dialogue data vouches for it (a graph line, or a known speaker saying
        /// something that reads as speech): map labels, menus and item text are TextMap keys too. Resolver only.
        /// Genshin (session 57); off elsewhere until measured.
        /// </summary>
        public bool RequiresDialogueEvidence { get; set; } = false;

        /// <summary>
        /// A read whose body boxes are barely lit (OcrReadEvidence) is a line fading out and counts as an empty read,
        /// so the fade cannot become the change gate's baseline. OCR worker only. Genshin (session 57).
        /// </summary>
        public bool RejectsFadedReads { get; set; } = false;

        // ── Session check (the Start card says why no line shows) ──
        //
        // How much evidence each sentence needs (Views/Shell/SessionCheck.cs). Property initializers like the vision
        // knobs, so a manifest without desktop.sessionCheck gets these; 0 switches a sentence off for that game.

        /// <summary>
        /// Seconds of unbroken black frames with the game in front, before any line was found in this run of
        /// translation, before the card says Kaption can't see the game. 30: a game's own launch screens keep the dialogue strip black for
        /// 10-20 s, and auto-start is already reading then.
        /// </summary>
        public int CheckBlackSeconds { get; set; } = 30;

        /// <summary>Different spoken lines (a speaker and a sentence) read in a row without one pack hit, before the card asks about the game's language.</summary>
        public int CheckUnmatchedReads { get; set; } = 20;

        /// <summary>Seconds with the game in front and nothing dialogue-like read, before a new install's card says no dialogue was seen yet.</summary>
        public int CheckQuietSeconds { get; set; } = 60;

        // ── Registered profiles ──
        //
        // Built from the embedded game manifests (games/<id>.json) by Services/Games/GameRegistry: beta and live
        // games, ordered by their "order" field, which is the UI order everywhere. The measurements behind each
        // number are in .plan/in-progress/OCR-PACING-TUNING.md (pacing) and ZZZ-SUPPORT.md (ZZZ regions, vision).
        // GI-Test/GameProfileGoldenTests pins Genshin, Star Rail and ZZZ to their hand-written values.

        /// <summary>
        /// Get profile by game ID. Returns a generic fallback profile if the game is unknown.
        /// The generic profile uses conservative bottom-center ratios that work for most
        /// dialogue-heavy games.
        /// </summary>
        public static GameRegionProfile Get(string gameId)
        {
            // Trim before lookup: the merged vision matcher tolerated padding,
            // and a Config.json edited by hand is exactly where stray whitespace
            // comes from.
            string key = gameId?.Trim();
            var profile = string.IsNullOrEmpty(key) ? null : GameRegistry.Current.ProfileOf(key);
            if (profile != null)
                return profile;

            // Generic fallback for unknown games. Vision knobs are NOT set here
            // — the property initializers already supply the accent/Genshin
            // values, which is what an unrecognised game should degrade to.
            return new GameRegionProfile
            {
                GameId = gameId ?? "Unknown",
                DisplayName = gameId ?? "Unknown",
                ProcessNames = new string[0],
                WindowTitles = new string[0],
                DialogueXPct = 0.15,
                DialogueYPct = 0.75,
                DialogueWPct = 0.70,
                DialogueHPct = 0.20,
                AnswerXPct = 0.55,
                AnswerYPct = 0.38,
                AnswerWPct = 0.38,
                AnswerHPct = 0.30,
            };
        }

        /// <summary>
        /// Profile for the game currently selected in <c>Config["Game"]</c>.
        /// Convenience for the vision-side call sites, which resolve per frame
        /// and have no game argument to hand.
        /// </summary>
        public static GameRegionProfile ForCurrentGame()
            => Get(Core.Config.Config.Get<string>("Game", "Genshin"));

        /// <summary>
        /// Friendly name for <paramref name="gameId"/>, falling back to the raw
        /// tag when the game isn't registered. Use this everywhere a game label
        /// is rendered — it replaces the per-call-site switch statements that
        /// used to drift out of sync with this registry.
        /// </summary>
        public static string DisplayNameOf(string gameId)
        {
            var profile = string.IsNullOrEmpty(gameId) ? null : GameRegistry.Current.ProfileOf(gameId);
            return profile != null ? profile.DisplayName ?? gameId : gameId;
        }

        /// <summary>Returns all registered game IDs, in manifest (UI) order.</summary>
        public static IEnumerable<string> RegisteredGameIds
        {
            get
            {
                foreach (var p in GameRegistry.Current.Profiles) yield return p.GameId;
            }
        }

        /// <summary>
        /// Every registered profile, in manifest (UI) order. This is the
        /// enumeration source the game pickers and inventories derive from —
        /// see the class remarks.
        /// </summary>
        public static IReadOnlyList<GameRegionProfile> RegisteredProfiles => GameRegistry.Current.Profiles;
    }
}
