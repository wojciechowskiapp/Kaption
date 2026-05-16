using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenCvSharp;
using PaddleOCRSharp;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Detection
{
    /// <summary>
    /// Full-screen OCR-based spatial clustering to detect where dialogue and
    /// answer-choice regions appear in a game frame. Works for any game at any
    /// resolution by analyzing the spatial distribution of detected text blocks.
    /// </summary>
    public static class OcrRegionDetector
    {
        /// <summary>
        /// Result of the region detection scan. Coordinates are relative to the
        /// input frame (game-window-relative), not screen-absolute.
        /// </summary>
        public struct DetectedRegions
        {
            /// <summary>Whether a dialogue region was identified.</summary>
            public bool DialogueFound;
            /// <summary>Dialogue region X offset within the frame.</summary>
            public int DialogueX;
            /// <summary>Dialogue region Y offset within the frame.</summary>
            public int DialogueY;
            /// <summary>Dialogue region width in pixels.</summary>
            public int DialogueW;
            /// <summary>Dialogue region height in pixels.</summary>
            public int DialogueH;

            /// <summary>Whether an answer-choice region was identified.</summary>
            public bool AnswerFound;
            /// <summary>Answer region X offset within the frame.</summary>
            public int AnswerX;
            /// <summary>Answer region Y offset within the frame.</summary>
            public int AnswerY;
            /// <summary>Answer region width in pixels.</summary>
            public int AnswerW;
            /// <summary>Answer region height in pixels.</summary>
            public int AnswerH;

            /// <summary>Total number of text blocks the OCR engine found.</summary>
            public int TextBlocksFound;
        }

        /// <summary>
        /// Internal representation of a single OCR text block with its axis-aligned
        /// bounding rectangle computed from the rotated BoxPoints.
        /// </summary>
        private struct BlockRect
        {
            public string Text;
            public Rectangle Bounds;

            public int CenterX => Bounds.X + Bounds.Width / 2;
            public int CenterY => Bounds.Y + Bounds.Height / 2;
            public int Top => Bounds.Y;
            public int Bottom => Bounds.Y + Bounds.Height;
            public int Left => Bounds.X;
            public int Right => Bounds.X + Bounds.Width;
        }

        /// <summary>
        /// Scan a full game frame with OCR and use spatial clustering to detect
        /// the dialogue region (bottom of screen) and answer-choice region
        /// (right side, above dialogue).
        ///
        /// This method is CPU-intensive and should be called from a background thread.
        /// </summary>
        /// <param name="engine">Initialized PaddleOCR engine.</param>
        /// <param name="frame">Full game window captured as an OpenCV Mat.</param>
        /// <param name="frameW">Frame width in pixels.</param>
        /// <param name="frameH">Frame height in pixels.</param>
        /// <returns>Detection result with regions in frame-relative coordinates.</returns>
        public static DetectedRegions DetectRegions(PaddleOCREngine engine, Mat frame, int frameW, int frameH)
        {
            if (engine == null)
                throw new ArgumentNullException(nameof(engine));
            if (frame == null || frame.IsDisposed || frame.Empty())
                throw new ArgumentException("Invalid frame Mat", nameof(frame));

            var result = new DetectedRegions();

            // ── Step 1: Run OCR on the full frame ──
            OCRResult ocrResult;
            try
            {
                ocrResult = engine.DetectTextFromMat(frame);
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"OCR failed during region detection: {ex.Message}");
                return result;
            }

            if (ocrResult?.TextBlocks == null || ocrResult.TextBlocks.Count == 0)
            {
                Logger.Log.Debug("Region detection: OCR found no text blocks");
                return result;
            }

            result.TextBlocksFound = ocrResult.TextBlocks.Count;
            Logger.Log.Debug($"Region detection: OCR found {result.TextBlocksFound} text blocks");

            // ── Step 2: Convert BoxPoints to axis-aligned bounding rects ──
            var allBlocks = new List<BlockRect>();
            foreach (var tb in ocrResult.TextBlocks)
            {
                if (tb.BoxPoints == null || tb.BoxPoints.Length < 4)
                    continue;

                var bounds = BoxPointsToBounds(tb.BoxPoints);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    continue;

                allBlocks.Add(new BlockRect { Text = tb.Text ?? "", Bounds = bounds });
            }

            if (allBlocks.Count == 0)
            {
                Logger.Log.Debug("Region detection: no valid bounding rects after conversion");
                return result;
            }

            // ── Step 3: Filter out likely game UI text ──
            var contentBlocks = allBlocks.Where(b => !IsLikelyGameUI(b.Text)).ToList();
            if (contentBlocks.Count == 0)
            {
                Logger.Log.Debug("Region detection: all blocks classified as game UI");
                return result;
            }

            Logger.Log.Debug($"Region detection: {contentBlocks.Count} content blocks after UI filter " +
                             $"(filtered {allBlocks.Count - contentBlocks.Count} UI blocks)");

            // ── Step 4: Split blocks by horizontal position ──
            // Answers are RIGHT-aligned (centerX > 55%), dialogue is CENTER-aligned.
            // Splitting BEFORE clustering prevents answer blocks from merging into the dialogue cluster.
            double xSplitThreshold = frameW * 0.55;
            var centerBlocks = contentBlocks.Where(b => b.CenterX <= xSplitThreshold).ToList();
            var rightBlocks = contentBlocks.Where(b => b.CenterX > xSplitThreshold).ToList();

            Logger.Log.Debug($"Region detection: {centerBlocks.Count} center blocks, {rightBlocks.Count} right blocks (split at x={xSplitThreshold:F0})");

            // ── Dialogue detection (from CENTER blocks only) ──
            var dialogueRegion = DetectDialogueRegion(centerBlocks, frameW, frameH);
            if (dialogueRegion != null)
            {
                var dr = dialogueRegion.Value;
                result.DialogueFound = true;
                result.DialogueX = dr.X;
                result.DialogueY = dr.Y;
                result.DialogueW = dr.Width;
                result.DialogueH = dr.Height;

                Logger.Log.Info($"Region detection: dialogue region at ({dr.X},{dr.Y}) {dr.Width}x{dr.Height}");

                // ── Answer detection (from RIGHT blocks, near dialogue) ──
                // Answer choices appear just above/beside the dialogue box,
                // typically in the bottom 50% of the frame
                double answerMinY = frameH * 0.35; // not too far above dialogue
                var answerCandidates = rightBlocks
                    .Where(b => b.CenterY < dr.Y + dr.Height)       // can overlap dialogue vertically
                    .Where(b => b.CenterY >= answerMinY)             // not in top 35% of screen
                    .Where(b => b.Text.Trim().Length > 3)            // meaningful text, not icons
                    .ToList();

                if (answerCandidates.Count >= 1)
                {
                    // Compute union bounding rect of all answer blocks
                    int aLeft = answerCandidates.Min(b => b.Left);
                    int aTop = answerCandidates.Min(b => b.Top);
                    int aRight = answerCandidates.Max(b => b.Right);
                    int aBottom = answerCandidates.Max(b => b.Bottom);

                    // Add generous padding (15%)
                    int padX = Math.Max((int)((aRight - aLeft) * 0.15), frameW / 50);
                    int padY = Math.Max((int)((aBottom - aTop) * 0.15), frameH / 50);

                    // Few answer options or narrow text: add extra padding so
                    // the region captures additional/wider options that may appear later
                    int answerClusterH = aBottom - aTop;
                    int answerClusterW = aRight - aLeft;
                    int extraTopPad = 0;
                    int extraBottomPad = 0;
                    int extraLeftPad = 0;
                    if (answerCandidates.Count <= 2 || answerClusterH < frameH * 0.08)
                    {
                        extraTopPad = (int)(frameH * 0.05);
                        extraBottomPad = (int)(frameH * 0.05);
                        Logger.Log.Debug($"Region detection: few answer options, adding extra vertical padding");
                    }
                    // Short answer text: extend left to catch longer options
                    if (answerClusterW < frameW * 0.20)
                    {
                        extraLeftPad = (int)(frameW * 0.08);
                        Logger.Log.Debug($"Region detection: narrow answers ({answerClusterW}px), adding {extraLeftPad}px left padding");
                    }

                    int ax = Math.Max(0, aLeft - padX - extraLeftPad);
                    int ay = Math.Max(0, aTop - padY - extraTopPad);
                    int aw = Math.Min(aRight - aLeft + 2 * padX + extraLeftPad, frameW - ax);
                    int ah = Math.Min(aBottom - aTop + padY + padY + extraTopPad + extraBottomPad, frameH - ay);

                    // Enforce minimum answer region size (room for up to 4 options)
                    int minAw = (int)(frameW * 0.25);
                    int minAh = (int)(frameH * 0.20);
                    if (aw < minAw) { int right = ax + aw; aw = minAw; ax = Math.Max(0, right - minAw); }
                    if (ah < minAh) { int bottom = ay + ah; ah = minAh; ay = Math.Max(0, bottom - minAh); }

                    // Allow answer region to overlap dialogue region slightly.
                    // Both regions are captured independently — overlap is fine and
                    // prevents answer options near the dialogue from being cut off.

                    if (ah > 0 && aw > 0)
                    {
                        result.AnswerFound = true;
                        result.AnswerX = ax;
                        result.AnswerY = ay;
                        result.AnswerW = aw;
                        result.AnswerH = ah;

                        Logger.Log.Info($"Region detection: answer region at ({ax},{ay}) {aw}x{ah} from {answerCandidates.Count} blocks");
                    }
                    else
                    {
                        Logger.Log.Debug("Region detection: answer region fully overlapped by dialogue, discarded");
                    }
                }
                else
                {
                    Logger.Log.Debug($"Region detection: no answer blocks found ({rightBlocks.Count} right blocks, {answerCandidates.Count} passed filters)");
                }
            }
            else
            {
                Logger.Log.Debug("Region detection: no dialogue region found in bottom area");
            }

            return result;
        }

        // ══════════════════════════════════════════════════════════════
        //  DIALOGUE DETECTION
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Detect the dialogue region by finding the largest vertical cluster of text
        /// blocks in the bottom portion of the frame.
        /// </summary>
        private static Rectangle? DetectDialogueRegion(List<BlockRect> blocks, int frameW, int frameH)
        {
            // Step 4: Filter to blocks in the bottom 35% of the frame
            double bottomThreshold = frameH * 0.65;
            var bottomBlocks = blocks.Where(b => b.Top >= bottomThreshold).ToList();

            // If nothing in bottom 35%, try bottom 45%
            if (bottomBlocks.Count == 0)
            {
                bottomThreshold = frameH * 0.55;
                bottomBlocks = blocks.Where(b => b.Top >= bottomThreshold).ToList();
                if (bottomBlocks.Count == 0)
                    return null;

                Logger.Log.Debug("Region detection: expanded dialogue search to bottom 50%");
            }

            // Step 5: Sort by Y coordinate (top of bounding rect)
            bottomBlocks.Sort((a, b) => a.Top.CompareTo(b.Top));

            // Step 6: Find the largest vertical cluster
            var bestCluster = FindLargestVerticalCluster(bottomBlocks);
            if (bestCluster == null || bestCluster.Count == 0)
                return null;

            // Step 7: Compute the union bounding rect of the cluster
            int unionLeft = bestCluster.Min(b => b.Left);
            int unionTop = bestCluster.Min(b => b.Top);
            int unionRight = bestCluster.Max(b => b.Right);
            int unionBottom = bestCluster.Max(b => b.Bottom);

            // Step 8: Expand with padding on all sides
            int padX = (int)((unionRight - unionLeft) * 0.08);
            int padY = (int)((unionBottom - unionTop) * 0.08);
            // Use a minimum padding so single-line dialogue gets a reasonable region
            padX = Math.Max(padX, frameW / 40);  // at least 2.5% of frame width
            padY = Math.Max(padY, frameH / 40);  // at least 2.5% of frame height

            // Single-line dialogue: add extra bottom padding for the subtitle overlay.
            // The translated text renders below the original, so the region must extend
            // far enough down to capture future multi-line dialogue at the same position.
            int clusterHeight = unionBottom - unionTop;
            int clusterWidth = unionRight - unionLeft;
            int extraBottomPad = 0;
            int extraHorizPad = 0;
            if (bestCluster.Count <= 2 || clusterHeight < frameH * 0.06)
            {
                extraBottomPad = (int)(frameH * 0.06);
                Logger.Log.Debug($"Region detection: single-line dialogue, adding {extraBottomPad}px bottom padding");
            }
            // Short detected text: extend horizontally to catch longer dialogue lines
            if (clusterWidth < frameW * 0.35)
            {
                extraHorizPad = (int)(frameW * 0.10);
                Logger.Log.Debug($"Region detection: narrow dialogue ({clusterWidth}px), adding {extraHorizPad}px horizontal padding");
            }

            int finalX = unionLeft - padX - extraHorizPad;
            int finalY = unionTop - padY;
            int finalW = (unionRight - unionLeft) + 2 * (padX + extraHorizPad);
            int finalH = (unionBottom - unionTop) + padY + padY + extraBottomPad;

            // Step 9: Enforce minimum dimensions for worst-case dialogue
            // (3 lines of text + NPC name + role text). OCR may detect a small
            // 1-line sample, but the region must fit the largest possible dialogue.
            // Dialogue in Genshin/Star Rail spans ~80% of screen width.
            int minW = (int)(frameW * 0.70);
            int minH = (int)(frameH * 0.14);

            if (finalW < minW)
            {
                int centerX = finalX + finalW / 2;
                finalX = centerX - minW / 2;
                finalW = minW;
            }
            if (finalH < minH)
            {
                // Expand upward — dialogue grows upward with more lines
                int bottom = finalY + finalH;
                finalH = minH;
                finalY = bottom - minH;
            }

            // Step 10: Clamp to frame bounds
            finalX = Math.Max(0, finalX);
            finalY = Math.Max(0, finalY);
            finalW = Math.Min(finalW, frameW - finalX);
            finalH = Math.Min(finalH, frameH - finalY);

            if (finalW <= 0 || finalH <= 0)
                return null;

            return new Rectangle(finalX, finalY, finalW, finalH);
        }

        /// <summary>
        /// Group blocks into vertical clusters by proximity, then return the group
        /// with the most members. Blocks are already sorted by Y.
        ///
        /// Two adjacent blocks belong to the same cluster if the vertical gap between
        /// them (current top minus previous bottom) is less than 3x the median block height.
        /// This tolerates decorative spacing between dialogue lines while splitting off
        /// distant HUD text.
        /// </summary>
        private static List<BlockRect> FindLargestVerticalCluster(List<BlockRect> sortedBlocks)
        {
            if (sortedBlocks.Count == 0)
                return null;
            if (sortedBlocks.Count == 1)
                return new List<BlockRect>(sortedBlocks);

            // Calculate median block height for the gap threshold
            var heights = sortedBlocks.Select(b => b.Bounds.Height).OrderBy(h => h).ToList();
            int medianHeight = heights[heights.Count / 2];
            if (medianHeight <= 0) medianHeight = 1;
            int maxGap = medianHeight * 3;

            // Group into clusters based on vertical proximity
            var clusters = new List<List<BlockRect>>();
            var currentCluster = new List<BlockRect> { sortedBlocks[0] };

            for (int i = 1; i < sortedBlocks.Count; i++)
            {
                int gap = sortedBlocks[i].Top - sortedBlocks[i - 1].Bottom;
                if (gap < maxGap)
                {
                    currentCluster.Add(sortedBlocks[i]);
                }
                else
                {
                    clusters.Add(currentCluster);
                    currentCluster = new List<BlockRect> { sortedBlocks[i] };
                }
            }
            clusters.Add(currentCluster);

            // Return the cluster with the most blocks
            // Tie-break: prefer the cluster that is lower on screen (more likely dialogue)
            List<BlockRect> best = null;
            int bestCount = 0;
            int bestMaxY = int.MinValue;

            foreach (var cluster in clusters)
            {
                int clusterMaxY = cluster.Max(b => b.Bottom);
                if (cluster.Count > bestCount ||
                    (cluster.Count == bestCount && clusterMaxY > bestMaxY))
                {
                    best = cluster;
                    bestCount = cluster.Count;
                    bestMaxY = clusterMaxY;
                }
            }

            Logger.Log.Debug($"Region detection: {clusters.Count} vertical clusters, " +
                             $"largest has {bestCount} blocks (median height={medianHeight}, maxGap={maxGap})");

            return best;
        }

        // ══════════════════════════════════════════════════════════════
        //  ANSWER DETECTION
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Detect the answer-choice region by finding vertically stacked text blocks
        /// on the right side of the frame, above the dialogue region.
        /// </summary>
        private static Rectangle? DetectAnswerRegion(
            List<BlockRect> allBlocks, Rectangle dialogueRegion, int frameW, int frameH)
        {
            // Step 10: Exclude blocks that overlap the dialogue region
            var candidates = new List<BlockRect>();
            foreach (var block in allBlocks)
            {
                if (block.Bounds.IntersectsWith(dialogueRegion))
                    continue;

                // Step 11: Must be in the right 55% of the frame
                if (block.CenterX <= frameW * 0.45)
                    continue;

                // Step 12: Must be above the dialogue region
                if (block.Top >= dialogueRegion.Y)
                    continue;

                // Step 13: Filter game UI
                if (IsLikelyGameUI(block.Text))
                    continue;

                candidates.Add(block);
            }

            if (candidates.Count < 2)
                return null;

            Logger.Log.Debug($"Region detection: {candidates.Count} answer candidates " +
                             $"(right side, above dialogue)");

            // Step 14: Find vertically stacked blocks with similar X centers.
            // Answer choices in most games are vertically aligned with consistent X
            // positions. We check if candidates cluster within 25% of frame width.
            var xCenterTolerance = frameW * 0.25;

            // Sort by Y for vertical stacking analysis
            candidates.Sort((a, b) => a.Top.CompareTo(b.Top));

            // Try to find the largest subset that is vertically stacked
            var bestStack = FindVerticallyStackedBlocks(candidates, xCenterTolerance);

            if (bestStack == null || bestStack.Count < 2)
                return null;

            // Compute union bounding rect of the stacked answer blocks
            int unionLeft = bestStack.Min(b => b.Left);
            int unionTop = bestStack.Min(b => b.Top);
            int unionRight = bestStack.Max(b => b.Right);
            int unionBottom = bestStack.Max(b => b.Bottom);

            // Step 15: Expand with 15% padding
            int padX = (int)((unionRight - unionLeft) * 0.15);
            int padY = (int)((unionBottom - unionTop) * 0.15);
            padX = Math.Max(padX, frameW / 50);  // at least 2% of frame width
            padY = Math.Max(padY, frameH / 50);

            int finalX = unionLeft - padX;
            int finalY = unionTop - padY;
            int finalW = (unionRight - unionLeft) + 2 * padX;
            int finalH = (unionBottom - unionTop) + 2 * padY;

            // Enforce minimum dimensions for worst-case (4 stacked answer choices).
            // OCR may detect only 1-2 options, but the region must fit all possible answers.
            int minW = (int)(frameW * 0.30);
            int minH = (int)(frameH * 0.25);

            if (finalW < minW)
            {
                // Expand leftward — answers are right-aligned
                int right = finalX + finalW;
                finalW = minW;
                finalX = right - minW;
            }
            if (finalH < minH)
            {
                // Expand upward — more options stack above existing ones
                int bottom = finalY + finalH;
                finalH = minH;
                finalY = bottom - minH;
            }

            // Clamp to frame bounds
            finalX = Math.Max(0, finalX);
            finalY = Math.Max(0, finalY);
            finalW = Math.Min(finalW, frameW - finalX);
            finalH = Math.Min(finalH, frameH - finalY);

            if (finalW <= 0 || finalH <= 0)
                return null;

            Logger.Log.Info($"Region detection: answer stack with {bestStack.Count} choices");
            return new Rectangle(finalX, finalY, finalW, finalH);
        }

        /// <summary>
        /// From a set of candidates sorted by Y, find the largest subset where all blocks
        /// have X centers within the given tolerance of each other. This identifies
        /// vertically stacked answer choices which share a horizontal alignment.
        /// </summary>
        private static List<BlockRect> FindVerticallyStackedBlocks(
            List<BlockRect> sortedCandidates, double xCenterTolerance)
        {
            if (sortedCandidates.Count < 2)
                return null;

            // For each block, try to build a stack of blocks aligned with it
            List<BlockRect> bestStack = null;
            int bestCount = 0;

            for (int anchor = 0; anchor < sortedCandidates.Count; anchor++)
            {
                int anchorCx = sortedCandidates[anchor].CenterX;
                var stack = new List<BlockRect> { sortedCandidates[anchor] };

                for (int j = anchor + 1; j < sortedCandidates.Count; j++)
                {
                    if (Math.Abs(sortedCandidates[j].CenterX - anchorCx) <= xCenterTolerance)
                    {
                        stack.Add(sortedCandidates[j]);
                    }
                }

                if (stack.Count > bestCount)
                {
                    bestStack = stack;
                    bestCount = stack.Count;
                }
            }

            return bestCount >= 2 ? bestStack : null;
        }

        // ══════════════════════════════════════════════════════════════
        //  UTILITY
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Convert PaddleOCR's 4-corner BoxPoints (which may be rotated) to an
        /// axis-aligned bounding rectangle.
        /// </summary>
        private static Rectangle BoxPointsToBounds(PointF[] points)
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            for (int i = 0; i < points.Length; i++)
            {
                if (points[i].X < minX) minX = points[i].X;
                if (points[i].Y < minY) minY = points[i].Y;
                if (points[i].X > maxX) maxX = points[i].X;
                if (points[i].Y > maxY) maxY = points[i].Y;
            }

            return new Rectangle(
                (int)minX,
                (int)minY,
                (int)(maxX - minX),
                (int)(maxY - minY));
        }

        /// <summary>
        /// Heuristic filter to reject text that is likely game HUD/UI rather than
        /// dialogue or answer text. Rejects:
        /// - Very short strings (less than 2 non-whitespace chars)
        /// - Strings where digits outnumber or equal letters (HP bars, levels, timers)
        /// - Short strings (&lt;4 chars) containing any digit
        /// </summary>
        private static bool IsLikelyGameUI(string text)
        {
            if (string.IsNullOrEmpty(text))
                return true;

            string t = text.Trim();
            if (t.Length < 2)
                return true;

            int digits = 0, letters = 0;
            foreach (char c in t)
            {
                if (char.IsDigit(c)) digits++;
                else if (char.IsLetter(c)) letters++;
            }

            // More digits than letters -> probably HP/MP/timer/coordinates
            if (digits > 0 && digits >= letters)
                return true;

            // Short text with any digit -> probably "Lv.5", "x3", "HP"
            if (t.Length < 4 && digits > 0)
                return true;

            return false;
        }
    }
}
