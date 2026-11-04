using System;
using System.Collections.Generic;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.OCR.Classification;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using PaddleOCRSharp;

namespace GI_Test
{
    /// <summary>
    /// Wuthering Waves' speaker/body split (<see cref="SpeakerRowTextBlockClassifier"/>) and the opt-in junk filter on
    /// the accent path. Text and boxes are OCR blocks recorded from gameplay footage at the measured region
    /// (.plan/in-progress/WUWA-STAGE-B.md); the colours are painted: pale gold for a name the gate sees, warm orange
    /// for a body row over sunlit scenery (it passes the gate), near-white for a name over pale art (it does not).
    /// </summary>
    [TestClass]
    public class SpeakerRowClassifierTests
    {
        private static readonly Scalar PaleGold = new Scalar(166, 230, 242);
        private static readonly Scalar SunlitScenery = new Scalar(60, 170, 230);
        private static readonly Scalar PaleArt = new Scalar(225, 235, 240);

        /// <summary>21:9 free roam (3440x1440, crop 1714x524). The first body row sits on warm scenery and passes the
        /// accent gate; the accent name band, stretched by WuWa's larger name font, takes it into the name.</summary>
        [TestMethod]
        public void FreeRoam_SunlitFirstRow_StaysInTheBody_AndHudReadoutsAreDropped()
        {
            using var frame = new Mat(524, 1714, MatType.CV_8UC3, Scalar.Black);
            var blocks = new List<TextBlock>
            {
                Paint(frame, "Muyu", 780, 50, 155, 79, PaleGold),
                Paint(frame, "Correct. When that war erupted, Tacet Discords swept into", 334, 147, 1038, 50, SunlitScenery),
                Paint(frame, "Xuanfang. The main battlefield was this Fortress, built by", 354, 202, 997, 47, Scalar.White),
                Paint(frame, "Mengzhou in earlier days against these monsters.", 421, 253, 873, 51, Scalar.White),
                Paint(frame, "19690/19690", 773, 461, 150, 30, Scalar.White),
                Paint(frame, "Lv.90", 1067, 451, 82, 51, Scalar.White),
            };

            var accent = AccentColorTextBlockClassifier.Instance.Classify(frame, blocks);
            Assert.AreEqual("Muyu Correct. When that war erupted, Tacet Discords swept into", accent.NpcName,
                "the defect this classifier exists for: the accent path swallows the sunlit row");

            var rows = SpeakerRowTextBlockClassifier.WithJunkFilter.Classify(frame, blocks);
            Assert.AreEqual("Muyu", rows.NpcName);
            Assert.AreEqual(
                "Correct. When that war erupted, Tacet Discords swept into\n" +
                "Xuanfang. The main battlefield was this Fortress, built by\n" +
                "Mengzhou in earlier days against these monsters.", rows.DialogueText);
        }

        /// <summary>16:10 cinematic over a pale illustration (crop 1542x472), typing: the name fails the gate.</summary>
        [TestMethod]
        public void PaleName_IsTakenByItsLargerFont_WhileTheLineTypesIn()
        {
            using var frame = new Mat(472, 1542, MatType.CV_8UC3, Scalar.Black);
            var blocks = new List<TextBlock>
            {
                Paint(frame, "Suoming", 666, 212, 223, 79, PaleArt),
                Paint(frame, "No ma", 198, 317, 126, 54, Scalar.White),
            };

            var accent = AccentColorTextBlockClassifier.Instance.Classify(frame, blocks);
            Assert.AreEqual(string.Empty, accent.NpcName);
            Assert.AreEqual("Suoming\nNo ma", accent.DialogueText, "the accent path leaves the name in the body");

            foreach (var source in new[] { frame, null })
            {
                var rows = SpeakerRowTextBlockClassifier.WithJunkFilter.Classify(source, blocks);
                Assert.AreEqual("Suoming", rows.NpcName, source == null ? "no colour frame" : "colour frame");
                Assert.AreEqual("No ma", rows.DialogueText);
            }
        }

