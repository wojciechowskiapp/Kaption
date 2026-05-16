using System.Collections.Generic;
using System.Windows;
using GI_Subtitles.Models;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>
    /// Parameters for subtitle layout calculation.
    /// </summary>
    public class SubtitleLayoutParams
    {
        /// <summary>Main dialogue text to display.</summary>
        public string Text { get; set; }

        /// <summary>NPC header text (may be empty).</summary>
        public string Header { get; set; }

        /// <summary>Requested font size in pixels.</summary>
        public double FontSize { get; set; }

        /// <summary>Capture region in screen pixels: [x, y, width, height].</summary>
        public int[] CaptureRegion { get; set; }

        /// <summary>DPI scale factor (e.g. 1.0 for 96 DPI, 1.5 for 144 DPI).</summary>
        public double Scale { get; set; }

        /// <summary>User-configured vertical offset (Pad).</summary>
        public int PadVertical { get; set; }

        /// <summary>User-configured horizontal offset.</summary>
        public int PadHorizontal { get; set; }

        /// <summary>Target screen bounds in WPF logical pixels.</summary>
        public Rect ScreenBounds { get; set; }

        /// <summary>Maximum allowed overlay height in logical pixels (0 = auto-calculate).</summary>
        public double MaxHeight { get; set; }

        /// <summary>Maximum allowed text width in logical pixels (0 = auto-calculate from region).</summary>
        public double MaxWidth { get; set; }

        /// <summary>Whether to auto-shrink font size to fit within MaxHeight.</summary>
        public bool AutoShrinkText { get; set; } = true;

        /// <summary>Minimum font size when auto-shrinking.</summary>
        public double MinFontSize { get; set; } = 12.0;

        /// <summary>Stroke thickness for text measurement (affects sizing).</summary>
        public double StrokeThickness { get; set; } = 2.0;

        /// <summary>Pixels per DIP for text measurement.</summary>
        public double PixelsPerDip { get; set; } = 1.0;

        /// <summary>
        /// Detected text blocks with positions from OCR.
        /// </summary>
        public List<TextBlockInfo> DetectedBlocks { get; set; }
    }

    /// <summary>
    /// Result of subtitle layout calculation.
    /// </summary>
    public class SubtitleLayoutResult
    {
        /// <summary>Window left position in logical pixels.</summary>
        public double Left { get; set; }

        /// <summary>Window top position in logical pixels.</summary>
        public double Top { get; set; }

        /// <summary>Window width in logical pixels.</summary>
        public double Width { get; set; }

        /// <summary>Window height in logical pixels.</summary>
        public double Height { get; set; }

        /// <summary>Effective font size (may be reduced from requested if auto-shrink is on).</summary>
        public double EffectiveFontSize { get; set; }

        /// <summary>Whether text was clipped due to exceeding MaxHeight even after shrinking.</summary>
        public bool TextClipped { get; set; }

        /// <summary>Effective max text width for the OutlinedTextBlock.</summary>
        public double EffectiveMaxTextWidth { get; set; }
    }

    /// <summary>
    /// Interface for subtitle layout calculation engines.
    /// Implementations define how subtitle overlays are positioned and sized.
    ///
    /// Current: DefaultSubtitleLayoutEngine (above dialogue, with safe zone)
    /// Future:  EmbeddedIllusionLayoutEngine (on top of dialogue, covering original text)
    /// </summary>
    public interface ISubtitleLayoutEngine
    {
        /// <summary>
        /// Calculate the optimal position, size, and font size for the subtitle overlay.
        /// </summary>
        SubtitleLayoutResult CalculateLayout(SubtitleLayoutParams parameters);
    }
}
