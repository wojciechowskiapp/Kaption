using System.Drawing;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The dialogue area is the game's measured block on its window, placed again when the window moves, and a box the
    /// player drew is a hint (design `.plan/research/s68/AUTO-DIALOGUE-AREA.md` 2.4 and 2.5). These guard the rules a
    /// player meets: what is placed, what is kept, and when nothing may be touched.
    /// </summary>
    [TestClass]
    public class DialogueAreaTests
    {
        private static readonly Rectangle Desktop = new(0, 0, 3840, 2160);
        private static readonly Rectangle FullHd = new(0, 0, 1920, 1080);

        private static GameRegionProfile Genshin => GameRegionProfile.Get("Genshin");

        private static (Rectangle Block, Rectangle Text) On(Rectangle client)
        {
            var placed = DialogueAreaPlacement.Place(Genshin, client, Desktop);
            Assert.AreEqual(PlacementStatus.Placed, placed.Status);
            return (placed.Area, placed.Text);
        }

        // ── The reconcile table (2.4), row by row ───────────────────────────

        [TestMethod]
        public void NothingSaved_PlacesTheBlock_OnceTheWindowIsSeen()
        {
            var (block, text) = On(FullHd);
            var d = DialogueAreaReconcile.Decide(null, DialogueAreaMode.Auto, FullHd, block, text);
            Assert.AreEqual(AreaAction.Place, d.Action);
            Assert.AreEqual(block, d.Area);

            var closed = DialogueAreaReconcile.Decide(null, DialogueAreaMode.Auto, null, default, default);
            Assert.AreEqual(AreaAction.Wait, closed.Action, "game closed: nothing to place yet");
        }

        [TestMethod]
        public void Automatic_PlacesTheBlockAgain_WhenTheWindowMoved_AndKeepsItWhenNothingChanged()
        {
            var (oldBlock, _) = On(FullHd);
            var moved = new Rectangle(200, 100, 1920, 1080);
            var (block, text) = On(moved);

            var d = DialogueAreaReconcile.Decide(oldBlock, DialogueAreaMode.Auto, moved, block, text);
            Assert.AreEqual(AreaAction.Place, d.Action);
            Assert.AreEqual(block, d.Area);

            Assert.AreEqual(AreaAction.Keep, DialogueAreaReconcile.Decide(block, DialogueAreaMode.Auto, moved, block, text).Action);
        }

        [TestMethod]
        public void ExactlyAsDrawn_IsNeverChanged()
        {
            var (block, text) = On(FullHd);
            var twoLines = new Rectangle(500, 880, 900, 60); // cuts the name and the last row
            foreach (Rectangle? client in new Rectangle?[] { FullHd, null, new Rectangle(300, 300, 1280, 720) })
            {
                var d = DialogueAreaReconcile.Decide(twoLines, DialogueAreaMode.Exact, client, block, text);
                Assert.AreEqual(AreaAction.Keep, d.Action, $"client {client}");
                Assert.AreEqual(twoLines, d.Area);
            }
        }

        [TestMethod]
        public void ADrawingThatCutsTheText_GrowsToHoldIt_AndEdgesThatCutNothingStay()
        {
            var (block, text) = On(FullHd);
            // Wider than the text on the left, but the top cuts the speaker's name and the bottom the last row.
            var drawn = new Rectangle(text.Left - 40, text.Top + 30, text.Width + 40, text.Height - 60);

            var d = DialogueAreaReconcile.Decide(drawn, DialogueAreaMode.Hand, FullHd, block, text);
            Assert.AreEqual(AreaAction.Widen, d.Action);
            Assert.IsTrue(d.Area.Contains(text), "the whole measured text fits now");
            Assert.AreEqual(drawn.Left, d.Area.Left, "the left edge cut nothing: it stays where the player put it");
            Assert.AreEqual(text.Top, d.Area.Top, "the top grows only as far as the name");
            Assert.AreEqual(text.Bottom, d.Area.Bottom);
            Assert.IsTrue(block.Contains(d.Area), "growth never goes past the game's block");
        }

        [TestMethod]
        public void ADrawingThatHoldsTheText_IsKept()
        {
            var (block, text) = On(FullHd);
            var drawn = Rectangle.Inflate(text, 10, 10);
            var d = DialogueAreaReconcile.Decide(drawn, DialogueAreaMode.Hand, FullHd, block, text);
            Assert.AreEqual(AreaAction.Keep, d.Action);
            Assert.AreEqual(AreaVerdict.Fits, d.Verdict);
            Assert.AreEqual(drawn, d.Area);
        }

        [TestMethod]
        public void AFarTooBigDrawing_IsKept_AndNamed()
        {
            var (block, text) = On(FullHd);
            var half = new Rectangle(0, 300, 1920, 780); // its centre is above the block: holding the text decides first
            var d = DialogueAreaReconcile.Decide(half, DialogueAreaMode.Hand, FullHd, block, text);
            Assert.AreEqual(AreaAction.Keep, d.Action);
            Assert.AreEqual(AreaVerdict.TooBig, d.Verdict);
        }

        [TestMethod]
        public void ADrawingSomewhereElse_IsKept_ThePlayerMeansOtherText()
        {
            var (block, text) = On(FullHd);
            var questLog = new Rectangle(40, 200, 400, 300);
            var d = DialogueAreaReconcile.Decide(questLog, DialogueAreaMode.Hand, FullHd, block, text);
            Assert.AreEqual(AreaAction.Keep, d.Action);
            Assert.AreEqual(AreaVerdict.Elsewhere, d.Verdict);

            var otherMonitor = new Rectangle(2400, 900, 800, 200);
            Assert.AreEqual(AreaVerdict.OutsideGame, DialogueAreaReconcile.Decide(otherMonitor, DialogueAreaMode.Hand, FullHd, block, text).Verdict);
        }

        [TestMethod]
        public void ADrawingMadeWithTheGameClosed_WaitsForTheWindow()
        {
            var drawn = new Rectangle(500, 880, 900, 60);
            var d = DialogueAreaReconcile.Decide(drawn, DialogueAreaMode.Hand, null, default, default);
            Assert.AreEqual(AreaAction.Wait, d.Action);
            Assert.AreEqual(drawn, d.Area, "kept as drawn until the window is seen");
        }

        [TestMethod]
        public void TheMode_OfAnOlderInstall_GoesByWhatItSaved()
        {
            Assert.AreEqual(DialogueAreaMode.Auto, DialogueArea.ModeOf(null, "", null), "nothing saved: automatic");
            Assert.AreEqual(DialogueAreaMode.Auto, DialogueArea.ModeOf(null, "288,810,1344,216", "288,810,1344,216"), "Kaption's own fill");
            Assert.AreEqual(DialogueAreaMode.Hand, DialogueArea.ModeOf(null, "300,800,1300,220", "288,810,1344,216"), "drawn, or saved by an older Detect");
            Assert.AreEqual(DialogueAreaMode.Exact, DialogueArea.ModeOf("exact", "300,800,1300,220", null));
            Assert.AreEqual(DialogueAreaMode.Hand, DialogueArea.ModeOf("bogus", "300,800,1300,220", null), "a hand-edited value reads by the area");
        }

        // ── Placement ───────────────────────────────────────────────────────

        [TestMethod]
        public void AMove_KeepsTheSize_SoTheReaderKeepsItsShape()
        {
            var (a, _) = On(new Rectangle(0, 0, 1600, 900));
            var (b, _) = On(new Rectangle(731, 412, 1600, 900));
            Assert.AreEqual(a.Size, b.Size);
            Assert.AreEqual(new Point(a.X + 731, a.Y + 412), b.Location);
        }

        [TestMethod]
        public void APlacement_IsTheBlockThatDetectSaves()
        {
            foreach (GameRegionProfile game in GameRegionProfile.RegisteredProfiles)
            {
                var placed = DialogueAreaPlacement.Place(game, FullHd, Desktop);
                var (expected, _) = GameWindowDetector.CalculateFromRatios(game, 0, 0, 1920, 1080);
                Assert.AreEqual(expected, DialogueArea.Format(placed.Area), game.GameId);
                Assert.IsTrue(placed.Area.Contains(placed.Text), game.GameId);
            }
        }

        [TestMethod]
        public void AWindowTooSmall_OrHangingOffTheScreen_IsNotPlaced_NeverASliver()
        {
            Assert.AreEqual(PlacementStatus.Unusable, DialogueAreaPlacement.Place(Genshin, new Rectangle(0, 0, 600, 340), Desktop).Status,
                "a launcher or a splash");
            var oneScreen = new Rectangle(0, 0, 1920, 1080);
            // Most of the window, and with it the dialogue, is below the screen.
            Assert.AreEqual(PlacementStatus.OffScreen, DialogueAreaPlacement.Place(Genshin, new Rectangle(0, 900, 1920, 1080), oneScreen).Status);
            // A bit of margin off the left edge only: the text is still whole, so it is placed and trimmed.
            var trimmed = DialogueAreaPlacement.Place(Genshin, new Rectangle(-200, 0, 1920, 1080), oneScreen);
            Assert.AreEqual(PlacementStatus.Placed, trimmed.Status);
            Assert.IsTrue(trimmed.Area.Left >= 0);
        }

        [TestMethod]
        public void TextInTheGapBetweenTwoMonitors_IsNotPlaced()
        {
            // A 1440p monitor beside a 1080p one: the desktop's bounding box has a corner no monitor shows.
            var monitors = new[] { new Rectangle(0, 0, 2560, 1440), new Rectangle(2560, 0, 1920, 1080) };
            var desktop = new Rectangle(0, 0, 4480, 1440);
            var window = new Rectangle(2560, 400, 1920, 1080); // dragged down on the smaller monitor
            Assert.AreEqual(PlacementStatus.Placed, DialogueAreaPlacement.Place(Genshin, window, desktop).Status,
                "the bounding box alone would accept it");
            Assert.AreEqual(PlacementStatus.OffScreen, DialogueAreaPlacement.Place(Genshin, window, desktop, monitors).Status);
            Assert.AreEqual(PlacementStatus.Placed, DialogueAreaPlacement.Place(Genshin, new Rectangle(2560, 0, 1920, 1080), desktop, monitors).Status);
        }

        [TestMethod]
        public void TextOverAShorterMiddleMonitor_IsNotPlaced_ThoughEveryCornerIsOnAMonitor()
        {
            // Three top-aligned monitors, the middle one shorter: the text's corners are on the outer two, its middle in
            // the gap under the middle monitor.
            var monitors = new[] { new Rectangle(0, 0, 1920, 1440), new Rectangle(1920, 0, 800, 600), new Rectangle(2720, 0, 1920, 1440) };
            var desktop = new Rectangle(0, 0, 4640, 1440);
            var spanning = new Rectangle(900, 300, 2800, 1100);
            var placed = DialogueAreaPlacement.Place(Genshin, spanning, desktop, monitors);
            Assert.IsTrue(placed.Text.Left < 1920 && placed.Text.Right > 2720 && placed.Text.Top > 600, "the setup is what it says");
            Assert.AreEqual(PlacementStatus.OffScreen, placed.Status);
        }

        // ── The follow rule while translating ───────────────────────────────

        [TestMethod]
        public void ADrag_PlacesNothing_UntilTheWindowIsStill()
        {
            var follow = new DialogueAreaFollow();
            follow.MarkPlaced(FullHd);
            Assert.AreEqual(FollowStep.Read, follow.Observe(FullHd, areaFitsClient: true));

            for (int x = 10; x < 200; x += 10)
                Assert.AreEqual(FollowStep.Hold, follow.Observe(new Rectangle(x, 0, 1920, 1080), areaFitsClient: false), $"x={x}: still moving");

            var settled = new Rectangle(200, 0, 1920, 1080);
            for (int i = 1; i < DialogueAreaFollow.StillTicks; i++)
                Assert.AreEqual(FollowStep.Hold, follow.Observe(settled, areaFitsClient: false), $"still for {i} ticks");
            Assert.AreEqual(FollowStep.Place, follow.Observe(settled, areaFitsClient: false));

            follow.MarkPlaced(settled);
            Assert.AreEqual(FollowStep.Read, follow.Observe(settled, areaFitsClient: true));
        }

        [TestMethod]
        public void TheFirstLook_AdoptsAnAreaThatAlreadyFits_WithoutHoldingAnyRead()
        {
            var follow = new DialogueAreaFollow();
            Assert.AreEqual(FollowStep.Read, follow.Observe(FullHd, areaFitsClient: true), "last session's area on the same window");
            Assert.IsTrue(follow.IsPlacedFor(FullHd));
        }

        // ── When nothing may be placed ──────────────────────────────────────

        [TestMethod]
        public void NothingIsPlaced_UnderTheDevSwitch_InACloudSession_OrOverExactlyAsDrawn()
        {
            var ok = new PlaceContext(DialogueAreaMode.Auto, KnownGame: true, HandDecided: false, DevSkipGameGate: false, CloudSession: false, Busy: false);
            Assert.IsTrue(DialogueArea.MayPlace(ok));
            Assert.IsFalse(DialogueArea.MayPlace(ok with { DevSkipGameGate = true }), "footage in a video player is not the game's window");
            Assert.IsFalse(DialogueArea.MayPlace(ok with { CloudSession = true }), "no local game window");
            Assert.IsFalse(DialogueArea.MayPlace(ok with { Mode = DialogueAreaMode.Exact }));
            Assert.IsFalse(DialogueArea.MayPlace(ok with { KnownGame = false }), "no measured block");
            Assert.IsFalse(DialogueArea.MayPlace(ok with { Busy = true }), "a picker or Ctrl+Q is open");
            Assert.IsTrue(DialogueArea.MayPlace(ok with { Mode = DialogueAreaMode.Hand }), "a drawing is checked once a session");
            Assert.IsFalse(DialogueArea.MayPlace(ok with { Mode = DialogueAreaMode.Hand, HandDecided = true }), "and then left alone");
        }

        // ── The probe watch while translation is off ────────────────────────

        [TestMethod]
        public void TheWatch_WaitsForThreeSteadyProbes_AndAMinimisedGameNeverUsesUpTheTry()
        {
            var watch = new DialogueAreaWatch();
            for (int i = 0; i < DialogueAreaWatch.MaxProbes * 2; i++)
                Assert.AreEqual(WatchStep.Wait, watch.Observe(GameWindowState.Minimised, default), "minimised is not missing");

            Assert.AreEqual(WatchStep.Wait, watch.Observe(GameWindowState.Found, FullHd));
            Assert.AreEqual(WatchStep.Wait, watch.Observe(GameWindowState.Found, FullHd));
            Assert.AreEqual(WatchStep.Ready, watch.Observe(GameWindowState.Found, FullHd));
            watch.MarkDecided(FullHd);
            Assert.AreEqual(WatchStep.Wait, watch.Observe(GameWindowState.Found, FullHd), "decided for this window");

            var resized = new Rectangle(0, 0, 2560, 1440);
            watch.Observe(GameWindowState.Found, resized);
            watch.Observe(GameWindowState.Found, resized);
            Assert.AreEqual(WatchStep.Ready, watch.Observe(GameWindowState.Found, resized), "a new steady window is decided again");
        }

        [TestMethod]
        public void AWindowTheFrameCannotBePlacedOn_IsNotAskedAgain_AndStillLetsTheCardOfferThePicker()
        {
            var watch = new DialogueAreaWatch();
            var offScreen = new Rectangle(0, 900, 1920, 1080);
            watch.Observe(GameWindowState.Found, offScreen);
            watch.Observe(GameWindowState.Found, offScreen);
            Assert.AreEqual(WatchStep.Ready, watch.Observe(GameWindowState.Found, offScreen));
            watch.MarkUnplaceable(offScreen);
            WatchStep last = WatchStep.Wait;
            for (int i = 3; i < DialogueAreaWatch.MaxProbes; i++)
            {
                last = watch.Observe(GameWindowState.Found, offScreen);
                Assert.AreNotEqual(WatchStep.Ready, last, "not asked again for the same window");
            }
            Assert.AreEqual(WatchStep.GiveUp, last, "nothing was placed: the card offers the picker");
        }

        [TestMethod]
        public void TheWatch_GivesUp_OnlyWhenTheGameNeverShowsAUsableWindow()
        {
            var watch = new DialogueAreaWatch();
            var splash = new Rectangle(100, 100, 600, 340);
            for (int i = 1; i < DialogueAreaWatch.MaxProbes; i++)
                Assert.AreEqual(WatchStep.Wait, watch.Observe(i % 2 == 0 ? GameWindowState.Found : GameWindowState.NotFound, splash));
            Assert.AreEqual(WatchStep.GiveUp, watch.Observe(GameWindowState.NotFound, default));
            Assert.AreEqual(WatchStep.Wait, watch.Observe(GameWindowState.NotFound, default), "said once");
        }

        // ── Local counters ──────────────────────────────────────────────────

        [TestMethod]
        public void TheCounters_KeepCountsOnly_AndTheFirstRunOnce()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kaption-tests", "area-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var counters = new DialogueAreaCounters(game => System.IO.Path.Combine(dir, game, DialogueAreaCounters.FileName), System.TimeSpan.Zero);
                counters.CountArea("Genshin", AreaOrigin.Placed, 1920, 1200);
                counters.CountArea("Genshin", AreaOrigin.Drawn, 0, 0);
                var first = new FirstRunReport("placed", "auto", "16:10", false, false, 1, 12, "under 1 min", false);
                Assert.IsTrue(counters.RecordFirstRun("Genshin", first));
                Assert.IsFalse(counters.RecordFirstRun("Genshin", first with { MatchedLines = 99 }), "the first run is written once");

                var reread = new DialogueAreaCounters(game => System.IO.Path.Combine(dir, game, DialogueAreaCounters.FileName), System.TimeSpan.Zero);
                Assert.AreEqual(1, reread.Get("Genshin", "shapes", "16:10"));
                Assert.AreEqual(1, reread.Get("Genshin", "areas", "drawn"));
                Assert.AreEqual(12, reread.FirstRun("Genshin").MatchedLines);
                string json = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "Genshin", DialogueAreaCounters.FileName));
                Assert.IsFalse(json.Contains("1920") || json.Contains("1200"), "buckets, never coordinates or sizes");
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, recursive: true); } catch (System.IO.IOException) { }
            }
        }

        // ── The one store ───────────────────────────────────────────────────

        [TestMethod]
        public void TheStore_SavesTheAreaAndItsMode_AndTellsTheApp()
        {
            AreaChange? seen = null;
            void Handler(AreaChange c) => seen = c;
            DialogueAreaStore.Changed += Handler;
            var counters = DialogueAreaStore.Counters;
            DialogueAreaStore.Counters = null;
            try
            {
                Config.Set(DialogueAreaStore.ModeKey, "");
                var area = new Rectangle(192, 712, 1536, 313);
                DialogueAreaStore.Commit(area, DialogueAreaMode.Auto, AreaOrigin.Placed, quiet: true);
                Assert.AreEqual("192,712,1536,313", Config.Get<string>(DialogueAreaStore.RegionKey, ""));
                Assert.AreEqual(DialogueAreaMode.Auto, DialogueAreaStore.Mode);
                Assert.AreEqual("192,712,1536,313", Config.Get<string>(DialogueAreaStore.FilledKey, ""), "Kaption's own area is remembered");
                Assert.IsTrue(seen?.Quiet == true && seen?.Region == "192,712,1536,313");

                var widened = new Rectangle(180, 700, 1560, 330);
                var drawn = new Rectangle(300, 760, 1300, 200);
                DialogueAreaStore.Commit(drawn, DialogueAreaMode.Hand, AreaOrigin.Drawn, quiet: false);
                DialogueAreaStore.Commit(widened, DialogueAreaMode.Hand, AreaOrigin.Widened, quiet: true);
                Assert.AreEqual(DialogueAreaMode.Hand, DialogueAreaStore.Mode, "widened is still the player's");
                Assert.AreEqual("300,760,1300,200", Config.Get<string>(DialogueAreaStore.PreviousKey, ""), "the way back");
                Assert.AreEqual(AreaOrigin.Widened, seen?.Origin);

                DialogueAreaStore.Commit(new Rectangle(170, 690, 1580, 340), DialogueAreaMode.Hand, AreaOrigin.Widened, quiet: true);
                Assert.AreEqual("300,760,1300,200", Config.Get<string>(DialogueAreaStore.PreviousKey, ""),
                    "widening Kaption's own widen again keeps the player's drawing as the way back");
                widened = new Rectangle(170, 690, 1580, 340);

                seen = null;
                DialogueAreaStore.Commit(widened, DialogueAreaMode.Hand, AreaOrigin.Drawn, quiet: false);
                Assert.IsNull(seen, "the same area in the same mode again changes nothing and resets nothing");
            }
            finally
            {
                DialogueAreaStore.Changed -= Handler;
                DialogueAreaStore.Counters = counters;
                Config.Set(DialogueAreaStore.RegionKey, "");
                Config.Set(DialogueAreaStore.WidenedKey, "");
                Config.Set(DialogueAreaStore.ModeKey, "");
                Config.Set(DialogueAreaStore.FilledKey, "");
                Config.Set(DialogueAreaStore.PreviousKey, "");
            }
        }
    }
}
