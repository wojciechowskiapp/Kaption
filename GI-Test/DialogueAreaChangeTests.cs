using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Automation;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Rendering;
using GI_Subtitles.Services.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// What the running app does when the dialogue area changes (auto-area PR 3a): the subtitle panel moves out of a
    /// frame Kaption placed, a frame placed inside a start starts translation once, and "Keep exactly my frame" keeps
    /// the player's frame as drawn. PR 3b: the panel is saved and applied against one centre, "Keep it" and "undone" are
    /// told apart, and a lookup that comes back after the game was switched is dropped.
    /// </summary>
    [TestClass]
    public class DialogueAreaChangeTests
    {
        private static readonly Rectangle Desktop = new(0, 0, 7680, 4320);

        // ── The panel and a placed frame ────────────────────────────────────

        /// <summary>
        /// The panel is placed from the area (MainWindow.UpdateWindowPosition, OverlayPlacement.WindowTop). For every
        /// game's measured frame and a full-screen window at the usual sizes, that place is clear of the frame, so the
        /// "subtitles cover the dialogue" gate never closes on a frame Kaption placed itself. The panel parked at the
        /// screen's bottom edge (where it waits when no area was saved at launch) is not: Wuthering Waves' frame
        /// reaches it, which is why every area change moves the panel.
        /// </summary>
        [TestMethod]
        public void APlacedFrame_NeverHasThePanelInsideIt_OnceThePanelFollowsTheFrame()
        {
            double referenceHeight = 0;
            RunSta(() => referenceHeight = SubtitleCard.ReferenceHeight(OverlayStyle.Default) + 10);
            Assert.IsTrue(referenceHeight > 20, "the one-line panel has a height");

            var sizes = new (Rectangle Client, double Scale)[]
            {
                (new Rectangle(0, 0, 1920, 1080), 1.0),
                (new Rectangle(0, 0, 2560, 1440), 1.25),
                (new Rectangle(0, 0, 3840, 2160), 1.5),
            };
            int checkedFrames = 0;
            foreach (GameRegionProfile profile in GameRegistry.Current.Profiles)
            {
                if (profile.DialogueText == null) continue;
                foreach (var (client, scale) in sizes)
                {
                    var placed = DialogueAreaPlacement.Place(profile, client, Desktop);
                    Assert.AreEqual(PlacementStatus.Placed, placed.Status, $"{profile.GameId} {client.Size}");
                    Rectangle area = placed.Area;
                    var frame = new System.Windows.Rect(area.X, area.Y, area.Width, area.Height);
                    // The idle strip and a one-line subtitle with a name: the two heights the panel has outside a long line.
                    foreach (double height in new[] { 20.0, referenceHeight })
                    {
                        double top = OverlayPlacement.WindowTop(area.Y / scale, area.Height / scale, profile.SubtitlePadVertical, referenceHeight, height);
                        var panel = new System.Windows.Rect(client.X, top * scale, client.Width, height * scale);
                        var check = OverlayRegionValidator.Check(panel, frame, System.Windows.Rect.Empty, System.Windows.Rect.Empty, overlayIsProjected: false);
                        Assert.IsFalse(check.HasOverlap, $"{profile.GameId} at {client.Width}x{client.Height}, panel {height:0} DIP high: {panel} inside {frame}");
                    }
                    checkedFrames++;
                }
            }
            Assert.IsTrue(checkedFrames >= 12, "every live game is checked");

            var wuwa = DialogueAreaPlacement.Place(GameRegionProfile.Get("WuWa"), new Rectangle(0, 0, 1920, 1080), Desktop).Area;
            var parked = new System.Windows.Rect(0, 1080 - 20, 1920, 20);
            Assert.IsTrue(OverlayRegionValidator.Check(parked, new System.Windows.Rect(wuwa.X, wuwa.Y, wuwa.Width, wuwa.Height),
                System.Windows.Rect.Empty, System.Windows.Rect.Empty, false).HasOverlap, "left parked, the panel sits inside WuWa's frame");
        }

        // ── A frame placed inside a start ───────────────────────────────────

        /// <summary>
        /// The Start button with nothing saved: its Region gate places the frame, and the store tells the running app.
        /// Told to the auto-start controller right there, the controller (game in front, every gate now open) started
        /// translation itself while the button's start was still running, and then the button started it again.
        /// </summary>
        [TestMethod]
        public void AFramePlacedInsideAStart_StartsTranslationOnce()
        {
            var posted = new Queue<Action>();
            var host = new StartPathHost();
            var controller = new AutoStartController(host, enabled: true);
            var recheck = new GateRecheck(() => controller.OnGatesChanged(), posted.Enqueue);
            void OnChanged(AreaChange _) => recheck.Request();

            var counters = DialogueAreaStore.Counters;
            DialogueAreaStore.Counters = null;
            DialogueAreaStore.Changed += OnChanged;
            try
            {
                ClearArea();
                controller.OnGameRunningChanged(true);
                controller.OnGameForeground(true);
                Assert.AreEqual(AutoStartState.Blocked, controller.State, "no frame yet: the Region gate holds auto-start");

                // The player presses Start (the button's path: the gates, then the controller hears of the user's start).
                host.StartFromButton();
                controller.OnUserStarted();
                while (posted.Count > 0) posted.Dequeue()();

                Assert.AreEqual(1, host.Starts, "one press, one start");
                Assert.AreEqual(AutoStartState.Translating, controller.State);
                Assert.IsFalse(controller.StartedByController, "the player's session: the game closing does not stop it");

                // Several saves in one pass ask the controller once.
                recheck.Request();
                recheck.Request();
                Assert.AreEqual(1, posted.Count);
            }
            finally
            {
                DialogueAreaStore.Changed -= OnChanged;
                DialogueAreaStore.Counters = counters;
                ClearArea();
            }
        }

        /// <summary>MainWindow's start path in small: the Region gate places the frame through the store, then the start.</summary>
        private sealed class StartPathHost : IAutoStartHost
        {
            public int Starts { get; private set; }
            public bool IsTranslationRunning { get; private set; }

            public StartGate FirstClosedGate() => DialogueArea.Parse(DialogueAreaStore.Region) == null ? StartGate.Region : StartGate.None;

            public bool StartThroughGates()
            {
                StartFromButton();
                return IsTranslationRunning;
            }

            public void StopTranslation() => IsTranslationRunning = false;

            public void StartFromButton()
            {
                if (IsTranslationRunning) return;
                if (DialogueArea.Parse(DialogueAreaStore.Region) == null)
                    DialogueAreaStore.Commit(new Rectangle(192, 712, 1536, 313), DialogueAreaMode.Auto, AreaOrigin.Placed, quiet: true,
                        new Rectangle(0, 0, 1920, 1080));
                Starts++;
                IsTranslationRunning = true;
            }
        }

        // ── "Keep exactly my frame" ─────────────────────────────────────────

        [TestMethod]
        public void KeepExactlyMyFrame_BringsBackTheDrawing_AndKaptionNeverWidensItAgain()
        {
            var counters = DialogueAreaStore.Counters;
            DialogueAreaStore.Counters = null;
            AreaChange? seen = null;
            void OnChanged(AreaChange c) => seen = c;
            DialogueAreaStore.Changed += OnChanged;
            try
            {
                ClearArea();
                var drawn = new Rectangle(500, 880, 900, 60);
                var widened = new Rectangle(320, 800, 1270, 210);
                DialogueAreaStore.CommitDrawing(drawn);
                Assert.AreEqual(DialogueAreaMode.Hand, DialogueAreaStore.Mode, "a drawing is a hint by default");
                DialogueAreaStore.Commit(widened, DialogueAreaMode.Hand, AreaOrigin.Widened, quiet: true);

                Assert.IsTrue(DialogueAreaStore.KeepExact(true));
                Assert.AreEqual(DialogueArea.Format(drawn), DialogueAreaStore.Region, "the frame the player drew, not Kaption's widen");
                Assert.AreEqual(DialogueAreaMode.Exact, DialogueAreaStore.Mode);
                Assert.AreEqual(AreaOrigin.Undone, seen?.Origin, "the drawing came back: counted apart from a plain choice");
                Assert.IsFalse(seen?.Quiet ?? true, "the player's own choice");

                // Never touched again: not on the next sight of the game, not by the tick, not by a fresh drawing.
                var placed = DialogueAreaPlacement.Place(GameRegionProfile.Get("Genshin"), new Rectangle(0, 0, 1920, 1080), Desktop);
                var d = DialogueAreaReconcile.Decide(drawn, DialogueAreaStore.Mode, new Rectangle(0, 0, 1920, 1080), placed.Area, placed.Text);
                Assert.AreEqual(AreaAction.Keep, d.Action);
                Assert.IsFalse(DialogueArea.MayPlace(new PlaceContext(DialogueAreaStore.Mode, true, false, false, false, false)));
                var redrawn = new Rectangle(510, 870, 880, 70);
                DialogueAreaStore.CommitDrawing(redrawn);
                Assert.AreEqual(DialogueAreaMode.Exact, DialogueAreaStore.Mode, "a new drawing keeps the player's choice");
                Assert.AreEqual(DialogueArea.Format(redrawn), DialogueAreaStore.Region);

                Assert.IsFalse(DialogueAreaStore.KeepExact(true), "already exact: nothing changes");
                Assert.IsTrue(DialogueAreaStore.KeepExact(false));
                Assert.AreEqual(DialogueAreaMode.Hand, DialogueAreaStore.Mode, "off again: the drawing is a hint");
                Assert.AreEqual(DialogueArea.Format(redrawn), DialogueAreaStore.Region, "turning it off keeps the frame where it is");

                // A new drawing ends the old widen: even drawn at the widen's very coordinates, "exactly mine" is now the
                // new drawing, never the older one the widen replaced.
                DialogueAreaStore.Commit(widened, DialogueAreaMode.Hand, AreaOrigin.Widened, quiet: true);
                Assert.IsTrue(DialogueAreaStore.KeepExact(true));
                DialogueAreaStore.CommitDrawing(widened);
                Assert.IsTrue(DialogueAreaStore.KeepExact(false));
                Assert.IsTrue(DialogueAreaStore.KeepExact(true));
                Assert.AreEqual(DialogueArea.Format(widened), DialogueAreaStore.Region, "the frame drawn last");
            }
            finally
            {
                DialogueAreaStore.Changed -= OnChanged;
                DialogueAreaStore.Counters = counters;
                ClearArea();
            }
        }

        // ── The panel's centre: saved and applied the same way ──────────────

        /// <summary>
        /// A drag saves a horizontal offset, an area change applies it again. Saved against the monitor's centre and
        /// applied against the game's, the panel jumped sideways at the next area change of a windowed game. Both now
        /// read the one centre, so a drag followed by an idle area change leaves the panel where it was dropped.
        /// </summary>
        [TestMethod]
        public void ADrag_ThenAnIdleAreaChange_LeavesThePanelWhereItWasDropped()
        {
            foreach (double scale in new[] { 1.0, 1.25, 1.5 })
            {
                var monitor = new Rectangle(0, 0, 2560, 1440);
                var game = new Rectangle(100, 120, 1920, 1080);     // windowed, not in the monitor's middle
                double centre = OverlayPlacement.CentreDip(monitor, scale, game);
                Assert.AreEqual((100 + 960) / scale, centre, 1e-9, "the panel hangs from the game's window");
                double width = 1536 / scale + 200;

                double dropped = centre - width / 2 + 173.4;         // the player drags it 173 DIPs to the right of centre
                int pad = OverlayPlacement.HorizontalPadFor(dropped, centre, width);
                // The frame is placed again for the same window (Followed, Detect): the same centre, the same place.
                double again = OverlayPlacement.WindowLeft(OverlayPlacement.CentreDip(monitor, scale, game), width, pad);
                Assert.AreEqual(dropped, again, 0.5, $"no jump at {scale:0.00}");

                // Measured from the monitor's centre (the old save) it would have jumped by the two centres' distance.
                double oldCentre = OverlayPlacement.CentreDip(monitor, scale, null);
                int oldPad = OverlayPlacement.HorizontalPadFor(dropped, oldCentre, width);
                Assert.IsTrue(Math.Abs(OverlayPlacement.WindowLeft(centre, width, oldPad) - dropped) > 20, "the old pair disagreed");

                // The window moves later: the panel moves with it by the same distance.
                var moved = game with { X = game.X + 200 };
                Assert.AreEqual(dropped + 200 / scale, OverlayPlacement.WindowLeft(OverlayPlacement.CentreDip(monitor, scale, moved), width, pad), 0.5);
            }
        }

        /// <summary>A windowed game across two monitors: the panel goes where the game's middle is, not where the frame starts.</summary>
        [TestMethod]
        public void AGameAcrossTwoMonitors_GetsThePanelOnTheMonitorHoldingItsMiddle()
        {
            var monitors = new[] { new Rectangle(0, 0, 1920, 1080), new Rectangle(1920, 0, 1920, 1080) };
            var game = new Rectangle(1000, 0, 1920, 1080);          // middle at x 1960: the right monitor
            var frameStart = new Point(1160, 700);                  // the frame starts on the left one
            Assert.AreEqual(1, OverlayPlacement.MonitorFor(monitors, frameStart, game), "the game's middle decides");
            Assert.AreEqual(0, OverlayPlacement.MonitorFor(monitors, frameStart, null), "a frame kept by hand: where it starts");
            Assert.AreEqual(0, OverlayPlacement.MonitorFor(monitors, frameStart, new Rectangle(0, 0, 1920, 1080)));
            Assert.AreEqual(-1, OverlayPlacement.MonitorFor(monitors, new Point(5000, 5000), new Rectangle(4000, 4000, 640, 360)), "no monitor holds either");

            // The centre is the game's on the monitor that holds it, the monitor's own on any other.
            Assert.AreEqual(1960.0, OverlayPlacement.CentreDip(monitors[1], 1.0, game), 1e-9);
            Assert.AreEqual(1920 + 960.0, OverlayPlacement.CentreDip(monitors[1], 1.0, null), 1e-9);
            Assert.AreEqual(960.0, OverlayPlacement.CentreDip(monitors[0], 1.0, game), 1e-9, "the game's middle is not on this monitor");
        }

        // ── "Keep it", "undone" and the counters ────────────────────────────

        [TestMethod]
        public void KeepIt_EndsTheWidenNote_AndUndoneIsCountedApartFromChosen()
        {
            string dir = Path.Combine(Path.GetTempPath(), "kaption-area-3b-" + Guid.NewGuid().ToString("N"));
            var counters = DialogueAreaStore.Counters;
            var mine = new DialogueAreaCounters(g => Path.Combine(dir, g + ".json"), TimeSpan.Zero);
            DialogueAreaStore.Counters = mine;
            string game = Config.Get<string>("Game", "Genshin");
            try
            {
                ClearArea();
                var drawn = new Rectangle(500, 880, 900, 60);
                var widened = new Rectangle(320, 800, 1270, 210);

                // Undone: the drawing comes back, and that is its own count.
                DialogueAreaStore.CommitDrawing(drawn);
                DialogueAreaStore.Commit(widened, DialogueAreaMode.Hand, AreaOrigin.Widened, quiet: true);
                Assert.IsTrue(DialogueAreaStore.IsWidened);
                Assert.IsTrue(DialogueAreaStore.KeepExact(true));
                Assert.AreEqual(1, mine.Get(game, "areas", "undone"));
                Assert.AreEqual(0, mine.Get(game, "areas", "chosen"), "a way back is not a plain choice");
                Assert.IsFalse(DialogueAreaStore.IsWidened);

                // A plain choice (off, then on again on the same frame) stays "chosen".
                Assert.IsTrue(DialogueAreaStore.KeepExact(false));
                Assert.IsTrue(DialogueAreaStore.KeepExact(true));
                Assert.AreEqual(2, mine.Get(game, "areas", "chosen"));
                Assert.AreEqual(1, mine.Get(game, "areas", "undone"));

                // Keep it: the widen is accepted, the note and the way back to the small drawing are gone, the frame stays a hint.
                DialogueAreaStore.CommitDrawing(drawn);
                DialogueAreaStore.KeepExact(false);
                DialogueAreaStore.Commit(widened, DialogueAreaMode.Hand, AreaOrigin.Widened, quiet: true);
                Assert.IsTrue(DialogueAreaStore.KeepWidened());
                Assert.IsFalse(DialogueAreaStore.IsWidened, "the Start card's note goes");
                Assert.AreEqual(DialogueArea.Format(widened), DialogueAreaStore.Region, "the frame itself is untouched");
                Assert.AreEqual(DialogueAreaMode.Hand, DialogueAreaStore.Mode, "still a hint");
                Assert.AreEqual(1, mine.Get(game, "areas", DialogueAreaCounters.KeptWidened));
                Assert.IsFalse(DialogueAreaStore.KeepWidened(), "nothing left to keep");

                // Exactly mine afterwards means this frame, not the smaller one Kaption replaced.
                Assert.IsTrue(DialogueAreaStore.KeepExact(true));
                Assert.AreEqual(DialogueArea.Format(widened), DialogueAreaStore.Region);
                Assert.AreEqual(1, mine.Get(game, "areas", "undone"), "no second way back was counted");

                // Keep mine on a warning: that very frame is not warned about again, a new drawing is.
                DialogueAreaStore.KeepExact(false);
                DialogueAreaStore.KeepMine();
                Assert.IsTrue(DialogueAreaStore.IsKeptMine);
                DialogueAreaStore.CommitDrawing(drawn);
                Assert.IsFalse(DialogueAreaStore.IsKeptMine);
            }
            finally
            {
                DialogueAreaStore.Counters = counters;
                ClearArea();
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        }

        // ── A lookup that comes back too late ───────────────────────────────

        /// <summary>
        /// Detect looks the window up off the UI thread. A game picked meanwhile (the frame and its mode unchanged) used
        /// to be saved with the old game's frame; a drawing made meanwhile is just as new.
        /// </summary>
        [TestMethod]
        public void ALookupStartedBeforeAGameSwitch_IsNotCurrentWhenItComesBack()
        {
            string game = Config.Get<string>("Game", "Genshin");
            var counters = DialogueAreaStore.Counters;
            DialogueAreaStore.Counters = null;
            try
            {
                ClearArea();
                Config.Set("Game", "Genshin");
                DialogueAreaStore.Commit(new Rectangle(192, 712, 1536, 313), DialogueAreaMode.Auto, AreaOrigin.Placed, quiet: true);
                var before = AreaSnapshot.Take();
                Assert.IsTrue(before.IsCurrent, "nothing happened meanwhile");

                Config.Set("Game", "StarRail");
                Assert.IsFalse(before.IsCurrent, "another game was picked: its answer is for the old one");
                Config.Set("Game", "Genshin");
                Assert.IsTrue(before.IsCurrent, "and back");

                DialogueAreaStore.CommitDrawing(new Rectangle(500, 880, 900, 60));
                Assert.IsFalse(before.IsCurrent, "a drawing made meanwhile is newer");
            }
            finally
            {
                Config.Set("Game", game);
                DialogueAreaStore.Counters = counters;
                ClearArea();
            }
        }

        /// <summary>
        /// A frame saved by something other than the tick (Detect, setup) says which window it was placed for: the caches
        /// then hold that window and no other, so a frame placed for another window is never taken for current.
        /// </summary>
        [TestMethod]
        public void AnOutsidePlacement_MarksTheCaches_ForThatWindowOnly()
        {
            var follow = new DialogueAreaFollow();
            var watch = new DialogueAreaWatch();
            var a = new Rectangle(0, 0, 1920, 1080);
            var b = new Rectangle(100, 100, 1280, 720);
            follow.MarkPlaced(a);
            watch.MarkDecided(a);

            DialogueArea.SyncCaches(new AreaChange("1,1,1,1", DialogueAreaMode.Auto, AreaOrigin.Placed, false, b), follow, watch);
            Assert.IsTrue(follow.IsPlacedFor(b));
            Assert.IsFalse(follow.IsPlacedFor(a), "the window it moved away from is not current any more");
            // The watch has decided b already, and a is a window it has to settle on again.
            for (int i = 0; i < DialogueAreaWatch.StableProbes; i++)
                Assert.AreEqual(WatchStep.Wait, watch.Observe(GameWindowState.Found, b), "b was decided for: not asked again");
            for (int i = 0; i < DialogueAreaWatch.StableProbes; i++)
                Assert.AreEqual(i < DialogueAreaWatch.StableProbes - 1 ? WatchStep.Wait : WatchStep.Ready, watch.Observe(GameWindowState.Found, a),
                    "a is a new window for the watch");

            // Placed for no window, or drawn: both look again.
            DialogueArea.SyncCaches(new AreaChange("1,1,1,1", DialogueAreaMode.Hand, AreaOrigin.Drawn, false, default), follow, watch);
            Assert.IsFalse(follow.IsPlacedFor(b));
        }

        private static void ClearArea()
        {
            foreach (string key in new[] { DialogueAreaStore.RegionKey, DialogueAreaStore.ModeKey, DialogueAreaStore.FilledKey,
                                           DialogueAreaStore.WidenedKey, DialogueAreaStore.PreviousKey, DialogueAreaStore.AcceptedKey })
                Config.Set(key, "");
        }
    }
}
