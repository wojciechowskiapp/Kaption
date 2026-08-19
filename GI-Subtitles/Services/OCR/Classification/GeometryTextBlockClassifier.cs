using System;
using System.Collections.Generic;
using GI_Subtitles.Models;
using OpenCvSharp;
using PaddleOCRSharp;

namespace GI_Subtitles.Services.OCR.Classification
{
    /// <summary>
    /// Speaker/body split by layout geometry, for games that render the speaker
    /// name in the same colour as the dialogue.
    ///
    /// <para>Zenless Zone Zero does exactly that in both of its story styles —
    /// the centred cinematic caption and the dark comic panel — so
    /// <see cref="AccentColorTextBlockClassifier"/> reports "no name" on every
    /// frame and the name silently joins the body. That is not a cosmetic loss:
    /// the fallback header split in <c>OptimizedMatcher.FindMatchWithHeaderSeparated</c>
    /// only peels line 0 when line 1 is more than twice as long, so a 9-character
    /// name glued to a short line survives into <c>NormalizeInput</c> and the
    /// concatenation cannot match anything within the distance ceiling.</para>
    ///
    /// <para><b>The signals, and the ones that were rejected.</b> Measured against
    /// real PaddleOCR output on both styles (<c>GI-Test/ZenlessLayoutDiagnostics.cs</c>):</para>
    /// <list type="bullet">
    ///   <item><description><b>Width ratio</b> (name band ÷ widest body band) —
    ///   0.141 cinematic, 0.083 comic. Primary signal.</description></item>
    ///   <item><description><b>Character count</b> (name ÷ body total) — 9/173 and
    ///   5/186. Secondary; it disambiguates when a body line happens to be short.</description></item>
    ///   <item><description><b>Glyph height ratio</b> — REJECTED. It measures 1.195
    ///   and 1.152: the name box is <em>taller</em>, not shorter, because
    ///   <c>det_db_unclip_ratio</c> inflates boxes around the heavy black outline.
    ///   A "names are smaller" term would point the wrong way.</description></item>
    ///   <item><description><b>Centre-X alignment</b> — REJECTED. Body boxes are
    ///   wide, so their centres also land near 50%; it does not separate a centred
    ///   name from a left-aligned body.</description></item>
    ///   <item><description><b>Vertical edge gap</b> — REJECTED as an edge measure.
    ///   In the cinematic style it is <em>negative</em> (−7.3 px): the name box
    ///   overlaps the first body line. Rows are grouped by centre-Y proximity
    ///   instead, which is sign-agnostic.</description></item>
    /// </list>
    ///
    /// <para><b>Which way to fail.</b> The two error modes are not symmetric.
    /// Promoting a real first body line to "name" deletes user-visible dialogue
    /// from the match input; leaving a name in the body reproduces today's
    /// behaviour, which the header-split fallback sometimes still recovers.
    /// The thresholds are therefore set to reject when uncertain.</para>
    /// </summary>
    public sealed class GeometryTextBlockClassifier : ITextBlockClassifier
    {
        /// <summary>Shared stateless instance — safe to reuse across threads.</summary>
        public static readonly GeometryTextBlockClassifier Instance =
            new GeometryTextBlockClassifier();

        // ── Row banding ────────────────────────────────────────────────────

        /// <summary>
        /// Two blocks share a row band when their vertical centres sit within this
        /// fraction of the shorter block's height.
        ///
        /// <para>0.5 means "the centres overlap within half a glyph", which is the
        /// natural definition of "same printed line". Checked against measurement:
        /// in the cinematic frame the name centre is 110.5 and the first body
        /// centre 148, a 37.5 px separation against a tolerance of 0.5 × 43 = 21.5,
        /// so they split correctly even though their <em>boxes</em> overlap. In the
        /// comic frame consecutive body lines sit 38 px apart against a 19 px
        /// tolerance. Raising this to 1.0 would merge adjacent body lines into one
        /// band and destroy the width signal.</para>
        /// </summary>
        internal const float RowBandCentreTolerance = 0.5f;

        // ── Scoring ────────────────────────────────────────────────────────

        /// <summary>Width ratio at or below which the width signal scores a full 1.0.</summary>
        /// <remarks>
        /// Measured names are 0.141 and 0.083, so 0.18 clears both with room to
        /// spare while staying well under the ratios a genuine short first line
        /// produces.
        /// </remarks>
        internal const float WidthRatioFullScore = 0.18f;

        /// <summary>Width ratio at or above which the width signal scores 0.</summary>
        /// <remarks>
        /// The reference counter-example is the existing 4K regression fixture:
        /// "Not now." at 220 px above a 700 px line, a ratio of 0.314. Setting the
        /// zero point at 0.32 puts that case a hair below the floor, so it
        /// contributes essentially nothing and cannot be carried over the accept
        /// threshold by the character term alone.
        /// </remarks>
        internal const float WidthRatioZeroScore = 0.32f;

