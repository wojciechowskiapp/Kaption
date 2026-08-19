using System;
using System.Collections.Generic;

namespace GI_Subtitles.Services.Detection
{
    /// <summary>
    /// Game-specific data for window detection and fallback ratio-based region calculation.
    /// Ratios are relative to the 16:9 reference area within the game window, so they
    /// remain correct on ultrawide and 16:10 monitors after aspect-ratio correction.
    ///
    /// <para>This type is also the app's <b>game registry</b>. Adding game #3 (ZZZ)
    /// surfaced six unsynchronised copies of "which games exist"; four of them now
    /// derive from <see cref="RegisteredProfiles"/> instead of holding their own
    /// list — <c>DictionaryInventoryService.KnownGames</c>, <c>SettingsWindow.GameDict</c>,
    /// the Dashboard display-name lookup, and this file's region/pacing data.
    /// <c>GameDialogueContextFactory</c> (strategy selection) and
    /// <c>GameDataUpdateService.ResolveUpstream</c> (per-mirror URL shapes) stay
    /// separate on purpose: they carry behaviour, not just identity.</para>
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

        // ── Pre-built profiles ──
        //
        // Declaration order IS the UI order: DictionaryInventoryService and the
        // Translations tab both enumerate RegisteredProfiles and render in this
        // sequence, so append new games at the end rather than inserting.
        // Keyed by GameId at construction (BuildIndex) so the key and the
        // GameId can never drift apart the way they could with the old
        // ["Genshin"] = new GameRegionProfile { GameId = "Genshin" } shape.

