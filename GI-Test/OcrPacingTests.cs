// ─────────────────────────────────────────────────────────────────────────────
// Per-game OCR pacing: the precedence rule that picks a value, and the values
// themselves.
//
// Two failure modes these exist to catch, both of which have happened:
//
//   * A profile added without pacing knobs. They are int?/double?, so an
//     omitted knob is null and silently falls through to a global default
//     tuned for a different game. ZZZ shipped in 2.2.0 with StarRail's numbers
//     borrowed wholesale and that fact lived only in a comment.
//   * A Config key suppressing the profile for EVERY game. Config.Has is
//     presence-based, so one pinned OcrInterval overrides Genshin, Star Rail
//     and ZZZ alike. ConfigMigrations v1 and v2 both exist to undo exactly
//     that, which is the project's settled position: pacing defaults belong in
//     the profile, Config carries only deliberate user pins.
//
// The precedence tests drive GameOcrTuning.ResolvePrecedence directly rather
// than through static Config state — the same shape as
// ConfigMigrations.GetUiRefreshMigrationTarget. NOTE for anyone extending this:
// inside `namespace GI_Test`, a bare `Config` binds to GI_Test.Config, not to
// the production one. That shadowing is why these tests touch no Config at all.
// ─────────────────────────────────────────────────────────────────────────────

