using System;
using System.Diagnostics;
using System.IO;
using GI_Subtitles.Services.OCR;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using PaddleOCRSharp;

namespace GI_Test
{
    /// <summary>
    /// DirectML compiles a fast path for the first detector input shape a session runs. A dialogue area picked after
    /// the engine loaded kept detection on the slow path until a restart (audit B1, 2026-11-18); the engine now
    /// rebuilds its detector for the area's shape.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class OcrDetectorShapeTests
    {
        // Star Rail's area at 1080p becomes 960x160 at the detector; an engine loaded with no area warms 960x224.
        private static readonly Size StarRail1080 = new(1344, 216);

        [TestMethod]
        public void DetectorInputShape_IsWhatTheDetectorsResizeAndPadProduce()
        {
            // The rebuild decision rests on this prediction: a wrong one rebuilds on every area or never.
            Size[] frames =
            {
                StarRail1080, new(1536, 312), new(1420, 290), new(1777, 332), new(2005, 332), new(2019, 379),
                new(1582, 315), new(3840, 850), new(1920, 1080), new(400, 60), new(960, 224), new(300, 900),
                new(1001, 101), new(2560, 405), new(1280, 47),
            };
            foreach (Size frame in frames)
            {
                using var image = new Mat(frame.Height, frame.Width, MatType.CV_8UC3, Scalar.All(0));
                using Mat resized = PaddleOCREngine.ResizeImage(image, 960);
                using Mat padded = PaddleOCREngine.PadTo32(resized);
                Assert.AreEqual(padded.Size(), PaddleOCREngine.DetectorInputShape(frame, 960), frame.ToString());
            }
        }

        [TestMethod]
        public void RebuildDetector_FollowsANewAreaShape_AndTheEngineStillReads()
        {
            using PaddleOCREngine engine = NewEngine(useGpu: false, gpuId: 0);
            engine.WarmUp(TimeSpan.FromSeconds(45), null);
            Assert.AreEqual(new Size(960, 224), engine.DetectorShape);
            Assert.IsFalse(engine.NeedsDetectorFor(StarRail1080), "the processor has no per-shape slow path");

            Assert.IsTrue(engine.RebuildDetector(StarRail1080, TimeSpan.FromSeconds(30)));
            Assert.AreEqual(new Size(960, 160), engine.DetectorShape);
            Assert.AreEqual(true, OcrReadProbe.TryRead(engine, StarRail1080, TimeSpan.FromSeconds(30)));
        }

        /// <summary>The bug and the fix on a real GPU: run alone with KAPTION_TEST_DIRECTML=1.</summary>
        [TestMethod]
        [TestCategory("DirectML")]
        public void DirectML_AnAreaOfAnotherShape_ReadsAsFastAsTheWarmedOneOnceTheDetectorIsRebuilt()
        {
            if (!DirectMLTestSupport.TryEnable(out string why)) Assert.Inconclusive(why);
            OcrAdapterChoice choice = OcrAdapterSelector.ChooseForThisProcess();

            using PaddleOCREngine engine = NewEngine(useGpu: true, choice.Index);
            Assert.IsTrue(engine.IsUsingGpu);
            engine.WarmUp(TimeSpan.FromSeconds(30), null);
            Assert.IsTrue(engine.NeedsDetectorFor(StarRail1080));
            Assert.IsFalse(engine.NeedsDetectorFor(new Size(1536, 312)), "Genshin's area is the warmed shape");
            double before = MedianReadMs(engine, StarRail1080);

            var rebuild = Stopwatch.StartNew();
            Assert.IsTrue(engine.PrepareDetectorFor(StarRail1080, TimeSpan.FromSeconds(10)));
            Console.WriteLine($"rebuild took {rebuild.ElapsedMilliseconds} ms (reads wait for it)");
            Assert.IsFalse(engine.NeedsDetectorFor(StarRail1080));
            double after = MedianReadMs(engine, StarRail1080);

            using PaddleOCREngine warmedOnArea = NewEngine(useGpu: true, choice.Index);
            warmedOnArea.WarmUp(TimeSpan.FromSeconds(30), StarRail1080);
            double reference = MedianReadMs(warmedOnArea, StarRail1080);

            Console.WriteLine($"median read of {StarRail1080.Width}x{StarRail1080.Height}: before the rebuild " +
                              $"{before:F1} ms, after {after:F1} ms, an engine warmed on it {reference:F1} ms");
            Assert.IsTrue(after < before / 2, $"after {after:F1} ms, before {before:F1} ms");
        }

        private static double MedianReadMs(PaddleOCREngine engine, Size frame, int reads = 30)
        {
            using var image = new Mat(frame.Height, frame.Width, MatType.CV_8UC3, Scalar.All(0));
            engine.DetectTextFromMat(image);
            var times = new double[reads];
            for (int i = 0; i < reads; i++)
            {
                var timer = Stopwatch.StartNew();
                engine.DetectTextFromMat(image);
                times[i] = timer.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times);
            return times[reads / 2];
        }

        private static PaddleOCREngine NewEngine(bool useGpu, int gpuId)
        {
            string assemblyRoot = Path.GetDirectoryName(typeof(OCRModelConfig).Assembly.Location);
            OcrModelProfile profile = OcrModelProfiles.Resolve(OcrModelProfiles.RecommendedId, "EN");
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
