using System.IO;
using GI_Subtitles.Services.OCR;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaddleOCRSharp;

namespace GI_Test
{
    /// <summary>
    /// Every shipped OCR profile must resolve to files that exist both in the
    /// repo and in the build output, or the release ships a fallback that
    /// cannot load.
    /// </summary>
    [TestClass]
    public class OcrModelAssetTests
    {
        private static string OutputInferenceRoot => Path.Combine(
            Path.GetDirectoryName(typeof(OCRModelConfig).Assembly.Location), "inference");

        private static string SourceInferenceRoot => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(OCRModelConfig).Assembly.Location),
            "..", "..", "..", "PaddleOCRSharp", "inference"));

        [TestMethod]
        [DataRow(OcrModelProfiles.RecommendedId, "EN")]
        [DataRow(OcrModelProfiles.RecommendedId, "JP")]
        [DataRow(OcrModelProfiles.CompatibilityId, "EN")]
        [DataRow(OcrModelProfiles.CompatibilityId, "JP")]
        public void Profile_ResolvesToFilesThatExist(string profileId, string language)
        {
            OcrModelProfile profile = OcrModelProfiles.Resolve(profileId, language);
            foreach (string root in new[] { SourceInferenceRoot, OutputInferenceRoot })
            {
                OCRModelConfig config = profile.CreateModelConfig(root);
                Assert.IsTrue(File.Exists(config.det_infer), $"{profile.ModelName}: missing {config.det_infer}");
                Assert.IsTrue(File.Exists(config.rec_infer), $"{profile.ModelName}: missing {config.rec_infer}");

                // The engine reads the dictionary from inference.yml first, else from keys.
                string yml = Path.Combine(Path.GetDirectoryName(config.rec_infer), "inference.yml");
                if (File.Exists(yml))
                    Assert.IsTrue(PaddleOCREngine.LoadLabelsFromYaml(yml).Count > 0, $"empty dictionary in {yml}");
                else
                    Assert.IsTrue(config.keys != null && File.Exists(config.keys),
                        $"{profile.ModelName}: no inference.yml and no dictionary at {config.keys}");
            }
        }

        [TestMethod]
        public void Compatibility_English_UsesEnPpOcrV5Recognizer_JapaneseKeepsV4()
        {
            OCRModelConfig en = OcrModelProfiles.Resolve(OcrModelProfiles.CompatibilityId, "EN")
                .CreateModelConfig(SourceInferenceRoot);
            OCRModelConfig jp = OcrModelProfiles.Resolve(OcrModelProfiles.CompatibilityId, "JP")
                .CreateModelConfig(SourceInferenceRoot);

            // MainWindow's CPU recovery tells the profiles apart by "v6" in ModelName.
            foreach (string language in new[] { "EN", "JP" })
            {
                StringAssert.Contains(OcrModelProfiles.Resolve(OcrModelProfiles.RecommendedId, language).ModelName, "v6");
                Assert.IsFalse(OcrModelProfiles.Resolve(OcrModelProfiles.CompatibilityId, language)
                    .ModelName.Contains("v6", System.StringComparison.OrdinalIgnoreCase));
            }

            StringAssert.Contains(en.rec_infer, "en_PP-OCRv5_mobile_rec_infer");
            StringAssert.Contains(jp.rec_infer, "jp_PP-OCRv4_mobile_rec_infer");
            // en_PP-OCRv5's official yml carries its 436-character English dictionary.
            Assert.AreEqual(436, PaddleOCREngine.LoadLabelsFromYaml(
                Path.Combine(Path.GetDirectoryName(en.rec_infer), "inference.yml")).Count);
        }

        [TestMethod]
        public void Compatibility_English_ReadsRealGenshinDialogueOnCpu()
        {
            string assemblyRoot = Path.GetDirectoryName(typeof(OCRModelConfig).Assembly.Location);
            string screenshotPath = Path.Combine(
                Path.GetFullPath(Path.Combine(assemblyRoot, "..", "..", "..")), "docs", "screenshots", "hero-dialog.jpg");
            OcrModelProfile profile = OcrModelProfiles.Resolve(OcrModelProfiles.CompatibilityId, "EN");
            var parameters = new OCRParameter
            {
                // The compatibility profile is the CPU fallback, so test it there.
                use_gpu = false,
                cpu_math_library_num_threads = 3,
                max_side_len = 960,
                det_db_thresh = profile.DetectionThreshold,
                det_db_box_thresh = profile.DetectionBoxThreshold,
                det_db_unclip_ratio = profile.DetectionUnclipRatio,
                rec_img_h = profile.RecognitionImageHeight,
                rec_score_thresh = 0.5f,
            };

            using var engine = new PaddleOCREngine(profile.CreateModelConfig(OutputInferenceRoot), parameters);
            using var screenshot = OpenCvSharp.Cv2.ImRead(screenshotPath);
            using var dialogueArea = new OpenCvSharp.Mat(
                screenshot,
                new OpenCvSharp.Rect(0, screenshot.Height * 3 / 4, screenshot.Width, screenshot.Height / 4));

            OCRResult result = engine.DetectTextFromMat(dialogueArea);

            StringAssert.Contains(result.Text, "Welcome to my humble shop",
                $"Unexpected compatibility-profile output: {result.Text}");
        }
    }
}
