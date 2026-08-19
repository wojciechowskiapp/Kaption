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
using GI_Subtitles.Services.OCR.Classification;

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
            using var fingerprint = new OpenCvSharp.Mat();
            Cv2.Resize(cropped, fingerprint, new OpenCvSharp.Size(32, 16), 0, 0, InterpolationFlags.Area);

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

            // The legacy 64-bit dHash above deliberately ignores a lot of
            // detail and produced exact collisions between different long
            // subtitle lines. Keep it for coarse robustness, but append a
            // second content signature over a denser normalized crop.
            ulong contentSignature = 14695981039346656037UL; // FNV-1a offset
            unsafe
            {
                byte* ptr = (byte*)fingerprint.DataPointer;
                int step = (int)fingerprint.Step();
                int rows = fingerprint.Rows;
                int cols = fingerprint.Cols;
                for (int y = 0; y < rows; y++)
                {
                    byte* row = ptr + y * step;
                    for (int x = 0; x < cols; x++)
                    {
                        contentSignature ^= row[x];
                        contentSignature *= 1099511628211UL;
                    }
                }
            }
            contentSignature ^= (uint)roi.Width;
            contentSignature *= 1099511628211UL;
            contentSignature ^= (uint)roi.Height;

            return $"{hash}-{contentSignature:X16}-{roi.Width:X4}{roi.Height:X4}";
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

                bool isColored = Classification.AccentColorTextBlockClassifier
                    .IsColoredTextBlock(hsvFrame, block.BoxPoints);

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
        ///
        /// <para><b>Compatibility shim.</b> The implementation moved to
        /// <see cref="AccentColorTextBlockClassifier"/> when the
        /// <see cref="ITextBlockClassifier"/> seam was introduced, so that Zenless
        /// Zone Zero — which prints speaker names in the same white as the body —
        /// could be split by geometry instead of by hue. This entry point keeps
        /// the accent behaviour verbatim for every existing caller and test.</para>
        ///
        /// <para>New code that knows which game it is running against should
        /// resolve a classifier through
        /// <see cref="TextBlockClassifierFactory.Create(string)"/> rather than
        /// calling this.</para>
        /// </summary>
        public static DetectedTextResult ClassifyTextBlocksWithPositions(
            OpenCvSharp.Mat colorFrame, List<TextBlock> textBlocks)
            => AccentColorTextBlockClassifier.Instance.Classify(colorFrame, textBlocks);
    }
}
