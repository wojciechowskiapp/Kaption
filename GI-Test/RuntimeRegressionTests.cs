using System.Collections.Generic;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.OCR;
using GI_Subtitles.Services.Translation;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using PaddleOCRSharp;

namespace GI_Test
{
    [TestClass]
    public class RuntimeRegressionTests
    {
        [TestMethod]
        public void Foreground_EmptyTitleExternalWindow_IsNotGame()
        {
            var result = GameForegroundClassifier.Classify(
                foregroundPid: 200,
                kaptionPid: 100,
                processName: "explorer",
                isKnownGameProcessId: false,
                windowTitle: "",
                profile: GameRegionProfile.Get("Genshin"),
                cloudSessionBypass: false,
                developmentBypass: false);

            Assert.AreEqual(ForegroundTarget.Other, result);
        }

        [TestMethod]
        public void Foreground_KaptionWindow_PausesCaptureWithoutBecomingOther()
        {
            var result = GameForegroundClassifier.Classify(
                100, 100, "Kaption", false, "Settings",
                GameRegionProfile.Get("Genshin"), false, false);

            Assert.AreEqual(ForegroundTarget.Kaption, result);
        }

        [TestMethod]
        public void Foreground_RegisteredGameProcess_IsGameEvenWithEmptyTitle()
        {
            var result = GameForegroundClassifier.Classify(
                200, 100, "GenshinImpact.exe", false, "",
                GameRegionProfile.Get("Genshin"), false, false);

            Assert.AreEqual(ForegroundTarget.Game, result);
        }

        [TestMethod]
        public void Foreground_BrowserTabWithGameTitle_IsNotGameWithoutCloudBypass()
        {
            var result = GameForegroundClassifier.Classify(
                200, 100, "chrome", false, "Genshin Impact - Wiki",
                GameRegionProfile.Get("Genshin"), false, false);

            Assert.AreEqual(ForegroundTarget.Other, result);
        }

        [TestMethod]
        public void Foreground_KnownCloudHost_IsGameAfterExplicitBypass()
        {
            var result = GameForegroundClassifier.Classify(
                200, 100, "chrome", false, "Genshin Impact on GeForce NOW",
                GameRegionProfile.Get("Genshin"), true, false);

            Assert.AreEqual(ForegroundTarget.Game, result);
        }

        [TestMethod]
        public void Foreground_KnownGamePid_IsGameWhenProcessNameLookupFails()
        {
            var result = GameForegroundClassifier.Classify(
                200, 100, "", true, "",
                GameRegionProfile.Get("Genshin"), false, false);

            Assert.AreEqual(ForegroundTarget.Game, result);
        }

        [TestMethod]
        public void Foreground_ReusedKnownPidWithDifferentProcess_IsNotGame()
        {
            var result = GameForegroundClassifier.Classify(
                200, 100, "notepad", true, "",
                GameRegionProfile.Get("Genshin"), false, false);

            Assert.AreEqual(ForegroundTarget.Other, result);
        }

        [TestMethod]
        public void WeightedMatcher_ChoosesLowestFloatDistanceWhenCeilingsTie()
        {
            var corpus = new Dictionary<string, string>
            {
                ["xyznnnnabcdefgh"] = "candidate A",
                ["hhhhhhhabcdefgh"] = "candidate B",
            };
            var matcher = new OptimizedMatcher(corpus, "EN");

            string result = matcher.FindClosestMatch("nnnnnnnabcdefgh", out string key);

            Assert.AreEqual("candidate B", result);
            Assert.AreEqual("hhhhhhhabcdefgh", key);
        }

        [TestMethod]
        public void BinaryChangeRatio_IsIndependentOfEmptyCropPadding()
        {
            double compact = BinaryFrameMetrics.NormalizedChangeRatio(120, 2000, 1880, 300_000);
            double padded4K = BinaryFrameMetrics.NormalizedChangeRatio(120, 2000, 1880, 8_000_000);

            Assert.AreEqual(compact, padded4K, 0.000001);
            Assert.IsTrue(compact > 0.01, "A meaningful glyph change must cross the existing trigger threshold.");
        }

        [DataTestMethod]
        [DataRow("0,0,1920,1080", true)]
        [DataRow("-1920,0,1920,1080", true)]
        [DataRow("0,0,0,1080", false)]
        [DataRow("x,0,1920,1080", false)]
        public void CaptureRegion_ValidatesDimensionsButAllowsVirtualDesktopOrigins(string value, bool expected)
        {
            bool actual = CaptureRegionValidator.TryParse(value, out _, out _, out _, out _);
            Assert.AreEqual(expected, actual);
        }

        [TestMethod]
        public void OverlapAlert_PassesOnlyNativeHitTestsThrough()
        {
            Assert.IsTrue(OverlapAlertOverlay.IsTransparentHitTestMessage(0x0084));
            Assert.IsFalse(OverlapAlertOverlay.IsTransparentHitTestMessage(0x0201));
        }

