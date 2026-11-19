using GI_Subtitles.Services.Capture;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// What the app holds about the read check. The panel stops being blacked out of a read only while the proof holds
    /// for the reader that read is made with, so each test is one way an old proof could outlive what it was about.
    /// </summary>
    [TestClass]
    public class OverlayReadProofTests
    {
        private static readonly object ReaderA = new(), ReaderB = new();

        /// <summary>Over the text is asked for, and a check through reader A in its setup 3 said "out of the frame".</summary>
        private static OverlayReadProof Proven(long now = 1_000)
        {
            var proof = new OverlayReadProof();
            proof.SettingsSay(true, now);
            Assert.IsTrue(proof.End(proof.Begin(), OverlayReadState.OutOfFrame, ReaderA, 3, now));
            return proof;
        }

        [TestMethod]
        public void NothingIsProvenAndNothingIsDue_UntilTheSettingsAskAndACheckSaysSo()
        {
            var proof = new OverlayReadProof();
            Assert.IsFalse(proof.Holds(ReaderA, 0));
            Assert.IsFalse(proof.Due(long.MaxValue - 1), "a player without the setting never gets a check");
            proof.TranslationStarted(500);
            Assert.IsFalse(proof.Due(long.MaxValue - 1), "not at a start either");

            Assert.IsTrue(proof.SettingsSay(true, 1_000));
            Assert.IsTrue(proof.Due(1_000), "asked for: a check is due at once");
            Assert.IsFalse(proof.Holds(ReaderA, 0), "asking is not proof");
        }

        [TestMethod]
        public void AProofHolds_OnlyForTheReaderAndTheSetupItWasProvenFor()
        {
            OverlayReadProof proof = Proven();
            Assert.IsTrue(proof.Holds(ReaderA, 3));
            Assert.IsFalse(proof.Holds(ReaderB, 3), "another reader (Desktop Duplication lost, the screen copy taken)");
            Assert.IsFalse(proof.Holds(ReaderA, 4), "the same reader, set up again");
            Assert.IsFalse(proof.Holds(null, 3));
            Assert.IsFalse(proof.Due(long.MaxValue - 1), "proven: nothing more to ask");
        }

        [TestMethod]
        public void AChangedSetting_TakesTheProofAway_WhateverWindowsWasOrWasNotTold()
        {
            // Recordings switched on while translating: the overlay is in screen capture again from that moment.
            OverlayReadProof proof = Proven();
            Assert.IsTrue(proof.SettingsSay(false, 2_000));
            Assert.AreEqual(OverlayReadState.Unknown, proof.State);
            Assert.IsFalse(proof.Holds(ReaderA, 3));
            Assert.IsFalse(proof.Due(long.MaxValue - 1), "and no check either: nothing is asked for");

            Assert.IsTrue(proof.SettingsSay(true, 3_000), "switched off again");
            Assert.IsTrue(proof.Due(3_000));
            Assert.IsFalse(proof.Holds(ReaderA, 3), "what was proven before the change does not come back");
            Assert.IsFalse(proof.SettingsSay(true, 4_000), "the same answer again changes nothing");
        }

        [TestMethod]
        public void AStart_TakesTheProofAway_AndTheFirstTickChecksAgain()
        {
            OverlayReadProof proof = Proven();
            proof.TranslationStarted(5_000);
            Assert.IsFalse(proof.Holds(ReaderA, 3));
            Assert.IsTrue(proof.Due(5_000));
            Assert.IsTrue(proof.FirstSinceStart);
            proof.End(proof.Begin(), OverlayReadState.OutOfFrame, ReaderA, 3, 5_100);
            Assert.IsFalse(proof.FirstSinceStart);
            Assert.IsTrue(proof.Holds(ReaderA, 3));
        }

        [TestMethod]
        public void ACheckThatProvedNothing_IsTriedAgain_LaterEachTime_UpToTheCap()
        {
            var proof = new OverlayReadProof();
            proof.SettingsSay(true, 0);
            long now = 0;
            long[] waits = new long[6];
            for (int i = 0; i < waits.Length; i++)
            {
                Assert.IsTrue(proof.Due(now));
                proof.End(proof.Begin(), OverlayReadState.Unknown, ReaderA, 0, now);
                Assert.IsFalse(proof.Due(now), "not in the same tick again");
                waits[i] = proof.DueAtMs - now;
                now = proof.DueAtMs;
            }
            CollectionAssert.AreEqual(new long[] { 10_000, 20_000, 40_000, 80_000, 120_000, 120_000 }, waits);
            Assert.AreEqual(6, proof.ChecksWithoutProof);
            Assert.IsFalse(proof.Holds(ReaderA, 0));
        }

        [TestMethod]
        public void AReaderThatStillSeesTheOverlay_IsNotAskedAgainOnATimer()
        {
            var proof = new OverlayReadProof();
            proof.SettingsSay(true, 0);
            proof.End(proof.Begin(), OverlayReadState.InFrame, ReaderA, 0, 100);
            Assert.AreEqual(OverlayReadState.InFrame, proof.State);
            Assert.IsFalse(proof.Due(long.MaxValue - 1), "asking every few seconds would only hold the capture tick");
            proof.TranslationStarted(200);
            Assert.IsTrue(proof.Due(200), "the next start asks again");
        }

        [TestMethod]
        public void ACheckOvertakenByAChange_ProvesNothing()
        {
            // The check lets other handlers in while it waits; a setting changed or a new reader arrived meanwhile.
            var proof = new OverlayReadProof();
            proof.SettingsSay(true, 0);
            long ticket = proof.Begin();
            proof.Forget(50);
            Assert.IsFalse(proof.End(ticket, OverlayReadState.OutOfFrame, ReaderA, 0, 100), "its answer is about a state that is gone");
            Assert.IsFalse(proof.Holds(ReaderA, 0));
            Assert.IsTrue(proof.Due(100), "and the check that the change asked for is still due");

            ticket = proof.Begin();
            proof.SettingsSay(false, 150);
            Assert.IsFalse(proof.End(ticket, OverlayReadState.OutOfFrame, ReaderA, 0, 200));
            Assert.IsFalse(proof.Holds(ReaderA, 0));
        }

        [TestMethod]
        public void TheAppReadingItsOwnLine_TakesTheProofAway_UntilTheNextStart()
        {
            OverlayReadProof proof = Proven();
            proof.Tripped();
            Assert.AreEqual(OverlayReadState.InFrame, proof.State);
            Assert.IsFalse(proof.Holds(ReaderA, 3));
            Assert.IsFalse(proof.Due(long.MaxValue - 1));
            proof.Forget(8_000);
            Assert.IsFalse(proof.Due(long.MaxValue - 1), "another reader does not get a check either: the last one was wrong");
            Assert.AreEqual(OverlayReadState.InFrame, proof.State);
            proof.TranslationStarted(9_000);
            Assert.IsTrue(proof.Due(9_000));
        }
    }
}
