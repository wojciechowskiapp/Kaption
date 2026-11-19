using GI_Subtitles.Services.Automation;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// What the Start card says about the dialogue area Kaption keeps by itself (design 2.4): its own frame is named so
    /// a wrong one gets fixed, a drawing it widened is said, and a game that never showed a usable window offers the
    /// picker instead of a silent closed gate. The placing rules themselves are in <see cref="DialogueAreaTests"/>.
    /// </summary>
    [TestClass]
    public class RegionAutoFillTests
    {
        [TestMethod]
        public void TheCard_NamesKaptionsOwnFrame_AndAWidenedDrawing()
        {
            var set = StartCardModel.Compute(new StartCardInputs
            {
                Translating = true, Reader = ReaderState.Ready, PackOnDisk = true, RegionAutoFill = RegionAutoFillNote.Set,
            });
            Assert.AreEqual(StartCardKind.Translating, set.Kind);
            Assert.IsTrue(set.ShowRegionAutoSetNote, "a frame Kaption chose is named, so a wrong one gets fixed");
            Assert.IsFalse(set.RegionAdjusted);

            var widened = StartCardModel.Compute(new StartCardInputs
            {
                Reader = ReaderState.Ready, PackOnDisk = true, RegionAutoFill = RegionAutoFillNote.Adjusted,
            });
            Assert.IsTrue(widened.ShowRegionAutoSetNote && widened.RegionAdjusted, "a drawing Kaption changed is never changed silently");
        }

        /// <summary>The note's two answers: Kaption's widen is kept or undone; a frame that is too big or outside is replaced or kept.</summary>
        [TestMethod]
        public void TheNote_OffersTwoAnswers_ForAWidenedFrame_AndForTheTwoWarnings()
        {
            Assert.AreEqual((RegionNoteAction.KeepWidened, RegionNoteAction.KeepExact), StartCardModel.NoteActions(RegionAutoFillNote.Adjusted));
            foreach (var note in new[] { RegionAutoFillNote.TooBig, RegionAutoFillNote.Outside })
            {
                Assert.AreEqual((RegionNoteAction.UseGameFrame, RegionNoteAction.KeepMine), StartCardModel.NoteActions(note));
                var card = StartCardModel.Compute(new StartCardInputs { Reader = ReaderState.Ready, PackOnDisk = true, RegionAutoFill = note });
                Assert.IsTrue(card.ShowRegionAutoSetNote);
                Assert.AreEqual(note, card.RegionNote);
                Assert.IsFalse(card.RegionAdjusted, "only a widen says Kaption adjusted the frame");
            }
            Assert.IsNull(StartCardModel.NoteActions(RegionAutoFillNote.Set), "Kaption's own frame has nothing to answer");
            Assert.IsNull(StartCardModel.NoteActions(RegionAutoFillNote.None));
        }

        /// <summary>While Kaption keeps the frame, no state sends the player to fix it.</summary>
        [TestMethod]
        public void InAutomaticMode_NoCheckSendsThePlayerToTheFrame()
        {
            var quiet = StartCardModel.Compute(new StartCardInputs
            {
                Translating = true, Reader = ReaderState.Ready, PackOnDisk = true, SessionCheck = SessionCheckKind.NoDialogue, AreaAutomatic = true,
            });
            Assert.AreEqual(StartCardKind.SessionNoDialogue, quiet.Kind);
            Assert.AreEqual(StartCardAction.Stop, quiet.Action, "the way to the frame is for a frame kept by hand");
            Assert.IsFalse(StartCardModel.OffersAreaAsSecondFix(StartCardKind.SessionNoMatch, areaAutomatic: true));
            Assert.IsFalse(StartCardModel.OffersAreaAsSecondFix(StartCardKind.SessionFullScreen, areaAutomatic: true));
            Assert.IsTrue(StartCardModel.OffersAreaAsSecondFix(StartCardKind.SessionNoMatch), "by hand it still does");
        }

        [TestMethod]
        public void TheCard_OffersThePicker_WhenNoGameWindowWasEverUsable()
        {
            var failed = StartCardModel.Compute(new StartCardInputs
            {
                Reader = ReaderState.Ready, PackOnDisk = true, AutoStartBlockedBy = StartGate.Region, RegionAutoFill = RegionAutoFillNote.Failed,
            });
            Assert.AreEqual(StartCardKind.AutoStartBlocked, failed.Kind);
            Assert.IsTrue(failed.RegionAutoFailed, "never a silent closed gate");
            Assert.AreEqual(StartCardAction.ResolveGate, failed.Action, "the Region gate's fix is the picker");
            Assert.AreEqual(StartGate.Region, failed.Gate);

            var otherGate = StartCardModel.Compute(new StartCardInputs
            {
                Reader = ReaderState.Ready, PackOnDisk = true, AutoStartBlockedBy = StartGate.DisplayModeTip, RegionAutoFill = RegionAutoFillNote.Failed,
            });
            Assert.IsFalse(otherGate.RegionAutoFailed, "only the Region gate talks about the area");
        }
    }
}
