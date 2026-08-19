using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GI_Subtitles.Models;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.OCR;
using GI_Subtitles.Services.OCR.Classification;
using GI_Subtitles.Services.Translation.Strategies;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using PaddleOCRSharp;

namespace GI_Test
{
    /// <summary>
    /// Coverage for the geometry-based speaker/body split that Zenless Zone Zero
    /// needs, plus the decoration stripping and junk filtering around it.
    ///
    /// <para>Two tiers. The synthetic tests below reproduce the exact box
    /// geometry measured by <see cref="ZenlessLayoutDiagnostics"/> on the two real
    /// screenshots, so they pin the classifier's behaviour without paying for OCR;
    /// they also cover the degenerate layouts real frames rarely produce. The
    /// golden tests at the bottom run the actual engine over the actual PNGs, and
    /// assert with tolerance — an exact-string assertion against OCR output rots
    /// into a disabled test the first time a recogniser model changes.</para>
    /// </summary>
    [TestClass]
    public class ZenlessClassifierTests
    {
        // ══════════════════════════════════════════════════════════════
        //  Measured layouts
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Style A. Speaker centred above centred body, no panel, decorations
        /// recognised as a leading hyphen. Note the name box BOTTOM (89+43=132)
        /// sits below the first body line's TOP (125): the edge gap is negative,
        /// which is why banding keys on centre-Y.
        /// </summary>
        [TestMethod]
        public void Geometry_CinematicLayout_SplitsCentredSpeakerFromBody()
        {
            var result = GeometryTextBlockClassifier.Instance.Classify(null, new List<TextBlock>
            {
                Block("-Remielle", 867, 89, 189, 43),
                Block("Though it seems you have no recollection of it whatsoever. Maybe... Maybe you were",
                      279, 125, 1343, 46),
                Block("already on the verge of losing consciousness then, and felt nothing at all?",
                      282, 166, 1189, 34),
                Block("UID: 1000290822.", 1761, 299, 159, 29),
            });

            Assert.AreEqual("Remielle", result.NpcName, "Speaker name, with the OCR'd decoration stripped.");
            Assert.AreEqual(2, result.DialogueBlocks.Count, "Both body lines stay; the watermark does not.");
            StringAssert.Contains(result.DialogueText, "on the verge of losing consciousness");
            Assert.IsFalse(result.DialogueText.Contains("1000290822"), "Watermark must not reach the matcher.");
        }

        /// <summary>
        /// Style B. Speaker in a pill tab above a dark panel, body left-aligned,
        /// three junk blocks (close button and two watermarks) on the right.
        /// </summary>
        [TestMethod]
        public void Geometry_ComicLayout_SplitsPillTabSpeakerFromLeftAlignedBody()
        {
            var result = GeometryTextBlockClassifier.Instance.Classify(null, new List<TextBlock>
            {
                Block("Billy", 917, 26, 80, 48),
                Block("Having breakfast together with you guys every", 473, 84, 856, 44),
                Block("morning like this... I dunno, it just feels like... it makes", 475, 125, 968, 38),
                Block("even my daily maintenance feel more like a special", 477, 163, 914, 38),
                Block("ritual,", 475, 199, 123, 43),
                Block("×", 1461, 214, 34, 36),
                Block("UBHEN92", 1567, 228, 308, 62),
                Block("UID: 1000290822 ", 1765, 299, 150, 30),
            });

            Assert.AreEqual("Billy", result.NpcName);
            Assert.AreEqual(4, result.DialogueBlocks.Count, "Four body lines, all three junk blocks dropped.");
            StringAssert.Contains(result.DialogueText, "Having breakfast together with you guys");
            StringAssert.Contains(result.DialogueText, "ritual,");
            Assert.IsFalse(result.DialogueText.Contains("UBHEN92"));
        }

        // ══════════════════════════════════════════════════════════════
        //  Edge cases
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Unattributed narration. One printed row with nothing below it is not a
        /// speaker — Zenless uses narration captions, so this is a live case and
        /// not just a degenerate guard.
        /// </summary>
        [TestMethod]
        public void Geometry_SingleRow_ProducesNoSpeaker()
        {
            var result = GeometryTextBlockClassifier.Instance.Classify(null, new List<TextBlock>
            {
                Block("The Hollow swallowed the district whole that night.", 300, 120, 1200, 44),
            });

            Assert.AreEqual(string.Empty, result.NpcName);
            Assert.AreEqual(1, result.DialogueBlocks.Count);
        }

