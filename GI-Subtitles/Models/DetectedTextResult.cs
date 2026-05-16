using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace GI_Subtitles.Models
{
    /// <summary>
    /// Result of text block classification: NPC name blocks vs dialogue blocks,
    /// with position data preserved for Embedded Illusion overlay positioning.
    ///
    /// Provides backward-compatible flat string properties (NpcName, DialogueText)
    /// plus position-aware block lists for the layout engine.
    /// </summary>
    public class DetectedTextResult
    {
        /// <summary>Text blocks classified as colored NPC name/title.</summary>
        public List<TextBlockInfo> NpcBlocks { get; set; } = new List<TextBlockInfo>();

        /// <summary>Text blocks classified as white dialogue text.</summary>
        public List<TextBlockInfo> DialogueBlocks { get; set; } = new List<TextBlockInfo>();

        /// <summary>Flat NPC name string (backward compatibility with existing pipeline).</summary>
        public string NpcName => string.Join(" ", NpcBlocks.Select(b => b.Text));

        /// <summary>Flat dialogue string (backward compatibility with existing pipeline).</summary>
        public string DialogueText => string.Join("\n", DialogueBlocks.Select(b => b.Text));

        /// <summary>
        /// Union bounding rectangle of all dialogue blocks in image-space pixels.
        /// Used by EmbeddedIllusionLayoutEngine to position the overlay.
        /// Returns RectangleF.Empty if no dialogue blocks exist.
        /// </summary>
        public RectangleF DialogueBoundsImageSpace
        {
            get
            {
                if (DialogueBlocks.Count == 0) return RectangleF.Empty;

                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;

                foreach (var block in DialogueBlocks)
                {
                    var r = block.BoundingRect;
                    if (r.Left < minX) minX = r.Left;
                    if (r.Top < minY) minY = r.Top;
                    if (r.Right > maxX) maxX = r.Right;
                    if (r.Bottom > maxY) maxY = r.Bottom;
                }

                if (minX >= maxX || minY >= maxY) return RectangleF.Empty;
                return new RectangleF(minX, minY, maxX - minX, maxY - minY);
            }
        }

        /// <summary>
        /// Union bounding rectangle of all blocks (NPC + dialogue) in image-space.
        /// </summary>
        public RectangleF AllBoundsImageSpace
        {
            get
            {
                var allBlocks = NpcBlocks.Concat(DialogueBlocks).ToList();
                if (allBlocks.Count == 0) return RectangleF.Empty;

                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;

                foreach (var block in allBlocks)
                {
                    var r = block.BoundingRect;
                    if (r.Left < minX) minX = r.Left;
                    if (r.Top < minY) minY = r.Top;
                    if (r.Right > maxX) maxX = r.Right;
                    if (r.Bottom > maxY) maxY = r.Bottom;
                }

                if (minX >= maxX || minY >= maxY) return RectangleF.Empty;
                return new RectangleF(minX, minY, maxX - minX, maxY - minY);
            }
        }
    }
}