        /// <summary>16:9 NPC scene (crop 1286x394): the lowest choice line reaches the region's top-right corner.</summary>
        [TestMethod]
        public void ChoicePanelText_IsNotDialogue_AndDoesNotHideTheSpeaker()
        {
            using var frame = new Mat(394, 1286, MatType.CV_8UC3, Scalar.Black);
            var blocks = new List<TextBlock>
            {
                Paint(frame, "Resting Celestial", 483, 122, 322, 51, PaleGold),
                Paint(frame, "(Briefly explain how things s", 968, 2, 319, 35, Scalar.White),
                Paint(frame, "How are things in the Hold these days? Is the Land of Xuanfang still", 171, 206, 940, 39, Scalar.White),
                Paint(frame, "safe?", 597, 242, 94, 48, Scalar.White),
            };

            var accent = AccentColorTextBlockClassifier.Instance.Classify(frame, blocks);
            StringAssert.Contains(accent.DialogueText, "(Briefly explain", "the accent path keeps choice text in the body");

            var rows = SpeakerRowTextBlockClassifier.WithJunkFilter.Classify(frame, blocks);
            Assert.AreEqual("Resting Celestial", rows.NpcName);
            Assert.AreEqual("How are things in the Hold these days? Is the Land of Xuanfang still\nsafe?", rows.DialogueText);
        }

        /// <summary>The name row was not detected and the first body row passes the gate: a row longer than any
        /// speaker name is never the speaker.</summary>
        [TestMethod]
        public void LongTopRow_StaysDialogue_EvenWhenAccentColoured()
        {
            using var frame = new Mat(524, 1714, MatType.CV_8UC3, Scalar.Black);
            var blocks = new List<TextBlock>
            {
                Paint(frame, "Correct. When that war erupted, Tacet Discords swept into", 334, 147, 1038, 50, SunlitScenery),
                Paint(frame, "Xuanfang. The main battlefield was this Fortress, built by", 354, 202, 997, 47, Scalar.White),
            };

            Assert.AreEqual("Correct. When that war erupted, Tacet Discords swept into",
                AccentColorTextBlockClassifier.Instance.Classify(frame, blocks).NpcName, "the accent path takes it as the name");

            var rows = SpeakerRowTextBlockClassifier.WithJunkFilter.Classify(frame, blocks);
            Assert.AreEqual(string.Empty, rows.NpcName);
            Assert.AreEqual(2, rows.DialogueBlocks.Count);
        }

        /// <summary>A short white first row the same height as the next is body text, not a speaker.</summary>
        [TestMethod]
        public void ShortWhiteFirstRow_OfBodyHeight_StaysDialogue()
        {
            using var frame = new Mat(394, 1286, MatType.CV_8UC3, Scalar.Black);
            var blocks = new List<TextBlock>
            {
                Paint(frame, "Hmm...", 590, 120, 106, 44, Scalar.White),
                Paint(frame, "I suppose that's one way to put it.", 380, 170, 520, 44, Scalar.White),
            };

            var rows = SpeakerRowTextBlockClassifier.WithJunkFilter.Classify(frame, blocks);
            Assert.AreEqual(string.Empty, rows.NpcName);
            Assert.AreEqual("Hmm...\nI suppose that's one way to put it.", rows.DialogueText);
        }

        [TestMethod]
        public void LoneRow_AccentWaitsAsNameOnly_WhiteIsDialogue()
        {
            using var frame = new Mat(394, 1286, MatType.CV_8UC3, Scalar.Black);
            var name = new List<TextBlock> { Paint(frame, "Lenie", 590, 100, 110, 51, PaleGold) };
            var lone = SpeakerRowTextBlockClassifier.WithJunkFilter.Classify(frame, name);
            Assert.AreEqual("Lenie", lone.NpcName);
            Assert.AreEqual(string.Empty, lone.DialogueText);

            var caption = new List<TextBlock> { Paint(frame, "If it had been you, she might still be here today...", 296, 310, 699, 35, Scalar.White) };
            var line = SpeakerRowTextBlockClassifier.WithJunkFilter.Classify(frame, caption);
            Assert.AreEqual(string.Empty, line.NpcName);
            Assert.AreEqual("If it had been you, she might still be here today...", line.DialogueText);
        }

