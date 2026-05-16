using System.Drawing;
using System.Windows;

namespace GI_Subtitles.Models
{
    /// <summary>
    /// A single OCR text block with position preserved for Embedded Illusion layout.
    /// Coordinates are in image-space (relative to the captured bitmap, not the screen).
    ///
    /// Used to pass bounding box data from the OCR pipeline to the layout engine,
    /// enabling overlay positioning directly on top of detected game dialogue.
    /// </summary>
    public class TextBlockInfo
    {
        /// <summary>Recognized text content.</summary>
        public string Text { get; set; }

        /// <summary>Four corners of the bounding box, in image-space pixels.</summary>
        public PointF[] BoxPoints { get; set; }

        /// <summary>Axis-aligned bounding rectangle (computed from BoxPoints).</summary>
        public RectangleF BoundingRect { get; set; }

        /// <summary>True = colored/golden NPC name; False = white dialogue text.</summary>
        public bool IsNpcText { get; set; }

        /// <summary>OCR confidence score (0.0 - 1.0).</summary>
        public float Confidence { get; set; }

        /// <summary>
        /// Convert image-space coordinates to screen-space (physical pixels)
        /// by adding the capture region origin offset.
        /// </summary>
        public RectangleF ToScreenSpace(int captureRegionX, int captureRegionY)
        {
            return new RectangleF(
                BoundingRect.X + captureRegionX,
                BoundingRect.Y + captureRegionY,
                BoundingRect.Width,
                BoundingRect.Height);
        }

        /// <summary>
        /// Convert to WPF logical pixels (for window positioning).
        /// WPF logical = screen physical / dpiScale
        /// </summary>
        public Rect ToWpfRect(int captureRegionX, int captureRegionY, double dpiScale)
        {
            var screen = ToScreenSpace(captureRegionX, captureRegionY);
            return new Rect(
                screen.X / dpiScale,
                screen.Y / dpiScale,
                screen.Width / dpiScale,
                screen.Height / dpiScale);
        }
    }
}