        [TestMethod]
        public void ConfigMigration_UiRefresh_ChangesOnlyLegacyDefault()
        {
            Assert.AreEqual(150, ConfigMigrations.GetUiRefreshMigrationTarget(true, 200));
            Assert.IsNull(ConfigMigrations.GetUiRefreshMigrationTarget(false, 200));
            Assert.IsNull(ConfigMigrations.GetUiRefreshMigrationTarget(true, 100));
            Assert.IsNull(ConfigMigrations.GetUiRefreshMigrationTarget(true, 300));
        }

        [TestMethod]
        public void NpcClassifier_RequiresGoldHueAndKeepsLowerHighlightAsDialogue()
        {
            using var frame = new Mat(150, 320, MatType.CV_8UC3, Scalar.Black);
            Cv2.Rectangle(frame, new Rect(10, 10, 120, 25), new Scalar(40, 190, 255), -1); // gold
            Cv2.Rectangle(frame, new Rect(10, 65, 280, 25), new Scalar(255, 80, 30), -1);  // saturated blue
            Cv2.Rectangle(frame, new Rect(10, 105, 100, 25), new Scalar(40, 190, 255), -1); // gold highlight

            var blocks = new List<TextBlock>
            {
                Block("Paimon", 10, 10, 120, 25),
                Block("This remains dialogue", 10, 65, 280, 25),
                Block("highlight", 10, 105, 100, 25),
            };

            var result = ImageProcessor.ClassifyTextBlocksWithPositions(frame, blocks);

            Assert.AreEqual("Paimon", result.NpcName);
            Assert.AreEqual(2, result.DialogueBlocks.Count);
            Assert.IsTrue(result.DialogueText.Contains("This remains dialogue"));
            Assert.IsTrue(result.DialogueText.Contains("highlight"));
        }

        [TestMethod]
        public void NpcClassifier_GoldHighlightOnSameLine_RemainsDialogue()
        {
            using var frame = new Mat(80, 360, MatType.CV_8UC3, Scalar.Black);
            Cv2.Rectangle(frame, new Rect(10, 25, 90, 28), new Scalar(40, 190, 255), -1);
            Cv2.Rectangle(frame, new Rect(105, 25, 220, 28), Scalar.White, -1);

            var result = ImageProcessor.ClassifyTextBlocksWithPositions(frame, new List<TextBlock>
            {
                Block("Important", 10, 25, 90, 28),
                Block("dialogue continues", 105, 25, 220, 28),
            });

            Assert.AreEqual(string.Empty, result.NpcName);
            Assert.AreEqual(2, result.DialogueBlocks.Count);
        }

        [TestMethod]
        public void NpcClassifier_GenshinSevenCyanName_IsSeparatedFromWhiteDialogue()
        {
            // The 7.0 UI uses light cyan (HSV hue ~98) for the speaker over a
            // deeper blue panel (hue ~110). White dialogue must remain intact.
            using var frame = new Mat(150, 640, MatType.CV_8UC3, new Scalar(235, 120, 45));
            Cv2.Rectangle(frame, new Rect(20, 18, 180, 28), new Scalar(255, 220, 130), -1);
            Cv2.Rectangle(frame, new Rect(20, 72, 580, 34), Scalar.White, -1);

            var result = ImageProcessor.ClassifyTextBlocksWithPositions(frame, new List<TextBlock>
            {
                Block("Eye of Graeae", 20, 18, 180, 28),
                Block("Agreed — this is likely a consequence of outdated railroad data.", 20, 72, 580, 34),
            });

            Assert.AreEqual("Eye of Graeae", result.NpcName);
            Assert.AreEqual(1, result.DialogueBlocks.Count);
            StringAssert.Contains(result.DialogueText, "outdated railroad data");
        }

        [TestMethod]
        public void NpcClassifier_IsolatedGoldName_WaitsForDialogueBody()
        {
            using var frame = new Mat(80, 240, MatType.CV_8UC3, Scalar.Black);
            Cv2.Rectangle(frame, new Rect(10, 20, 120, 28), new Scalar(40, 190, 255), -1);

            var result = ImageProcessor.ClassifyTextBlocksWithPositions(frame,
                new List<TextBlock> { Block("Paimon", 10, 20, 120, 28) });

            Assert.AreEqual("Paimon", result.NpcName);
            Assert.AreEqual(string.Empty, result.DialogueText);
        }

        [TestMethod]
        public void NpcRoleClassifier_DoesNotDropShortFirstDialogueLineAt4KScale()
        {
            using var frame = new Mat(500, 1000, MatType.CV_8UC3, Scalar.Black);
            Cv2.Rectangle(frame, new Rect(30, 20, 240, 55), new Scalar(40, 190, 255), -1);
            Cv2.Rectangle(frame, new Rect(30, 145, 220, 58), Scalar.White, -1);
            Cv2.Rectangle(frame, new Rect(30, 245, 700, 58), Scalar.White, -1);

            var result = ImageProcessor.ClassifyTextBlocksWithPositions(frame, new List<TextBlock>
            {
                Block("Paimon", 30, 20, 240, 55),
                Block("Not now.", 30, 145, 220, 58),
                Block("This second dialogue line is much longer.", 30, 245, 700, 58),
            });

            Assert.AreEqual(2, result.DialogueBlocks.Count);
            Assert.IsTrue(result.DialogueText.Contains("Not now."));
        }

