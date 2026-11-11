using System;
using System.Windows;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>
    /// Where the optional Kaption mark sits on the subtitle panel: a small K at the end of the band, in the panel's right
    /// padding and level with the last line, so it is never over the text. Pure geometry in DIPs; the panel draws it.
    /// </summary>
    public static class KaptionMarkLayout
    {
        /// <summary>The mark's side: 14 DIPs under small subtitles, 16 under large ones.</summary>
        public const double MinSide = 14, MaxSide = 16;
        /// <summary>Side per DIP of subtitle size: the default size 22 gives a 15 DIP mark.</summary>
        public const double SidePerSize = 0.66;
        /// <summary>Clear space on each side of the mark inside its column.</summary>
        public const double Gap = 4;
        /// <summary>A line's height over its em size, close enough for every face the overlay offers.</summary>
        public const double LineFactor = 1.33;
        /// <summary>The mark is quieter than the text it sits beside.</summary>
        public const double Opacity = 0.9;

        public static double Side(int subtitleSize) => Math.Clamp(Math.Round(subtitleSize * SidePerSize), MinSide, MaxSide);

        /// <summary>The side padding the panel needs so the mark has its own column beside the text.</summary>
        public static double Column(int subtitleSize) => Side(subtitleSize) + 2 * Gap;

        /// <summary>
        /// The mark's square inside <paramref name="panel"/> (the panel as drawn): centred in the right padding, level
        /// with the last line of text of em size <paramref name="textSize"/>, kept inside the panel. Empty when the
        /// panel has no room for it.
        /// </summary>
        public static Rect Box(Rect panel, int subtitleSize, Thickness padding, double textSize)
        {
            double side = Side(subtitleSize);
            if (panel.IsEmpty || padding.Right < side || panel.Width < padding.Left + padding.Right || panel.Height < side + 2)
                return Rect.Empty;
            double x = panel.Right - padding.Right + (padding.Right - side) / 2;
            double lastLineCentre = panel.Bottom - padding.Bottom - Math.Max(1, textSize) * LineFactor / 2;
            double y = Math.Clamp(lastLineCentre - side / 2, panel.Top + 1, panel.Bottom - side - 1);
            return new Rect(Math.Round(x), Math.Round(y), side, side);
        }
    }
}