        /// <summary>Character ratio at or below which the count signal scores a full 1.0.</summary>
        /// <remarks>Measured names are 0.052 and 0.027; 0.10 clears both.</remarks>
        internal const float CharRatioFullScore = 0.10f;

        /// <summary>Character ratio at or above which the count signal scores 0.</summary>
        /// <remarks>
        /// "Not now." is 8 characters against 41 of body, a ratio of 0.195, which
        /// lands mid-ramp at roughly 0.47 — high enough to matter, not high enough
        /// to carry the decision on its own.
        /// </remarks>
        internal const float CharRatioZeroScore = 0.28f;

        /// <summary>
        /// Weight of the width term. Width is the primary signal because it is a
        /// property of the rendered layout rather than of the sentence: a speaker
        /// name occupies a name-sized box no matter how terse the line under it is.
        /// </summary>
        internal const float WidthWeight = 0.65f;

        /// <summary>
        /// Weight of the character-count term. It is genuine independent evidence
        /// but it degrades on the exact case that matters — a short first body
        /// line — so it takes the smaller share.
        /// </summary>
        internal const float CharWeight = 0.35f;

        /// <summary>
        /// Combined score required to call the top band a speaker name.
        ///
        /// <para>Both measured layouts score a clean 1.0. The "Not now." counter-case
        /// scores 0.65 × 0.04 + 0.35 × 0.47 ≈ 0.19. With that much daylight the
        /// threshold could sit anywhere in between; 0.60 is placed high so that a
        /// mid-ramp width (a name-shaped box that is not conclusively narrow)
        /// still needs a strongly name-like character count to pass.</para>
        /// </summary>
        internal const float AcceptScore = 0.60f;

        /// <summary>
        /// Hard cap on speaker-name length, applied before scoring.
        ///
        /// <para>Insurance against a pathological frame: a very long body makes
        /// even a substantial first line look small as a <em>ratio</em>. Genshin's
        /// longest observed role line ("Owner, With Wind Comes Glory") is 28
        /// characters, so 40 leaves generous headroom over anything a name plus
        /// title realistically occupies.</para>
        /// </summary>
        internal const int MaxSpeakerNameChars = 40;

        /// <inheritdoc/>
        /// <remarks>
        /// <paramref name="colorFrame"/> is ignored — the whole point of this
        /// classifier is that colour carries no signal here. Null is fine.
        /// </remarks>
        public DetectedTextResult Classify(Mat colorFrame, List<TextBlock> textBlocks)
        {
            var result = new DetectedTextResult();
            if (textBlocks == null || textBlocks.Count == 0)
                return result;

            // ── 1. Usable blocks only ──
            var usable = new List<TextBlockInfo>(textBlocks.Count);
            foreach (var block in textBlocks)
            {
                if (block == null || string.IsNullOrWhiteSpace(block.Text)) continue;
                if (block.BoxPoints == null || block.BoxPoints.Length < 4) continue;

                var info = OcrBlockGeometry.ToTextBlockInfo(block, isNpc: false);
                if (info.BoundingRect.Width <= 0 || info.BoundingRect.Height <= 0) continue;
                usable.Add(info);
            }
            if (usable.Count == 0) return result;

            // ── 2. Drop watermarks / HUD before any geometry is measured ──
            // Junk distorts row banding and union widths, so it has to go first.
            // If the filter would empty the frame it is the filter that is wrong,
            // not the frame: fall back to the unfiltered set and let the scoring
            // decide (it will find no speaker, which is the safe answer).
            var kept = new List<TextBlockInfo>(usable.Count);
            foreach (var info in usable)
            {
                if (!OcrTextJunkFilter.IsJunk(info.Text)) kept.Add(info);
            }
            if (kept.Count == 0) kept = usable;

            // ── 3. Group into printed rows ──
            List<List<TextBlockInfo>> bands = GroupIntoRowBands(kept);

            // ── 4. One row means narration or a lone line: no speaker. ──
            // ZZZ uses unattributed narration captions, so this is a real case,
            // not just a degenerate guard.
            if (bands.Count < 2)
            {
                AddAllAsDialogue(result, bands);
                return result;
            }

            // ── 5. Score the topmost band as the speaker candidate ──
            var head = bands[0];

            float headWidth = UnionWidth(head);
            int headChars = TotalTrimmedLength(head);

            float widestBodyWidth = 0f;
            int bodyChars = 0;
            for (int i = 1; i < bands.Count; i++)
            {
                float w = UnionWidth(bands[i]);
                if (w > widestBodyWidth) widestBodyWidth = w;
                bodyChars += TotalTrimmedLength(bands[i]);
            }

            bool isSpeaker =
                headChars > 0 &&
                headChars <= MaxSpeakerNameChars &&
                widestBodyWidth > 0f &&
                bodyChars > 0 &&
                Score(headWidth / widestBodyWidth, headChars / (float)bodyChars) >= AcceptScore;

            // ── 6. Emit ──
            if (!isSpeaker)
            {
                AddAllAsDialogue(result, bands);
                return result;
            }

            foreach (var block in head)
            {
                // Decoration stripping is confined to the name field. If a block
                // is nothing but ornament, keep the raw text rather than emitting
                // an empty name — losing the text outright would be worse.
                string stripped = SpeakerNameDecoration.Strip(block.Text);
                if (stripped.Length > 0) block.Text = stripped;

                block.IsNpcText = true;
                result.NpcBlocks.Add(block);
            }

            for (int i = 1; i < bands.Count; i++)
            {
                foreach (var block in bands[i])
                {
                    block.IsNpcText = false;
                    result.DialogueBlocks.Add(block);
                }
            }

            return result;
        }

