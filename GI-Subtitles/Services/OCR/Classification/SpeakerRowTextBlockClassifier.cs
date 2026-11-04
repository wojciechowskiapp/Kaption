using System;
using System.Collections.Generic;
using GI_Subtitles.Models;
using OpenCvSharp;
using PaddleOCRSharp;

namespace GI_Subtitles.Services.OCR.Classification
{
    /// <summary>
    /// Speaker/body split for a centred dialogue column whose speaker is the top row: Wuthering Waves.
    ///
    /// <para>WuWa prints the speaker in pale gold and a larger font, centred above up to four centred body rows. The
    /// accent gate reads the panel-backed layouts well, but free-roam lines have no panel: white body text over sunlit
    /// scenery passes it, and <see cref="AccentColorTextBlockClassifier"/>'s name band (top + 1.5 × the name box, which
    /// the larger name font stretches over the first body row) pulls that row into the name. Over pale art the name
    /// fails the gate and stays in the body, where the header split then matches the name itself.</para>
    ///
    /// <para>So the speaker is decided per printed row, and only the top row of the dialogue column can be it: when it
    /// has at most <see cref="GeometryTextBlockClassifier.MaxSpeakerNameChars"/> characters and is accent-coloured or
    /// at least <see cref="TallerNameRatio"/> times as tall as the body rows. Accent rows lower down stay in the body.
    /// Rows starting right of <see cref="SideRowStartFraction"/> of the frame are the choice panel or HUD keys:
    /// centred text always starts left of the centre. Measurements: .plan/in-progress/WUWA-STAGE-B.md.</para>
    /// </summary>
    public sealed class SpeakerRowTextBlockClassifier : ITextBlockClassifier
    {
        /// <summary>Shared stateless instance — safe to reuse across threads.</summary>
        public static readonly SpeakerRowTextBlockClassifier Instance = new SpeakerRowTextBlockClassifier(filterJunk: false);

        /// <summary>The same split after dropping <see cref="OcrTextJunkFilter"/> blocks (profile <c>FiltersOcrJunk</c>).</summary>
        public static readonly SpeakerRowTextBlockClassifier WithJunkFilter = new SpeakerRowTextBlockClassifier(filterJunk: true);

        /// <summary>
        /// A row whose left edge lies right of this fraction of the frame width is not dialogue. WuWa's choice panels
        /// start at 0.75 of the default region; 0.62 leaves room for a region drawn off-centre by about 0.07 of the band.
        /// </summary>
        internal const float SideRowStartFraction = 0.62f;

        /// <summary>
        /// Name row height over the median body row height that marks the name without colour. Measured: names 1.33×
        /// (p05 1.04), body rows 0.90–1.10× (p99 1.16).
        /// </summary>
        internal const float TallerNameRatio = 1.15f;

        private readonly bool _filterJunk;

        private SpeakerRowTextBlockClassifier(bool filterJunk)
        {
            _filterJunk = filterJunk;
        }

        /// <inheritdoc/>
        public DetectedTextResult Classify(Mat colorFrame, List<TextBlock> textBlocks)
        {
            var result = new DetectedTextResult();
            if (textBlocks == null || textBlocks.Count == 0)
                return result;

            // OCR order is kept for the output: the body is joined in it, as on the accent path.
            var usable = new List<TextBlockInfo>(textBlocks.Count);
            foreach (var block in textBlocks)
            {
                if (block == null || string.IsNullOrWhiteSpace(block.Text)) continue;
                if (block.BoxPoints == null || block.BoxPoints.Length < 4) continue;
                if (_filterJunk && OcrTextJunkFilter.IsJunk(block.Text)) continue;

                var info = OcrBlockGeometry.ToTextBlockInfo(block, isNpc: false);
                if (info.BoundingRect.Width <= 0 || info.BoundingRect.Height <= 0) continue;
                usable.Add(info);
            }
            if (usable.Count == 0)
                return result;

            // Without the frame its width is unknown, so nothing counts as a side panel.
            bool hasColour = colorFrame != null && !colorFrame.Empty();
            float sideStart = hasColour ? SideRowStartFraction * colorFrame.Width : float.MaxValue;

            var column = new List<List<TextBlockInfo>>();
            foreach (var row in GeometryTextBlockClassifier.GroupIntoRowBands(usable))
            {
                if (LeftEdge(row) < sideStart) column.Add(row);
            }
            if (column.Count == 0)
                return result;

            List<TextBlockInfo> head = column[0];
            bool headIsName = false;
            if (TrimmedLength(head) <= GeometryTextBlockClassifier.MaxSpeakerNameChars)
            {
                // A lone row has no body to compare heights with; like the accent path, an accent one waits as name-only.
                if (column.Count > 1 && RowHeight(head) >= TallerNameRatio * MedianRowHeight(column))
                    headIsName = true;
                else if (hasColour)
                    headIsName = IsAccentRow(colorFrame, head);
            }

            foreach (var info in usable)
            {
                if (!InColumn(column, info)) continue;
                bool isName = headIsName && head.Contains(info);
                info.IsNpcText = isName;
                if (isName) result.NpcBlocks.Add(info);
                else result.DialogueBlocks.Add(info);
            }
            return result;
        }

        private static bool IsAccentRow(Mat colorFrame, List<TextBlockInfo> row)
        {
            using var hsvFrame = new Mat();
            Cv2.CvtColor(colorFrame, hsvFrame, ColorConversionCodes.BGR2HSV);
            foreach (var block in row)
            {
                if (AccentColorTextBlockClassifier.IsColoredTextBlock(hsvFrame, block.BoxPoints)) return true;
            }
            return false;
        }

        private static bool InColumn(List<List<TextBlockInfo>> column, TextBlockInfo info)
        {
            foreach (var row in column)
            {
                if (row.Contains(info)) return true;
            }
            return false;
        }

        /// <summary>Median height of the rows below the head.</summary>
        private static float MedianRowHeight(List<List<TextBlockInfo>> column)
        {
            var heights = new List<float>(column.Count - 1);
            for (int i = 1; i < column.Count; i++) heights.Add(RowHeight(column[i]));
            heights.Sort();
            int mid = heights.Count / 2;
            return heights.Count % 2 == 1 ? heights[mid] : (heights[mid - 1] + heights[mid]) / 2f;
        }

        private static float RowHeight(List<TextBlockInfo> row)
        {
            float h = 0f;
            foreach (var block in row) h = Math.Max(h, block.BoundingRect.Height);
            return h;
        }

        private static float LeftEdge(List<TextBlockInfo> row)
        {
            float left = float.MaxValue;
            foreach (var block in row) left = Math.Min(left, block.BoundingRect.Left);
            return left;
        }

        private static int TrimmedLength(List<TextBlockInfo> row)
        {
            int total = 0;
            foreach (var block in row) total += block.Text.Trim().Length;
            return total;
        }
    }
}
