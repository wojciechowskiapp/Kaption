using System;
using System.Drawing;
using System.Linq;
using GI_Subtitles.Services.Capture;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The read check decides whether the subtitle overlay may sit on the text the app reads, so what matters here is
    /// that it can never answer "out of the frame" without proof: each test is one way the proof could be missing.
    /// </summary>
    [TestClass]
    public class OverlayReadCheckTests
    {
        /// <summary>
        /// A screen as the check sees it: an overlay with a strip, something that takes the overlay out of the reader's
        /// frames (at once, late, or never), frames that arrive or do not, and a window that can move or go away.
        /// </summary>
        private sealed class World : IOverlayReadHost
        {
            public long Now;
            public bool StripCanBeDrawn = true;
            public bool HidingWorks = true;
            /// <summary>Hidden from the plain screen copy as well (Windows hides the window from every capture).</summary>
            public bool HidesFromGdiToo;
            /// <summary>Frames that pass before a show or hide request takes effect.</summary>
            public int ApplyAfterFrames;
            public bool LiftRefused;
            public bool FramesArrive = true;
            /// <summary>Frames still to come before they stop arriving (null = no end).</summary>
            public int? FramesLeft;
            public Rectangle? Area = new Rectangle(100, 50, StripCode.Width, StripCode.Height);
            /// <summary>Called each time the check asks to hide the overlay: where a test moves or removes things.</summary>
            public Action<World> OnHide;
            /// <summary>Frames (counted from the last request) in which the strip shows whatever the hiding says.</summary>
            public Func<int, bool> ShowsAnyway;
            /// <summary>Frames (counted from the last request) that are new pictures the reader could not hand over.</summary>
            public Func<int, bool> Unreadable;
            /// <summary>Called after each new frame.</summary>
            public Action<World> OnFrame;
            /// <summary>Something is over the strip, or the panel has faded: its place is the same and nothing shows it.</summary>
            public bool Covered;

            public bool Hidden;
            /// <summary>The overlay starts out hidden from the reader, as after an earlier run.</summary>
            public bool StartHidden { set { Hidden = value; _effectiveHidden = value; } }
            public bool StripDrawn;
            public int HideRequests;
            private long _frame = 1;
            private int _framesSinceRequest;
            public int FramesSinceRequest => _framesSinceRequest;
            private bool _effectiveHidden;
            private bool _pending;

            public long NowMs => Now;

            public void ShowStrip(bool shown) => StripDrawn = shown && StripCanBeDrawn;

            public bool SetHidden(bool hidden)
            {
                if (!hidden && LiftRefused) return false;
                if (hidden) { HideRequests++; OnHide?.Invoke(this); }
                Hidden = hidden;
                _framesSinceRequest = 0;
                _pending = true;
                Settle();
                return true;
            }

            private void Settle()
            {
                if (_pending && _framesSinceRequest >= ApplyAfterFrames)
                {
                    _effectiveHidden = Hidden && HidingWorks;
                    _pending = false;
                }
            }

            public Rectangle? StripArea() => Area;

            public StripLook ReadThroughReader(Rectangle area)
            {
                if (Unreadable != null && Unreadable(_framesSinceRequest)) return new StripLook(StripSight.NoFrame, _frame);
                bool forced = ShowsAnyway != null && ShowsAnyway(_framesSinceRequest);
                bool seen = StripDrawn && !Covered && Area == area && (!_effectiveHidden || forced);
                return new StripLook(seen ? StripSight.Seen : StripSight.NotSeen, _frame);
            }

            public StripSight ReadThroughGdi(Rectangle area) =>
                StripDrawn && !Covered && Area == area && !(_effectiveHidden && HidesFromGdiToo) ? StripSight.Seen : StripSight.NotSeen;

            public void Wait()
            {
                Now += 8;
                if (FramesLeft == 0) FramesArrive = false;
                if (!FramesArrive) return;
                if (FramesLeft > 0) FramesLeft--;
                _frame++;
                _framesSinceRequest++;
                Settle();
                OnFrame?.Invoke(this);
            }
        }

        [TestMethod]
        public void Out_of_the_readers_frames_and_still_on_screen_is_proven()
        {
            var world = new World();
            OverlayReadResult result = OverlayReadCheck.Run(world);
            Assert.AreEqual(OverlayReadState.OutOfFrame, result.State, result.Reason);
            Assert.AreEqual(3, result.VerdictFrames.Count);
            Assert.IsTrue(result.VerdictFrames.All(f => f > result.SwitchFrame), "only frames taken after the switch count");
            Assert.AreEqual(3, result.VerdictFrames.Distinct().Count(), "three different frames, not one read three times");
            Assert.IsFalse(world.StripDrawn, "the strip is taken off again");
            Assert.IsTrue(world.Hidden, "the overlay is left hidden from the reader");
        }

        [TestMethod]
        public void Hiding_that_takes_a_while_still_passes()
        {
            var world = new World { ApplyAfterFrames = 20 };
            Assert.AreEqual(OverlayReadState.OutOfFrame, OverlayReadCheck.Run(world).State);
        }

        [TestMethod]
        public void An_overlay_that_stays_in_the_frames_is_in_frame()
        {
            var world = new World { HidingWorks = false };
            OverlayReadResult result = OverlayReadCheck.Run(world);
            Assert.AreEqual(OverlayReadState.InFrame, result.State, result.Reason);
            Assert.IsFalse(world.StripDrawn);
        }

        [TestMethod]
        public void A_strip_that_is_never_seen_proves_nothing()
        {
            var world = new World { StripCanBeDrawn = false };
            Assert.AreEqual(OverlayReadState.Unknown, OverlayReadCheck.Run(world).State);
        }

        [TestMethod]
        public void An_overlay_windows_will_not_show_again_proves_nothing()
        {
            var world = new World { StartHidden = true, LiftRefused = true };
            Assert.AreEqual(OverlayReadState.Unknown, OverlayReadCheck.Run(world).State);
        }

        [TestMethod]
        public void Without_new_frames_nothing_is_known()
        {
            var world = new World { FramesArrive = false };
            Assert.AreEqual(OverlayReadState.Unknown, OverlayReadCheck.Run(world).State);
        }

        [TestMethod]
        public void Frames_that_stop_after_the_switch_do_not_count_as_gone()
        {
            // The strip is not in the old frame's place any more as far as the world goes, but no frame after the switch
            // ever arrives: reading the same frame again and again must not add up to a verdict.
            var world = new World();
            world.OnHide = w => w.FramesArrive = false;
            OverlayReadResult result = OverlayReadCheck.Run(world);
            Assert.AreEqual(OverlayReadState.Unknown, result.State, result.Reason);
        }

        [TestMethod]
        public void Too_few_new_frames_after_the_switch_prove_nothing()
        {
            // Two frames after the switch miss the strip and then the screen stands still: not three, so no verdict,
            // and not "in frame" either, because nothing showed the strip.
            var world = new World();
            world.OnHide = w => w.FramesLeft = 2;
            OverlayReadResult result = OverlayReadCheck.Run(world);
            Assert.AreEqual(OverlayReadState.Unknown, result.State, result.Reason);
        }

        [TestMethod]
        public void A_window_that_moved_after_the_control_is_not_taken_for_gone()
        {
            var world = new World { HidingWorks = false };
            world.OnHide = w => { if (w.HideRequests == 1) w.Area = new Rectangle(140, 54, StripCode.Width, StripCode.Height); };
            OverlayReadResult result = OverlayReadCheck.Run(world);
            Assert.AreEqual(OverlayReadState.InFrame, result.State, result.Reason);
            Assert.AreEqual(1, result.Restarts);
        }

        [TestMethod]
        public void A_window_that_moved_once_is_checked_again_at_its_new_place()
        {
            var world = new World();
            world.OnHide = w => { if (w.HideRequests == 1) w.Area = new Rectangle(140, 54, StripCode.Width, StripCode.Height); };
            OverlayReadResult result = OverlayReadCheck.Run(world);
            Assert.AreEqual(OverlayReadState.OutOfFrame, result.State, result.Reason);
            Assert.AreEqual(1, result.Restarts);
        }

        [TestMethod]
        public void A_window_that_keeps_moving_proves_nothing()
        {
            var world = new World();
            world.OnHide = w => w.Area = new Rectangle(100 + 10 * w.HideRequests, 50, StripCode.Width, StripCode.Height);
            OverlayReadResult result = OverlayReadCheck.Run(world);
            Assert.AreEqual(OverlayReadState.Unknown, result.State, result.Reason);
        }

        [TestMethod]
        public void A_window_that_went_away_after_the_control_proves_nothing()
        {
            var world = new World { HidingWorks = false };
            world.OnHide = w => w.Area = null;
            Assert.AreEqual(OverlayReadState.Unknown, OverlayReadCheck.Run(world).State);
        }

        [TestMethod]
        public void A_strip_taken_away_after_the_control_is_not_taken_for_gone()
        {
            // Nothing hides the overlay here; the strip simply stops being drawn. The reader would miss it, and so does
            // the plain screen copy: no proof.
            var world = new World { HidingWorks = false };
            world.OnHide = w => w.StripDrawn = false;
            OverlayReadResult result = OverlayReadCheck.Run(world);
            Assert.AreEqual(OverlayReadState.Unknown, result.State, result.Reason);
        }

        [TestMethod]
        public void A_strip_covered_while_the_reader_is_watched_proves_nothing()
        {
            // Nothing hides the overlay here either. The strip is on the screen when the hiding is asked for; with the
            // next frame something covers it, or the window fades: its place stays the same and the reader's new frames
            // miss it. Only the plain screen copy tells that it was not there to be seen. For good, or for just as long
            // as the check counts its frames.
            foreach (int? backAfter in new int?[] { null, 4 })
            {
                var world = new World { HidingWorks = false };
                world.OnFrame = w => { if (w.Hidden) w.Covered = backAfter == null || w.FramesSinceRequest < backAfter; };
                OverlayReadResult result = OverlayReadCheck.Run(world);
                Assert.AreNotEqual(OverlayReadState.OutOfFrame, result.State, result.Reason);
                Assert.AreEqual(1, world.HideRequests, "no second try that could pass by repeating itself");
            }
        }

        [TestMethod]
        public void A_strip_that_a_plain_screen_copy_misses_too_proves_nothing()
        {
            // Hidden from every capture, faded or covered: the reader misses the strip, but so does everything else,
            // and "missed" is then no proof that the reader is kept out. Whether it applies at once or late.
            foreach (int late in new[] { 0, 6 })
            {
                var world = new World { HidesFromGdiToo = true, ApplyAfterFrames = late };
                OverlayReadResult result = OverlayReadCheck.Run(world);
                Assert.AreEqual(OverlayReadState.Unknown, result.State, result.Reason);
                Assert.AreEqual(1, world.HideRequests, "no second try that could pass by repeating itself");
            }
        }

        [TestMethod]
        public void A_frame_that_shows_the_strip_starts_the_count_again()
        {
            // Frames 1 and 2 after the request are clean, frame 3 shows the strip once more, then it stays gone.
            var world = new World { ShowsAnyway = sinceRequest => sinceRequest == 3 };
            OverlayReadResult result = OverlayReadCheck.Run(world);
            Assert.AreEqual(OverlayReadState.OutOfFrame, result.State, result.Reason);
            long first = result.VerdictFrames[0];
            CollectionAssert.AreEqual(new[] { first, first + 1, first + 2 }, result.VerdictFrames.ToArray(), "three frames in a row");
            Assert.IsTrue(first - result.SwitchFrame > 3, "the clean frames before the one that showed the strip do not count");
        }

        [TestMethod]
        public void A_new_picture_that_could_not_be_read_starts_the_count_again()
        {
            // Frame 1 after the request is clean, frame 2 is a new picture the reader could not hand over, and nobody
            // knows what it showed. Two more clean frames are not three in a row; three more are.
            var world = new World { Unreadable = sinceRequest => sinceRequest == 2 };
            world.OnHide = w => w.FramesLeft = 4;
            OverlayReadResult result = OverlayReadCheck.Run(world);
            Assert.AreEqual(OverlayReadState.Unknown, result.State, "clean, unread, clean, clean: " + result.Reason);

            world = new World { Unreadable = sinceRequest => sinceRequest == 2 };
            result = OverlayReadCheck.Run(world);
            Assert.AreEqual(OverlayReadState.OutOfFrame, result.State, result.Reason);
            Assert.IsTrue(result.VerdictFrames.All(f => f - result.SwitchFrame > 2), "only frames after the unread one count");
        }

        [TestMethod]
        public void The_whole_check_gives_up_after_its_time_limit()
        {
            // Each of the two switches takes most of a second, and together they are over the limit for the whole check.
            var world = new World { ApplyAfterFrames = 115, StartHidden = true };
            var limits = new OverlayReadLimits { StepMs = 1000, TotalMs = 1200 };
            OverlayReadResult result = OverlayReadCheck.Run(world, limits);
            Assert.AreEqual(OverlayReadState.Unknown, result.State, result.Reason);
            Assert.IsTrue(result.ElapsedMs <= limits.TotalMs + 50, $"stopped at {result.ElapsedMs} ms");
            Assert.IsFalse(world.StripDrawn);
        }

        // ---------------------------------------------------------------- the strip's colours

        private static byte[] Strip((byte B, byte G, byte R) left, (byte B, byte G, byte R) right)
        {
            var pixels = new byte[StripCode.Width * StripCode.Height * 4];
            for (int y = 0; y < StripCode.Height; y++)
            for (int x = 0; x < StripCode.Width; x++)
            {
                var c = x < StripCode.Width / 2 ? left : right;
                int i = (y * StripCode.Width + x) * 4;
                pixels[i] = c.B; pixels[i + 1] = c.G; pixels[i + 2] = c.R; pixels[i + 3] = 255;
            }
            return pixels;
        }

        [TestMethod]
        public void The_strip_is_magenta_then_cyan()
        {
            Assert.IsTrue(StripCode.Matches(Strip((255, 0, 255), (255, 255, 0)), StripCode.Width, StripCode.Height));
            Assert.IsTrue(StripCode.Matches(Strip((236, 22, 240), (241, 233, 18)), StripCode.Width, StripCode.Height), "a little off is still the strip");
        }

        [TestMethod]
        public void Anything_else_is_not_the_strip()
        {
            Assert.IsFalse(StripCode.Matches(Strip((0, 200, 0), (0, 200, 0)), StripCode.Width, StripCode.Height), "the game's picture");
            Assert.IsFalse(StripCode.Matches(Strip((255, 255, 0), (255, 0, 255)), StripCode.Width, StripCode.Height), "the halves the other way round");
            Assert.IsFalse(StripCode.Matches(Strip((255, 0, 255), (128, 128, 128)), StripCode.Width, StripCode.Height), "one half only");
            Assert.IsFalse(StripCode.Matches(Strip((255, 0, 255), (255, 0, 255)), StripCode.Width, StripCode.Height), "magenta all over");
            Assert.IsFalse(StripCode.Matches(new byte[12], StripCode.Width, StripCode.Height), "too few pixels");
        }
    }
}
