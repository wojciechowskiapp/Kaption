using System.IO;
using GI_Subtitles.Services.Capture;
using GI_Subtitles.Services.OCR;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaddleOCRSharp;

namespace GI_Test
{
    [TestClass]
    public class OcrPipelineRegressionTests
    {
        [TestMethod]
        public void DxgiRegion_PrimaryOutput_UsesOutputLocalCoordinates()
        {
            bool ok = DxgiScreenCapture.TryTranslateRegionToOutput(
                100, 200, 800, 300,
                0, 0, 1920, 1080,
                out int localX, out int localY);

            Assert.IsTrue(ok);
            Assert.AreEqual(100, localX);
            Assert.AreEqual(200, localY);
        }

        [TestMethod]
        public void DxgiRegion_SecondMonitor_IsRejectedByPrimaryOutput()
        {
            bool ok = DxgiScreenCapture.TryTranslateRegionToOutput(
                2100, 700, 900, 250,
                0, 0, 1920, 1080,
                out _, out _);

            Assert.IsFalse(ok,
                "A second-monitor region must fall back to GDI, not clamp to one primary-output pixel.");
        }

        [TestMethod]
        public void DxgiRegion_NegativeOutputOrigin_TranslatesCorrectly()
        {
            bool ok = DxgiScreenCapture.TryTranslateRegionToOutput(
                -1800, 100, 800, 300,
                -1920, 0, 1920, 1080,
                out int localX, out int localY);

            Assert.IsTrue(ok);
            Assert.AreEqual(120, localX);
            Assert.AreEqual(100, localY);
        }

        [TestMethod]
        public void CtcDecoder_ReturnsMeanConfidenceForDecodedCharacters()
        {
            // Classes: blank, A, B, space. Duplicate A at t=2 is collapsed.
            var output = new DenseTensor<float>(new[]
            {
                0.90f, 0.05f, 0.03f, 0.02f,
                0.10f, 0.80f, 0.05f, 0.05f,
                0.10f, 0.70f, 0.10f, 0.10f,
                0.10f, 0.10f, 0.60f, 0.20f,
            }, new[] { 1, 4, 4 });

            var decoded = PaddleOCREngine.DecodeText(output, new[] { "A", "B" });

            Assert.AreEqual("AB", decoded.Text);
            Assert.AreEqual(0.70f, decoded.Score, 0.0001f);
        }

        [TestMethod]
        public void PpOcrV6YamlDictionary_UnquotesCharactersAndMatchesModelClasses()
        {
            string assemblyRoot = Path.GetDirectoryName(typeof(OCRModelConfig).Assembly.Location);
            string yamlPath = Path.Combine(
                assemblyRoot, "inference", "Rec", "V6",
                "PP-OCRv6_small_rec_infer", "inference.yml");

            var labels = PaddleOCREngine.LoadLabelsFromYaml(yamlPath);

            Assert.AreEqual(18_708, labels.Count);
            Assert.AreEqual("!", labels[0], "Quoted YAML punctuation must not retain quote marks.");
            Assert.IsTrue(labels.Contains("A"));
            Assert.IsTrue(labels.Contains("あ"));
            Assert.AreEqual("'", PaddleOCREngine.ParseYamlListScalar("''''"));
            Assert.AreEqual("\\", PaddleOCREngine.ParseYamlListScalar("\\"));
        }

        [TestMethod]
        public void OcrModelProfile_DefaultsToV6Small_WithSafeV4Fallback()
        {
            OcrModelProfile recommended = OcrModelProfiles.Resolve(null, "EN");

            Assert.AreEqual(OcrModelProfiles.RecommendedId, recommended.Id);
            Assert.AreEqual("PP-OCRv6 Small", recommended.ModelName);
            Assert.IsTrue(recommended.FallsBackToCompatibilityProfile);
            Assert.AreEqual(0.20f, recommended.DetectionThreshold, 0.0001f);
            Assert.AreEqual(0.45f, recommended.DetectionBoxThreshold, 0.0001f);
            Assert.AreEqual(1.40f, recommended.DetectionUnclipRatio, 0.0001f);
        }

        [TestMethod]
        public void OcrModelProfile_V4Compatibility_KeepsJapaneseRecognizer()
        {
            OcrModelProfile compatibility = OcrModelProfiles.Resolve(
                OcrModelProfiles.CompatibilityId, "JP");
            OCRModelConfig config = compatibility.CreateModelConfig("C:\\models");

            Assert.AreEqual(OcrModelProfiles.CompatibilityId, compatibility.Id);
            StringAssert.Contains(config.rec_infer, "jp_PP-OCRv4_mobile_rec_infer");
            Assert.IsFalse(compatibility.FallsBackToCompatibilityProfile);
        }

