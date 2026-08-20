// ─────────────────────────────────────────────────────────────────────────────
// The OCR trigger gate decides, every tick, whether to spend an OCR inference.
// It was extracted from MainWindow.GetOCR precisely so it could be reasoned
// about and measured — but it shipped with no tests at all, which meant the
// session-40 pacing sweep was re-tuning a state machine nothing pinned.
//
// Two properties in here are load-bearing and surprising enough that a future
// "cleanup" would plausibly break them without noticing:
//
//   * The forced timeout compares with STRICT >, and only on a tick boundary,
//     so the knob is quantised by OcrIntervalMs. At a 100 ms tick every value
//     in the OPEN interval (0, 0.1) fires at the first tick; 0.1 itself does
//     NOT (0.1 > 0.1 is false) and slips to the second, landing with 0.15. That
//     is why the pacing sweep measured F=0.15 and F=0.1 as byte-identical.
//     The boundary value is the surprising one — mind the open interval.
//   * MinStableFrames is a hard floor of 2 applied AFTER the profile and the
//     user Config key. A profile asking for 1 still gets 2.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using GI_Subtitles.Services.OCR;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Behaviour of <see cref="OcrTriggerGate"/> — the "should we run OCR on
    /// this frame?" state machine that sets subtitle latency for all three
    /// games. Pure: no pixels, no wall clock, so these run anywhere.
    /// </summary>
    [TestClass]
    public class OcrTriggerGateTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>A tick with everything settled unless overridden.</summary>
        private static OcrTriggerGate.Inputs Tick(
            double? vsPrev = 0.0,
            double? vsBaseline = 1.0,
            double? overWindow = null,
            double atSeconds = 0.0,
            bool chain = false,
            int stableChain = 2,
            int stableDefault = 3,
            double forceAfter = 1.0)
            => new OcrTriggerGate.Inputs
            {
                ChangeVsPrevious = vsPrev,
                ChangeVsOcrBaseline = vsBaseline,
                ChangeOverWindow = overWindow,
                HasSingleChainPrediction = chain,
                StableFramesChain = stableChain,
                StableFramesDefault = stableDefault,
                ForceOcrAfterSeconds = forceAfter,
                NowUtc = T0.AddSeconds(atSeconds),
            };

        [TestMethod]
        public void FirstTick_WithNoBaselineAndNoHistory_IsNotReadyUntilStableFramesAccrue()
        {
            var gate = new OcrTriggerGate();

            // No previous frame counts as stable, no baseline counts as changed,
            // but one stable frame is below the floor of 2.
            OcrTriggerGate.Decision d = gate.Evaluate(Tick(vsPrev: null, vsBaseline: null));

            Assert.IsFalse(d.ReadyForOcr, "A single stable frame must not trigger OCR — that is the flicker path.");
            Assert.IsTrue(d.ChangedVsOcr, "A missing OCR baseline must count as changed, or the first line is never read.");
            Assert.AreEqual(1, d.ConsecutiveStableFrames);
        }

        [TestMethod]
        public void ReachingTheStableFrameThreshold_TriggersEagerPreview()
        {
            var gate = new OcrTriggerGate();
            gate.Evaluate(Tick(vsPrev: null, vsBaseline: null, stableDefault: 2));

            OcrTriggerGate.Decision d = gate.Evaluate(Tick(vsPrev: 0.0, vsBaseline: null, stableDefault: 2));

            Assert.IsTrue(d.ReadyForOcr, "The second stable frame meets a threshold of 2.");
            Assert.IsTrue(d.EagerPreview, "Stable frames with no window sample is the eager-preview path.");
            Assert.IsFalse(d.Forced);
            Assert.IsFalse(d.StableOverWindow);
        }

        [TestMethod]
        public void StableFrameThreshold_IsNotReachedEarly()
        {
            // Genshin shipped StableFramesDefault=3 until 2026-08-20; the count
            // must actually be honoured rather than collapsing to the floor.
            var gate = new OcrTriggerGate();
            gate.Evaluate(Tick(vsPrev: null, vsBaseline: null, stableDefault: 3));

            Assert.IsFalse(gate.Evaluate(Tick(vsPrev: 0.0, vsBaseline: null, stableDefault: 3)).ReadyForOcr,
                "Two stable frames must not satisfy a threshold of 3.");
            Assert.IsTrue(gate.Evaluate(Tick(vsPrev: 0.0, vsBaseline: null, stableDefault: 3)).ReadyForOcr,
                "The third does.");
        }

        [TestMethod]
        public void StableOverWindow_TriggersImmediately_WithoutWaitingForConsecutiveFrames()
        {
            var gate = new OcrTriggerGate();

            // One tick only: the consecutive counter is 1, below the floor, but
            // the window says the line has genuinely settled.
            OcrTriggerGate.Decision d = gate.Evaluate(Tick(overWindow: 0.0));

            Assert.IsTrue(d.ReadyForOcr);
            Assert.IsTrue(d.StableOverWindow);
            Assert.IsFalse(d.EagerPreview, "Window and eager are distinct paths; the harness attributes reads by them.");
        }

        [TestMethod]
        public void UnchangedVsBaseline_NeverTriggers_HoweverStableTheScreenIs()
        {
            var gate = new OcrTriggerGate();

            for (int i = 0; i < 20; i++)
            {
                OcrTriggerGate.Decision d = gate.Evaluate(
                    Tick(vsPrev: 0.0, vsBaseline: 0.0, overWindow: 0.0, atSeconds: i));
                Assert.IsFalse(d.ReadyForOcr,
                    "Re-reading a frame identical to the last OCR baseline is pure waste.");
                Assert.IsFalse(d.Forced, "The timeout must not fire while the screen matches the baseline.");
            }
        }

        [TestMethod]
        public void MinStableFrames_FloorsAProfileAskingForLess()
        {
            var gate = new OcrTriggerGate();

            OcrTriggerGate.Decision d = gate.Evaluate(Tick(stableDefault: 1));

            Assert.AreEqual(OcrTriggerGate.MinStableFrames, d.StableFramesNeeded,
                "A profile or Config key asking for 1 must still be floored at 2.");
            Assert.IsFalse(d.ReadyForOcr, "One stable frame is below the floor even when the caller asked for 1.");
        }

        [TestMethod]
        public void ChainPrediction_UsesTheChainThreshold_NotTheDefaultOne()
        {
            var gate = new OcrTriggerGate();

            OcrTriggerGate.Decision withChain = gate.Evaluate(
                Tick(chain: true, stableChain: 2, stableDefault: 9));
            Assert.AreEqual(2, withChain.StableFramesNeeded);

            gate.Reset();
            OcrTriggerGate.Decision without = gate.Evaluate(
                Tick(chain: false, stableChain: 2, stableDefault: 9));
            Assert.AreEqual(9, without.StableFramesNeeded);
        }

        [TestMethod]
        public void ForcedPath_FiresOnlyAfterTheTimeoutStrictlyElapses()
        {
            var gate = new OcrTriggerGate();

            // Screen is churning: never stable vs previous, always changed vs baseline.
            OcrTriggerGate.Decision first = gate.Evaluate(Tick(vsPrev: 0.9, atSeconds: 0.0, forceAfter: 0.5));
            Assert.IsTrue(first.ChangedVsOcrJustStarted, "Divergence timer starts on the first changed tick.");
            Assert.IsFalse(first.ReadyForOcr);

            Assert.IsFalse(gate.Evaluate(Tick(vsPrev: 0.9, atSeconds: 0.4, forceAfter: 0.5)).Forced,
                "0.4 s has not exceeded a 0.5 s timeout.");

            Assert.IsFalse(gate.Evaluate(Tick(vsPrev: 0.9, atSeconds: 0.5, forceAfter: 0.5)).Forced,
                "The comparison is strictly greater-than, so exactly 0.5 s must not fire.");

            OcrTriggerGate.Decision fired = gate.Evaluate(Tick(vsPrev: 0.9, atSeconds: 0.6, forceAfter: 0.5));
            Assert.IsTrue(fired.Forced, "0.6 s exceeds a 0.5 s timeout.");
            Assert.IsTrue(fired.ReadyForOcr, "Forced implies ready.");
            Assert.IsFalse(fired.EagerPreview, "A forced read is not an eager preview.");
        }

        /// <summary>
        /// The strict &gt; comparison means the timeout can only be observed on a
        /// tick boundary, so ForceOcrAfterSeconds is quantised by the OCR
        /// interval. At a 100 ms tick, 0.05 s fires one tick EARLIER than 0.1 s:
        /// 0.1 &gt; 0.05 is true at the first tick, while 0.1 &gt; 0.1 is false and
        /// slips to the second. So 0.1 and 0.15 are the pair that behave
        /// identically — which is why the sweep saw them produce identical
        /// Genshin numbers, and why lowering the timeout below one tick period
        /// buys nothing without also lowering the interval.
        /// </summary>
        [DataTestMethod]
        [DataRow(0.15, 0.2, "below one tick, so the 0.2 s tick is the first that exceeds it")]
        [DataRow(0.10, 0.2, "exactly one tick cannot fire on strict >, so it waits for the next")]
        [DataRow(0.05, 0.1, "half a tick is exceeded by the very first tick")]
        [DataRow(0.25, 0.3, "between two and three ticks")]
        public void ForcedPath_IsQuantisedByTheTickPeriod(double forceAfter, double expectedFireSeconds, string why)
        {
            const double tickSeconds = 0.1;
            var gate = new OcrTriggerGate();

            double? firedAt = null;
            for (int tick = 0; tick <= 10 && firedAt == null; tick++)
            {
                double t = tick * tickSeconds;
                if (gate.Evaluate(Tick(vsPrev: 0.9, atSeconds: t, forceAfter: forceAfter)).Forced)
                    firedAt = t;
            }

            Assert.IsNotNull(firedAt, $"Timeout never fired for F={forceAfter} ({why}).");
            Assert.AreEqual(expectedFireSeconds, firedAt.Value, 1e-9, why);
        }

        [TestMethod]
        public void NotifyBaselineCommitted_RestartsTheDivergenceTimerFromTheNewRead()
        {
            var gate = new OcrTriggerGate();

            gate.Evaluate(Tick(vsPrev: 0.9, atSeconds: 0.0, forceAfter: 0.5));
            Assert.IsTrue(gate.Evaluate(Tick(vsPrev: 0.9, atSeconds: 0.6, forceAfter: 0.5)).Forced);

            gate.NotifyOcrStarted();
            gate.NotifyBaselineCommitted();

            // The clock keeps running, but the timer restarted, so 0.7 s absolute
            // is only 0.0 s since the new baseline.
            Assert.IsFalse(gate.Evaluate(Tick(vsPrev: 0.9, atSeconds: 0.7, forceAfter: 0.5)).Forced,
                "After a read commits, the timeout must measure from that read, not from the original divergence.");
            Assert.IsTrue(gate.Evaluate(Tick(vsPrev: 0.9, atSeconds: 1.4, forceAfter: 0.5)).Forced,
                "0.7 s after the new baseline exceeds the timeout again.");
        }

        [TestMethod]
        public void NotifyOcrStarted_ClearsTheConsecutiveStableCount()
        {
            var gate = new OcrTriggerGate();
            gate.Evaluate(Tick(vsPrev: null, vsBaseline: null));
            gate.Evaluate(Tick(vsPrev: 0.0, vsBaseline: null));
            Assert.AreEqual(2, gate.ConsecutiveStableFrames);

            gate.NotifyOcrStarted();

            Assert.AreEqual(0, gate.ConsecutiveStableFrames,
                "Without this reset the gate would re-fire on the very next stable tick.");
        }

        [TestMethod]
        public void MatchingTheBaselineAgain_ClearsTheDivergenceTimer()
        {
            var gate = new OcrTriggerGate();

            gate.Evaluate(Tick(vsPrev: 0.9, vsBaseline: 1.0, atSeconds: 0.0, forceAfter: 0.5));
            Assert.AreNotEqual(DateTime.MinValue, gate.ChangedVsOcrSince);

            gate.Evaluate(Tick(vsPrev: 0.9, vsBaseline: 0.0, atSeconds: 0.1, forceAfter: 0.5));
            Assert.AreEqual(DateTime.MinValue, gate.ChangedVsOcrSince,
                "A screen that returns to the baseline (fade out, menu close) must not keep a stale timeout armed.");

            Assert.IsFalse(gate.Evaluate(Tick(vsPrev: 0.9, vsBaseline: 1.0, atSeconds: 0.2, forceAfter: 0.5)).Forced,
                "The timer restarts from the re-divergence, not from the original one.");
        }

        [TestMethod]
        public void Reset_ClearsBothCounters()
        {
            var gate = new OcrTriggerGate();
            gate.Evaluate(Tick(vsPrev: 0.9, atSeconds: 0.0));
            gate.Evaluate(Tick(vsPrev: 0.0, atSeconds: 0.1));

            gate.Reset();

            Assert.AreEqual(0, gate.ConsecutiveStableFrames);
            Assert.AreEqual(DateTime.MinValue, gate.ChangedVsOcrSince);
        }

        [TestMethod]
        public void ChangeThreshold_IsInclusive_SoRatiosAtTheThresholdCountAsStable()
        {
            var gate = new OcrTriggerGate(0.01);

            OcrTriggerGate.Decision atThreshold = gate.Evaluate(Tick(vsPrev: 0.01, vsBaseline: 0.01));
            Assert.AreEqual(1, atThreshold.ConsecutiveStableFrames,
                "A change exactly at the threshold counts as stable.");
            Assert.IsFalse(atThreshold.ChangedVsOcr,
                "...and as unchanged versus the baseline, which is what stops a read.");

            gate.Reset();
            OcrTriggerGate.Decision above = gate.Evaluate(Tick(vsPrev: 0.011, vsBaseline: 0.011));
            Assert.AreEqual(0, above.ConsecutiveStableFrames);
            Assert.IsTrue(above.ChangedVsOcr);
        }

        [TestMethod]
        public void IncomparableFrames_CountAsMaximallyChanged()
        {
            var gate = new OcrTriggerGate();

            // PositiveInfinity is the documented "frame size or channel count
            // changed" signal — a resolution switch must not read as stable.
            OcrTriggerGate.Decision d = gate.Evaluate(
                Tick(vsPrev: double.PositiveInfinity, vsBaseline: double.PositiveInfinity));

            Assert.AreEqual(0, d.ConsecutiveStableFrames);
            Assert.IsTrue(d.ChangedVsOcr);
            Assert.IsFalse(d.ReadyForOcr);
        }
    }
}
