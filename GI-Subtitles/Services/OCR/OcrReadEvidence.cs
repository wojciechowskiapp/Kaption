using System.Collections.Generic;
using GI_Subtitles.Models;

namespace GI_Subtitles.Services.OCR
{
    /// <summary>
    /// Whether an OCR read saw text that is actually lit. OCR reads the colour frame, so it still reads a line while
    /// the dialogue box fades out; the change gate compares binarised frames (threshold 220), where that dim line is
    /// already black. Committing such a read left nothing for the gate to see change: the overlay kept the line
    /// through the black screen and free roam that followed, until the next text (session 57).
    /// </summary>
    public static class OcrReadEvidence
    {
        /// <summary>
        /// Lit pixels per pixel of body box below which the read is a fading line. Measured on 16 Genshin scenes:
        /// fades sit under 0.015, typed and settled lines at 0.05 and up (median 0.16).
        /// </summary>
        public const double MinLitFraction = 0.02;

        /// <param name="litPixels">Pixels above the binary threshold in the whole capture.</param>
        /// <param name="bodyBoxArea">Summed area of the dialogue-body boxes, same pixels.</param>
        public static bool IsFaded(int litPixels, double bodyBoxArea) =>
            bodyBoxArea > 0 && litPixels < bodyBoxArea * MinLitFraction;

        /// <summary>The body boxes' summed area; 0 when the read has no positioned body.</summary>
        public static double BodyArea(IEnumerable<TextBlockInfo> bodyBlocks)
        {
            double area = 0;
            if (bodyBlocks == null) return 0;
            foreach (TextBlockInfo b in bodyBlocks)
                area += (double)b.BoundingRect.Width * b.BoundingRect.Height;
            return area;
        }
    }
}
