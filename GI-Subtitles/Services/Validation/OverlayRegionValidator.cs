using System;
using System.Globalization;
using System.Windows;

namespace GI_Subtitles.Services.Validation
{
    /// <summary>
    /// Detects the "overlay inside capture region" misconfiguration — the setup
    /// that causes Kaption's OCR to re-read its own translated output and
    /// translate it again, forever.
    ///
    /// <para>The app already has a reactive defense (<c>MainWindow.MaskOverlayAreas</c>
    /// blacks out overlay pixels before OCR), but masking only stops the literal
    /// feedback loop — it does not prevent OCR from failing (the user sees
    /// "translation broken" without knowing why) and it does not teach the user
    /// how to fix the layout. This validator exists to surface the problem
    /// proactively at every UX point where the user can create or aggravate it:
    /// the OCR start gate, every region-change path, overlay-size changes in
    /// Settings, and the Ctrl+Shift+D diagnostic overlay.</para>
    ///
    /// <para>All math is done in screen physical pixels. The <c>Region</c> CSV
    /// is stored in screen pixels (<c>INotifyIcon.ChooseRegion</c> writes raw
    /// values from <c>Screenshot.Screenshot.GetRegion</c>), and the overlay's
    /// WPF <c>Left/Top/Width/Height</c> in DIPs are multiplied by the system
    /// DPI scale the same way <c>MaskOverlayAreas</c> already does. Tolerance
    /// is strict: a non-empty intersection of any width/height fails the
    /// check — even a few pixels of overlap corrupt OCR output because the
    /// mask replaces them with black.</para>
    /// </summary>
    public static class OverlayRegionValidator
    {
        // Conservative estimate used when the overlay has never rendered (first-run
        // wizard, live drag): we don't know the effective font size or line count
        // yet, but we know the engine caps height at Config["MaxOverlayHeight"] (or
        // 25% of screen when 0), so using that cap for projection is an upper bound
        // on where the overlay CAN appear — i.e. a check that passes the projection
        // also passes at render time.
        private const double ProjectionDefaultMaxHeightFraction = 0.25;

        // Default overlay width when user hasn't set one. Mirrors the Config.Get
        // default in MainWindow.UpdateWindowHeightAndTop (line ~1194).
        private const int DefaultMaxOverlayWidth = 900;

        // Fallback vertical offset if Config.GetPad is not used by the caller.
        // Matches MainWindow's -140 default.
        private const int DefaultPadVertical = -140;

