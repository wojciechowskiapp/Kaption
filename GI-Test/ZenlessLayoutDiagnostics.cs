using System;
using System.IO;
using System.Linq;
using GI_Subtitles.Models;
using GI_Subtitles.Services.OCR;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using PaddleOCRSharp;

namespace GI_Test
{
    /// <summary>
    /// Diagnostic harness for the Zenless Zone Zero layout work — NOT an assertion suite.
    ///
    /// ZZZ presents dialogue in two structurally different ways, neither of which
    /// matches the Genshin/HSR layout family the pipeline was tuned for:
    ///
    ///   (a) cinematic  — speaker name CENTERED above CENTERED body text, flanked by
    ///                    decorative dots, white-on-scene with a heavy black outline
    ///                    and NO panel background.
    ///   (b) comic      — speaker name in a small centered pill tab above a solid dark
    ///                    rounded panel; body text LEFT-aligned inside the panel.
    ///
    /// Both render the speaker name in plain white, so the shipping HSV accent gate
    /// in <see cref="ImageProcessor.ClassifyTextBlocksWithPositions"/> has no signal to
    /// work with. This harness dumps the real OCR geometry so the replacement
    /// classifier can be tuned against measurements instead of guesses.
    ///
    /// Run it and read the console output; it asserts only that OCR returned something.
    /// </summary>
    [TestClass]
    public class ZenlessLayoutDiagnostics
    {
        /// <summary>Fraction of frame height, measured from the bottom, that the crop covers.</summary>
        private const double BottomCropFraction = 0.30;

        [TestMethod]
        [TestCategory("Diagnostics")]
        public void Dump_ZenlessCinematic_Layout()
        {
            DumpLayout("zzz-cinematic.png", "STYLE A — cinematic (centered name, no panel)");
        }

        [TestMethod]
        [TestCategory("Diagnostics")]
        public void Dump_ZenlessComic_Layout()
        {
            DumpLayout("zzz-comic.png", "STYLE B — comic strip (pill-tab name, dark panel)");
        }

        /// <summary>
        /// Genshin reference run. Gives a known-good baseline in the same output format,
        /// so the ZZZ numbers can be read as "differs from what works" rather than in a vacuum.
        /// </summary>
        [TestMethod]
        [TestCategory("Diagnostics")]
        public void Dump_GenshinReference_Layout()
        {
            DumpLayout("hero-dialog.jpg", "REFERENCE — Genshin (accent name, dark panel)");
        }

        private static void DumpLayout(string fileName, string caption)
        {
            string assemblyRoot = Path.GetDirectoryName(typeof(OCRModelConfig).Assembly.Location);
            string repoRoot = Path.GetFullPath(Path.Combine(assemblyRoot, "..", "..", ".."));
            string screenshotPath = Path.Combine(repoRoot, "docs", "screenshots", fileName);

            if (!File.Exists(screenshotPath))
            {
                Assert.Inconclusive($"Screenshot not found: {screenshotPath}");
            }

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

            using var engine = new PaddleOCREngine(config, parameters);
            engine.WarmUp(TimeSpan.FromSeconds(30));

            using var screenshot = Cv2.ImRead(screenshotPath);
            int cropTop = (int)(screenshot.Height * (1.0 - BottomCropFraction));
            using var crop = new Mat(
                screenshot,
                new Rect(0, cropTop, screenshot.Width, screenshot.Height - cropTop));

            OCRResult result = engine.DetectTextFromMat(crop);

            Console.WriteLine();
            Console.WriteLine("================================================================");
            Console.WriteLine(caption);
            Console.WriteLine($"file       : {fileName}");
            Console.WriteLine($"frame      : {screenshot.Width}x{screenshot.Height}");
            Console.WriteLine($"crop       : y={cropTop} h={crop.Height} (bottom {BottomCropFraction:P0})");
            Console.WriteLine($"provider   : {engine.ExecutionProvider} (gpu={engine.IsUsingGpu})");
            Console.WriteLine("================================================================");

            if (result?.TextBlocks == null || result.TextBlocks.Count == 0)
            {
                Console.WriteLine("!! OCR returned NO blocks.");
                Assert.Inconclusive($"No OCR blocks for {fileName} — cannot profile this layout.");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("--- RAW OCR BLOCKS (crop space, top-to-bottom) ---");
            Console.WriteLine($"{"#",-3} {"x",6} {"y",6} {"w",6} {"h",6} {"cx%",7} {"conf",6}  text");

            var ordered = result.TextBlocks
                .Where(b => b.BoxPoints != null && b.BoxPoints.Length >= 4)
                .Select(b => new
                {
                    Block = b,
                    X = b.BoxPoints.Min(p => p.X),
                    Y = b.BoxPoints.Min(p => p.Y),
                    R = b.BoxPoints.Max(p => p.X),
                    B = b.BoxPoints.Max(p => p.Y),
                })
                .OrderBy(b => b.Y)
                .ToList();

            int i = 0;
            foreach (var b in ordered)
            {
                float w = b.R - b.X;
                float h = b.B - b.Y;
                // Centre of the block as a percentage of crop width — the signal that
                // separates a centered name from a left-aligned body.
                double centreXPct = ((b.X + w / 2f) / crop.Width) * 100.0;
                Console.WriteLine(
                    $"{i,-3} {b.X,6:F0} {b.Y,6:F0} {w,6:F0} {h,6:F0} {centreXPct,6:F1}% {b.Block.Score,6:F3}  \"{b.Block.Text}\"");
                i++;
            }

            // Geometry summary — the four signals the replacement classifier scores on.
            if (ordered.Count >= 2)
            {
                var top = ordered[0];
                var rest = ordered.Skip(1).ToList();
                float topW = top.R - top.X;
                float topH = top.B - top.Y;
                float restMaxW = rest.Max(b => b.R - b.X);
                float restAvgH = rest.Average(b => b.B - b.Y);
                float gap = rest.Min(b => b.Y) - top.B;

                Console.WriteLine();
                Console.WriteLine("--- GEOMETRY SIGNALS (topmost block vs the rest) ---");
                Console.WriteLine($"width ratio      : {topW / restMaxW,6:F3}   (name expected << 1)");
                Console.WriteLine($"height ratio     : {topH / restAvgH,6:F3}   (name expected < 1)");
                Console.WriteLine($"vertical gap     : {gap,6:F1}px  ({gap / Math.Max(1f, topH),5:F2} x name height)");
                Console.WriteLine($"top block chars  : {top.Block.Text?.Length,6}   (name expected short)");
                Console.WriteLine($"body chars       : {rest.Sum(b => b.Block.Text?.Length ?? 0),6}");
            }

            // What the SHIPPING classifier does with this frame. For both ZZZ styles the
            // expected outcome is that the speaker name lands in the dialogue body.
            DetectedTextResult classified = ImageProcessor.ClassifyTextBlocksWithPositions(
                crop, result.TextBlocks);

            Console.WriteLine();
            Console.WriteLine("--- SHIPPING CLASSIFIER (HSV accent gate) ---");
            Console.WriteLine($"NpcName      : \"{classified.NpcName}\"");
            Console.WriteLine($"NpcBlocks    : {classified.NpcBlocks.Count}");
            Console.WriteLine($"DialogueBlks : {classified.DialogueBlocks.Count}");
            Console.WriteLine($"DialogueText : \"{classified.DialogueText?.Replace("\n", " | ")}\"");
            Console.WriteLine("================================================================");
            Console.WriteLine();

            Assert.IsTrue(result.TextBlocks.Count > 0);
        }
    }
}