        /// <summary>
        /// Blend the two signals. Each is mapped through a linear ramp from
        /// "conclusively name-like" (1.0) to "conclusively not" (0.0), then
        /// weighted. Ramps rather than hard cut-offs so a borderline layout
        /// degrades smoothly instead of flipping on a pixel.
        /// </summary>
        internal static float Score(float widthRatio, float charRatio)
        {
            return WidthWeight * Ramp(widthRatio, WidthRatioFullScore, WidthRatioZeroScore)
                 + CharWeight * Ramp(charRatio, CharRatioFullScore, CharRatioZeroScore);
        }

        /// <summary>Linear 1→0 ramp between <paramref name="full"/> and <paramref name="zero"/>.</summary>
        private static float Ramp(float value, float full, float zero)
        {
            if (value <= full) return 1f;
            if (value >= zero) return 0f;
            return (zero - value) / (zero - full);
        }

        /// <summary>
        /// Group blocks into printed rows by vertical-centre proximity.
        ///
        /// <para>Centre proximity rather than edge gap: the cinematic style's name
        /// box overlaps the first body line, so <c>top − bottom</c> is negative
        /// there and any positive-gap requirement would fail on the exact layout
        /// this classifier exists to handle.</para>
        ///
        /// <para>Bands come back ordered top-to-bottom, and blocks within a band
        /// left-to-right, so the caller can concatenate them in reading order.</para>
        /// </summary>
        internal static List<List<TextBlockInfo>> GroupIntoRowBands(List<TextBlockInfo> blocks)
        {
            var bands = new List<List<TextBlockInfo>>();
            if (blocks == null || blocks.Count == 0) return bands;

            var sorted = new List<TextBlockInfo>(blocks);
            sorted.Sort((a, b) => OcrBlockGeometry.CentreY(a).CompareTo(OcrBlockGeometry.CentreY(b)));

            List<TextBlockInfo> current = null;
            float centreSum = 0f;
            float minHeight = 0f;

            foreach (var block in sorted)
            {
                float centre = OcrBlockGeometry.CentreY(block);
                float height = Math.Max(1f, block.BoundingRect.Height);

                if (current == null)
                {
                    current = new List<TextBlockInfo> { block };
                    centreSum = centre;
                    minHeight = height;
                    continue;
                }

                float bandCentre = centreSum / current.Count;
                float tolerance = RowBandCentreTolerance * Math.Min(minHeight, height);

                if (Math.Abs(centre - bandCentre) <= tolerance)
                {
                    current.Add(block);
                    centreSum += centre;
                    if (height < minHeight) minHeight = height;
                }
                else
                {
                    bands.Add(current);
                    current = new List<TextBlockInfo> { block };
                    centreSum = centre;
                    minHeight = height;
                }
            }

            if (current != null) bands.Add(current);

            foreach (var band in bands)
            {
                if (band.Count > 1)
                    band.Sort((a, b) => a.BoundingRect.Left.CompareTo(b.BoundingRect.Left));
            }

            return bands;
        }

        /// <summary>Horizontal extent of a band: rightmost edge minus leftmost edge.</summary>
        private static float UnionWidth(List<TextBlockInfo> band)
        {
            float left = float.MaxValue, right = float.MinValue;
            foreach (var block in band)
            {
                if (block.BoundingRect.Left < left) left = block.BoundingRect.Left;
                if (block.BoundingRect.Right > right) right = block.BoundingRect.Right;
            }
            return right > left ? right - left : 0f;
        }

        private static int TotalTrimmedLength(List<TextBlockInfo> band)
        {
            int total = 0;
            foreach (var block in band)
            {
                if (!string.IsNullOrEmpty(block.Text)) total += block.Text.Trim().Length;
            }
            return total;
        }

        private static void AddAllAsDialogue(DetectedTextResult result, List<List<TextBlockInfo>> bands)
        {
            foreach (var band in bands)
            {
                foreach (var block in band)
                {
                    block.IsNpcText = false;
                    result.DialogueBlocks.Add(block);
                }
            }
        }
    }
}
