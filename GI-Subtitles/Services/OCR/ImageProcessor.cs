using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using OpenCvSharp;
using PaddleOCRSharp;
using GI_Subtitles.Core.Cache;
using GI_Subtitles.Models;

namespace GI_Subtitles.Services.OCR
{
    /// <summary>
    /// Image processing utilities for OCR
    /// </summary>
    public class ImageProcessor
    {
        public static string ComputeRobustHash(OpenCvSharp.Mat srcMat)
        {
            if (srcMat == null) return string.Empty;

            // 1. Convert to grayscale
            using var gray = new OpenCvSharp.Mat();
            if (srcMat.Channels() == 3 || srcMat.Channels() == 4)
                Cv2.CvtColor(srcMat, gray, ColorConversionCodes.BGR2GRAY);
            else
                srcMat.CopyTo(gray);

            // 2. Key step: binarization (thresholding)
            using var bin = new OpenCvSharp.Mat();
            Cv2.Threshold(gray, bin, 220, 255, ThresholdTypes.Binary);

            using var points = new OpenCvSharp.Mat();
            Cv2.FindNonZero(bin, points);

            Rect roi;
            if (points.Total() > 0)
            {
                roi = Cv2.BoundingRect(points);

                int padding = 2;
                roi.X = Math.Max(0, roi.X - padding);
                roi.Y = Math.Max(0, roi.Y - padding);
                roi.Width = Math.Min(bin.Width - roi.X, roi.Width + padding * 2);
                roi.Height = Math.Min(bin.Height - roi.Y, roi.Height + padding * 2);
            }
            else
            {
                // All-black image: directly return an all-zero hash, or treat as empty
                return new string('0', 64);
            }

            // Crop out the region that only contains text
            using var cropped = new OpenCvSharp.Mat(bin, roi);
            using var resized = new OpenCvSharp.Mat();
            Cv2.Resize(cropped, resized, new OpenCvSharp.Size(9, 8), 0, 0, InterpolationFlags.Area);

            // 4. Compute hash (resized is derived from a binary image but becomes grayscale due to Area interpolation)
            var hash = new StringBuilder(64);

            unsafe
            {
                byte* ptr = (byte*)resized.DataPointer;
                int step = (int)resized.Step();

                for (int y = 0; y < 8; y++)
                {
                    byte* row = ptr + (y * step);
                    for (int x = 0; x < 8; x++)
                    {
                        // Compare "text density" of adjacent blocks
                        hash.Append(row[x] > row[x + 1] ? '1' : '0');
                    }
                }
            }

            return hash.ToString();
        }

        // CalculateHammingDistance + FindSimilarImageHash removed 2026-04-18
        // (see MainWindow.xaml.cs:3339). The fuzzy Hamming-distance cache path
        // caused cross-dialog ghost subtitles (LRU-touch feedback loop —
        // project_ocr_cache_ghosts.md). Only the exact-hash lookup path
        // survives. Methods deleted in net8 migration Phase 5 since every
        // remaining reference was just a comment.

        /// <summary>
        /// Classify OCR text blocks into NPC name (colored/golden) vs dialogue (white) using HSV saturation.
        /// Genshin NPC names are rendered in warm gold (#FFD893-ish), while dialogue text is white.
        /// In HSV space, white text has very low saturation (~0-15), while golden text has high saturation (>45).
        /// </summary>
        /// <param name="colorFrame">BGR color Mat of the captured region</param>
        /// <param name="textBlocks">Text blocks from PaddleOCR with BoxPoints in image coordinates</param>
        /// <param name="npcName">Output: detected NPC name/title text, or empty if none</param>
        /// <param name="dialogueText">Output: dialogue text lines joined by newline</param>
        public static void ClassifyTextBlocks(OpenCvSharp.Mat colorFrame, List<TextBlock> textBlocks,
            out string npcName, out string dialogueText)
        {
            npcName = "";
            dialogueText = "";

            if (colorFrame == null || colorFrame.Empty() || textBlocks == null || textBlocks.Count == 0)
            {
                // Fallback: join all text
                if (textBlocks != null && textBlocks.Count > 0)
                    dialogueText = string.Join("\n", textBlocks.Select(b => b.Text));
                return;
            }

            using var hsvFrame = new OpenCvSharp.Mat();
            Cv2.CvtColor(colorFrame, hsvFrame, ColorConversionCodes.BGR2HSV);

            var npcParts = new List<string>();
            var dialogueParts = new List<string>();

            foreach (var block in textBlocks)
            {
                if (block.BoxPoints == null || block.BoxPoints.Length < 4 || string.IsNullOrWhiteSpace(block.Text))
                    continue;

                bool isColored = IsColoredTextBlock(hsvFrame, block.BoxPoints);

                if (isColored)
                    npcParts.Add(block.Text);
                else
                    dialogueParts.Add(block.Text);
            }

            // Safety fallback: if ALL blocks were classified as colored (NPC), treat them all as dialogue
            // This prevents losing all text when color detection is wrong (e.g. special lighting)
            if (dialogueParts.Count == 0 && npcParts.Count > 0)
            {
                dialogueText = string.Join("\n", npcParts);
                npcName = "";
                return;
            }

            npcName = string.Join(" ", npcParts);
            dialogueText = string.Join("\n", dialogueParts);
        }

