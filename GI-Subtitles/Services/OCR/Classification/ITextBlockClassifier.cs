using System.Collections.Generic;
using GI_Subtitles.Models;
using PaddleOCRSharp;

namespace GI_Subtitles.Services.OCR.Classification
{
    /// <summary>
    /// Splits a frame's OCR text blocks into "speaker name" and "dialogue body".
    ///
    /// <para>Games disagree about how they mark the speaker, and the disagreement
    /// is not a matter of degree — it is a different <em>signal</em>. Genshin and
    /// Star Rail print the name in an accent colour (gold, or light cyan in the
    /// 7.0 UI), so a hue/saturation gate separates it cleanly. Zenless Zone Zero
    /// prints the name in plain white in both of its dialogue styles, so that
    /// gate has nothing to measure and every implementation of it returns
    /// "no name" — the name silently lands in the body.</para>
    ///
    /// <para>Hence this seam. Pick the implementation per game via
    /// <see cref="TextBlockClassifierFactory"/>; do not try to make one
    /// classifier cover both by loosening thresholds.</para>
    ///
    /// <para><b>Performance contract:</b> implementations run on the OCR tick,
    /// once per inference. Box arithmetic over the usual &lt;20 blocks is fine.
    /// Never call <c>FormattedText.Measure</c>, never touch the layout engine,
    /// never allocate per-pixel buffers outside of what OpenCV already does.</para>
    /// </summary>
    public interface ITextBlockClassifier
    {
        /// <summary>
        /// Classify <paramref name="textBlocks"/> into NPC-name and dialogue blocks.
        /// </summary>
        /// <param name="colorFrame">
        /// BGR colour Mat of the captured region. Colour-based implementations
        /// need it; geometry-based ones ignore it and tolerate null.
        /// </param>
        /// <param name="textBlocks">
        /// Blocks from PaddleOCR, with <c>BoxPoints</c> in image coordinates.
        /// </param>
        /// <returns>
        /// Never null. An empty result means "nothing usable in this frame".
        /// </returns>
        DetectedTextResult Classify(OpenCvSharp.Mat colorFrame, List<TextBlock> textBlocks);
    }
}
