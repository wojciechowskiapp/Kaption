using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GI_Subtitles.Models;
using OpenCvSharp;
using PaddleOCRSharp;

namespace GI_Subtitles.Services.OCR.Classification
{
    /// <summary>
    /// Speaker/body split by accent colour — the shipping behaviour for Genshin
    /// Impact and Honkai: Star Rail, moved here verbatim from
    /// <c>ImageProcessor.ClassifyTextBlocksWithPositions</c> when the
    /// <see cref="ITextBlockClassifier"/> seam was introduced for Zenless Zone Zero.
    ///
    /// <para>A block is a speaker name when at least 20% of its bright
    /// (V &gt; 180) pixels are saturated (S &gt; 45) <em>and</em> sit in the gold
    /// band (H 8–45) or the light-cyan band (H 75–105) of the 7.0 dialogue UI.
    /// Both games' dialogue bodies are white, so their saturation is near zero
    /// and they never trip the gate.</para>
    ///
    /// <para>The hue/saturation thresholds, the band-bottom rule, the all-accent
    /// fallback and the role-text reclassification came across from
    /// <c>ImageProcessor</c> unchanged. The one rule since revised is the
    /// name-versus-highlight separation test, which compared box edges and so
    /// dropped the speaker on 29% of a recorded Genshin scene; it now compares
    /// row centres. The <c>NpcClassifier_*</c> tests in
    /// <c>RuntimeRegressionTests</c> pin both behaviours and still call through
    /// <c>ImageProcessor</c>'s shim.</para>
    /// </summary>
    public sealed class AccentColorTextBlockClassifier : ITextBlockClassifier
    {
        /// <summary>Shared stateless instance — safe to reuse across threads.</summary>
        public static readonly AccentColorTextBlockClassifier Instance =
            new AccentColorTextBlockClassifier();

