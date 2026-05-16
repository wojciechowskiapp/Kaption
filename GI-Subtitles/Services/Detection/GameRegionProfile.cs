using System.Collections.Generic;

namespace GI_Subtitles.Services.Detection
{
    /// <summary>
    /// Game-specific data for window detection and fallback ratio-based region calculation.
    /// Ratios are relative to the 16:9 reference area within the game window, so they
    /// remain correct on ultrawide and 16:10 monitors after aspect-ratio correction.
    /// </summary>
    public class GameRegionProfile
    {
        /// <summary>Unique game identifier used as dictionary key.</summary>
        public string GameId { get; set; }

        /// <summary>Win32 process names to search for (without .exe).</summary>
        public string[] ProcessNames { get; set; }

        /// <summary>Window title substrings to match when process lookup fails.</summary>
        public string[] WindowTitles { get; set; }

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

        // ── Pre-built profiles ──

        private static readonly Dictionary<string, GameRegionProfile> Profiles =
            new Dictionary<string, GameRegionProfile>
            {
                ["Genshin"] = new GameRegionProfile
                {
                    GameId = "Genshin",
                    ProcessNames = new[] { "GenshinImpact", "YuanShen" },
                    WindowTitles = new[] { "Genshin Impact" },
                    // Full dialogue width. Sized for worst case: 4-line dialogue + NPC name.
                    // Answer region sized for 4-5 stacked options, overlaps dialogue intentionally.
                    DialogueXPct = 0.10,
                    DialogueYPct = 0.66,
                    DialogueWPct = 0.80,
                    DialogueHPct = 0.29,
                    AnswerXPct = 0.58,
                    AnswerYPct = 0.38,
                    AnswerWPct = 0.35,
                    AnswerHPct = 0.38,
                    // Genshin pacing: slow typewriter. 100 ms tick + 5-frame window
                    // = OCR waits ~500 ms for the line to finish typing. That wait
                    // is THE reason we don't flicker on Genshin; don't lower it.
                    OcrIntervalMs        = 100,
                    StabilityWindowFrames = 5,
                    StableFramesChain     = 2,
                    StableFramesDefault   = 3,
                    ForceOcrAfterSeconds  = 1.0,
                },
                ["StarRail"] = new GameRegionProfile
                {
                    GameId = "StarRail",
                    ProcessNames = new[] { "StarRail" },
                    WindowTitles = new[] { "Honkai: Star Rail" },
                    DialogueXPct = 0.15,
                    DialogueYPct = 0.75,
                    DialogueWPct = 0.70,
                    DialogueHPct = 0.20,
                    AnswerXPct = 0.55,
                    AnswerYPct = 0.38,
                    AnswerWPct = 0.38,
                    AnswerHPct = 0.30,
                    // HSR pacing: no typewriter — text appears whole, and voice
                    // lines rotate faster than Genshin. Sample hot (60 ms) with
                    // a tight 2-frame stability window so we catch a line and
                    // fire OCR before the scene advances. Force-OCR floor halved
                    // because a "still-changing after 1 s" case on HSR usually
                    // means we're mid-scene-transition, not waiting for a
                    // typewriter to settle.
                    OcrIntervalMs        = 60,
                    StabilityWindowFrames = 2,
                    StableFramesChain     = 2,
                    StableFramesDefault   = 2,
                    ForceOcrAfterSeconds  = 0.5,
                },
            };

        /// <summary>
        /// Get profile by game ID. Returns a generic fallback profile if the game is unknown.
        /// The generic profile uses conservative bottom-center ratios that work for most
        /// dialogue-heavy games.
        /// </summary>
        public static GameRegionProfile Get(string gameId)
        {
            if (!string.IsNullOrEmpty(gameId) && Profiles.TryGetValue(gameId, out var profile))
                return profile;

            // Generic fallback for unknown games
            return new GameRegionProfile
            {
                GameId = gameId ?? "Unknown",
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

        /// <summary>Returns all registered game IDs.</summary>
        public static IEnumerable<string> RegisteredGameIds => Profiles.Keys;
    }
}
