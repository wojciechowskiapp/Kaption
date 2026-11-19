using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GI_Subtitles.Services.Answers;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Hints;
using GI_Subtitles.Services.Rendering;
using GI_Subtitles.Services.Stamina;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Where the in-game hint card goes (Services/Hints/HintPlacement), against every shipped game's own dialogue
    /// area, the subtitle panel where the app really puts it, and the manifest's answers box and stamina strip. The
    /// card's sizes are the design's (a 300 x 190 DIP sheet); the window measures the real ones.
    /// </summary>
    [TestClass]
    public class HintPlacementTests
    {
        private static readonly Size[] Windows = { new(1920, 1080), new(2560, 1080) };

        private static Size Dip(double width, double height, double scale) => new((int)Math.Round(width * scale), (int)Math.Round(height * scale));
        private static Size Sheet(double scale = 1) => Dip(300, 190, scale);
        /// <summary>The sheet without its header and footer.</summary>
        private static Size ShortSheet(double scale = 1) => Dip(300, 146, scale);
        private static Size Echo(double scale = 1) => Dip(360, 64, scale);

        private static IEnumerable<(GameManifest Game, GameRegionProfile Profile)> Games()
        {
            var games = GameRegistry.Current.Listed.Select(m => (m, GameRegistry.Current.ProfileOf(m.GameId))).ToList();
            Assert.IsTrue(games.Count > 0, "the registry lists games");
            return games;
        }

        /// <summary>The dialogue area the app sets for a game window, by its own rule.</summary>
        private static Rectangle DialogueOf(GameRegionProfile profile, Rectangle client)
        {
            string region = GameWindowDetector.CalculateFromRatios(profile, client.X, client.Y, client.Width, client.Height).dialogueRegion;
            int[] v = region.Split(',').Select(int.Parse).ToArray();
            return new Rectangle(v[0], v[1], v[2], v[3]);
        }

        /// <summary>
        /// The subtitle panel for a three-line subtitle as wide as the app lets one get, where the app puts it
        /// (LookGeometry.PanelRect: the profile's offset above the dialogue area, centred on the screen).
        /// </summary>
        private static Rectangle WideSubtitle(GameRegionProfile profile, Rectangle client, Rectangle dialogue)
        {
            var monitor = new System.Windows.Rect(client.X, client.Y, client.Width, client.Height);
            var scene = new LookScene(monitor, monitor, new System.Windows.Rect(dialogue.X, dialogue.Y, dialogue.Width, dialogue.Height), string.Empty, true);
            double oneLine = SubtitleCard.ReferenceHeight(OverlayStyle.Default);
            var panel = new System.Windows.Size(LookGeometry.TextWidth(scene, 0), oneLine * 2);
            System.Windows.Rect r = LookGeometry.PanelRect(scene, profile.SubtitlePadVertical, 0, panel, oneLine + OverlayStyle.WindowMargin * 2);
            return Rectangle.FromLTRB((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom));
        }

        private static void AssertKeepsClear(HintPlaced placed, HintScene scene, string what)
        {
            Assert.IsTrue(scene.Client.Contains(placed.Bounds), $"{what}: {placed.Bounds} leaves the game {scene.Client}");
            Rectangle grown = Rectangle.Inflate(placed.Bounds, 7, 7);
            Assert.IsFalse(grown.IntersectsWith(scene.Dialogue), $"{what}: {placed.Bounds} is on the dialogue area {scene.Dialogue}");
            foreach (Rectangle area in scene.KeepClear)
                Assert.IsFalse(grown.IntersectsWith(area), $"{what}: {placed.Bounds} is on {area}");
        }

        [TestMethod]
        public void InEveryGame_TheSheetSitsAtTheLeftAboveTheDialogue()
        {
            foreach (var (game, profile) in Games())
                foreach (Size window in Windows)
                    foreach (Point origin in new[] { Point.Empty, new Point(1920, 40) })
                    {
                        var client = new Rectangle(origin, window);
                        var scene = new HintScene(client, DialogueOf(profile, client), HintPlacement.GameAreas(game.Desktop, client, staminaReaderOn: true), 1);
                        string what = $"{game.Id} {window.Width}x{window.Height} at {origin}";

                        Assert.IsTrue(HintPlacement.TryPlace(scene, HintPlace.Left, Sheet(), ShortSheet(), out HintPlaced placed), what);
                        Assert.AreEqual((HintPlace.Left, false), (placed.Place, placed.Compact), what);
                        Assert.AreEqual(client.Left + 16, placed.Bounds.Left, what);
                        Assert.AreEqual(scene.Dialogue.Top - 24, placed.Bounds.Bottom, what);
                        AssertKeepsClear(placed, scene, what);
                    }

            // The design's own figure: Wuthering Waves at 1920x1080.
            var wuwa = Games().Single(g => g.Game.Id == "wuwa");
            var full = new Rectangle(0, 0, 1920, 1080);
            Assert.IsTrue(HintPlacement.TryPlace(new HintScene(full, DialogueOf(wuwa.Profile, full), Array.Empty<Rectangle>(), 1),
                HintPlace.Left, Sheet(), ShortSheet(), out HintPlaced p));
            Assert.AreEqual(new Rectangle(16, 466, 300, 190), p.Bounds);
        }

        [TestMethod]
        public void DuringADialogue_TheCardNeverTouchesTheDialogue_TheSubtitle_OrTheAnswers()
        {
            bool sawAnswers = false;
            foreach (var (game, profile) in Games())
                foreach (Size window in Windows)
                {
                    var client = new Rectangle(Point.Empty, window);
                    Rectangle dialogue = DialogueOf(profile, client);
                    Rectangle subtitle = WideSubtitle(profile, client, dialogue);
                    Assert.IsTrue(subtitle.Bottom <= dialogue.Top && subtitle.Width > dialogue.Width, $"{game.Id}: the panel sits above the dialogue, wider than it ({subtitle})");
                    var keepClear = HintPlacement.GameAreas(game.Desktop, client, staminaReaderOn: false);
                    sawAnswers |= keepClear.Count > 0;
                    keepClear.Add(subtitle);
                    var scene = new HintScene(client, dialogue, keepClear, 1);

                    foreach (var (name, full, compact) in new[] { ("sheet", Sheet(), ShortSheet()), ("echo", Echo(), Size.Empty) })
                    {
                        string what = $"{game.Id} {window.Width}x{window.Height} {name}";
                        Assert.IsTrue(HintPlacement.TryPlace(scene, HintPlace.Left, full, compact, out HintPlaced placed), $"{what}: there is a place");
                        AssertKeepsClear(placed, scene, what);
                        // At 16:9 a subtitle of full width reaches over the left place, so the card goes to the top;
                        // at 21:9 the subtitle is centred far from the left edge and the card stays.
                        Assert.AreEqual(window.Width == 1920 ? HintPlace.Top : HintPlace.Left, placed.Place, what);
                    }
                }
            Assert.IsTrue(sawAnswers, "at least one game ships an answers box to keep clear of");
        }

        [TestMethod]
        public void ACardThatIsUp_MovesOnlyWhenSomethingComesUnderIt()
        {
            var (game, profile) = Games().Single(g => g.Game.Id == "genshin");
            var client = new Rectangle(0, 0, 1920, 1080);
            Rectangle dialogue = DialogueOf(profile, client);
            var quiet = new HintScene(client, dialogue, Array.Empty<Rectangle>(), 1);
            Assert.IsTrue(HintPlacement.TryPlace(quiet, HintPlace.Left, Sheet(), ShortSheet(), out HintPlaced up));

            Assert.IsTrue(HintPlacement.TryKeep(quiet, up, Sheet(), ShortSheet(), out HintPlaced kept));
            Assert.AreEqual(up, kept, "nothing changed: it stays");

            // A wide subtitle arrives under it: the card moves to the top rather than cover the line.
            var talking = quiet with { KeepClear = new[] { WideSubtitle(profile, client, dialogue) } };
            Assert.IsFalse(HintPlacement.IsClear(talking, up.Bounds));
            Assert.IsTrue(HintPlacement.TryKeep(talking, up, Sheet(), ShortSheet(), out HintPlaced moved));
            Assert.AreEqual(HintPlace.Top, moved.Place);
            AssertKeepsClear(moved, talking, "moved");

            // And when the top is taken as well, it has to go.
            var crowded = talking with { KeepClear = talking.KeepClear.Append(new Rectangle(0, 0, 1920, 200)).ToArray() };
            Assert.IsFalse(HintPlacement.TryKeep(crowded, up, Sheet(), ShortSheet(), out _));
        }

        [TestMethod]
        public void WithThePanelsWidestSizeKeptClear_ACardDoesNotMoveWhenALongLineComes()
        {
            // The check of #341: a pinned sheet at 16:9 sat at the left and jumped to the top at the first long line.
            // The window now keeps the most the subtitle panel can cover clear from the start.
            foreach (var (game, profile) in Games())
                foreach (Size window in Windows)
                {
                    var client = new Rectangle(0, 0, window.Width, window.Height);
                    Rectangle dialogue = DialogueOf(profile, client);
                    Rectangle wide = WideSubtitle(profile, client, dialogue);

                    // The subtitle window as the app lays it out before any line: the dialogue's width plus 200,
                    // centred, one line high at the profile's offset.
                    double oneLine = SubtitleCard.ReferenceHeight(OverlayStyle.Default) + OverlayStyle.WindowMargin * 2;
                    int width = dialogue.Width + 200;
                    var idle = new Rectangle(client.Left + (client.Width - width) / 2, dialogue.Top + profile.SubtitlePadVertical, width, (int)Math.Ceiling(oneLine));
                    bool upward = OverlayPlacement.GrowsUpward(dialogue.Top, dialogue.Height, profile.SubtitlePadVertical, oneLine);
                    Rectangle reserve = HintPlacement.PanelReserve(idle, upward, wide.Width + 8, (int)(client.Height * DefaultSubtitleLayoutEngine.DefaultMaxHeightScreenFraction));

                    var quiet = new HintScene(client, dialogue, new[] { reserve }, 1);
                    if (!HintPlacement.TryPlace(quiet, HintPlace.Left, Sheet(), ShortSheet(), out HintPlaced placed)) continue;
                    var talking = quiet with { KeepClear = new[] { reserve, wide } };
                    Assert.IsTrue(HintPlacement.TryKeep(talking, placed, Sheet(), ShortSheet(), out HintPlaced kept), $"{game.Id} {window}");
                    Assert.AreEqual(placed, kept, $"{game.Id} {window}: the sheet stays where it was put");
                }
        }

        [TestMethod]
        public void TheShortSheet_IsTriedBeforeTheNextPlace()
        {
            // 125% scaling: the full sheet at the left would reach into the quest tracker, the short one does not.
            var (_, profile) = Games().Single(g => g.Game.Id == "wuwa");
            var client = new Rectangle(0, 0, 1920, 1080);
            var scene = new HintScene(client, DialogueOf(profile, client), Array.Empty<Rectangle>(), 1.25);

            Assert.IsTrue(HintPlacement.TryPlace(scene, HintPlace.Left, Sheet(1.25), ShortSheet(1.25), out HintPlaced placed));
            Assert.AreEqual((HintPlace.Left, true), (placed.Place, placed.Compact));
            Assert.AreEqual(ShortSheet(1.25), placed.Bounds.Size);
            Assert.IsTrue(placed.Bounds.Top >= client.Height * HintPlacement.LeftTopLimit);

            // A card with no short form (an echo, a tip) goes on to the next place instead.
            Assert.IsTrue(HintPlacement.TryPlace(scene, HintPlace.Left, Sheet(1.25), Size.Empty, out placed));
            Assert.AreEqual((HintPlace.Top, false), (placed.Place, placed.Compact));
        }

        [TestMethod]
        public void TheTop_IsRefusedWhileTheStaminaStripIsBeingRead()
        {
            bool sawStrip = false;
            foreach (var (game, profile) in Games().Where(g => g.Game.Desktop?.StaminaScreen != null))
            {
                sawStrip = true;
                var client = new Rectangle(0, 0, 1920, 1080);
                Rectangle dialogue = DialogueOf(profile, client);
                // The player moved the card to the top.
                var readerOff = new HintScene(client, dialogue, HintPlacement.GameAreas(game.Desktop, client, staminaReaderOn: false), 1);
                Assert.IsTrue(HintPlacement.TryPlace(readerOff, HintPlace.Top, Sheet(), ShortSheet(), out HintPlaced placed), game.Id);
                Assert.AreEqual(HintPlace.Top, placed.Place, game.Id);

                var readerOn = readerOff with { KeepClear = HintPlacement.GameAreas(game.Desktop, client, staminaReaderOn: true) };
                Assert.IsTrue(readerOn.KeepClear.Contains(StaminaLocator.SearchArea(client, game.Desktop.StaminaScreen)), game.Id);
                Assert.IsTrue(HintPlacement.TryPlace(readerOn, HintPlace.Top, Sheet(), ShortSheet(), out placed), game.Id);
                Assert.AreEqual(HintPlace.Left, placed.Place, $"{game.Id}: a card over the strip would blind the stamina reader");
            }
            Assert.IsTrue(sawStrip, "at least one game ships a stamina strip");
        }

        [TestMethod]
        public void NothingFits_GivesNothing()
        {
            var (game, profile) = Games().Single(g => g.Game.Id == "wuwa");
            var client = new Rectangle(0, 0, 1920, 1080);
            Rectangle dialogue = DialogueOf(profile, client);

            // A subtitle over the left place and the stamina strip over the top one.
            var keepClear = HintPlacement.GameAreas(game.Desktop, client, staminaReaderOn: true);
            keepClear.Add(WideSubtitle(profile, client, dialogue));
            Assert.IsFalse(HintPlacement.TryPlace(new HintScene(client, dialogue, keepClear, 1), HintPlace.Left, Sheet(), ShortSheet(), out HintPlaced placed));
            Assert.AreEqual(default, placed);

            // A window too small to hold the card beside its own dialogue.
            var small = new Rectangle(0, 0, 400, 225);
            Assert.IsFalse(HintPlacement.TryPlace(new HintScene(small, DialogueOf(profile, small), Array.Empty<Rectangle>(), 1),
                HintPlace.Left, Sheet(), ShortSheet(), out _));
        }

        [TestMethod]
        public void AskedForOutsideTheGame_TheSheetSitsAtTheLeftOfThatScreen()
        {
            // The screen's work area stands in for the game, and there is no dialogue area to stay above.
            var workArea = new Rectangle(2560, 0, 1920, 1040);
            Assert.IsTrue(HintPlacement.TryPlace(new HintScene(workArea, Rectangle.Empty, Array.Empty<Rectangle>(), 1), HintPlace.Left, Sheet(), ShortSheet(), out HintPlaced placed));
            Assert.AreEqual(new Rectangle(2560 + 16, (1040 - 190) / 2, 300, 190), placed.Bounds);
        }
    }
}
