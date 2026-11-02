using System;
using System.Windows;
using System.Windows.Media;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>
    /// Default subtitle layout: positions overlay above the dialogue area with safe zone protection.
    /// Prevents overlay from growing unbounded and overlapping game UI (dialogue response options).
    ///
    /// Key behaviors:
    /// - Respects MaxHeight to prevent unbounded growth
    /// - Auto-shrinks font size when text exceeds MaxHeight (optional)
    /// - Centers horizontally on capture region
    /// - Positions vertically using user's Pad offset
    /// - Clamps to screen bounds (prefers moving up over clipping)
    /// </summary>
    public class DefaultSubtitleLayoutEngine : ISubtitleLayoutEngine
    {
        // Overlay border padding (must match MainWindow.xaml Border Padding="16,8,16,12")
        private const double PaddingHorizontal = 32; // 16 left + 16 right
        private const double PaddingVertical = 20;   // 8 top + 12 bottom
        private const double HeaderEstimatedHeight = 24; // approximate header line height + margin
        private const double WindowMargin = 10;      // extra margin around content
        private const double ExtraWidthPadding = 200; // extra width beyond capture region

        /// <summary>
        /// Default max height as fraction of screen height.
        /// Used when user hasn't configured a specific MaxHeight.
        /// </summary>
        private const double DefaultMaxHeightScreenFraction = 0.25;

        public SubtitleLayoutResult CalculateLayout(SubtitleLayoutParams p)
        {
            if (p.CaptureRegion == null || p.CaptureRegion.Length < 4)
                return CreateFallbackResult(p);

            double scale = p.Scale > 0 ? p.Scale : 1.0;

            // Calculate screen-relative positions in WPF logical pixels
            double regionX = p.CaptureRegion[0] / scale;
            double regionY = p.CaptureRegion[1] / scale;
            double regionW = p.CaptureRegion[2] / scale;

            // Determine max width for text (clamped to overlay window width)
            double overlayWidth = regionW + ExtraWidthPadding;
            double windowTextWidth = overlayWidth - PaddingHorizontal;
            double maxTextWidth = p.MaxWidth > 0 ? Math.Min(p.MaxWidth, windowTextWidth) : windowTextWidth;

            // Determine max height
            double maxHeight = p.MaxHeight;
            if (maxHeight <= 0)
            {
                maxHeight = p.ScreenBounds.Height * DefaultMaxHeightScreenFraction;
            }

            // Measure text and auto-shrink if needed
            double fontSize = p.FontSize > 0 ? p.FontSize : 16;
            double minFontSize = p.MinFontSize > 0 ? p.MinFontSize : 12;
            bool textClipped = false;

            var first = Measure(p, fontSize, maxTextWidth);
            double contentHeight = first.Height;
            int maxLines = p.PreferredMaxLines;
            bool TooLong(int lines) => maxLines > 0 && lines > maxLines;

            if ((contentHeight > maxHeight || TooLong(first.Lines)) && p.AutoShrinkText)
            {
                // Smaller in 1px steps: the largest size that fits the height and the preferred line count wins. The
                // line count may cost at most a fifth of the size (three lines at 22 read better than two at 14); past
                // that only the height counts, and text is never cut short to save a line.
                double linesFloor = Math.Max(minFontSize, Math.Floor(fontSize * 0.8));
                double? best = null, heightOnly = null;
                double bestHeight = 0, heightOnlyHeight = 0;
                if (first.Height <= maxHeight) { heightOnly = fontSize; heightOnlyHeight = first.Height; }
                for (double tryFontSize = fontSize - 1; tryFontSize >= minFontSize; tryFontSize -= 1)
                {
                    if (heightOnly.HasValue && tryFontSize < linesFloor) break;
                    var m = Measure(p, tryFontSize, maxTextWidth);
                    if (m.Height > maxHeight) continue;
                    if (!heightOnly.HasValue) { heightOnly = tryFontSize; heightOnlyHeight = m.Height; }
                    if (tryFontSize >= linesFloor && !TooLong(m.Lines)) { best = tryFontSize; bestHeight = m.Height; break; }
                    if (!TooLong(first.Lines)) break; // only the height was the problem, and it is solved
                }

                if (best.HasValue) { fontSize = best.Value; contentHeight = bestHeight; }
                else if (heightOnly.HasValue) { fontSize = heightOnly.Value; contentHeight = heightOnlyHeight; }
                else
                {
                    fontSize = minFontSize;
                    textClipped = true;
                    contentHeight = maxHeight; // Hard clamp
                }
            }
            else if (contentHeight > maxHeight)
            {
                textClipped = true;
                contentHeight = maxHeight;
            }

            // Calculate position
            double baseTop = regionY + p.PadVertical;

            // Center horizontally on screen that contains the capture region
            double screenLeft = p.ScreenBounds.Left;
            double screenWidth = p.ScreenBounds.Width;
            double left = screenLeft + (screenWidth - overlayWidth) / 2 + p.PadHorizontal;

            // Clamp to screen bounds
            double screenTop = p.ScreenBounds.Top;
            double screenBottom = p.ScreenBounds.Bottom;

            double top = baseTop;
            if (top + contentHeight > screenBottom)
            {
                top = screenBottom - contentHeight;
            }
            if (top < screenTop)
            {
                top = screenTop;
            }

            return new SubtitleLayoutResult
            {
                Left = left,
                Top = top,
                Width = overlayWidth,
                Height = contentHeight,
                EffectiveFontSize = fontSize,
                TextClipped = textClipped,
                EffectiveMaxTextWidth = maxTextWidth
            };
        }

        /// <summary>Panel height (with the window margin) and line count at <paramref name="fontSize"/>.</summary>
        private (double Height, int Lines) Measure(SubtitleLayoutParams p, double fontSize, double maxTextWidth)
        {
            if (p.MeasureContent != null)
            {
                var m = p.MeasureContent(fontSize, maxTextWidth);
                return (m.Size.Height + WindowMargin, m.Lines);
            }
            var textSize = MeasureFullContent(p.Text, p.Header, fontSize, maxTextWidth, p.StrokeThickness, p.PixelsPerDip);
            return (textSize.Height + PaddingVertical + WindowMargin, 0);
        }

        private Size MeasureFullContent(string text, string header, double fontSize, double maxWidth, double strokeThickness, double pixelsPerDip)
        {
            if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(header))
                return new Size(0, fontSize + strokeThickness * 2);

            var fontFamily = new FontFamily("Segoe UI");
            double totalHeight = 0;
            double maxContentWidth = 0;

            // Measure header if present
            if (!string.IsNullOrEmpty(header))
            {
                double headerFontSize = Math.Max(fontSize - 2, 10);
                var headerSize = OutlinedTextBlock.MeasureText(
                    header, headerFontSize, fontFamily, FontWeights.Bold,
                    FontStyles.Normal, maxWidth, strokeThickness, pixelsPerDip);
                totalHeight += headerSize.Height + 4; // 4px margin below header
                maxContentWidth = Math.Max(maxContentWidth, headerSize.Width);
            }

            // Measure main text
            if (!string.IsNullOrEmpty(text))
            {
                var textSize = OutlinedTextBlock.MeasureText(
                    text, fontSize, fontFamily, FontWeights.Bold,
                    FontStyles.Normal, maxWidth, strokeThickness, pixelsPerDip);
                totalHeight += textSize.Height;
                maxContentWidth = Math.Max(maxContentWidth, textSize.Width);
            }

            return new Size(maxContentWidth, totalHeight);
        }

        private SubtitleLayoutResult CreateFallbackResult(SubtitleLayoutParams p)
        {
            return new SubtitleLayoutResult
            {
                Left = 100,
                Top = 100,
                Width = 600,
                Height = 80,
                EffectiveFontSize = p.FontSize > 0 ? p.FontSize : 16,
                TextClipped = false,
                EffectiveMaxTextWidth = 550
            };
        }
    }
}