        [TestMethod]
        public void PpOcrV6Small_RecognizesRealGenshinScreenText()
        {
            string assemblyRoot = Path.GetDirectoryName(typeof(OCRModelConfig).Assembly.Location);
            string repoRoot = Path.GetFullPath(Path.Combine(assemblyRoot, "..", "..", ".."));
            string screenshotPath = Path.Combine(
                repoRoot, "landing", "public", "en-pl-npc-dialog2.jpg");
            OcrModelProfile profile = OcrModelProfiles.Resolve(
                OcrModelProfiles.RecommendedId, "EN");
            OCRModelConfig config = profile.CreateModelConfig(
                Path.Combine(assemblyRoot, "inference"));
            var parameters = new OCRParameter
            {
                // Exercise the production path: request DirectML and allow
                // PaddleOCREngine's built-in CPU fallback on headless CI.
                use_gpu = true,
                cpu_math_library_num_threads = 3,
                max_side_len = 960,
                det_db_thresh = profile.DetectionThreshold,
                det_db_box_thresh = profile.DetectionBoxThreshold,
                det_db_unclip_ratio = profile.DetectionUnclipRatio,
                rec_img_h = profile.RecognitionImageHeight,
                rec_score_thresh = 0.5f,
            };

            var timer = System.Diagnostics.Stopwatch.StartNew();
            using var engine = new PaddleOCREngine(config, parameters);
            long initializationMs = timer.ElapsedMilliseconds;
            using var screenshot = OpenCvSharp.Cv2.ImRead(screenshotPath);
            using var dialogueArea = new OpenCvSharp.Mat(
                screenshot,
                new OpenCvSharp.Rect(0, screenshot.Height * 3 / 4,
                    screenshot.Width, screenshot.Height / 4));

            timer.Restart();
            OCRResult result = engine.DetectTextFromMat(dialogueArea);
            long firstInferenceMs = timer.ElapsedMilliseconds;
            timer.Restart();
            OCRResult warmResult = engine.DetectTextFromMat(dialogueArea);
            long warmInferenceMs = timer.ElapsedMilliseconds;

            System.Console.WriteLine(
                $"PP-OCRv6 provider={engine.ExecutionProvider}; GPU={engine.IsUsingGpu}; " +
                $"init={initializationMs}ms; first={firstInferenceMs}ms; warm={warmInferenceMs}ms");

            StringAssert.Contains(
                result.Text, "Welcome to my humble shop",
                $"Unexpected PP-OCRv6 output: {result.Text}");
            StringAssert.Contains(
                warmResult.Text, "Welcome to my humble shop",
                $"Unexpected warmed PP-OCRv6 output: {warmResult.Text}");
        }

        [TestMethod]
        public void PpOcrV6Small_IgnoresNoDialogueGameArea()
        {
            string assemblyRoot = Path.GetDirectoryName(typeof(OCRModelConfig).Assembly.Location);
            string repoRoot = Path.GetFullPath(Path.Combine(assemblyRoot, "..", "..", ".."));
            string screenshotPath = Path.Combine(
                repoRoot, "landing", "public", "en-pl-npc-dialog2.jpg");
            OcrModelProfile profile = OcrModelProfiles.Resolve(
                OcrModelProfiles.RecommendedId, "EN");
            var parameters = new OCRParameter
            {
                use_gpu = false,
                cpu_math_library_num_threads = 3,
                max_side_len = 960,
                det_db_thresh = profile.DetectionThreshold,
                det_db_box_thresh = profile.DetectionBoxThreshold,
                det_db_unclip_ratio = profile.DetectionUnclipRatio,
                rec_img_h = profile.RecognitionImageHeight,
                rec_score_thresh = 0.5f,
            };

            using var engine = new PaddleOCREngine(
                profile.CreateModelConfig(Path.Combine(assemblyRoot, "inference")),
                parameters);
            using var screenshot = OpenCvSharp.Cv2.ImRead(screenshotPath);
            using var noDialogueArea = new OpenCvSharp.Mat(
                screenshot,
                new OpenCvSharp.Rect(0, 0, screenshot.Width, screenshot.Height * 11 / 20));

            OCRResult result = engine.DetectTextFromMat(noDialogueArea);

            Assert.AreEqual(string.Empty, result.Text,
                $"Scenery-only area produced OCR hallucination: {result.Text}");
        }

    }
}