using System.Linq;
using GI_Subtitles.Services.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Pacing precedence and the shipped per-game values, measured by the
    /// benchmark harness over recorded gameplay (see
    /// <c>.plan/in-progress/OCR-PACING-TUNING.md</c>).
    /// </summary>
    [TestClass]
    public class OcrPacingTests
    {
        private static readonly string[] ShippingGames = { "Genshin", "StarRail", "ZZZ" };

        // ── Precedence ────────────────────────────────────────────────────

        [TestMethod]
        public void Precedence_UserPin_BeatsProfileAndGlobal()
        {
            Assert.AreEqual(37, GameOcrTuning.ResolvePrecedence(true, 37, 100, 60),
                "An explicitly pinned Config value must win over the per-game profile.");
            Assert.AreEqual(0.75, GameOcrTuning.ResolvePrecedence(true, 0.75, 0.25, 1.0), 1e-9,
                "Same rule for the double-valued knob.");
        }

        [TestMethod]
        public void Precedence_NoPin_UsesProfileValue()
        {
            Assert.AreEqual(60, GameOcrTuning.ResolvePrecedence(false, 999, 60, 100),
                "With no user pin the per-game profile decides, and the Config value is irrelevant.");
            Assert.AreEqual(0.15, GameOcrTuning.ResolvePrecedence(false, 9.9, 0.15, 1.0), 1e-9);
        }

        [TestMethod]
        public void Precedence_NoPinAndNoProfileValue_FallsBackToGlobal()
        {
            Assert.AreEqual(100, GameOcrTuning.ResolvePrecedence(false, 999, null, 100),
                "A game profile that omits a knob must inherit the global default, not 0.");
            Assert.AreEqual(1.0, GameOcrTuning.ResolvePrecedence(false, 9.9, null, 1.0), 1e-9);
        }

        [TestMethod]
        public void Precedence_PinnedValueIsHonouredEvenWhenItEqualsTheGlobalDefault()
        {
            // Config.Has is presence-based: a key holding the default value is
            // indistinguishable from a hand-tuned one and still suppresses the
            // profile. This is the trap ConfigMigrations v1/v2 clean up after.
            Assert.AreEqual(GameOcrTuning.DefaultOcrIntervalMs,
                GameOcrTuning.ResolvePrecedence(true, GameOcrTuning.DefaultOcrIntervalMs, 60,
                                                GameOcrTuning.DefaultOcrIntervalMs),
                "A pin equal to the global default still beats the profile — that is what makes a stale pin harmful.");
        }

        // ── Profile completeness ──────────────────────────────────────────

        [TestMethod]
        public void EveryShippingProfile_SetsAllFivePacingKnobs()
        {
            foreach (GameRegionProfile p in GameRegionProfile.RegisteredProfiles)
            {
                Assert.IsTrue(p.OcrIntervalMs.HasValue, $"{p.GameId} leaves OcrIntervalMs null.");
                Assert.IsTrue(p.StabilityWindowFrames.HasValue, $"{p.GameId} leaves StabilityWindowFrames null.");
                Assert.IsTrue(p.StableFramesChain.HasValue, $"{p.GameId} leaves StableFramesChain null.");
                Assert.IsTrue(p.StableFramesDefault.HasValue, $"{p.GameId} leaves StableFramesDefault null.");
                Assert.IsTrue(p.ForceOcrAfterSeconds.HasValue, $"{p.GameId} leaves ForceOcrAfterSeconds null.");
            }
        }

        [TestMethod]
        public void EveryShippingProfile_KeepsPacingInsideTheResolverClampRange()
        {
            // A value outside the clamp is not a crash, it is worse: the resolver
            // silently substitutes the boundary, so the profile says one thing
            // and the OCR loop runs another.
            foreach (GameRegionProfile p in GameRegionProfile.RegisteredProfiles)
            {
                AssertInRange(p.GameId, "OcrIntervalMs", p.OcrIntervalMs.Value,
                    GameOcrTuning.MinOcrIntervalMs, GameOcrTuning.MaxOcrIntervalMs);
                AssertInRange(p.GameId, "StabilityWindowFrames", p.StabilityWindowFrames.Value,
                    GameOcrTuning.MinStabilityWindow, GameOcrTuning.MaxStabilityWindow);
                AssertInRange(p.GameId, "StableFramesChain", p.StableFramesChain.Value,
                    GameOcrTuning.MinStableFrames, GameOcrTuning.MaxStableFrames);
                AssertInRange(p.GameId, "StableFramesDefault", p.StableFramesDefault.Value,
                    GameOcrTuning.MinStableFrames, GameOcrTuning.MaxStableFrames);

                double f = p.ForceOcrAfterSeconds.Value;
                Assert.IsTrue(f >= GameOcrTuning.MinForceOcrAfterSeconds && f <= GameOcrTuning.MaxForceOcrAfterSeconds,
                    $"{p.GameId}.ForceOcrAfterSeconds={f} is outside the resolver clamp " +
                    $"[{GameOcrTuning.MinForceOcrAfterSeconds}, {GameOcrTuning.MaxForceOcrAfterSeconds}].");
            }
        }

        [TestMethod]
        public void StableFrameCounts_AreNotBelowTheGateFloor()
        {
            // OcrTriggerGate floors both at MinStableFrames (2). A profile asking
            // for 1 is not honoured, so it is a lie in the source rather than a
            // setting — see OcrTriggerGateTests.MinStableFrames_FloorsAProfileAskingForLess.
            foreach (GameRegionProfile p in GameRegionProfile.RegisteredProfiles)
            {
                Assert.IsTrue(p.StableFramesChain.Value >= GI_Subtitles.Services.OCR.OcrTriggerGate.MinStableFrames,
                    $"{p.GameId}.StableFramesChain={p.StableFramesChain} is below the gate floor and will be silently raised.");
                Assert.IsTrue(p.StableFramesDefault.Value >= GI_Subtitles.Services.OCR.OcrTriggerGate.MinStableFrames,
                    $"{p.GameId}.StableFramesDefault={p.StableFramesDefault} is below the gate floor and will be silently raised.");
            }
        }

        [TestMethod]
        public void UnknownGame_InheritsGlobalDefaults_RatherThanZero()
        {
            GameRegionProfile p = GameRegionProfile.Get("SomeGameWeDoNotShip");

            Assert.AreEqual(GameOcrTuning.DefaultOcrIntervalMs,
                GameOcrTuning.ResolvePrecedence(false, 0, p.OcrIntervalMs, GameOcrTuning.DefaultOcrIntervalMs),
                "An unrecognised game must pace at the global default, not at 0 ms.");
            Assert.AreEqual(GameOcrTuning.DefaultForceOcrAfterSeconds,
                GameOcrTuning.ResolvePrecedence(false, 0d, p.ForceOcrAfterSeconds, GameOcrTuning.DefaultForceOcrAfterSeconds), 1e-9,
                "Likewise the force timeout — 0 s would read every single tick.");
        }

        // ── The measured values ───────────────────────────────────────────

        /// <summary>
        /// The shipped pacing, as measured over 4,700 recorded frames across
        /// eight scenes. These are assertions about a deliberate choice, not
        /// about arithmetic: if you change a number here, re-run
        /// <c>sweep --axis ForceOcrAfterSeconds=...</c> and update the record in
        /// <c>.plan/in-progress/OCR-PACING-TUNING.md</c> with the new evidence.
        /// </summary>
        [DataTestMethod]
        [DataRow("Genshin", 100, 5, 2, 2, 0.25, "types dialogue over 1-2 s, so a shorter force timeout re-reads mid-animation")]
        [DataRow("StarRail", 60, 2, 2, 2, 0.15, "mostly whole lines, so eager reads cost nothing; 50 vs 60 ms measured identical")]
        [DataRow("ZZZ", 60, 2, 2, 2, 0.15, "paint churn is flat to 0.15 then jumps at 0.1; 50 vs 60 ms measured identical")]
        public void ShippingProfiles_CarryTheMeasuredPacing(
            string game, int interval, int window, int chain, int dflt, double force, string why)
        {
            GameRegionProfile p = GameRegionProfile.Get(game);

            Assert.AreEqual(interval, p.OcrIntervalMs.Value, $"{game} OcrIntervalMs — {why}");
            Assert.AreEqual(window, p.StabilityWindowFrames.Value, $"{game} StabilityWindowFrames — {why}");
            Assert.AreEqual(chain, p.StableFramesChain.Value, $"{game} StableFramesChain — {why}");
            Assert.AreEqual(dflt, p.StableFramesDefault.Value, $"{game} StableFramesDefault — {why}");
            Assert.AreEqual(force, p.ForceOcrAfterSeconds.Value, 1e-9, $"{game} ForceOcrAfterSeconds — {why}");
        }

        [TestMethod]
        public void ForceTimeout_IsAtLeastOneOcrInterval_OrTheKnobDoesNothing()
        {
            // The gate compares elapsed time with strict >, and only on a tick
            // boundary, so a force timeout below one OCR interval is
            // indistinguishable from one exactly at it. Shipping such a value
            // would advertise a responsiveness the loop cannot deliver.
            foreach (string game in ShippingGames)
            {
                GameRegionProfile p = GameRegionProfile.Get(game);
                double tickSeconds = p.OcrIntervalMs.Value / 1000.0;

                Assert.IsTrue(p.ForceOcrAfterSeconds.Value >= tickSeconds,
                    $"{game}: ForceOcrAfterSeconds={p.ForceOcrAfterSeconds} is below one OCR interval " +
                    $"({tickSeconds:F3} s), so it is quantised up and the number is misleading.");
            }
        }

        [TestMethod]
        public void AllShippingGames_AreCoveredByThisFile()
        {
            // Guards the DataRow list above against a fourth game being added
            // and quietly inheriting whatever the global defaults happen to be.
            var registered = GameRegionProfile.RegisteredProfiles
                .Select(p => p.GameId)
                .OrderBy(id => id, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            CollectionAssert.AreEqual(
                ShippingGames.OrderBy(g => g, System.StringComparer.OrdinalIgnoreCase).ToList(),
                registered,
                "A game was added or removed. Measure its pacing with the benchmark harness and add a DataRow, " +
                "rather than letting it inherit Genshin's global defaults.");
        }

        // ── The v4 pacing reset ───────────────────────────────────────────

        [TestMethod]
        public void PacingReset_ClearsExactlyTheKeysTheResolverConsults()
        {
            // ConfigMigrations v4 clears pinned pacing so the measured per-game
            // profiles apply. The migration no longer keeps its own copy of the
            // key list — it delegates to GameOcrTuning.ConfigKeys — so this pins
            // two things: that the list is still the five knobs we think it is,
            // and that the migration is still reading it rather than a private
            // duplicate that could drift.
            //
            // An earlier version of this test compared a literal array against
            // ConfigMigrations.PacingKeys and claimed to catch "a sixth knob
            // added to GameOcrTuning". It could not: it never mentioned
            // GameOcrTuning, and it locked the very list you would edit as part
            // of that fix.
            CollectionAssert.AreEquivalent(
                new[] { "OcrInterval", "StabilityWindow", "StableFramesChain",
                        "StableFramesDefault", "ForceOcrAfterSeconds" },
                GameOcrTuning.ConfigKeys,
                "The pacing knob list changed. Update the v4 reset's expectations deliberately, " +
                "and re-measure — a new knob that outranks the profile needs a value in every game profile.");

            Assert.AreSame(
                GameOcrTuning.ConfigKeys,
                GI_Subtitles.Core.Config.ConfigMigrations.PacingKeys,
                "The pacing reset must read GameOcrTuning's list, not a copy — a copy goes stale " +
                "silently and leaves the new key pinned for every game, forever.");
        }

        private static void AssertInRange(string game, string knob, int value, int min, int max)
        {
            Assert.IsTrue(value >= min && value <= max,
                $"{game}.{knob}={value} is outside the resolver clamp [{min}, {max}] and will be silently rewritten.");
        }
    }
}
