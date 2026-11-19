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
    /// Star Rail's read area (x 0.10-0.90 of the screen) takes in the free-roam prompts at its right edge, the choice
    /// options above the speaker and the energy counter. Text and boxes are OCR blocks recorded at that area on 1920x1080
    /// footage (crop 1536x252, .plan/in-progress/AUTO-AREA-1C.md); the colours are the median bright pixel of each box
    /// in the recorded frame: gold names, near-white body and prompts, a dim grey choice option.
    /// </summary>
    [TestClass]
    public class StarRailSideRowTests
    {
        private const int CropW = 1536, CropH = 252;
        private static readonly Scalar NameGold = new Scalar(146, 192, 215);
        private static readonly Scalar Body = new Scalar(252, 249, 244);
        private static readonly Scalar DimChoice = new Scalar(150, 150, 150);

        private static ITextBlockClassifier StarRail => TextBlockClassifierFactory.Create("StarRail");

        /// <summary>Free roam near a teleport anchor: the prompt alone, which the pack has as "Teleport".</summary>
        [TestMethod]
        public void TeleportPrompt_AtTheRightEdge_IsNotDialogue()
        {
            using var frame = new Mat(CropH, CropW, MatType.CV_8UC3, Scalar.Black);
            var blocks = new List<TextBlock> { Paint(frame, "Teleport", 1401, 169, 120, 46, Body) };

            Assert.AreEqual("Teleport", AccentColorTextBlockClassifier.Instance.Classify(frame, blocks).DialogueText,
                "the defect: the plain accent path reads the prompt as a line, and the pack translates it");

            var result = StarRail.Classify(frame, blocks);
            Assert.AreEqual(0, result.NpcBlocks.Count + result.DialogueBlocks.Count);
        }

        /// <summary>The map's quest panel: "Stop Navigation" (read cut) sits at the right edge of the menu's rows.</summary>
        [TestMethod]
        public void NavigationButton_IsDropped_FromAMenuFrame()
        {
            using var frame = new Mat(CropH, CropW, MatType.CV_8UC3, Scalar.Black);
            var blocks = new List<TextBlock>
            {
                Paint(frame, "Rewards Preview", 415, 33, 192, 31, Body),
                Paint(frame, "VI - Silvermane Guard Restricted Zone", 0, 173, 290, 23, Body),
                Paint(frame, "Stop Navigatic", 1363, 184, 178, 38, Body),
            };

            StringAssert.Contains(StarRail.Classify(frame, blocks).DialogueText, "Rewards Preview");
            Assert.IsFalse(StarRail.Classify(frame, blocks).DialogueText.Contains("Navigat"));
        }

        /// <summary>The widest recorded row (x 0.124-0.876 of the screen) and its speaker. The reader can split a long
        /// row; a piece that starts right of the line still belongs to a row that starts left of it.</summary>
        [TestMethod]
        public void WidestRow_StaysWhole_AndTheSpeakerStaysTheName()
        {
            const string Row1 = "But disposing of Vache's reagents isn't enough to solve the medical supply shortage. Many of my patients can't";
            const string Row2 = "be seen promptly, and my st";

            using var frame = new Mat(CropH, CropW, MatType.CV_8UC3, Scalar.Black);
            var blocks = new List<TextBlock>
            {
                Paint(frame, "Natasha", 685, 12, 164, 54, NameGold),
                Paint(frame, Row1, 46, 80, 1443, 40, Body),
                Paint(frame, Row2, 376, 113, 382, 39, Body),
                Paint(frame, "V", 754, 215, 26, 19, Body),
            };

            var result = StarRail.Classify(frame, blocks);
            Assert.AreEqual("Natasha", result.NpcName);
            Assert.AreEqual(Row1 + "\n" + Row2, result.DialogueText, "the continue glyph is junk; both rows stay");

            using var split = new Mat(CropH, CropW, MatType.CV_8UC3, Scalar.Black);
            var pieces = new List<TextBlock>
            {
                Paint(split, "Natasha", 685, 12, 164, 54, NameGold),
                Paint(split, "But disposing of Vache's reagents isn't enough to solve the medical supply shortage.", 46, 80, 1100, 40, Body),
                Paint(split, "Many of my patients can't", 1165, 80, 324, 40, Body),
                Paint(split, Row2, 376, 113, 382, 39, Body),
            };
            var whole = StarRail.Classify(split, pieces);
            Assert.AreEqual("Natasha", whole.NpcName);
            StringAssert.Contains(whole.DialogueText, "Many of my patients can't");
        }

        /// <summary>A choice option ends beside the name, a little higher. Before, it shared the name's height and the
        /// name was not separated from the "body": the line was read as "Farewell, Natasha. Natasha Of course, ...".</summary>
        [TestMethod]
        public void ChoiceOptionBesideTheName_NoLongerPullsTheNameIntoTheBody()
        {
            const string Line = "Of course, even if you get injured by accident, no need to panic. Just leave it to the doctor.";
            using var frame = new Mat(CropH, CropW, MatType.CV_8UC3, Scalar.Black);
            var blocks = new List<TextBlock>
            {
                Paint(frame, "Farewell, Natasha.", 1118, 3, 294, 35, DimChoice),
                Paint(frame, "Natasha", 688, 14, 160, 50, NameGold),
                Paint(frame, Line, 181, 80, 1171, 38, Body),
            };

            Assert.AreEqual(string.Empty, AccentColorTextBlockClassifier.Instance.Classify(frame, blocks).NpcName,
                "the defect: the option sits within half a name height of the name");

            var result = StarRail.Classify(frame, blocks);
            Assert.AreEqual("Natasha", result.NpcName);
            Assert.AreEqual(Line, result.DialogueText);

            // Two pixels lower the option shares the name's row band; it is still its own piece of that row.
            using var lower = new Mat(CropH, CropW, MatType.CV_8UC3, Scalar.Black);
            var jittered = new List<TextBlock>
            {
                Paint(lower, "Farewell, Natasha.", 1118, 5, 294, 35, DimChoice),
                Paint(lower, "Natasha", 688, 14, 160, 50, NameGold),
                Paint(lower, Line, 181, 80, 1171, 38, Body),
            };
            var shared = StarRail.Classify(lower, jittered);
            Assert.AreEqual("Natasha", shared.NpcName);
            Assert.AreEqual(Line, shared.DialogueText);
        }

        /// <summary>Free roam: the energy counter's digit shares the body row's height at the right edge.</summary>
        [TestMethod]
        public void EnergyCounterDigit_InTheBodyRow_IsDropped()
        {
            using var frame = new Mat(CropH, CropW, MatType.CV_8UC3, Scalar.Black);
            var blocks = new List<TextBlock>
            {
                Paint(frame, "Mikiyukı", 704, -4, 126, 35, NameGold),
                Paint(frame, "I hear Fu Xuan was also in that battle..", 510, 32, 515, 40, Body),
                Paint(frame, "3", 1485, 47, 58, 42, Body),
            };

            var result = StarRail.Classify(frame, blocks);
            Assert.AreEqual("Mikiyukı", result.NpcName);
            Assert.AreEqual("I hear Fu Xuan was also in that battle..", result.DialogueText);
        }

        /// <summary>Genshin keeps the plain accent instance: rows at the right edge still reach its body.</summary>
        [TestMethod]
        public void SideRowRule_IsOptIn_StarRailOnly()
        {
            Assert.AreSame(AccentColorTextBlockClassifier.Instance, TextBlockClassifierFactory.Create("Genshin"));
            Assert.IsFalse(GameRegionProfile.Get("Genshin").DropsSideRows);
            Assert.IsTrue(GameRegionProfile.Get("StarRail").DropsSideRows);
            Assert.IsTrue(GameRegionProfile.Get("StarRail").FiltersOcrJunk);
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