        /// <summary>
        /// Classify text blocks into NPC name vs dialogue with position data preserved.
        /// Returns a DetectedTextResult with TextBlockInfo objects carrying bounding boxes
        /// for use by the Embedded Illusion layout engine.
        ///
        /// This is the position-aware version of ClassifyTextBlocks.
        /// The original method is kept for backward compatibility.
        /// </summary>
        public static DetectedTextResult ClassifyTextBlocksWithPositions(
            OpenCvSharp.Mat colorFrame, List<TextBlock> textBlocks)
        {
            var result = new DetectedTextResult();

            if (textBlocks == null || textBlocks.Count == 0)
                return result;

            // If no color frame, treat all as dialogue (preserve positions)
            if (colorFrame == null || colorFrame.Empty())
            {
                foreach (var block in textBlocks)
                {
                    if (string.IsNullOrWhiteSpace(block.Text)) continue;
                    result.DialogueBlocks.Add(ToTextBlockInfo(block, isNpc: false));
                }
                return result;
            }

            using var hsvFrame = new OpenCvSharp.Mat();
            Cv2.CvtColor(colorFrame, hsvFrame, ColorConversionCodes.BGR2HSV);

            foreach (var block in textBlocks)
            {
                if (block.BoxPoints == null || block.BoxPoints.Length < 4 || string.IsNullOrWhiteSpace(block.Text))
                    continue;

                bool isColored = IsColoredTextBlock(hsvFrame, block.BoxPoints);
                var info = ToTextBlockInfo(block, isColored);

                if (isColored)
                    result.NpcBlocks.Add(info);
                else
                    result.DialogueBlocks.Add(info);
            }

            // Safety fallback: if ALL blocks were colored, treat all as dialogue
            if (result.DialogueBlocks.Count == 0 && result.NpcBlocks.Count > 0)
            {
                result.DialogueBlocks.AddRange(result.NpcBlocks);
                result.NpcBlocks.Clear();
            }

            // NPC role text detection: when NPC name (gold) is present and there are
            // multiple dialogue blocks, detect role text (e.g. "Owner, With Wind Comes Glory")
            // positioned between the NPC name and actual dialogue. Role text is white/grey
            // so the color classifier treats it as dialogue, but it shouldn't be translated.
            if (result.NpcBlocks.Count > 0 && result.DialogueBlocks.Count > 1)
            {
                ReclassifyRoleTextBlocks(result);
            }

            return result;
        }