        /// <inheritdoc/>
        public DetectedTextResult Classify(Mat colorFrame, List<TextBlock> textBlocks)
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
                    result.DialogueBlocks.Add(OcrBlockGeometry.ToTextBlockInfo(block, isNpc: false));
                }
                return result;
            }

            using var hsvFrame = new Mat();
            Cv2.CvtColor(colorFrame, hsvFrame, ColorConversionCodes.BGR2HSV);

            var candidates = new List<(TextBlockInfo Info, bool IsNpcColor)>();
            foreach (var block in textBlocks)
            {
                if (block.BoxPoints == null || block.BoxPoints.Length < 4 || string.IsNullOrWhiteSpace(block.Text))
                    continue;

                bool isNpcColor = IsColoredTextBlock(hsvFrame, block.BoxPoints);
                candidates.Add((OcrBlockGeometry.ToTextBlockInfo(block, isNpcColor), isNpcColor));
            }

            // NPC names occupy the first accent-coloured line above the body.
            // Genshin 7.0 also uses a light cyan name style. A highlighted word
            // or a coloured/HDR background lower in the crop
            // must remain dialogue instead of deleting most of the sentence.
            var coloured = candidates.Where(candidate => candidate.IsNpcColor).ToList();
            var nonColoured = candidates.Where(candidate => !candidate.IsNpcColor).ToList();
            float npcBandBottom = float.MinValue;
            if (coloured.Count > 0)
            {
                var topmost = coloured.OrderBy(candidate => candidate.Info.BoundingRect.Top).First();
                float top = topmost.Info.BoundingRect.Top;
                float topLineHeight = Math.Max(1f, topmost.Info.BoundingRect.Height);

                // A speaker name is a gold/cyan line on its own row; accent
                // highlighting *inside* a dialogue line must stay in the body.
                // Row occupancy answers that, where the gap between the two boxes
                // does not: Genshin's name and first body line leave 1–6 px
                // between their detected boxes, so a gap threshold is decided by
                // detector jitter, and any block above the name — a choice-menu
                // option, a glyph hallucinated on the character art — is not the
                // body being tested for. A lone accent line is kept as NPC-only so
                // the bounded OCR retry can wait for typewriter text instead of
                // matching a name as if it were dialogue.
                bool isolatedNameOnly = candidates.Count == 1;
                bool separatedFromBody = false;
                if (nonColoured.Count > 0)
                {
                    float accentCentre = OcrBlockGeometry.CentreY(topmost.Info);
                    float requiredSeparation = Math.Max(2f, topLineHeight * 0.5f);
                    separatedFromBody = nonColoured.All(candidate =>
                        Math.Abs(OcrBlockGeometry.CentreY(candidate.Info) - accentCentre)
                            >= requiredSeparation);
                }

                if (isolatedNameOnly || separatedFromBody)
                    npcBandBottom = top + topLineHeight * 1.5f;
            }

            foreach (var candidate in candidates)
            {
                bool isNpc = candidate.IsNpcColor && candidate.Info.BoundingRect.Top <= npcBandBottom;
                candidate.Info.IsNpcText = isNpc;
                if (isNpc)
                    result.NpcBlocks.Add(candidate.Info);
                else
                    result.DialogueBlocks.Add(candidate.Info);
            }

            // Multiple all-accent blocks are more likely highlighted dialogue.
            // Keep a single isolated top line as NPC-only (see above).
            if (result.DialogueBlocks.Count == 0 && result.NpcBlocks.Count > 1)
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

            // Compute NPC name edges and average height (for resolution-relative thresholds)
            float npcTop = float.MaxValue;
            float npcBottom = 0;
            float npcAvgHeight = 0;
            foreach (var npc in result.NpcBlocks)
            {
                if (npc.BoundingRect.Top < npcTop)
                    npcTop = npc.BoundingRect.Top;
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

            float medianDialogueHeight = sorted
                .Select(block => Math.Max(1f, block.BoundingRect.Height))
                .OrderBy(height => height)
                .ElementAt(sorted.Count / 2);

            // Resolution-relative gap: a fixed 15 px threshold wrongly
            // reclassified ordinary two-line dialogue at 4K.
            if (gapIndex < 0 || maxGap < Math.Max(8f, medianDialogueHeight * 0.65f)) return;

            var aboveGap = sorted.Take(gapIndex + 1).ToList();
            var belowGap = sorted.Skip(gapIndex + 1).ToList();

            // Role text is always shorter than dialogue
            int aboveTextLen = aboveGap.Sum(b => b.Text.Length);
            int belowTextLen = belowGap.Sum(b => b.Text.Length);
            if (aboveTextLen >= belowTextLen) return;

            // Role text is visibly smaller than dialogue. Character count alone
            // is not evidence: a short first dialogue line is still dialogue.
            float aboveAvgHeight = aboveGap.Average(b => Math.Max(1f, b.BoundingRect.Height));
            float belowAvgHeight = belowGap.Average(b => Math.Max(1f, b.BoundingRect.Height));
            if (aboveAvgHeight > belowAvgHeight * 0.82f) return;

            // Role text is printed between the speaker and the dialogue, so a
            // block above the speaker is some other on-screen element — a choice
            // option, a quest banner. Folding one of those into the name deletes
            // it from the match input, which is worse than leaving it in the body.
            if (npcTop < float.MaxValue &&
                aboveGap.Any(block => OcrBlockGeometry.CentreY(block) <= npcTop)) return;

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
        /// Detect speaker-name accent pixels. Genshin uses gold/amber in the
        /// classic dialogue UI and light cyan in the 7.0 blue dialogue UI.
        /// Restricting cyan to H=75..105 deliberately excludes the deeper blue
        /// panel/background (normally H≈107..115), while the 20% bright-pixel
        /// ratio prevents a few background pixels from reclassifying white body.
        ///
        /// <para>Internal rather than private so the legacy
        /// <c>ImageProcessor.ClassifyTextBlocks</c> overload — dead in the runtime
        /// path but still compiled — can keep calling the one implementation
        /// instead of carrying a copy that could drift.</para>
        /// </summary>
        internal static bool IsColoredTextBlock(Mat hsvFrame, PointF[] boxPoints)
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
            using var cropped = new Mat(hsvFrame, roi);

            // Split into H, S, V channels
            var channels = Cv2.Split(cropped);
            try
            {
                var hChannel = channels[0];
                var sChannel = channels[1];
                var vChannel = channels[2];

                // Mask: only bright pixels (V > 180) — these are the actual text pixels
                using var brightMask = new Mat();
                Cv2.Threshold(vChannel, brightMask, 180, 255, ThresholdTypes.Binary);

                int brightCount = Cv2.CountNonZero(brightMask);
                if (brightCount < 5)
                    return false; // Not enough bright pixels to classify

                // NPC names use a gold/amber or light-cyan hue. Saturation alone classified
                // any bright HDR/coloured background as an NPC name and could
                // discard most dialogue blocks.
                using var saturatedMask = new Mat();
                Cv2.Threshold(sChannel, saturatedMask, 45, 255, ThresholdTypes.Binary);
                using var goldHueMask = new Mat();
                Cv2.InRange(hChannel, new Scalar(8), new Scalar(45), goldHueMask);
                using var cyanHueMask = new Mat();
                Cv2.InRange(hChannel, new Scalar(75), new Scalar(105), cyanHueMask);
                using var accentHueMask = new Mat();
                Cv2.BitwiseOr(goldHueMask, cyanHueMask, accentHueMask);
                using var accentTextMask = new Mat();
                Cv2.BitwiseAnd(brightMask, saturatedMask, accentTextMask);
                Cv2.BitwiseAnd(accentTextMask, accentHueMask, accentTextMask);

                int accentCount = Cv2.CountNonZero(accentTextMask);
                return accentCount >= Math.Max(5, (int)Math.Ceiling(brightCount * 0.20));
            }
            finally
            {
                // Dispose all channel Mats
                foreach (var ch in channels) ch?.Dispose();
            }
        }
    }
}
