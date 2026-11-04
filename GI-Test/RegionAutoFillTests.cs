using GI_Subtitles.Services.Automation;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// ux-01: a player who set Kaption up with the game closed gets the dialogue area filled once, when the game is in
    /// front, from the game's measured ratios. A saved area is never replaced and a failed try is said on the card.
    /// </summary>
    [TestClass]
    public class RegionAutoFillTests
    {
        private static readonly RegionAutoFill.WindowRect FullHd = new(0, 0, 1920, 1080);

        [TestMethod]
        public void ShouldTry_OnlyWithNoArea_TheRegionGateAlone_AndTheGameReallyInFront()
        {
            for (int bits = 0; bits < 32; bits++)
            {
                bool saved = (bits & 1) != 0, blocked = (bits & 2) != 0, front = (bits & 4) != 0, decided = (bits & 8) != 0, busy = (bits & 16) != 0;
                bool expected = !saved && blocked && front && !decided && !busy;
                Assert.AreEqual(expected, RegionAutoFill.ShouldTry(saved, blocked, front, decided, busy),
                    $"saved={saved} blocked={blocked} front={front} decided={decided} busy={busy}");
            }
        }

        [TestMethod]
        public void Fills_AfterTheWindowKeepsOneSize_ForThreeProbes()
        {
            var fill = new RegionAutoFill();
            Assert.AreEqual(RegionAutoFillStep.Wait, fill.Observe(FullHd));
            Assert.AreEqual(RegionAutoFillStep.Wait, fill.Observe(FullHd));
            Assert.AreEqual(RegionAutoFillStep.Fill, fill.Observe(FullHd), "three probes, about 3 s, at one size");
        }

        [TestMethod]
        public void AWindowStillOpening_RestartsTheSteadyRun()
        {
            var fill = new RegionAutoFill();
            fill.Observe(new RegionAutoFill.WindowRect(0, 0, 1280, 720));
            fill.Observe(new RegionAutoFill.WindowRect(0, 0, 1280, 720));
            Assert.AreEqual(RegionAutoFillStep.Wait, fill.Observe(FullHd), "the switch to full screen starts over");
            Assert.AreEqual(RegionAutoFillStep.Wait, fill.Observe(FullHd));
            Assert.AreEqual(RegionAutoFillStep.Fill, fill.Observe(FullHd));
        }

        [TestMethod]
        public void LeavingTheFront_RestartsTheRun_ButKeepsTheProbesSpent()
        {
            var fill = new RegionAutoFill();
            fill.Observe(FullHd);
            fill.Observe(FullHd);
            fill.Interrupt();
            Assert.AreEqual(RegionAutoFillStep.Wait, fill.Observe(FullHd), "a steady run needs the game in front throughout");
            Assert.AreEqual(RegionAutoFillStep.Wait, fill.Observe(FullHd));
            Assert.AreEqual(RegionAutoFillStep.Fill, fill.Observe(FullHd));
        }

        [TestMethod]
        public void ASplashOrAMissingWindow_GivesUpAfterTheLimit_SoTheCardCanSaySo()
        {
            var fill = new RegionAutoFill();
            var splash = new RegionAutoFill.WindowRect(100, 100, 600, 340);
            for (int i = 1; i < RegionAutoFill.MaxProbes; i++)
                Assert.AreEqual(RegionAutoFillStep.Wait, fill.Observe(i % 2 == 0 ? splash : null), $"probe {i}");
            Assert.AreEqual(RegionAutoFillStep.GiveUp, fill.Observe(null));
            Assert.IsFalse(RegionAutoFill.IsUsable(splash), "under 640x360 is a launcher or a splash");
            Assert.IsTrue(RegionAutoFill.IsUsable(new RegionAutoFill.WindowRect(0, 0, 640, 360)));
        }

        [TestMethod]
        public void OneAttemptPerSession()
        {
            var fill = new RegionAutoFill();
            fill.MarkFailed();
            Assert.IsTrue(fill.Decided);
            Assert.AreEqual(RegionAutoFillStep.Wait, fill.Observe(FullHd), "after a decision nothing more is tried");
            Assert.IsFalse(RegionAutoFill.ShouldTry(regionSaved: false, blockedOnRegion: true, gameInFront: true, decided: fill.Decided, busy: false));
        }

        [TestMethod]
        public void NeverOverwritesASavedArea()
        {
            Assert.IsFalse(RegionAutoFill.MaySave(regionSavedNow: true, "0,0,10,10"), "the player drew one while the lookup ran");
            Assert.IsFalse(RegionAutoFill.MaySave(regionSavedNow: false, null), "nothing computed");
            Assert.IsTrue(RegionAutoFill.MaySave(regionSavedNow: false, "0,0,10,10"));
        }

        [TestMethod]
        public void TheNote_FollowsTheSavedArea()
        {
            var fill = new RegionAutoFill();
            Assert.AreEqual(RegionAutoFillNote.None, fill.NoteFor("", regionSaved: false), "nothing tried yet");
            fill.MarkSet("288,810,1344,216");
            Assert.AreEqual(RegionAutoFillNote.Set, fill.NoteFor("288,810,1344,216", regionSaved: true));
            Assert.AreEqual(RegionAutoFillNote.None, fill.NoteFor("300,800,1300,220", regionSaved: true), "the player fixed the frame: the note goes");

            var failed = new RegionAutoFill();
            failed.MarkFailed();
            Assert.AreEqual(RegionAutoFillNote.Failed, failed.NoteFor("", regionSaved: false));
            Assert.AreEqual(RegionAutoFillNote.None, failed.NoteFor("1,2,3,4", regionSaved: true), "drawn by hand afterwards: nothing to report");
        }

        [TestMethod]
        public void TheNote_SurvivesARestart_WithoutUsingUpTheTry()
        {
            var fill = new RegionAutoFill();
            fill.Restore("288,810,1344,216");
            Assert.AreEqual(RegionAutoFillNote.Set, fill.NoteFor("288,810,1344,216", regionSaved: true), "an earlier session filled it");
            Assert.AreEqual(RegionAutoFillNote.None, fill.NoteFor("300,800,1300,220", regionSaved: true), "redrawn since: hand-made now");
            Assert.IsFalse(fill.Decided, "if the area goes, this session may still fill it");

            var none = new RegionAutoFill();
            none.Restore(null);
            Assert.AreEqual(RegionAutoFillNote.None, none.NoteFor("288,810,1344,216", regionSaved: true));
            none.Restore("");
            Assert.IsNull(none.FilledRegion);
        }

        [TestMethod]
        public void TheArea_IsTheProfilesRatios_ClampedToTheDesktop()
        {
            var profile = GameRegionProfile.Get("Genshin");
            string region = RegionAutoFill.RegionFor(profile, FullHd, 0, 0, 1920, 1080);
            var (expected, _) = GameWindowDetector.CalculateFromRatios(profile, 0, 0, 1920, 1080);
            Assert.AreEqual(expected, region, "the same area setup saves when no dialogue is on screen");

            // A window half off the right edge of a one-monitor desktop: trimmed to what can be captured.
            string trimmed = RegionAutoFill.RegionFor(profile, new RegionAutoFill.WindowRect(1000, 0, 1920, 1080), 0, 0, 1920, 1080);
            Assert.IsNotNull(trimmed);
            string[] p = trimmed.Split(',');
            Assert.IsTrue(int.Parse(p[0]) + int.Parse(p[2]) <= 1920, trimmed);

            Assert.IsNull(RegionAutoFill.RegionFor(profile, new RegionAutoFill.WindowRect(5000, 5000, 1920, 1080), 0, 0, 1920, 1080), "off the desktop");
            Assert.IsNull(RegionAutoFill.RegionFor(profile, new RegionAutoFill.WindowRect(0, 0, 320, 200), 0, 0, 1920, 1080), "a splash");
            Assert.IsNull(RegionAutoFill.RegionFor(null, FullHd, 0, 0, 1920, 1080));
        }

        [TestMethod]
        public void TheCard_SaysTheAreaWasSet_OrThatTheTryFailed_WithThePicker()
        {
            var set = StartCardModel.Compute(new StartCardInputs
            {
                Translating = true, Reader = ReaderState.Ready, PackOnDisk = true, RegionAutoFill = RegionAutoFillNote.Set,
            });
            Assert.AreEqual(StartCardKind.Translating, set.Kind);
            Assert.IsTrue(set.ShowRegionAutoSetNote, "a frame Kaption chose is named, so a wrong one gets fixed");

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