        /// <summary>
        /// Convert a PaddleOCR TextBlock to a TextBlockInfo with computed bounding rect.
        /// </summary>
        private static TextBlockInfo ToTextBlockInfo(TextBlock block, bool isNpc)
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            if (block.BoxPoints != null && block.BoxPoints.Length >= 4)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (block.BoxPoints[i].X < minX) minX = block.BoxPoints[i].X;
                    if (block.BoxPoints[i].Y < minY) minY = block.BoxPoints[i].Y;
                    if (block.BoxPoints[i].X > maxX) maxX = block.BoxPoints[i].X;
                    if (block.BoxPoints[i].Y > maxY) maxY = block.BoxPoints[i].Y;
                }
            }
            else
            {
                minX = minY = 0;
                maxX = maxY = 0;
            }

            return new TextBlockInfo
            {
                Text = block.Text,
                BoxPoints = block.BoxPoints != null ? (PointF[])block.BoxPoints.Clone() : new PointF[4],
                BoundingRect = new RectangleF(minX, minY, maxX - minX, maxY - minY),
                IsNpcText = isNpc,
                Confidence = block.Score
            };
        }

        /// <summary>
        /// Reclassify NPC role text blocks (e.g. "Owner, With Wind Comes Glory") from dialogue to NPC.
        ///
        /// In Genshin Impact, the dialogue area layout is:
        ///   NPC Name (gold)  ← already classified as NPC by color
        ///   Role Text (white/grey, smaller font, between decorative lines)  ← sometimes absent
        ///   Dialogue (white, larger font)
        ///
        /// Called only when NPC name (gold) was detected AND there are 2+ dialogue blocks.
        /// Strategy: sort dialogue blocks by Y, find the largest vertical gap.
        /// Blocks above the gap with shorter text that are near the NPC name → role text.
        ///
        /// Edge cases handled:
        /// - No role text: gap between dialogue blocks is small → early return, no reclassification
        /// - Multi-line dialogue: OCR blocks are close together (small gap) → not reclassified
        /// - Cutscene (no NPC name): caller guards with NpcBlocks.Count > 0 check
        /// - Different resolutions: proximity threshold scales with NPC block height
        /// </summary>
        private static void ReclassifyRoleTextBlocks(DetectedTextResult result)
        {
            var sorted = result.DialogueBlocks
                .OrderBy(b => b.BoundingRect.Top + b.BoundingRect.Height / 2f)
                .ToList();

            if (sorted.Count < 2) return;

            // Compute NPC name bottom edge and average height (for resolution-relative thresholds)
            float npcBottom = 0;
            float npcAvgHeight = 0;
            foreach (var npc in result.NpcBlocks)
            {
                if (npc.BoundingRect.Bottom > npcBottom)
                    npcBottom = npc.BoundingRect.Bottom;
                npcAvgHeight += npc.BoundingRect.Height;
            }
            if (result.NpcBlocks.Count > 0)
                npcAvgHeight /= result.NpcBlocks.Count;

            // Find the largest Y gap between consecutive dialogue blocks
            float maxGap = 0;
            int gapIndex = -1;
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                float gap = sorted[i + 1].BoundingRect.Top - sorted[i].BoundingRect.Bottom;
                if (gap > maxGap)
                {
                    maxGap = gap;
                    gapIndex = i;
                }
            }

            // Gap must be meaningful (>15px covers the decorative separator line in Genshin).
            // Small gaps between multi-line dialogue blocks won't trigger this.
            if (gapIndex < 0 || maxGap < 15f) return;

            var aboveGap = sorted.Take(gapIndex + 1).ToList();
            var belowGap = sorted.Skip(gapIndex + 1).ToList();

            // Role text is always shorter than dialogue
            int aboveTextLen = aboveGap.Sum(b => b.Text.Length);
            int belowTextLen = belowGap.Sum(b => b.Text.Length);
            if (aboveTextLen >= belowTextLen) return;

            // Above-gap blocks must be near the NPC name (scales with resolution:
            // 3x NPC name height, minimum 80px to handle low-res captures)
            float proximityThreshold = Math.Max(npcAvgHeight * 3f, 80f);
            float aboveCenterY = aboveGap.Average(b => b.BoundingRect.Top + b.BoundingRect.Height / 2f);
            if (npcBottom > 0 && (aboveCenterY - npcBottom) > proximityThreshold) return;

            // All checks passed — reclassify above-gap blocks as NPC role text
            foreach (var block in aboveGap)
            {
                block.IsNpcText = true;
                result.DialogueBlocks.Remove(block);
                result.NpcBlocks.Add(block);
            }
        }

        /// <summary>
        /// Check if a text block's pixels are colored (high saturation) vs white (low saturation).
        /// Samples bright pixels (V > 180) within the bounding rect and checks mean saturation.
        /// </summary>
        private static bool IsColoredTextBlock(OpenCvSharp.Mat hsvFrame, PointF[] boxPoints)
        {
            // Compute axis-aligned bounding rect from the 4 corners
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                if (boxPoints[i].X < minX) minX = boxPoints[i].X;
                if (boxPoints[i].Y < minY) minY = boxPoints[i].Y;
                if (boxPoints[i].X > maxX) maxX = boxPoints[i].X;
                if (boxPoints[i].Y > maxY) maxY = boxPoints[i].Y;
            }

            // Clamp to frame bounds
            int x1 = Math.Max(0, (int)minX);
            int y1 = Math.Max(0, (int)minY);
            int x2 = Math.Min(hsvFrame.Width - 1, (int)maxX);
            int y2 = Math.Min(hsvFrame.Height - 1, (int)maxY);

            if (x2 <= x1 || y2 <= y1)
                return false;

            // Crop the bounding rect from the HSV frame
            var roi = new Rect(x1, y1, x2 - x1, y2 - y1);
            using var cropped = new OpenCvSharp.Mat(hsvFrame, roi);

            // Split into H, S, V channels
            var channels = Cv2.Split(cropped);
            try
            {
                using var hChannel = channels[0];
                using var sChannel = channels[1];
                using var vChannel = channels[2];

                // Mask: only bright pixels (V > 180) — these are the actual text pixels
                using var brightMask = new OpenCvSharp.Mat();
                Cv2.Threshold(vChannel, brightMask, 180, 255, ThresholdTypes.Binary);

                int brightCount = Cv2.CountNonZero(brightMask);
                if (brightCount < 5)
                    return false; // Not enough bright pixels to classify

                // Compute mean saturation of bright pixels only
                var meanSat = Cv2.Mean(sChannel, brightMask);

                // Saturation threshold: >45 means colored (golden NPC name), <=45 means white (dialogue)
                return meanSat.Val0 > 45;
            }
            finally
            {
                // Dispose all channel Mats
                foreach (var ch in channels) ch?.Dispose();
            }
        }
    }
}