        /// <summary>
        /// Measured on 800 recorded Genshin frames: the detected name box and the
        /// first body box are separated by 1–6 px, so an edge-gap threshold is
        /// decided by detector jitter and the speaker flickers in and out on
        /// visually identical frames. Row centres are a stable line apart.
        /// </summary>
        [TestMethod]
        public void NpcClassifier_HairlineGapBelowName_StillYieldsSpeaker()
        {
            using var frame = new Mat(260, 320, MatType.CV_8UC3, Scalar.Black);
            Cv2.Rectangle(frame, new Rect(10, 100, 120, 58), new Scalar(40, 190, 255), -1);
            Cv2.Rectangle(frame, new Rect(10, 161, 280, 46), Scalar.White, -1);

            var result = ImageProcessor.ClassifyTextBlocksWithPositions(frame, new List<TextBlock>
            {
                Block("Paimon", 10, 100, 120, 58),
                Block("What? How did you know we were coming?", 10, 161, 280, 46),
            });

            Assert.AreEqual("Paimon", result.NpcName,
                "A 3 px gap is one line apart, not a highlight on the dialogue line.");
            Assert.AreEqual(1, result.DialogueBlocks.Count);
        }

        /// <summary>
        /// A choice menu renders above the dialogue box. It is not the body, so
        /// it must neither suppress the speaker nor be absorbed into it — the
        /// latter would delete it from the match input.
        /// </summary>
        [TestMethod]
        public void NpcClassifier_ChoiceOptionAboveName_KeepsSpeakerAndKeepsOptionInBody()
        {
            using var frame = new Mat(260, 320, MatType.CV_8UC3, Scalar.Black);
            Cv2.Rectangle(frame, new Rect(150, 10, 140, 30), Scalar.White, -1);
            Cv2.Rectangle(frame, new Rect(10, 100, 120, 58), new Scalar(40, 190, 255), -1);
            Cv2.Rectangle(frame, new Rect(10, 161, 280, 46), Scalar.White, -1);

            var result = ImageProcessor.ClassifyTextBlocksWithPositions(frame, new List<TextBlock>
            {
                Block("Whichever one you're wearing", 150, 10, 140, 30),
                Block("Ying'er", 10, 100, 120, 58),
                Block("Relax... I know why you're here, and what you came for.", 10, 161, 280, 46),
            });

            Assert.AreEqual("Ying'er", result.NpcName);
            StringAssert.Contains(result.DialogueText, "Whichever one you're wearing");
            Assert.AreEqual(2, result.DialogueBlocks.Count);
        }

        /// <summary>
        /// The role line sits between the speaker and the dialogue, and still
        /// belongs to the speaker rather than the translated body.
        /// </summary>
        [TestMethod]
        public void NpcRoleClassifier_WhiteRoleLineBelowName_IsReclassified()
        {
            using var frame = new Mat(320, 800, MatType.CV_8UC3, Scalar.Black);
            Cv2.Rectangle(frame, new Rect(30, 20, 240, 55), new Scalar(40, 190, 255), -1);
            Cv2.Rectangle(frame, new Rect(30, 100, 200, 30), Scalar.White, -1);
            Cv2.Rectangle(frame, new Rect(30, 175, 700, 58), Scalar.White, -1);

            var result = ImageProcessor.ClassifyTextBlocksWithPositions(frame, new List<TextBlock>
            {
                Block("Ying'er", 30, 20, 240, 55),
                Block("Shop Assistant", 30, 100, 200, 30),
                Block("Relax... I know why you're here, and what you came for.", 30, 175, 700, 58),
            });

            Assert.AreEqual(2, result.NpcBlocks.Count);
            StringAssert.Contains(result.NpcName, "Shop Assistant");
            Assert.AreEqual(1, result.DialogueBlocks.Count);
        }

        [TestMethod]
        public void RobustFrameHash_DistinguishesDifferentNormalizedTextContent()
        {
            using var first = new Mat(120, 480, MatType.CV_8UC3, Scalar.Black);
            using var second = new Mat(120, 480, MatType.CV_8UC3, Scalar.Black);
            Cv2.PutText(first, "THE QUICK BROWN FOX", new OpenCvSharp.Point(8, 75),
                HersheyFonts.HersheySimplex, 1.0, Scalar.White, 2);
            Cv2.PutText(second, "THE QUICK BROWN BOX", new OpenCvSharp.Point(8, 75),
                HersheyFonts.HersheySimplex, 1.0, Scalar.White, 2);

            Assert.AreNotEqual(
                ImageProcessor.ComputeRobustHash(first),
                ImageProcessor.ComputeRobustHash(second));
        }

        private static TextBlock Block(string text, float x, float y, float width, float height)
            => new TextBlock
            {
                Text = text,
                Score = 0.99f,
                BoxPoints = new[]
                {
                    new System.Drawing.PointF(x, y),
                    new System.Drawing.PointF(x + width, y),
                    new System.Drawing.PointF(x + width, y + height),
                    new System.Drawing.PointF(x, y + height),
                },
            };
    }
}
