using System.Drawing;
using GI_Subtitles.Services.Hints;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// When the hint card's three buttons can be clicked (Services/Hints/HintInteractivity). The card appears by
    /// itself over a game, so its buttons must never be where a click meant for the game lands: they open only for
    /// a visible pointer that has come to rest on the card.
    /// </summary>
    [TestClass]
    public class HintInteractivityTests
    {
        private static readonly Rectangle Card = new(16, 466, 300, 190);
        // Above the card's top right corner, a small gap between them.
        private static readonly Rectangle Strip = new(216, 430, 100, 30);
        private const int Slop = 4;
        /// <summary>A place in the game, away from the card and its strip.</summary>
        private static readonly Point? InTheGame = new Point(900, 300);

        /// <summary>One look every 200 ms at where the pointer is; <see cref="Hidden"/> is a pointer the game has hidden.</summary>
        private static HintPointerState Watch(HintPointerState state, ref long clock, params Point?[] looks)
        {
            foreach (Point? at in looks)
            {
                state = HintInteractivity.Next(state, at.HasValue, at ?? Point.Empty, Card, Strip, clock, Slop);
                clock += HintInteractivity.TickMs;
            }
            return state;
        }

        private static Point? On(int x, int y) => new Point(x, y);
        private static readonly Point? Hidden = null;

        [TestMethod]
        public void AHiddenPointer_NeverOpensTheStrip_AndClosesItAtOnce()
        {
            long clock = 1000;
            // The game is in mouse-look: the pointer is parked on the card's spot, unseen, for two seconds.
            var state = Watch(default, ref clock, Hidden, Hidden, Hidden, Hidden, Hidden, Hidden, Hidden, Hidden, Hidden, Hidden);
            Assert.IsFalse(state.StripOpen);
            Assert.IsFalse(state.Holds, "an unseen pointer does not pause the card's clock either");

            // Open, then the player goes back to the game and the pointer disappears.
            state = Watch(default, ref clock, InTheGame, On(100, 500), On(100, 500), On(100, 500));
            Assert.IsTrue(state.StripOpen);
            state = Watch(state, ref clock, Hidden);
            Assert.IsFalse(state.StripOpen, "nothing clickable may stay under a pointer the player cannot see");

            // A menu opens and the pointer comes into view right on the card: it did not travel there.
            state = Watch(state, ref clock, On(100, 500), On(100, 500), On(100, 500), On(100, 500));
            Assert.IsFalse(state.StripOpen);
        }

        [TestMethod]
        public void ACardThatAppearsUnderAParkedPointer_OpensNothing_UntilThePointerLeavesAndComesBack()
        {
            long clock = 1000;
            // The pointer was parked on a menu entry before the card came up under it (the window starts from default).
            var state = Watch(default, ref clock, On(100, 500), On(100, 500), On(100, 500), On(101, 500), On(100, 500), On(100, 500));
            Assert.IsFalse(state.StripOpen, "the player never went to the card");
            Assert.IsFalse(state.Holds);

            // Moving about on the card is still not a visit.
            state = Watch(state, ref clock, On(200, 600), On(200, 600), On(200, 600));
            Assert.IsFalse(state.StripOpen);

            // Away and back, and resting there: now it is on purpose.
            state = Watch(state, ref clock, InTheGame, On(100, 500), On(100, 500));
            Assert.IsFalse(state.StripOpen, "200 ms is not a rest");
            state = Watch(state, ref clock, On(100, 500));
            Assert.IsTrue(state.StripOpen);
        }

        [TestMethod]
        public void APointerOnItsWayAcross_OpensNothing_HoweverSlowly()
        {
            long clock = 1000;
            // Across the card in a second and a half, a little further at every look.
            var state = Watch(default, ref clock, On(5, 495), On(20, 500), On(60, 510), On(100, 520), On(140, 530), On(180, 540), On(220, 550), On(260, 560), On(300, 570));
            Assert.IsTrue(state.Armed, "it came from outside: only its not stopping keeps the strip shut");
            Assert.IsFalse(state.StripOpen);
            Assert.IsFalse(state.Resting);
            state = Watch(state, ref clock, On(400, 600));
            Assert.IsFalse(state.StripOpen);
        }

        [TestMethod]
        public void APointerThatRestsOnTheCard_OpensTheStrip_WhichStaysWhileItIsUsed()
        {
            long clock = 1000;
            // It arrives, settles (a hand is never quite still), and after 400 ms the strip is there.
            var state = Watch(default, ref clock, InTheGame, On(60, 480), On(120, 520));
            Assert.IsFalse(state.StripOpen, "just arrived");
            state = Watch(state, ref clock, On(121, 522));
            Assert.IsFalse(state.StripOpen, "200 ms is not a rest");
            state = Watch(state, ref clock, On(119, 521));
            Assert.IsTrue(state.StripOpen);
            Assert.IsTrue(state.Resting);

            // Up to the strip across the gap between the two windows, and along its buttons.
            state = Watch(state, ref clock, On(250, 480), On(260, 462), On(250, 445), On(290, 445));
            Assert.IsTrue(state.StripOpen);
            Assert.IsTrue(state.Holds, "the card does not run out while the player is at its buttons");

            // Away from both: it stays for a moment, then goes; coming back in time keeps it.
            state = Watch(state, ref clock, On(600, 300), On(650, 300));
            Assert.IsTrue(state.StripOpen, "away for less than 600 ms");
            state = Watch(state, ref clock, On(250, 445), On(600, 300), On(650, 300), On(700, 300));
            Assert.IsTrue(state.StripOpen, "the time away starts again after a visit");
            state = Watch(state, ref clock, On(700, 300));
            Assert.IsFalse(state.StripOpen, "600 ms away");

            // Where the strip was is no longer anything: a pointer there opens nothing.
            state = Watch(state, ref clock, On(250, 445), On(250, 445), On(250, 445), On(250, 445));
            Assert.IsFalse(state.StripOpen);
        }
    }
}
