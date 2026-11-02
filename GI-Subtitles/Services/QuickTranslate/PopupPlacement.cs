using System;
using System.Windows;

namespace GI_Subtitles.Services.QuickTranslate
{
    /// <summary>
    /// Where to put the popup relative to the area the player selected. Pure geometry: all
    /// inputs share one unit (physical pixels), so the caller handles DPI.
    /// </summary>
    public static class PopupPlacement
    {
        public static Rect Compute(Rect selection, Size popup, Rect workArea, double gap = 8)
        {
            double w = Math.Min(popup.Width, workArea.Width);
            double h = popup.Height;
            double centredLeft = ClampX(selection.Left + selection.Width / 2 - w / 2, w, workArea);

            // 1. Below the selection.
            double below = selection.Bottom + gap;
            if (below + h <= workArea.Bottom)
                return new Rect(centredLeft, below, w, h);

            // 2. Above it.
            double above = selection.Top - gap - h;
            if (above >= workArea.Top)
                return new Rect(centredLeft, above, w, h);

            // 3. Beside it, on the side with more room.
            double top = ClampY(selection.Top, h, workArea);
            double roomRight = workArea.Right - (selection.Right + gap);
            double roomLeft = (selection.Left - gap) - workArea.Left;
            if (roomRight >= w && roomRight >= roomLeft)
                return new Rect(selection.Right + gap, top, w, h);
            if (roomLeft >= w)
                return new Rect(selection.Left - gap - w, top, w, h);

            // 4. No free side: overlap the selection but stay on screen.
            return new Rect(centredLeft, ClampY(below, h, workArea), w, h);
        }

        private static double ClampX(double left, double w, Rect wa) =>
            Math.Max(wa.Left, Math.Min(left, wa.Right - w));

        // Taller-than-screen content pins to the top; the window scrolls it.
        private static double ClampY(double top, double h, Rect wa) =>
            Math.Max(wa.Top, Math.Min(top, wa.Bottom - h));
    }
}
