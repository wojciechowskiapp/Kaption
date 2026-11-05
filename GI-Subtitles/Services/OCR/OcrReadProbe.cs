using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Threading;
using GI_Subtitles.Common;
using OpenCvSharp.Extensions;
using PaddleOCRSharp;
using CvSize = OpenCvSharp.Size;
using Logger = GI_Subtitles.Common.Logger;
using Mat = OpenCvSharp.Mat;

namespace GI_Subtitles.Services.OCR
{
    internal enum OcrReadProbeVerdict
    {
        GpuReads,
        GpuUnreadableCpuReads,
        Inconclusive,
    }

    /// <summary>
    /// Checks that a GPU engine reads text, not only that it runs fast. The warm-up gates time DirectML and nothing else,
    /// so a driver that returns wrong recognition output passed them (KAP-EF8C-C5CF: on an Intel UHD the detector found
    /// every dialogue box and the recognizer accepted none). One known word goes on a canvas of the warmed detector size,
    /// so the check reuses compiled shapes; a word this short crops into the 320 recognition bucket warm-up compiled.
    /// </summary>
    internal static class OcrReadProbe
    {
        internal const string Word = "Traveler";

        // Font size in detector pixels, i.e. after the long edge is scaled down to 960.
        private const double DetectorFontPx = 36;
        // Below this the word is too small to tell a bad GPU from a borderline read (a 3440x80 strip gives ~13 px).
        private const double MinLegibleDetectorFontPx = 20;
        private const int DetectorLongEdge = 960;
        private const double MinSimilarity = 0.75;

        internal static CvSize CanvasFor(CvSize? captureRegion) => PaddleOCREngine.WarmUpDetectorSize(captureRegion);

        /// <summary>False for canvases too thin to draw the word at a safely readable size; the check is skipped there.</summary>
        internal static bool IsLegible(CvSize canvas)
        {
            (float fontPx, double detectorScale) = FontFor(canvas);
            return fontPx * detectorScale >= MinLegibleDetectorFontPx;
        }

        private static (float FontPx, double DetectorScale) FontFor(CvSize canvas)
        {
            int width = Math.Max(32, canvas.Width);
            int height = Math.Max(32, canvas.Height);
            double detectorScale = Math.Min(1.0, DetectorLongEdge / (double)Math.Max(width, height));
            return ((float)Math.Clamp(DetectorFontPx / detectorScale, 16, height * 0.6), detectorScale);
        }

        /// <summary>White bold text on near-black, centred, sized to read the same at any canvas size.</summary>
        internal static Mat Render(CvSize canvas)
        {
            int width = Math.Max(32, canvas.Width);
            int height = Math.Max(32, canvas.Height);
            float fontPx = FontFor(canvas).FontPx;

            using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(24, 24, 28));
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                Font font = new("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                try
                {
                    SizeF size = graphics.MeasureString(Word, font);
                    if (size.Width > width * 0.9f)
                    {
                        float fitted = Math.Max(8f, fontPx * width * 0.9f / size.Width);
                        font.Dispose();
                        font = new Font("Segoe UI", fitted, FontStyle.Bold, GraphicsUnit.Pixel);
                        size = graphics.MeasureString(Word, font);
                    }
                    graphics.DrawString(Word, font, Brushes.White, (width - size.Width) / 2f, (height - size.Height) / 2f);
                }
                finally { font.Dispose(); }
            }
            return bitmap.ToMat();
        }

        /// <summary>True when one block reads as the word, allowing about one misread letter.</summary>
        internal static bool Reads(OCRResult result)
        {
            if (result?.TextBlocks == null) return false;
            string expected = Letters(Word);
            return result.TextBlocks.Any(block => Similarity(Letters(block?.Text), expected) >= MinSimilarity);
        }

        /// <summary>Null when the check could not run (threw, timed out): that must never count against the GPU.</summary>
        internal static bool? TryRead(PaddleOCREngine engine, CvSize canvas, TimeSpan timeout)
        {
            if (engine == null) return null;
            if (!IsLegible(canvas))
            {
                Logger.Log.Info($"OCR read check skipped: a {canvas.Width}x{canvas.Height} region is too thin for the test word.");
                return null;
            }
            try
            {
                using Mat image = Render(canvas);
                using var cts = new CancellationTokenSource(timeout);
                OCRResult result = engine.DetectTextFromMat(image, cts.Token);
                bool reads = Reads(result);
                Logger.Log.Info(
                    $"OCR read check on {engine.ExecutionProvider}: {(reads ? "read" : "did not read")} the test word " +
                    $"({canvas.Width}x{canvas.Height}, saw \"{Summarize(result)}\").");
                return reads;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"OCR read check could not run on {engine.ExecutionProvider}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Only a GPU miss that the processor reads moves OCR to the processor. Same model, same input: a healthy GPU
        /// cannot fail where the processor passes, and a check nobody passes says nothing about the GPU.
        /// </summary>
        internal static OcrReadProbeVerdict Decide(bool? gpuReads, bool? cpuReads) => gpuReads switch
        {
            true => OcrReadProbeVerdict.GpuReads,
            false when cpuReads == true => OcrReadProbeVerdict.GpuUnreadableCpuReads,
            _ => OcrReadProbeVerdict.Inconclusive,
        };

        private static string Letters(string text) =>
            new((text ?? string.Empty).Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());

        private static double Similarity(string actual, string expected)
        {
            if (actual.Length == 0 || expected.Length == 0) return 0;
            return 1.0 - Levenshtein(actual, expected) / (double)Math.Max(actual.Length, expected.Length);
        }

        private static int Levenshtein(string a, string b)
        {
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) previous[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }
                (previous, current) = (current, previous);
            }
            return previous[b.Length];
        }

        private static string Summarize(OCRResult result)
        {
            string text = string.Join(" | ", result?.TextBlocks?.Select(b => b?.Text?.Trim()) ?? Enumerable.Empty<string>());
            return text.Length <= 60 ? text : text.Substring(0, 60) + "…";
        }
    }
}
