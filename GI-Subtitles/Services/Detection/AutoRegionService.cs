using System;
using System.Drawing;
using GI_Subtitles.Services.Capture;
using OpenCvSharp.Extensions;
using PaddleOCRSharp;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Detection
{
    /// <summary>
    /// High-level orchestrator for automatic capture-region detection.
    /// Combines game window detection, full-screen OCR scan, and ratio-based
    /// fallback into a single <see cref="Detect"/> call.
    ///
    /// Fallback chain: OCR AI detection -> ratio-based -> manual selection.
    /// Must be called from a background thread (OCR is CPU-intensive).
    /// </summary>
    public static class AutoRegionService
    {
        /// <summary>
        /// Result of an auto-detection attempt. Contains the detected regions as
        /// comma-separated "x,y,w,h" strings ready to be saved to Config, plus
        /// metadata about how the regions were determined.
        /// </summary>
        public class Result
        {
            /// <summary>Whether detection succeeded and regions are available.</summary>
            public bool Success { get; set; }

            /// <summary>
            /// Dialogue region in screen-absolute "x,y,w,h" format, or null on failure.
            /// </summary>
            public string DialogueRegion { get; set; }

            /// <summary>
            /// Answer region in screen-absolute "x,y,w,h" format, or null if
            /// answers were not detected or detection failed.
            /// </summary>
            public string AnswerRegion { get; set; }

            /// <summary>Detected game resolution, e.g. "2560x1440".</summary>
            public string Resolution { get; set; }

            /// <summary>
            /// Human-readable description of which detection method produced the result
            /// (e.g. "OCR scan (12 text blocks)" or "Ratio-based (no dialogue visible)").
            /// </summary>
            public string Method { get; set; }

            /// <summary>Error message when Success is false.</summary>
            public string Error { get; set; }

            /// <summary>Create a failed result with an error message.</summary>
            public static Result Failed(string error)
            {
                return new Result { Success = false, Error = error };
            }

            /// <summary>Create a successful result with region data.</summary>
            public static Result Succeeded(string dialogue, string answer, string resolution, string method)
            {
                return new Result
                {
                    Success = true,
                    DialogueRegion = dialogue,
                    AnswerRegion = answer,
                    Resolution = resolution,
                    Method = method,
                };
            }
        }

        /// <summary>
        /// Auto-detect dialogue and answer regions by scanning the game screen.
        /// Uses OCR-based AI detection with ratio-based fallback.
        ///
        /// <para><b>Thread safety:</b> Must be called from a background thread.
        /// The OCR scan is CPU/GPU-intensive and will block for several hundred
        /// milliseconds on a full-resolution frame.</para>
        /// </summary>
        /// <param name="gameId">
        /// Game identifier (e.g. "Genshin", "StarRail") used to look up the
        /// <see cref="GameRegionProfile"/> for window detection and fallback ratios.
        /// </param>
        /// <param name="engine">
        /// Initialized PaddleOCR engine, or null to skip OCR and use ratio fallback.
        /// </param>
        /// <returns>
        /// A <see cref="Result"/> with dialogue/answer regions in screen-absolute
        /// coordinates, ready to be saved to Config.
        /// </returns>
        public static Result Detect(string gameId, PaddleOCREngine engine)
        {
            // 1. Get game profile
            var profile = GameRegionProfile.Get(gameId);
            Logger.Log.Info($"Auto-detect started for game \"{profile.GameId}\"");

            // 2. Find game window
            GameWindowDetector.WindowInfo? windowInfo;
            try
            {
                windowInfo = GameWindowDetector.FindGameWindow(profile);
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Window detection failed: {ex.Message}");
                return Result.Failed($"Window detection error: {ex.Message}");
            }

            if (windowInfo == null)
                return Result.Failed("Game window not found. Make sure the game is running.");

            var wi = windowInfo.Value;
            string resolution = $"{wi.GameW}x{wi.GameH}";

            // 3. Capture full game window using GDI (always available, one-shot)
            // GDI is used for calibration because DXGI may not be initialized yet and
            // requires a persistent duplication session. For a single calibration frame
            // GDI is simpler and equally reliable. One-shot: keep a plain allocation so
            // full-screen buckets don't pollute the BitmapPool for the OCR hot path.
            Bitmap bitmap;
            try
            {
                bitmap = new Bitmap(wi.GameW, wi.GameH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var gdi = new GdiScreenCapture())
                {
                    gdi.CaptureRegionInto(wi.GameX, wi.GameY, wi.GameW, wi.GameH, bitmap);
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Screen capture failed during auto-detect: {ex.Message}");
                return Result.Failed($"Failed to capture game screen: {ex.Message}");
            }

            if (bitmap == null)
                return Result.Failed("Failed to capture game screen (bitmap was null).");

            // 4. Try OCR-based detection
            using (bitmap)
            {
                if (engine == null)
                {
                    Logger.Log.Info("OCR engine not ready, using ratio-based fallback");
                    var (d, a) = GameWindowDetector.CalculateFromRatios(
                        profile, wi.GameX, wi.GameY, wi.GameW, wi.GameH);
                    return Result.Succeeded(d, a, resolution, "Ratio-based (OCR engine not ready)");
                }

                OpenCvSharp.Mat frameMat = null;
                try
                {
                    frameMat = BitmapConverter.ToMat(bitmap);

                    var detected = OcrRegionDetector.DetectRegions(engine, frameMat, wi.GameW, wi.GameH);

                    if (detected.DialogueFound)
                    {
                        // Convert from game-window-relative to screen-absolute coordinates
                        string dialogueRegion = $"{wi.GameX + detected.DialogueX}," +
                                                $"{wi.GameY + detected.DialogueY}," +
                                                $"{detected.DialogueW},{detected.DialogueH}";

                        string answerRegion = detected.AnswerFound
                            ? $"{wi.GameX + detected.AnswerX}," +
                              $"{wi.GameY + detected.AnswerY}," +
                              $"{detected.AnswerW},{detected.AnswerH}"
                            : null;

                        Logger.Log.Info($"Auto-detect succeeded via OCR: dialogue={dialogueRegion}, " +
                                        $"answer={answerRegion ?? "none"}, blocks={detected.TextBlocksFound}");

                        return Result.Succeeded(dialogueRegion, answerRegion, resolution,
                            $"OCR scan ({detected.TextBlocksFound} text blocks)");
                    }

                    // 5. OCR found no dialogue — fall back to ratios
                    Logger.Log.Info("OCR scan found no dialogue region, using ratio-based fallback");
                    var (dr, ar) = GameWindowDetector.CalculateFromRatios(
                        profile, wi.GameX, wi.GameY, wi.GameW, wi.GameH);
                    return Result.Succeeded(dr, ar, resolution,
                        "Ratio-based (no dialogue visible on screen)");
                }
                catch (Exception ex)
                {
                    Logger.Log.Error($"OCR detection failed, using ratio fallback: {ex.Message}");

                    // OCR crashed — still provide ratio-based regions rather than failing entirely
                    var (dr, ar) = GameWindowDetector.CalculateFromRatios(
                        profile, wi.GameX, wi.GameY, wi.GameW, wi.GameH);
                    return Result.Succeeded(dr, ar, resolution,
                        $"Ratio-based (OCR error: {ex.Message})");
                }
                finally
                {
                    frameMat?.Dispose();
                }
            }
        }
    }
}