        [TestMethod]
        public void OnlySidePanelText_IsAnEmptyFrame()
        {
            using var frame = new Mat(394, 1286, MatType.CV_8UC3, Scalar.Black);
            var result = SpeakerRowTextBlockClassifier.WithJunkFilter.Classify(frame, new List<TextBlock>
            {
                Paint(frame, "Your spam ad blew up my", 968, 40, 300, 35, Scalar.White),
            });
            Assert.AreEqual(0, result.NpcBlocks.Count + result.DialogueBlocks.Count);
        }

        /// <summary>Genshin and Star Rail keep the unfiltered accent instance: their HUD text still reaches the body.</summary>
        [TestMethod]
        public void JunkFilter_IsOptIn_OnTheAccentPath()
        {
            using var frame = new Mat(394, 1286, MatType.CV_8UC3, Scalar.Black);
            var blocks = new List<TextBlock>
            {
                Paint(frame, "...Mengzhou Fish in Vinegar?", 420, 108, 440, 43, Scalar.White),
                Paint(frame, "15695/15695", 585, 347, 106, 19, Scalar.White),
                Paint(frame, "Lv.90", 801, 339, 59, 35, Scalar.White),
            };

            Assert.AreEqual("...Mengzhou Fish in Vinegar?\n15695/15695\nLv.90",
                AccentColorTextBlockClassifier.Instance.Classify(frame, blocks).DialogueText);
            Assert.AreEqual("...Mengzhou Fish in Vinegar?",
                AccentColorTextBlockClassifier.WithJunkFilter.Classify(frame, blocks).DialogueText);

            var junkOnly = AccentColorTextBlockClassifier.WithJunkFilter.Classify(frame, blocks.GetRange(1, 2));
            Assert.AreEqual(0, junkOnly.NpcBlocks.Count + junkOnly.DialogueBlocks.Count, "HUD readouts alone are no dialogue");
        }

        [TestMethod]
        public void Factory_PicksByCapability_AndLeavesTheLiveGamesAlone()
        {
            Assert.AreSame(SpeakerRowTextBlockClassifier.WithJunkFilter, TextBlockClassifierFactory.Create("WuWa"));
            Assert.AreSame(SpeakerRowTextBlockClassifier.WithJunkFilter, TextBlockClassifierFactory.Create("Wuthering Waves"));
            Assert.AreSame(AccentColorTextBlockClassifier.Instance, TextBlockClassifierFactory.Create("Genshin"));
            Assert.AreSame(AccentColorTextBlockClassifier.Instance, TextBlockClassifierFactory.Create("StarRail"));
            Assert.AreSame(AccentColorTextBlockClassifier.Instance, TextBlockClassifierFactory.Create("NotAGame"));
            Assert.AreSame(GeometryTextBlockClassifier.Instance, TextBlockClassifierFactory.Create("ZZZ"));

            foreach (string live in new[] { "Genshin", "StarRail", "ZZZ" })
            {
                var p = GameRegionProfile.Get(live);
                Assert.IsFalse(p.UsesSpeakerRowClassifier, live);
                Assert.IsFalse(p.FiltersOcrJunk, live);
            }
        }

        private static TextBlock Paint(Mat frame, string text, int x, int y, int w, int h, Scalar colour)
        {
            Cv2.Rectangle(frame, new Rect(x, Math.Max(0, y), w, h), colour, -1);
            return new TextBlock
            {
                Text = text,
                Score = 0.99f,
                BoxPoints = new[]
                {
                    new System.Drawing.PointF(x, y),
                    new System.Drawing.PointF(x + w, y),
                    new System.Drawing.PointF(x + w, y + h),
                    new System.Drawing.PointF(x, y + h),
                },
            };
        }
    }
}