        /// <summary>
        /// The primary signal, inverted: a top row wider than everything under it
        /// is a body line, whatever its character count suggests.
        /// </summary>
        [TestMethod]
        public void Geometry_TopRowWiderThanBody_StaysDialogue()
        {
            var result = GeometryTextBlockClassifier.Instance.Classify(null, new List<TextBlock>
            {
                Block("A very wide opening line that runs the full panel width", 200, 100, 1200, 44),
                Block("short reply", 200, 160, 300, 44),
            });

            Assert.AreEqual(string.Empty, result.NpcName);
            Assert.AreEqual(2, result.DialogueBlocks.Count);
        }

        /// <summary>
        /// The counter-case the thresholds are calibrated against: a genuinely
        /// short first dialogue line above a long one. Character count alone would
        /// call this a name; the width term has to veto it.
        ///
        /// <para>Deleting a body line is the expensive mistake — it removes text
        /// from the match input — so the classifier is tuned to refuse when the
        /// evidence is mixed.</para>
        /// </summary>
        [TestMethod]
        public void Geometry_ShortFirstBodyLine_IsNotMistakenForSpeaker()
        {
            var result = GeometryTextBlockClassifier.Instance.Classify(null, new List<TextBlock>
            {
                Block("Not now.", 30, 145, 220, 58),
                Block("This second dialogue line is much longer.", 30, 245, 700, 58),
            });

            Assert.AreEqual(string.Empty, result.NpcName);
            Assert.AreEqual(2, result.DialogueBlocks.Count);
            StringAssert.Contains(result.DialogueText, "Not now.");
        }

        /// <summary>
        /// Overlapping boxes. The name box extends 10 px past the top of the first
        /// body line, so <c>top − bottom</c> is negative; centre-Y proximity still
        /// separates them.
        /// </summary>
        [TestMethod]
        public void Geometry_OverlappingNameAndBodyBoxes_StillSplit()
        {
            var result = GeometryTextBlockClassifier.Instance.Classify(null, new List<TextBlock>
            {
                Block("Anby", 900, 100, 180, 50),   // bottom = 150
                Block("Target acquired. Moving to intercept the Ethereal.", 200, 140, 1200, 50), // top = 140
            });

            Assert.AreEqual("Anby", result.NpcName);
            Assert.AreEqual(1, result.DialogueBlocks.Count);
        }

        /// <summary>
        /// Two boxes on one printed line must land in one band, so neither can be
        /// promoted to speaker. Mirrors the accent classifier's same-line
        /// highlight case.
        /// </summary>
        [TestMethod]
        public void Geometry_TwoBlocksOnOneLine_ShareOneBand()
        {
            var result = GeometryTextBlockClassifier.Instance.Classify(null, new List<TextBlock>
            {
                Block("Important", 10, 25, 90, 28),
                Block("dialogue continues", 105, 25, 220, 28),
            });

            Assert.AreEqual(string.Empty, result.NpcName);
            Assert.AreEqual(2, result.DialogueBlocks.Count);
        }

        /// <summary>
        /// A row of pure ornament carries no name. It is dropped as junk rather
        /// than promoted, and the real body survives intact.
        /// </summary>
        [TestMethod]
        public void Geometry_DecorationOnlyRow_IsDroppedNotPromoted()
        {
            var result = GeometryTextBlockClassifier.Instance.Classify(null, new List<TextBlock>
            {
                Block("··", 940, 40, 40, 30),
                Block("A full width opening line of dialogue text here", 280, 120, 1300, 44),
                Block("and a second full width line right underneath it", 280, 170, 1280, 44),
            });

            Assert.AreEqual(string.Empty, result.NpcName);
            Assert.AreEqual(2, result.DialogueBlocks.Count);
            Assert.IsFalse(result.DialogueText.Contains("··"));
        }

