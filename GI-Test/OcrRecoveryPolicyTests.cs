using System;
using GI_Subtitles.Services.OCR;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Escalation rules behind MainWindow.ScheduleOcrRuntimeRecovery, and the
    /// version-scoping rule behind the persistent GPU quarantine. Both were
    /// extracted from the window specifically so the 2026-08-17 field failure
    /// (intermittent DirectML stalls with successes interleaved) has a
    /// regression test that runs without a GPU.
    /// </summary>
    [TestClass]
    public class OcrRecoveryPolicyTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 17, 3, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void RecordTimeout_TwoConsecutiveTimeouts_TriggersRecovery()
        {
            var policy = new OcrRecoveryPolicy();

            Assert.IsFalse(policy.RecordTimeout(T0).ShouldRecover,
                "One timeout is not yet evidence of a stalled provider.");
            OcrRecoveryDecision second = policy.RecordTimeout(T0.AddSeconds(16));

            Assert.IsTrue(second.ShouldRecover);
            Assert.AreEqual(2, second.ConsecutiveTimeouts);
            Assert.AreEqual(2, second.TimeoutsInWindow);
        }

        [TestMethod]
        public void RecordTimeout_ThreeInsideWindowWithSuccessesBetween_TriggersRecovery()
        {
            // The exact shape that used to slip past the consecutive-only rule:
            // every successful frame reset the counter, so a provider stalling
            // two frames out of three never escalated.
            var policy = new OcrRecoveryPolicy();

            Assert.IsFalse(policy.RecordTimeout(T0).ShouldRecover);
            policy.RecordSuccess();
            Assert.IsFalse(policy.RecordTimeout(T0.AddSeconds(40)).ShouldRecover);
            policy.RecordSuccess();

            OcrRecoveryDecision third = policy.RecordTimeout(T0.AddSeconds(80));

            Assert.IsTrue(third.ShouldRecover,
                "Three timeouts inside 120s must escalate even with successes interleaved.");
            Assert.AreEqual(1, third.ConsecutiveTimeouts,
                "A success immediately before the timeout leaves the consecutive counter at one.");
            Assert.AreEqual(3, third.TimeoutsInWindow);
        }

        [TestMethod]
        public void RecordTimeout_ThreeSpreadBeyondWindow_DoesNotTrigger()
        {
            var policy = new OcrRecoveryPolicy();

            policy.RecordTimeout(T0);
            policy.RecordSuccess();
            policy.RecordTimeout(T0.AddSeconds(70));
            policy.RecordSuccess();

            // At T0+140s the first timeout is 140s old and drops out of the
            // 120s window, leaving only two.
            OcrRecoveryDecision third = policy.RecordTimeout(T0.AddSeconds(140));

            Assert.IsFalse(third.ShouldRecover,
                "Occasional timeouts minutes apart are normal load, not a broken provider.");
            Assert.AreEqual(2, third.TimeoutsInWindow);
        }

        [TestMethod]
        public void RecordSuccess_KeepsRollingWindowButClearsConsecutiveCounter()
        {
            var policy = new OcrRecoveryPolicy();

            policy.RecordTimeout(T0);
            policy.RecordSuccess();

            Assert.AreEqual(0, policy.ConsecutiveTimeouts);
            Assert.AreEqual(1, policy.CountTimeoutsInWindow(T0.AddSeconds(1)),
                "One good frame must not erase the evidence of the stall before it.");
        }

        [TestMethod]
        public void CountTimeoutsInWindow_PrunesEntriesOlderThanTheWindow()
        {
            var policy = new OcrRecoveryPolicy();

            policy.RecordTimeout(T0);
            policy.RecordSuccess();

            Assert.AreEqual(1, policy.CountTimeoutsInWindow(T0.AddSeconds(119)));
            Assert.AreEqual(0, policy.CountTimeoutsInWindow(T0.AddSeconds(121)));
        }

        [TestMethod]
        public void CountTimeoutsInWindow_DropsEntriesFromABackwardsClockJump()
        {
            var policy = new OcrRecoveryPolicy();

            policy.RecordTimeout(T0.AddSeconds(30));

            Assert.AreEqual(0, policy.CountTimeoutsInWindow(T0),
                "A timeout stamped in the future can only come from a clock adjustment.");
        }

        [TestMethod]
        public void Reset_ClearsBothTriggers()
        {
            var policy = new OcrRecoveryPolicy();

            policy.RecordTimeout(T0);
            policy.RecordTimeout(T0.AddSeconds(16));
            policy.Reset();

            Assert.AreEqual(0, policy.ConsecutiveTimeouts);
            Assert.AreEqual(0, policy.CountTimeoutsInWindow(T0.AddSeconds(17)));
            Assert.IsFalse(policy.RecordTimeout(T0.AddSeconds(30)).ShouldRecover,
                "A freshly rebuilt engine is judged on its own frames.");
        }

        [TestMethod]
        public void GpuQuarantine_IsActiveOnlyForTheVersionThatRecordedIt()
        {
            Assert.IsTrue(OcrGpuQuarantine.IsActive(true, "2.1.1.0", "2.1.1.0"));
            Assert.IsFalse(OcrGpuQuarantine.IsActive(true, "2.1.1.0", "2.1.2.0"),
                "Every release gets one fresh DirectML attempt — it may be the fix.");
            Assert.IsFalse(OcrGpuQuarantine.IsActive(false, "2.1.1.0", "2.1.1.0"));
            Assert.IsFalse(OcrGpuQuarantine.IsActive(true, null, "2.1.1.0"),
                "A flag with no recorded version cannot be scoped, so it must not block the GPU.");
            Assert.IsFalse(OcrGpuQuarantine.IsActive(true, "   ", "2.1.1.0"));
            Assert.IsFalse(OcrGpuQuarantine.IsActive(true, "2.1.1.0", null));
        }
    }
}
