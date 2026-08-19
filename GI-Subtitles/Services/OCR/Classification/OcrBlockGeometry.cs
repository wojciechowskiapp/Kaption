using System.Drawing;
using GI_Subtitles.Models;
using PaddleOCRSharp;

namespace GI_Subtitles.Services.OCR.Classification
{
    /// <summary>
    /// Shared box arithmetic for the classifiers. Kept in one place so the
    /// accent and geometry paths derive identical bounding rectangles from the
    /// same OCR output — a divergence here would show up as an unexplained
    /// layout shift when a user switches games.
    /// </summary>
    internal static class OcrBlockGeometry
    {
        /// <summary>
        /// Convert a PaddleOCR <see cref="TextBlock"/> to a <see cref="TextBlockInfo"/>
        /// with an axis-aligned bounding rect computed from its four (possibly
        /// rotated) corners. Blocks without usable corners collapse to a
        /// zero rect rather than throwing — the caller decides what to do
        /// with a degenerate block.
        /// </summary>
        internal static TextBlockInfo ToTextBlockInfo(TextBlock block, bool isNpc)
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

        /// <summary>Vertical centre of a block, in image-space pixels.</summary>
        internal static float CentreY(TextBlockInfo block)
            => block.BoundingRect.Top + block.BoundingRect.Height / 2f;
    }
}