        /// <summary>
        /// A frame of nothing but watermarks. The junk filter would empty it, so
        /// the classifier falls back to the unfiltered set — losing text outright
        /// is worse than passing junk to a matcher that will simply not match it.
        /// No speaker is claimed either way.
        /// </summary>
        [TestMethod]
        public void Geometry_AllJunkFrame_KeepsTextAndClaimsNoSpeaker()
        {
            var result = GeometryTextBlockClassifier.Instance.Classify(null, new List<TextBlock>
            {
                Block("UBHEN92", 1567, 228, 308, 62),
                Block("UID: 1000290822.", 1761, 299, 159, 29),
            });

            Assert.AreEqual(0, result.NpcBlocks.Count);
            Assert.AreEqual(2, result.DialogueBlocks.Count);
        }

        /// <summary>A long top row is never a name, however small its ratios look.</summary>
        [TestMethod]
        public void Geometry_TopRowOverNameLengthCap_StaysDialogue()
        {
            string longLine = new string('a', GeometryTextBlockClassifier.MaxSpeakerNameChars + 1);
            string body = new string('b', 900);

            var result = GeometryTextBlockClassifier.Instance.Classify(null, new List<TextBlock>
            {
                Block(longLine, 900, 100, 100, 40),
                Block(body, 200, 200, 1400, 40),
            });

            Assert.AreEqual(string.Empty, result.NpcName);
            Assert.AreEqual(2, result.DialogueBlocks.Count);
        }

        [TestMethod]
        public void Geometry_NullAndEmptyInput_ReturnEmptyResult()
        {
            Assert.AreEqual(0, GeometryTextBlockClassifier.Instance.Classify(null, null).DialogueBlocks.Count);
            Assert.AreEqual(0, GeometryTextBlockClassifier.Instance
                .Classify(null, new List<TextBlock>()).DialogueBlocks.Count);
        }

        /// <summary>
        /// Blocks come back in reading order regardless of the order OCR emitted
        /// them, so <c>DialogueText</c> concatenates into a matchable sentence.
        /// </summary>
        [TestMethod]
        public void Geometry_ShuffledInput_EmitsBlocksInReadingOrder()
        {
            var result = GeometryTextBlockClassifier.Instance.Classify(null, new List<TextBlock>
            {
                Block("third line of the body text here", 200, 220, 1000, 40),
                Block("Nicole", 900, 100, 160, 40),
                Block("second line of the body text here", 200, 160, 1100, 40),
            });

            Assert.AreEqual("Nicole", result.NpcName);
            Assert.AreEqual("second line of the body text here\nthird line of the body text here",
                result.DialogueText);
        }

        // ══════════════════════════════════════════════════════════════
        //  Junk filter
        // ══════════════════════════════════════════════════════════════

        [DataTestMethod]
        [DataRow("UID: 1000290822.", true, "digit-dominant watermark")]
        [DataRow("UBHEN92", true, "single-token handle with digits")]
        [DataRow("×", true, "single glyph close button")]
        [DataRow("1000290822", true, "no letters at all")]
        [DataRow("Auto", true, "HUD label, exact match")]
        [DataRow("-Remielle", false, "speaker name")]
        [DataRow("Billy", false, "speaker name")]
        [DataRow("ritual,", false, "short but real body line")]
        [DataRow("Having breakfast together with you guys every", false, "body line")]
        [DataRow("Skip it, we're leaving.", false, "HUD word inside a real sentence")]
        public void JunkFilter_MatchesMeasuredBlocks(string text, bool expected, string because)
        {
            Assert.AreEqual(expected, OcrTextJunkFilter.IsJunk(text), because);
        }

        // ══════════════════════════════════════════════════════════════
        //  Decoration stripping / name normalization
        // ══════════════════════════════════════════════════════════════

        [DataTestMethod]
        [DataRow("-Remielle", "Remielle")]
        [DataRow("··Remielle··", "Remielle")]
        [DataRow("~ Von Lycaon ~", "Von Lycaon")]
        [DataRow("Billy", "Billy")]
        [DataRow("  Ellen Joe  ", "Ellen Joe")]
        [DataRow("Nicole:", "Nicole")]
        [DataRow("··", "")]
        [DataRow("", "")]
        public void SpeakerNameDecoration_StripsOrnamentRunsOnly(string raw, string expected)
        {
            Assert.AreEqual(expected, SpeakerNameDecoration.Strip(raw));
        }

