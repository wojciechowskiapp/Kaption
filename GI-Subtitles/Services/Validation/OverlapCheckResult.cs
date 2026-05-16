using System.Windows;

namespace GI_Subtitles.Services.Validation
{
    /// <summary>
    /// Which capture region the overlay overlaps with. "None" is the safe case.
    /// Stored as a stable enum (rather than a string) so call sites can branch
    /// on severity without risking typos against a label.
    /// </summary>
    public enum OverlapRegionKind
    {
        None,
        Dialogue,   // primary OCR region (Config["Region"])
        Secondary,  // optional secondary region (Config["Region2"])
        Answer,     // dialogue-answer region (Config["AnswerRegion"])
    }

    /// <summary>
    /// Outcome of an <see cref="OverlayRegionValidator"/> check.
    ///
    /// All rectangles are in <b>screen physical pixels</b> — the same coordinate
    /// space as the stored <c>Region</c> CSV. Callers that need WPF logical
    /// pixels (e.g. to draw on a transparent overlay Canvas) must divide by the
    /// DPI scale themselves; the validator deliberately keeps one coordinate
    /// space to avoid round-trip rounding when intersecting.
    /// </summary>
    public class OverlapCheckResult
    {
        /// <summary>True if the projected/actual overlay rectangle intersects any region with width &gt; 0 and height &gt; 0.</summary>
        public bool HasOverlap { get; set; }

        /// <summary>The first region that overlaps, in check order Dialogue → Secondary → Answer. <see cref="OverlapRegionKind.None"/> when safe.</summary>
        public OverlapRegionKind Kind { get; set; }

        /// <summary>The region rectangle (screen pixels) that caused the overlap. <see cref="Rect.Empty"/> when safe.</summary>
        public Rect RegionRect { get; set; }

        /// <summary>The overlay rectangle (screen pixels) used for the check.</summary>
        public Rect OverlayRect { get; set; }

        /// <summary>Intersection rectangle (screen pixels) — useful for visualisation (red-fill the overlap on Ctrl+Shift+D).</summary>
        public Rect IntersectionRect { get; set; }

        /// <summary>True if <see cref="OverlayRect"/> is an estimate (layout-engine projection) rather than the live rendered overlay bounds.</summary>
        public bool OverlayWasProjected { get; set; }

        public static OverlapCheckResult Safe(Rect overlayRect, bool projected)
            => new OverlapCheckResult
            {
                HasOverlap = false,
                Kind = OverlapRegionKind.None,
                RegionRect = Rect.Empty,
                OverlayRect = overlayRect,
                IntersectionRect = Rect.Empty,
                OverlayWasProjected = projected,
            };
    }
}