        /// <summary>
        /// Parse a CSV region string ("x,y,w,h" in screen pixels) into a
        /// <see cref="Rect"/>. Returns <see cref="Rect.Empty"/> for any malformed
        /// input: missing entries, non-integer values, zero or negative size.
        /// Safe to call on null / whitespace / legacy placeholder strings.
        /// </summary>
        public static Rect ParseRegion(string[] csv)
        {
            if (csv == null || csv.Length < 4) return Rect.Empty;
            if (!int.TryParse(csv[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)) return Rect.Empty;
            if (!int.TryParse(csv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) return Rect.Empty;
            if (!int.TryParse(csv[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)) return Rect.Empty;
            if (!int.TryParse(csv[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) return Rect.Empty;
            if (w <= 0 || h <= 0) return Rect.Empty;
            return new Rect(x, y, w, h);
        }

        /// <summary>
        /// Intersect two screen-pixel rectangles. Returns
        /// <see cref="Rect.Empty"/> when either input is empty or when they do
        /// not overlap. WPF's <see cref="Rect.Intersect(Rect)"/> mutates the
        /// caller; this wrapper keeps inputs unchanged and guards against the
        /// empty case.
        /// </summary>
        private static Rect Intersect(Rect a, Rect b)
        {
            if (a.IsEmpty || b.IsEmpty) return Rect.Empty;
            var copy = a;
            copy.Intersect(b);
            return copy.IsEmpty ? Rect.Empty : copy;
        }

        /// <summary>
        /// Check whether the projected overlay rectangle overlaps any of the
        /// three regions in priority order (Dialogue → Secondary → Answer).
        /// Stops at the first overlap — the UX only reports one region at a
        /// time to avoid warning spam. All rectangles are in screen pixels.
        /// </summary>
        public static OverlapCheckResult Check(
            Rect overlayRect,
            Rect dialogueRegion,
            Rect secondaryRegion,
            Rect answerRegion,
            bool overlayIsProjected)
        {
            if (overlayRect.IsEmpty || overlayRect.Width <= 0 || overlayRect.Height <= 0)
                return OverlapCheckResult.Safe(overlayRect, overlayIsProjected);

            var probes = new[]
            {
                (kind: OverlapRegionKind.Dialogue, rect: dialogueRegion),
                (kind: OverlapRegionKind.Secondary, rect: secondaryRegion),
                (kind: OverlapRegionKind.Answer, rect: answerRegion),
            };

            foreach (var probe in probes)
            {
                if (probe.rect.IsEmpty) continue;
                var inter = Intersect(overlayRect, probe.rect);
                if (!inter.IsEmpty && inter.Width > 0 && inter.Height > 0)
                {
                    return new OverlapCheckResult
                    {
                        HasOverlap = true,
                        Kind = probe.kind,
                        RegionRect = probe.rect,
                        OverlayRect = overlayRect,
                        IntersectionRect = inter,
                        OverlayWasProjected = overlayIsProjected,
                    };
                }
            }

            return OverlapCheckResult.Safe(overlayRect, overlayIsProjected);
        }

        /// <summary>
        /// Convenience overload that parses the three CSV region arrays and
        /// forwards to the typed <see cref="Check(Rect,Rect,Rect,Rect,bool)"/>.
        /// Any region that fails to parse is treated as "not configured" and
        /// silently skipped.
        /// </summary>
        public static OverlapCheckResult Check(
            Rect overlayRect,
            string[] dialogueRegionCsv,
            string[] secondaryRegionCsv,
            string[] answerRegionCsv,
            bool overlayIsProjected)
        {
            return Check(
                overlayRect,
                ParseRegion(dialogueRegionCsv),
                ParseRegion(secondaryRegionCsv),
                ParseRegion(answerRegionCsv),
                overlayIsProjected);
        }

        // Geometry constants — MUST match DefaultSubtitleLayoutEngine so the
        // projection and the real render agree. If those values ever change,
        // update both places together. Encoded as constants here (instead of
        // calling into the engine) because the projection runs at ~60 Hz
        // during live region drag and WPF text-measurement on a 400-char
        // string would stutter the drag loop.
        private const double EngineExtraWidthPadding = 200;

        /// <summary>
        /// Upper-bound projection of where the subtitle overlay will appear
        /// given a capture region. Mirrors
        /// <see cref="DefaultSubtitleLayoutEngine.CalculateLayout"/>'s
        /// geometry (not its text measurement) so we can run it in the hot
        /// mouse-move path without burning CPU. The returned rectangle is
        /// the LARGEST the overlay can grow to — if the projection says
        /// "safe", the rendered overlay cannot sneak into overlap at
        /// runtime.
        ///
        /// <para>All inputs are in screen physical pixels; the result is in
        /// screen physical pixels too. <paramref name="dpiScale"/> is used
        /// only to scale MaxHeight/MaxWidth which are stored in DIPs.</para>
        /// </summary>
        public static Rect ProjectOverlayRect(
            Rect captureRegionScreenPx,
            Rect screenBoundsScreenPx,
            double dpiScale,
            int maxOverlayHeightPx,
            int maxOverlayWidthPx,
            int padVertical,
            int padHorizontal)
        {
            if (captureRegionScreenPx.IsEmpty || dpiScale <= 0 || screenBoundsScreenPx.IsEmpty)
                return Rect.Empty;

            double scale = dpiScale;

            // Region in logical pixels — matches the engine's `regionX / scale` conversion.
            double regionLogicalY = captureRegionScreenPx.Y / scale;
            double regionLogicalW = captureRegionScreenPx.Width / scale;

            // Screen bounds in logical pixels.
            double screenTopLogical = screenBoundsScreenPx.Y / scale;
            double screenBottomLogical = (screenBoundsScreenPx.Y + screenBoundsScreenPx.Height) / scale;
            double screenLeftLogical = screenBoundsScreenPx.X / scale;
            double screenWidthLogical = screenBoundsScreenPx.Width / scale;
            double screenHeightLogical = screenBoundsScreenPx.Height / scale;

            // Overlay width: region + extra padding (engine uses a fixed 200px extra).
            double overlayWidthLogical = regionLogicalW + EngineExtraWidthPadding;

            // Upper-bound height. Match the engine's 0→25% fallback so the
            // projection matches what will actually render when the user
            // has MaxHeight=auto.
            double maxHeightLogical = maxOverlayHeightPx > 0
                ? maxOverlayHeightPx
                : screenHeightLogical * ProjectionDefaultMaxHeightFraction;

            // Vertical position: engine's `regionY + PadVertical`, clamped to screen.
            double topLogical = regionLogicalY + padVertical;
            if (topLogical + maxHeightLogical > screenBottomLogical)
                topLogical = screenBottomLogical - maxHeightLogical;
            if (topLogical < screenTopLogical)
                topLogical = screenTopLogical;

            // Horizontal position: centred on the screen that contains the region.
            double leftLogical = screenLeftLogical + (screenWidthLogical - overlayWidthLogical) / 2 + padHorizontal;

            // The MaxWidth setting caps the TEXT width inside the overlay,
            // not the overlay chrome itself — for the outer bounding box
            // we use the engine's `regionW + ExtraWidthPadding` formula.
            // MaxOverlayWidth < overlay-chrome width would shrink inner
            // text but not the border, so the outer box size is unchanged.
            // We still bound by the caller-supplied cap to match the engine
            // when `MaxWidth` is set low enough to override the formula.
            if (maxOverlayWidthPx > 0 && maxOverlayWidthPx < overlayWidthLogical)
                overlayWidthLogical = maxOverlayWidthPx;

            // Convert back to screen pixels.
            return new Rect(
                leftLogical * scale,
                topLogical * scale,
                overlayWidthLogical * scale,
                maxHeightLogical * scale);
        }

        /// <summary>
        /// Project overlay with the same defaults MainWindow uses. Wrap
        /// Config reads here so callers in different windows/threads don't
        /// duplicate the defaults and drift apart.
        /// </summary>
        public static Rect ProjectOverlayRectUsingConfig(
            Rect captureRegionScreenPx,
            Rect screenBoundsScreenPx,
            double dpiScale)
        {
            int maxH = Core.Config.Config.Get<int>("MaxOverlayHeight", 0);
            int maxW = Core.Config.Config.Get<int>("MaxOverlayWidth", DefaultMaxOverlayWidth);
            int padV = Core.Config.Config.GetPad(DefaultPadVertical);
            int padH = Core.Config.Config.GetPadHorizontal(0);
            return ProjectOverlayRect(
                captureRegionScreenPx, screenBoundsScreenPx, dpiScale,
                maxH, maxW, padV, padH);
        }
    }
}