        /// <summary>
        /// The bug the normalizer exists for: the default splits on
        /// <c>{' ', ',', '.'}</c> only, so a leading hyphen rides along into the
        /// index key and the speaker never resolves.
        /// </summary>
        [TestMethod]
        public void ZzzNameNormalizer_StripsWhatTheDefaultNormalizerLeavesBehind()
        {
            var zzz = new ZzzNameNormalizer();
            var trim = new TrimNameNormalizer();

            Assert.AreEqual("-remielle", trim.ExtractFirstName("-Remielle"),
                "Baseline: this is the failure being fixed.");

            Assert.AreEqual("remielle", zzz.ExtractFirstName("-Remielle"));
            Assert.AreEqual("remielle", zzz.NormalizeFull("··Remielle··"));
            Assert.AreEqual("von", zzz.ExtractFirstName("~ Von Lycaon ~"));
            Assert.AreEqual("billy", zzz.ExtractFirstName("Billy"));
        }

        [TestMethod]
        public void ZzzNameNormalizer_EmptyAndOrnamentOnlyInputs_ReturnEmpty()
        {
            var zzz = new ZzzNameNormalizer();

            Assert.AreEqual(string.Empty, zzz.ExtractFirstName(null));
            Assert.AreEqual(string.Empty, zzz.ExtractFirstName(""));
            Assert.AreEqual(string.Empty, zzz.ExtractFirstName("···"));
            Assert.AreEqual(string.Empty, zzz.NormalizeFull("···"));
        }

        [TestMethod]
        public void NpcNameNormalizerFactory_SelectsPerGame()
        {
            Assert.IsInstanceOfType(NpcNameNormalizerFactory.Create("zzz"), typeof(ZzzNameNormalizer));
            Assert.IsInstanceOfType(NpcNameNormalizerFactory.Create("Genshin"), typeof(TrimNameNormalizer));
            Assert.IsInstanceOfType(NpcNameNormalizerFactory.Create(null), typeof(TrimNameNormalizer));
        }

        // ══════════════════════════════════════════════════════════════
        //  Per-game selection
        // ══════════════════════════════════════════════════════════════

        [DataTestMethod]
        [DataRow("zzz")]
        [DataRow("ZZZ")]
        [DataRow("Zenless")]
        [DataRow("ZenlessZoneZero")]
        [DataRow("Zenless Zone Zero")]
        public void ClassifierFactory_ZenlessSpellings_SelectGeometry(string game)
        {
            Assert.IsInstanceOfType(
                TextBlockClassifierFactory.Create(game), typeof(GeometryTextBlockClassifier));
        }

        [DataTestMethod]
        [DataRow("Genshin")]
        [DataRow("StarRail")]
        [DataRow("")]
        [DataRow(null)]
        [DataRow("SomeUnreleasedGame")]
        public void ClassifierFactory_EverythingElse_SelectsAccentColour(string game)
        {
            Assert.IsInstanceOfType(
                TextBlockClassifierFactory.Create(game), typeof(AccentColorTextBlockClassifier));
        }

        /// <summary>
        /// The shipping games must resolve to the values that used to be inline
        /// literals, so routing through the profile changes nothing for them.
        /// </summary>
        [TestMethod]
        public void VisionProfile_ShippingGames_KeepTheirInlinedDefaults()
        {
            foreach (string game in new[] { "Genshin", "StarRail", "", null, "Unknown" })
            {
                var p = GameRegionProfile.Get(game);
                Assert.IsFalse(p.UsesGeometryClassifier, game ?? "(null)");
                Assert.AreEqual(-140, p.SubtitlePadVertical, game ?? "(null)");
                Assert.AreEqual(0.55, p.RegionSplitXFraction, 1e-9, game ?? "(null)");
                Assert.AreEqual(0.70, p.RegionMinWidthFraction, 1e-9, game ?? "(null)");
                Assert.IsFalse(p.StrictRegionJunkFilter, game ?? "(null)");
            }
        }

        [TestMethod]
        public void VisionProfile_Zenless_LiftsOverlayAndNarrowsRegionFloor()
        {
            var p = GameRegionProfile.Get("zzz");

            Assert.IsTrue(p.UsesGeometryClassifier);
            Assert.IsTrue(p.SubtitlePadVertical < -140,
                "Zenless captions wrap taller, so the overlay needs more clearance above the region.");
            Assert.IsTrue(p.RegionMinWidthFraction < 0.70,
                "A 0.70 floor over-widens the narrow comic panel and pulls in HUD/watermarks.");
            Assert.IsTrue(p.StrictRegionJunkFilter);
        }

