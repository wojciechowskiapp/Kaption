using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GI_Subtitles.Services.Capture;
using GI_Subtitles.Services.OCR;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using PaddleOCRSharp;

namespace GI_Test
{
    /// <summary>
    /// The startup read check: a GPU engine must read a known word, not just run fast. Field report KAP-EF8C-C5CF:
    /// DirectML on an Intel UHD found every dialogue box and the recognizer accepted none, which the speed-only
    /// gates passed.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class OcrReadProbeTests
    {
        // Warm-up fallback, two real field regions, a thin strip, a 4K-wide region, a full 1080p frame, a small box.
        private static readonly Size[] Canvases =
        {
            new(960, 224), new(1313, 291), new(1369, 306), new(1920, 150),
            new(3840, 850), new(1920, 1080), new(400, 60),
        };

        // ── Decision ────────────────────────────────────────────────────────

        [TestMethod]
        public void Decide_OnlyAGpuMissThatTheProcessorReadsMovesToTheProcessor()
        {
            Assert.AreEqual(OcrReadProbeVerdict.GpuReads, OcrReadProbe.Decide(true, null));
            Assert.AreEqual(OcrReadProbeVerdict.GpuReads, OcrReadProbe.Decide(true, false));
            Assert.AreEqual(OcrReadProbeVerdict.GpuUnreadableCpuReads, OcrReadProbe.Decide(false, true));
            Assert.AreEqual(OcrReadProbeVerdict.Inconclusive, OcrReadProbe.Decide(false, false));
            Assert.AreEqual(OcrReadProbeVerdict.Inconclusive, OcrReadProbe.Decide(false, null));
            Assert.AreEqual(OcrReadProbeVerdict.Inconclusive, OcrReadProbe.Decide(null, true));
            Assert.AreEqual(OcrReadProbeVerdict.Inconclusive, OcrReadProbe.Decide(null, null));
        }

        [TestMethod]
        [DataRow("Traveler")]
        [DataRow("TRAVELER")]
        [DataRow("Traveler.")]
        [DataRow("Travelcr")]
        [DataRow(" Traveler ")]
        public void Reads_AcceptsTheWordWithOneMisreadLetter(string text)
        {
            Assert.IsTrue(OcrReadProbe.Reads(Result(text)));
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("~~")]
        [DataRow("Paimon")]
        [DataRow("Tr")]
        public void Reads_RejectsAnythingElse(string text)
        {
            Assert.IsFalse(OcrReadProbe.Reads(Result(text)));
        }

        [TestMethod]
        public void Reads_RejectsAnEmptyResult()
        {
            Assert.IsFalse(OcrReadProbe.Reads(null));
            Assert.IsFalse(OcrReadProbe.Reads(new OCRResult()));
            Assert.IsFalse(OcrReadProbe.Reads(new OCRResult { TextBlocks = new List<TextBlock>() }));
        }

        [TestMethod]
        public void Render_FillsTheRequestedCanvasWithLightTextOnDark()
        {
            foreach (Size canvas in Canvases)
            {
                using Mat image = OcrReadProbe.Render(canvas);
                Assert.AreEqual(canvas, image.Size(), canvas.ToString());
                Assert.AreEqual(3, image.Channels());

                using Mat gray = image.CvtColor(ColorConversionCodes.BGR2GRAY);
                Cv2.MinMaxLoc(gray, out double min, out double max);
                double litShare = Cv2.CountNonZero(gray.Threshold(200, 255, ThresholdTypes.Binary)) / (double)gray.Total();
                Assert.IsTrue(max > 200 && min < 60, $"{canvas}: min {min}, max {max}");
                Assert.IsTrue(litShare is > 0.001 and < 0.25, $"{canvas}: lit share {litShare:P2}");
            }
        }

        [TestMethod]
        public void CanvasFor_IsTheWarmedDetectorShape()
        {
            Assert.AreEqual(new Size(1313, 291), OcrReadProbe.CanvasFor(new Size(1313, 291)));
            Assert.AreEqual(PaddleOCREngine.WarmUpDetectorSize(null), OcrReadProbe.CanvasFor(null));
            Assert.AreEqual(new Size(960, 224), OcrReadProbe.CanvasFor(null));
        }

        // ── Real models ─────────────────────────────────────────────────────

        [TestMethod]
        [DataRow(OcrModelProfiles.RecommendedId)]
        [DataRow(OcrModelProfiles.CompatibilityId)]
        public void ProcessorReadsTheWord_AtEveryCanvasSize_InTheWarmedRecognitionWidth(string profileId)
        {
            using PaddleOCREngine engine = NewEngine(profileId, useGpu: false, gpuId: 0);
            Assert.IsFalse(engine.IsUsingGpu);
            int recognitionHeight = OcrModelProfiles.Resolve(profileId, "EN").RecognitionImageHeight;

            foreach (Size canvas in Canvases)
            {
                using Mat image = OcrReadProbe.Render(canvas);
                OCRResult result = engine.DetectTextFromMat(image);
                Assert.IsTrue(OcrReadProbe.Reads(result),
                    $"{profileId} {canvas}: read \"{string.Join(" | ", result.TextBlocks.Select(b => b.Text))}\"");

                // One crop, narrow enough for the 320 recognition bucket that warm-up compiled.
                TextBlock block = result.TextBlocks.Single();
                float width = block.BoxPoints.Max(p => p.X) - block.BoxPoints.Min(p => p.X);
                float height = block.BoxPoints.Max(p => p.Y) - block.BoxPoints.Min(p => p.Y);
                int bucket = PaddleOCREngine.GetRecognitionInputWidth(
                    (int)Math.Ceiling(width), (int)Math.Ceiling(height), recognitionHeight);
                Assert.AreEqual(320, bucket, $"{profileId} {canvas}: crop {width:F0}x{height:F0}");
            }

            // Too thin to draw the word safely: skipped, never a verdict against the GPU.
            Assert.IsNull(OcrReadProbe.TryRead(engine, new Size(3440, 80), TimeSpan.FromSeconds(10)));
        }

        [TestMethod]
        public void IsLegible_EveryTestedCanvas_ButNotAThinUltrawideStrip()
        {
            foreach (Size canvas in Canvases)
                Assert.IsTrue(OcrReadProbe.IsLegible(canvas), canvas.ToString());
            Assert.IsFalse(OcrReadProbe.IsLegible(new Size(3440, 80)));
        }

        [TestMethod]
        [TestCategory("DirectML")]
        public void DirectML_HealthyGpuReadsTheWordAtEverySize_SoItIsNeverMovedToTheProcessor()
        {
            if (!DirectMLTestSupport.TryEnable(out string why)) Assert.Inconclusive(why);
            OcrAdapterChoice choice = OcrAdapterSelector.ChooseForThisProcess();
            foreach (Size canvas in Canvases)
            {
                // One engine per size, as in production: warm-up claims DirectML's one fast shape per session.
                using PaddleOCREngine engine = NewEngine(OcrModelProfiles.RecommendedId, useGpu: true, gpuId: choice.Index);
                Assert.IsTrue(engine.IsUsingGpu, "DirectML did not start with the package DirectML.dll loaded.");
                engine.WarmUp(TimeSpan.FromSeconds(30), canvas);
                Assert.AreEqual(true, OcrReadProbe.TryRead(engine, canvas, TimeSpan.FromSeconds(30)), canvas.ToString());
            }
        }

        // ── LoadEngine, end to end on this machine's GPU ────────────────────

        [TestMethod]
        [TestCategory("DirectML")]
        public void DirectML_LoadEngine_LeavesTheGpuUnlessTheProcessorProvesItWrong()
        {
            if (!DirectMLTestSupport.TryEnable(out string why)) Assert.Inconclusive(why);

            // The real check on a healthy GPU.
            Scenario(null, engine =>
            {
                Assert.IsTrue(engine.IsUsingGpu, "healthy GPU");
                Assert.IsFalse(Quarantined(), OcrGpuQuarantine.Reason);
            });

            // The GPU reads nothing and the processor reads the word: a warm processor engine for this launch (a
            // strike), and the quarantine only when the next launch misses too.
            Func<PaddleOCREngine, Size, bool?> gpuMisses = (candidate, _) => !candidate.IsUsingGpu;
            Scenario(gpuMisses, engine =>
            {
                Assert.IsFalse(engine.IsUsingGpu, "unreadable GPU");
                Assert.AreEqual("CPUExecutionProvider", engine.ExecutionProvider);
                Assert.IsFalse(Quarantined(), "one miss is a strike, not a quarantine");
                Assert.IsTrue(OcrGpuQuarantine.StrikeThisSession);
                using Mat check = OcrReadProbe.Render(new Size(960, 224));
                Assert.IsTrue(OcrReadProbe.Reads(engine.DetectTextFromMat(check)), "the processor engine must be usable");

                using PaddleOCREngine second = SettingsWindow.LoadEngine("EN", null, null, gpuMisses);
                Assert.IsFalse(second.IsUsingGpu);
                Assert.IsTrue(Quarantined(), "the second miss in a row persists it");
                StringAssert.Contains(OcrGpuQuarantine.Reason, "did not read");
            });

            // Nobody reads it, the check cannot run, or it throws: the GPU stays and nothing is recorded.
            var keepTheGpu = new Func<PaddleOCREngine, Size, bool?>[]
            {
                (_, _) => false,
                (_, _) => null,
                (_, _) => throw new InvalidOperationException("check exploded"),
            };
            for (int i = 0; i < keepTheGpu.Length; i++)
            {
                int scenario = i;
                Scenario(keepTheGpu[i], engine =>
                {
                    Assert.IsTrue(engine.IsUsingGpu, $"scenario {scenario}");
                    Assert.IsFalse(Quarantined(), $"scenario {scenario}: {OcrGpuQuarantine.Reason}");
                });
            }
        }

        private static bool Quarantined() => OcrGpuQuarantine.IsActiveForCurrentVersion();

        private static void Scenario(Func<PaddleOCREngine, Size, bool?> check, Action<PaddleOCREngine> verify)
        {
            OcrGpuQuarantine.Clear("test");
            try
            {
                using PaddleOCREngine engine = SettingsWindow.LoadEngine("EN", null, null, check);
                verify(engine);
            }
            finally
            {
                OcrGpuQuarantine.Clear("test");
            }
        }

        // ── helpers ─────────────────────────────────────────────────────────

        private static OCRResult Result(string text) =>
            new() { TextBlocks = new List<TextBlock> { new() { Text = text, Score = 0.99f } } };

        private static PaddleOCREngine NewEngine(string profileId, bool useGpu, int gpuId)
        {
            string assemblyRoot = Path.GetDirectoryName(typeof(OCRModelConfig).Assembly.Location);
            OcrModelProfile profile = OcrModelProfiles.Resolve(profileId, "EN");
            return new PaddleOCREngine(profile.CreateModelConfig(Path.Combine(assemblyRoot, "inference")), new OCRParameter
            {
                use_gpu = useGpu,
                gpu_id = gpuId,
                cpu_math_library_num_threads = 3,
                max_side_len = 960,
                det_db_thresh = profile.DetectionThreshold,
                det_db_box_thresh = profile.DetectionBoxThreshold,
                det_db_unclip_ratio = profile.DetectionUnclipRatio,
                rec_img_h = profile.RecognitionImageHeight,
                rec_score_thresh = 0.5f,
            });
        }
    }
}
