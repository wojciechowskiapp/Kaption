using System.Drawing;
using GI_Subtitles.Services.Answers;
using GI_Subtitles.Services.Games;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Where the app looks for Wuthering Waves' dialogue choices, from the manifest as shipped, against where the
    /// footage shows them (.plan/in-progress/ANSWERS-PR1.md). The rectangles are the measured ink of the option text
    /// and of the "F" prompt beside the selected option, in window pixels; the box itself is never pinned.
    /// </summary>
    [TestClass]
    public class AnswerGeometryTests
    {
        private static GameAnswers WuWa =>
            GameRegistry.Current.FindByWireId("wuwa")?.Desktop?.Answers ?? throw new AssertFailedException("WuWa ships no usable answers block in its embedded manifest");

        private static Rectangle Place(Rectangle client)
        {
            Assert.AreEqual(AnswerAvailability.Available, AnswerGeometry.Availability(WuWa, client.Size), $"{client.Size}");
            Assert.IsTrue(AnswerGeometry.TryPlace(WuWa, client, out Rectangle box, out double scale), $"{client.Size}");
            Assert.IsTrue(scale > 0);
            return box;
        }

        private static void Holds(Rectangle box, Rectangle text, string what) =>
            Assert.IsTrue(box.Contains(text), $"{what}: text {text} is not inside the answers box {box}");

        private static void LeavesOut(Rectangle box, Rectangle prompt) =>
            Assert.IsFalse(box.IntersectsWith(prompt), $"the key prompt {prompt} is inside the answers box {box}");

        [TestMethod]
        public void At1920x1080_TheBoxHoldsTheMeasuredOptions_AndNotTheKeyPrompt()
        {
            Rectangle box = Place(new Rectangle(0, 0, 1920, 1080));
            Holds(box, Rectangle.FromLTRB(1278, 522, 1677, 727), "two options of two and three lines");
            Holds(box, Rectangle.FromLTRB(1439, 563, 1727, 740), "a letterboxed scene's options (the menu drawn at 0.75)");
            LeavesOut(box, Rectangle.FromLTRB(1146, 522, 1200, 562));

            // The same game window on a second monitor: the box moves with it.
            Rectangle moved = Place(new Rectangle(1920, 40, 1920, 1080));
            Assert.AreEqual(new Rectangle(box.X + 1920, box.Y + 40, box.Width, box.Height), moved);
        }

        [TestMethod]
        public void At2304x1440_TheMenuIsPinnedToTheCorner_SoTheBoxFollowsIt()
        {
            // 16:10: the interface scales by width (1.2), and the menu sits lower in the picture than at 16:9.
            Rectangle box = Place(new Rectangle(0, 0, 2304, 1440));
            Holds(box, Rectangle.FromLTRB(1544, 764, 1991, 1014), "two options of two and three lines");
            LeavesOut(box, Rectangle.FromLTRB(1362, 764, 1452, 814));
        }

        [TestMethod]
        public void At3440x1440_TheBoxHoldsTheMenuOutsideTheCentredBand()
        {
            Rectangle box = Place(new Rectangle(0, 0, 3440, 1440));
            Holds(box, Rectangle.FromLTRB(2585, 757, 3105, 975), "two options of two lines");
            LeavesOut(box, Rectangle.FromLTRB(2400, 757, 2469, 810));
        }

        [TestMethod]
        public void AShapeNobodyMeasured_GetsNoAnswers()
        {
            foreach (var size in new[] { new Size(1440, 1080), new Size(1024, 768), new Size(1280, 1024), new Size(5120, 1440) })
            {
                Assert.AreEqual(AnswerAvailability.NotForShape, AnswerGeometry.Availability(WuWa, size), $"{size}");
                Assert.IsFalse(AnswerGeometry.TryPlace(WuWa, new Rectangle(Point.Empty, size), out _, out _), $"{size}");
            }
        }

        [TestMethod]
        public void AGameWithoutAUsableBlock_GetsNoAnswers()
        {
            var client = new Rectangle(0, 0, 1920, 1080);
            Assert.AreEqual(AnswerAvailability.NotForGame, AnswerGeometry.Availability(null, client.Size));
            Assert.IsFalse(AnswerGeometry.TryPlace(null, client, out _, out _));

            // A box that leaves its canvas is a broken block: answers stay off for the game, nothing is guessed.
            GameAnswers wuwa = WuWa;
            var broken = new GameAnswers
            {
                Canvas = wuwa.Canvas, Checked = wuwa.Checked, Ink = wuwa.Ink, PillPad = wuwa.PillPad,
                Box = new GameAssistBox { Edge = "bottomRight", X = -100, Y = -724, W = 500, H = 454 },
            };
            Assert.AreEqual(AnswerAvailability.NotForGame, AnswerGeometry.Availability(broken, client.Size));
            Assert.IsFalse(AnswerGeometry.TryPlace(broken, client, out _, out _));

            // So is a block whose box is fine but which cannot say what option text looks like.
            foreach (GameAnswersInk ink in new[] { null, new GameAnswersInk { Level = 0, LineHeight = 26 }, new GameAnswersInk { Level = 200, LineHeight = 0 } })
            {
                var inkless = new GameAnswers { Canvas = wuwa.Canvas, Checked = wuwa.Checked, Box = wuwa.Box, PillPad = wuwa.PillPad, Ink = ink };
                Assert.AreEqual(AnswerAvailability.NotForGame, AnswerGeometry.Availability(inkless, client.Size));
            }
        }
    }
}
