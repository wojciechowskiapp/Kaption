using System;
using System.Text;
using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Services.Detection
{
    /// <summary>
    /// Per-game OCR pacing resolver. Centralises the "which value do we use"
    /// decision for the OCR loop's five timing knobs so MainWindow doesn't
    /// have to inline the precedence chain at five different read sites.
    ///
    /// Resolution order (highest precedence first):
    ///   1. User-set Config.json value (<see cref="Config.Has"/> returns true).
    ///      Honoured regardless of profile — power users who hand-tuned a
    ///      value keep it across game switches.
    ///   2. Per-game profile value (non-null in <see cref="GameRegionProfile"/>).
    ///      Acts as the PER-GAME DEFAULT. Different games can recommend
    ///      different values because their text-render pacing differs.
    ///   3. Hard-coded global fallback. Last resort when a new game is added
    ///      without tuning values and the user has never touched Config.
    ///
    /// Why this matters: Genshin's typewriter pacing and HSR's instant-render
    /// pacing demand different stability windows. A single global default
    /// either leaves HSR users waiting 500 ms through empty-stability frames
    /// (lines disappear before OCR fires) or leaves Genshin users flickering
    /// on mid-typewriter frames. Per-game profiles thread the needle.
    ///
    /// <para><b>Precedence rule 1 is sharp, and deliberately so.</b>
    /// <see cref="Config.Has"/> is presence-based: a key present with a value
    /// equal to the default is indistinguishable from a hand-tuned one, and it
    /// suppresses the profile for EVERY game, not just the one it was set on.
    /// That is why <c>ConfigMigrations</c> v1 and v2 both REMOVE
    /// <c>OcrInterval</c> when it still holds a shipped default rather than
    /// rewriting it — the project's settled position is that pacing defaults
    /// live in the profile and Config carries only deliberate user pins.</para>
    ///
    /// Call these accessors once per OCR tick. A pinned knob costs one Config
    /// lock acquire; an unpinned one costs two plus a profile dictionary probe.
    /// Both sit well below the tick budget — but note <see cref="Describe"/>
    /// deliberately pays it several times over, which is why it is called once
    /// at OCR start and never per tick.
    /// </summary>
    public static class GameOcrTuning
    {
        // ── Global fallbacks ───────────────────────────────────────────────
        // Last resort only: reached when a game profile leaves a knob null AND
        // the user has never pinned it. Public because the benchmark harness
        // resolves the same chain and must not carry its own copy of these
        // numbers — a drifted mirror would have it measure pacing the app does
        // not actually run.
        public const int DefaultOcrIntervalMs = 100;
        public const int DefaultStabilityWindow = 5;
        public const int DefaultStableFramesChain = 2;
        public const int DefaultStableFramesDefault = 3;
        public const double DefaultForceOcrAfterSeconds = 1.0;

        // ── Clamp ranges ───────────────────────────────────────────────────
        // Applied AFTER precedence, so neither a profile typo nor a hand-edited
        // Config can put the OCR loop somewhere it cannot recover from. Public
        // so the Settings UI clamps to the same range it will be read back at;
        // these used to disagree (the UI capped StabilityWindow at 10 while the
        // resolver allowed 30), which let the UI silently rewrite a valid value.
        public const int MinOcrIntervalMs = 50;
        public const int MaxOcrIntervalMs = 1000;
        public const int MinStabilityWindow = 1;
        public const int MaxStabilityWindow = 30;
        public const int MinStableFrames = 1;
        public const int MaxStableFrames = 30;
        public const double MinForceOcrAfterSeconds = 0.1;
        public const double MaxForceOcrAfterSeconds = 10.0;

        /// <summary>
        /// Every Config key this resolver consults, and therefore every key that
        /// can suppress a per-game profile default.
        ///
        /// <para>Lives here rather than in <c>ConfigMigrations</c> because this
        /// is the class that decides a key matters. The pacing-reset migration
        /// references this array, so a knob added here is cleared there for free
        /// instead of being silently left pinned.</para>
        /// </summary>
        public static readonly string[] ConfigKeys =
        {
            "OcrInterval",
            "StabilityWindow",
            "StableFramesChain",
            "StableFramesDefault",
            "ForceOcrAfterSeconds",
        };

        /// <summary>Minimum ms between OCR runs for the current game.</summary>
        public static int OcrIntervalMs() =>
            Math.Clamp(ResolveInt("OcrInterval", p => p.OcrIntervalMs, DefaultOcrIntervalMs),
                       MinOcrIntervalMs, MaxOcrIntervalMs);

        /// <summary>Window size (in ticks) for the "stable over window" check.</summary>
        public static int StabilityWindow() =>
            Math.Clamp(ResolveInt("StabilityWindow", p => p.StabilityWindowFrames, DefaultStabilityWindow),
                       MinStabilityWindow, MaxStabilityWindow);

        /// <summary>Consecutive stable frames needed when chain prediction is active.</summary>
        public static int StableFramesChain() =>
            Math.Clamp(ResolveInt("StableFramesChain", p => p.StableFramesChain, DefaultStableFramesChain),
                       MinStableFrames, MaxStableFrames);

        /// <summary>Consecutive stable frames needed when no chain prediction is active.</summary>
        public static int StableFramesDefault() =>
            Math.Clamp(ResolveInt("StableFramesDefault", p => p.StableFramesDefault, DefaultStableFramesDefault),
                       MinStableFrames, MaxStableFrames);

        /// <summary>Seconds after which we force an OCR re-check when the screen
        /// keeps changing without ever stabilising.</summary>
        public static double ForceOcrAfterSeconds() =>
            Math.Clamp(ResolveDouble("ForceOcrAfterSeconds", p => p.ForceOcrAfterSeconds, DefaultForceOcrAfterSeconds),
                       MinForceOcrAfterSeconds, MaxForceOcrAfterSeconds);

        /// <summary>
        /// The precedence rule itself, as a pure function of its inputs.
        ///
        /// <para>Split out from the accessors so it can be asserted directly
        /// instead of through static <c>Config</c> and <c>GameRegionProfile</c>
        /// state — the same shape as
        /// <c>ConfigMigrations.GetUiRefreshMigrationTarget</c>. The accessors
        /// call THIS rather than re-implementing the chain, so a test of this
        /// function is a test of what actually ships.</para>
        /// </summary>
        internal static int ResolvePrecedence(bool keyIsSet, int configValue, int? profileValue, int globalFallback)
            => keyIsSet ? configValue : (profileValue ?? globalFallback);

        /// <inheritdoc cref="ResolvePrecedence(bool,int,int?,int)"/>
        internal static double ResolvePrecedence(bool keyIsSet, double configValue, double? profileValue, double globalFallback)
            => keyIsSet ? configValue : (profileValue ?? globalFallback);

        /// <summary>
        /// One line naming every resolved knob and where it came from. Logged
        /// once when OCR starts, not per tick: a user reporting "subtitles are
        /// slow" is usually a user with a stale Config pin suppressing the
        /// per-game profile, and nothing in the log used to say so.
        /// </summary>
        public static string Describe()
        {
            GameRegionProfile p = CurrentProfile();
            var sb = new StringBuilder();
            sb.Append("OCR pacing for ").Append(p.GameId).Append(": ");
            Append(sb, "interval", "OcrInterval", OcrIntervalMs(), p.OcrIntervalMs.HasValue);
            sb.Append(", ");
            Append(sb, "window", "StabilityWindow", StabilityWindow(), p.StabilityWindowFrames.HasValue);
            sb.Append(", ");
            Append(sb, "stableChain", "StableFramesChain", StableFramesChain(), p.StableFramesChain.HasValue);
            sb.Append(", ");
            Append(sb, "stableDefault", "StableFramesDefault", StableFramesDefault(), p.StableFramesDefault.HasValue);
            sb.Append(", ");
            Append(sb, "forceAfter", "ForceOcrAfterSeconds", ForceOcrAfterSeconds(), p.ForceOcrAfterSeconds.HasValue);
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string label, string configKey, object value, bool profileHasValue)
        {
            string source = Config.Has(configKey)
                ? "user pin"
                : profileHasValue ? "profile" : "global default";
            sb.Append(label).Append('=').Append(value).Append(" (").Append(source).Append(')');
        }

        private static GameRegionProfile CurrentProfile() =>
            GameRegionProfile.Get(Config.Get<string>("Game", "Genshin"));

        // Both resolvers short-circuit before touching the profile. These run
        // three-plus times per OCR tick, and CurrentProfile() costs a Config lock
        // plus a full JsonNode.Deserialize<string> plus a dictionary probe — and
        // allocates a whole fallback profile object when Game is unrecognised.
        // An earlier version evaluated it unconditionally and then discarded the
        // result whenever a pin was set, putting that cost on the hot path for
        // exactly the users who had hand-tuned their machine.
        //
        // ResolvePrecedence remains the single place the RULE lives; these only
        // avoid computing an argument the rule is about to ignore.

        private static int ResolveInt(
            string configKey,
            Func<GameRegionProfile, int?> profileField,
            int globalFallback)
        {
            if (Config.Has(configKey))
                return ResolvePrecedence(true, Config.Get<int>(configKey, globalFallback), null, globalFallback);

            return ResolvePrecedence(false, globalFallback, profileField(CurrentProfile()), globalFallback);
        }

        private static double ResolveDouble(
            string configKey,
            Func<GameRegionProfile, double?> profileField,
            double globalFallback)
        {
            if (Config.Has(configKey))
                return ResolvePrecedence(true, Config.Get<double>(configKey, globalFallback), null, globalFallback);

            return ResolvePrecedence(false, globalFallback, profileField(CurrentProfile()), globalFallback);
        }
    }
}