        // ══════════════════════════════════════════════════════════════
        //  Accent path is untouched
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// The refactor moved the accent logic behind an interface. This is the
        /// belt to <c>RuntimeRegressionTests.NpcClassifier_*</c>'s braces: the shim
        /// and the extracted classifier must produce the same split.
        /// </summary>
        [TestMethod]
        public void AccentClassifier_ShimAndExtractedClassAgree()
        {
            using var frame = new Mat(150, 320, MatType.CV_8UC3, Scalar.Black);
            Cv2.Rectangle(frame, new Rect(10, 10, 120, 25), new Scalar(40, 190, 255), -1); // gold, BGR
            Cv2.Rectangle(frame, new Rect(10, 65, 280, 25), Scalar.White, -1);

            var blocks = new List<TextBlock>
            {
                Block("Paimon", 10, 10, 120, 25),
                Block("This remains dialogue", 10, 65, 280, 25),
            };

            var viaShim = GI_Subtitles.Services.OCR.ImageProcessor
                .ClassifyTextBlocksWithPositions(frame, blocks);
            var viaClass = AccentColorTextBlockClassifier.Instance.Classify(frame, blocks);

            Assert.AreEqual("Paimon", viaShim.NpcName);
            Assert.AreEqual(viaShim.NpcName, viaClass.NpcName);
            Assert.AreEqual(viaShim.DialogueText, viaClass.DialogueText);
        }

        /// <summary>
        /// The measured failure that motivated the whole workstream: a white
        /// speaker name gives the accent gate nothing to measure, so it lands in
        /// the body. Recorded as an assertion so nobody "fixes" Zenless by
        /// loosening the hue bands.
        /// </summary>
        [TestMethod]
        public void AccentClassifier_WhiteSpeakerName_FindsNoName()
        {
            using var frame = new Mat(240, 1000, MatType.CV_8UC3, Scalar.Black);
            Cv2.Rectangle(frame, new Rect(430, 30, 140, 40), Scalar.White, -1);
            Cv2.Rectangle(frame, new Rect(100, 110, 800, 40), Scalar.White, -1);

            var result = GI_Subtitles.Services.OCR.ImageProcessor.ClassifyTextBlocksWithPositions(
                frame, new List<TextBlock>
                {
                    Block("Billy", 430, 30, 140, 40),
                    Block("Having breakfast together with you guys every", 100, 110, 800, 40),
                });

            Assert.AreEqual(string.Empty, result.NpcName, "No accent signal exists in a white-on-white UI.");
            Assert.AreEqual(2, result.DialogueBlocks.Count, "…so the name lands in the body. That is the bug.");
        }

        // ══════════════════════════════════════════════════════════════
        //  Golden — real OCR over the real screenshots
        // ══════════════════════════════════════════════════════════════

        [TestMethod]
        public void Golden_ZenlessCinematic_YieldsSpeakerAndBody()
        {
            var result = ClassifyScreenshot("zzz-cinematic.png");

            AssertNameMatches("Remielle", result.NpcName);
            AssertBodyContains(result.DialogueText, "on the verge of losing consciousness");
            Assert.IsFalse(Normalize(result.DialogueText).Contains("1000290822"),
                "The UID watermark must not survive into the dialogue body.");
        }

        [TestMethod]
        public void Golden_ZenlessComic_YieldsSpeakerAndBody()
        {
            var result = ClassifyScreenshot("zzz-comic.png");

            AssertNameMatches("Billy", result.NpcName);
            AssertBodyContains(result.DialogueText, "having breakfast together with you guys");
            Assert.IsFalse(Normalize(result.DialogueText).Contains("1000290822"),
                "The UID watermark must not survive into the dialogue body.");
        }

        // ══════════════════════════════════════════════════════════════
        //  Harness
        // ══════════════════════════════════════════════════════════════

        /// <summary>Fraction of frame height, from the bottom, that the crop covers. Matches the diagnostics harness.</summary>
        private const double BottomCropFraction = 0.30;

        private static PaddleOCREngine _engine;
        private static string _engineFailure;