        private static readonly GameRegionProfile[] ProfileList =
            {
                new GameRegionProfile
                {
                    GameId = "Genshin",
                    DisplayName = "Genshin Impact",
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
                new GameRegionProfile
                {
                    GameId = "StarRail",
                    DisplayName = "Honkai: Star Rail",
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
                new GameRegionProfile
                {
                    GameId = "ZZZ",
                    DisplayName = "Zenless Zone Zero",
                    // Spellings that existed in the tree before "ZZZ" was
                    // settled on. Preserved from the merged GameVisionProfile's
                    // IsZenless matcher: a hand-edited Config.json saying
                    // "Zenless" should still get ZZZ's vision knobs rather than
                    // silently reverting to the Genshin accent path.
                    Aliases = new[] { "Zenless", "ZenlessZoneZero", "Zenless Zone Zero", "Zenless-Zone-Zero" },
                    ProcessNames = new[] { "ZenlessZoneZero" },
                    WindowTitles = new[] { "Zenless Zone Zero", "ZenlessZoneZero" },
                    // ZZZ ships TWO dialogue layouts and this one region has to
                    // cover both, because the user picks a region once and the
                    // game switches styles mid-scene. Measured off real frames
                    // captured at 1919x1079 (docs/screenshots/zzz-*.png):
                    //
                    //   style A "cinematic" — body x 279..1622, name y 844, body ends y 1001
                    //   style B "comic"     — body x 473..1443, name pill y 781, body ends y 1000
                    //
                    // Union in pixels: x 279..1622, y 781..1001. As fractions:
                    //   x 0.145..0.845  (width 0.700)
                    //   y 0.724..0.928  (height 0.204)
                    //
                    // The ratios below pad that union to 0.13..0.87 x 0.70..0.97.
                    // Slack against the measured frames, in pixels:
                    //
                    //   left    30 px   (edge 249, style A body starts 279)
                    //   right   48 px   (edge 1669, style A body ends 1622)
                    //   top     26 px   (edge 755, style B name pill at 781)
                    //   bottom  46 px   (edge 1047, body ends 1001)
                    //
                    // Top is still the tightest edge and the one that matters
                    // most: style B's name pill is the highest text either
                    // layout draws, and clipping it costs the geometry
                    // classifier the exact block it keys on — which surfaces as
                    // "ZZZ speaker detection doesn't work", not as "the region
                    // is a few pixels short". An earlier draft used 0.72, worth
                    // only ~4 px of headroom; 0.70 buys 26 px for essentially
                    // nothing, since the junk filter already drops watermark
                    // blocks that drift into a larger crop.
                    //
                    // 0.70 is also exactly where GI-Test/ZenlessLayoutDiagnostics.cs
                    // puts its crop top (BottomCropFraction = 0.30), so numbers
                    // measured in the harness stay directly comparable to what
                    // the app sees at runtime.
                    //
                    // Do not raise DialogueYPct without re-measuring. If you
                    // change it, keep DialogueYPct + DialogueHPct == 0.97 so
                    // the bottom edge stays put.
                    DialogueXPct = 0.13,
                    DialogueYPct = 0.70,
                    DialogueWPct = 0.74,
                    DialogueHPct = 0.27,
                    // Answer ratios are StarRail's, unverified against ZZZ.
                    // ZZZ choice prompts were not part of the frames measured
                    // for this pass — treat as a placeholder.
                    AnswerXPct = 0.55,
                    AnswerYPct = 0.38,
                    AnswerWPct = 0.38,
                    AnswerHPct = 0.30,
                    // PROVISIONAL pacing — pending live measurement (task 3.10
                    // in .plan/in-progress/ZZZ-SUPPORT.md). Starting from HSR's
                    // shape rather than Genshin's because ZZZ's cinematic mode
                    // renders whole lines with no typewriter animation, so the
                    // 500 ms Genshin-style stability wait would just lose lines
                    // to the next scene. Revisit once someone times real
                    // dialogue cadence in-game.
                    OcrIntervalMs        = 60,
                    StabilityWindowFrames = 2,
                    StableFramesChain     = 2,
                    StableFramesDefault   = 2,
                    ForceOcrAfterSeconds  = 0.5,

                    // ── Vision knobs. Only the departures from the accent
                    //    defaults are listed; the rest inherit via the property
                    //    initializers above.

                    UsesGeometryClassifier = true,
                    StripsSpeakerNameDecoration = true,

                    // −140 clears roughly three wrapped overlay lines: at the
                    // default font size 22 a line costs ~29 px, plus 30 px of
                    // border padding and window margin, so four lines already
                    // stand 147 px tall and reach back down into the capture
                    // region — which trips the overlap guard and pauses OCR.
                    // Genshin gets away with it because its dialogue panel is
                    // wide; the measured ZZZ bodies (173 and 186 characters,
                    // ~200 after the Polish expansion) wrap to four or five
                    // lines against the default 900 px overlay width. −180
                    // covers five.
                    SubtitlePadVertical = -180,

                    // RegionSplitXFraction stays at the 0.55 default, deliberately.
                    // The split separates centred dialogue from right-aligned
                    // answer choices, and no ZZZ answer-choice screenshot has
                    // been captured yet. Two geometry assumptions in this feature
                    // were already overturned by measurement; a third guess does
                    // not belong in shipping code.

                    // 0.70 comes from "dialogue spans ~80% of screen width",
                    // which holds for Genshin and Star Rail. The measured ZZZ
                    // comic panel spans 50.5%, so the 0.70 floor inflates the
                    // detected region by a fifth of the screen and pulls the
                    // on-screen close button and the streamer watermark inside
                    // it. 0.55 still leaves room for longer lines.
                    RegionMinWidthFraction = 0.55,

                    // ZZZ frames in the wild carry UID stamps and overlay
                    // watermarks the existing digit-ratio heuristic misses.
                    StrictRegionJunkFilter = true,
                },
            };

        // Case-insensitive so a hand-edited Config.json holding "genshin" or
        // "zzz" still resolves to the real profile instead of silently falling
        // through to the generic ratios. Every UI path writes canonical casing,
        // so this only widens what resolves — it cannot change the result of a
        // lookup that already succeeded.
        private static readonly Dictionary<string, GameRegionProfile> Profiles = BuildIndex(ProfileList);

        private static Dictionary<string, GameRegionProfile> BuildIndex(GameRegionProfile[] list)
        {
            var map = new Dictionary<string, GameRegionProfile>(list.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var p in list)
            {
                map[p.GameId] = p;
                if (p.Aliases == null) continue;
                foreach (var alias in p.Aliases)
                    if (!string.IsNullOrWhiteSpace(alias)) map[alias] = p;
            }
            return map;
        }

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
            if (!string.IsNullOrEmpty(key) && Profiles.TryGetValue(key, out var profile))
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
            if (!string.IsNullOrEmpty(gameId) && Profiles.TryGetValue(gameId, out var profile))
                return profile.DisplayName ?? gameId;
            return gameId;
        }

        /// <summary>Returns all registered game IDs, in declaration (UI) order.</summary>
        public static IEnumerable<string> RegisteredGameIds
        {
            get
            {
                foreach (var p in ProfileList) yield return p.GameId;
            }
        }

        /// <summary>
        /// Every registered profile, in declaration (UI) order. This is the
        /// enumeration source the other game registries derive from — see the
        /// class remarks.
        /// </summary>
        public static IReadOnlyList<GameRegionProfile> RegisteredProfiles => ProfileList;
    }
}
