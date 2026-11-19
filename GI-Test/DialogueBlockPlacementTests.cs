using System.Drawing;
using GI_Subtitles.Services.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// "Set automatically" and setup's game step save <see cref="AutoRegionService.PlaceBlock"/>. It used to box the
    /// text one moment showed (a 14% height floor), so a one-line talk saved an area the next long line did not fit.
    /// Every registered game must get its whole measured block, holding the text the manifest says it draws.
    /// </summary>
    [TestClass]
    public class DialogueBlockPlacementTests
    {
        private static readonly Rectangle Desktop = new Rectangle(0, 0, 3840, 2160);

        // Full screen at 16:9 and 21:9, a windowed game away from the origin.
        private static readonly Rectangle[] Clients =
        {
            new Rectangle(0, 0, 1920, 1080),
            new Rectangle(0, 0, 2560, 1440),
            new Rectangle(0, 0, 3440, 1440),
            new Rectangle(300, 200, 1600, 900),
        };

        [TestMethod]
        public void EveryGame_GetsItsWholeBlock_AndItHoldsTheGamesText()
        {
            foreach (GameRegionProfile game in GameRegionProfile.RegisteredProfiles)
            {
                Assert.IsNotNull(game.DialogueText, $"{game.GameId}: no measured text extent in its manifest");
                RegionFraction t = game.DialogueText.Value;
                var textOnly = new GameRegionProfile { DialogueXPct = t.X, DialogueYPct = t.Y, DialogueWPct = t.W, DialogueHPct = t.H };

                foreach (Rectangle client in Clients)
                {
                    string label = $"{game.GameId} on {client.Width}x{client.Height}";
                    string placed = AutoRegionService.PlaceBlock(game, client, Desktop);
                    var (block, _) = GameWindowDetector.CalculateFromRatios(game, client.X, client.Y, client.Width, client.Height);
                    Assert.AreEqual(block, placed, $"{label}: the saved area is the whole block");

                    Rectangle area = Parse(placed);
                    Rectangle text = Parse(GameWindowDetector.CalculateFromRatios(textOnly, client.X, client.Y, client.Width, client.Height).dialogueRegion);
                    Assert.IsTrue(area.Contains(text), $"{label}: area {area} does not hold the measured text {text}");
                }
            }
        }

        private static Rectangle Parse(string region)
        {
            Assert.IsTrue(CaptureRegionValidator.TryParse(region, out int x, out int y, out int w, out int h), region);
            return new Rectangle(x, y, w, h);
        }
    }
}