        private static DetectedTextResult ClassifyScreenshot(string fileName)
        {
            string path = Path.Combine(RepoRoot(), "docs", "screenshots", fileName);
            if (!File.Exists(path))
                Assert.Inconclusive($"Screenshot not found: {path}");

            PaddleOCREngine engine = SharedEngine();

            using var screenshot = Cv2.ImRead(path);
            if (screenshot.Empty())
                Assert.Inconclusive($"Could not decode {fileName}.");

            int cropTop = (int)(screenshot.Height * (1.0 - BottomCropFraction));
            using var crop = new Mat(screenshot,
                new Rect(0, cropTop, screenshot.Width, screenshot.Height - cropTop));

            OCRResult ocr = engine.DetectTextFromMat(crop);
            if (ocr?.TextBlocks == null || ocr.TextBlocks.Count == 0)
                Assert.Inconclusive($"OCR returned no blocks for {fileName}; nothing to classify.");

            return GeometryTextBlockClassifier.Instance.Classify(crop, ocr.TextBlocks);
        }

        /// <summary>
        /// One engine for the whole class — construction plus warm-up costs
        /// several seconds, and MSTest runs a class's methods sequentially.
        /// A missing or unloadable model set degrades every golden test to
        /// Inconclusive rather than red, matching the convention elsewhere in
        /// this suite.
        /// </summary>
        private static PaddleOCREngine SharedEngine()
        {
            if (_engine != null) return _engine;
            if (_engineFailure != null) Assert.Inconclusive(_engineFailure);

            try
            {
                string assemblyRoot = Path.GetDirectoryName(typeof(OCRModelConfig).Assembly.Location);
                OcrModelProfile profile = OcrModelProfiles.Resolve(OcrModelProfiles.RecommendedId, "EN");
                OCRModelConfig config = profile.CreateModelConfig(Path.Combine(assemblyRoot, "inference"));
                var parameters = new OCRParameter
                {
                    use_gpu = true,
                    cpu_math_library_num_threads = 3,
                    max_side_len = 960,
                    det_db_thresh = profile.DetectionThreshold,
                    det_db_box_thresh = profile.DetectionBoxThreshold,
                    det_db_unclip_ratio = profile.DetectionUnclipRatio,
                    rec_img_h = profile.RecognitionImageHeight,
                    rec_score_thresh = 0.5f,
                };

                var engine = new PaddleOCREngine(config, parameters);
                engine.WarmUp(TimeSpan.FromSeconds(30));
                _engine = engine;
                return _engine;
            }
            catch (Exception ex)
            {
                _engineFailure =
                    $"OCR engine unavailable in this test host ({ex.GetType().Name}: {ex.Message}). " +
                    "The inference/ model folder is maintained out-of-band and is not checked in.";
                Assert.Inconclusive(_engineFailure);
                return null; // unreachable
            }
        }

        [ClassCleanup]
        public static void DisposeEngine()
        {
            _engine?.Dispose();
            _engine = null;
        }

        private static string RepoRoot()
        {
            string assemblyRoot = Path.GetDirectoryName(typeof(OCRModelConfig).Assembly.Location);
            return Path.GetFullPath(Path.Combine(assemblyRoot, "..", "..", ".."));
        }

        // ── Tolerant assertions ───────────────────────────────────────

        /// <summary>
        /// Compare on letters and digits only, lower-cased. OCR drifts on
        /// punctuation, spacing and diacritics far more than on glyph identity,
        /// and none of that drift changes whether the classifier did its job.
        /// </summary>
        private static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static void AssertNameMatches(string expected, string actual)
        {
            string e = Normalize(expected);
            string a = Normalize(actual);
            int distance = EditDistance(e, a);

            Assert.IsTrue(distance <= 2,
                $"Expected speaker ≈ \"{expected}\" but got \"{actual}\" " +
                $"(normalized \"{e}\" vs \"{a}\", edit distance {distance}).");
        }

        private static void AssertBodyContains(string body, string phrase)
        {
            Assert.IsTrue(Normalize(body).Contains(Normalize(phrase)),
                $"Expected the dialogue body to contain ≈ \"{phrase}\". Body was: \"{body}\".");
        }

        private static int EditDistance(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) previous[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(
                        Math.Min(current[j - 1] + 1, previous[j] + 1),
                        previous[j - 1] + cost);
                }
                Array.Copy(current, previous, current.Length);
            }

            return previous[b.Length];
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
