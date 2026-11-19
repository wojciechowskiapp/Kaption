using System;
using System.Collections.Generic;
using System.Drawing;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>
    /// Where the overlay window goes for a saved vertical offset ("Pad", DIPs from the dialogue area's top).
    /// <para>Before 2.6 the offset placed the panel's top and a longer line grew downward, towards the game's dialogue.
    /// Now the panel grows away from the dialogue: above it, the bottom edge stays put and a second line extends upward;
    /// below it, the top stays. The fixed edge is where a one-line subtitle with a name lands, which is exactly where the
    /// same saved offset put it before, so nobody's position moves with the update.</para>
    /// All values in DIPs; the window is the panel plus <see cref="OverlayStyle.WindowMargin"/> above and below.
    /// </summary>
    public static class OverlayPlacement
    {
        /// <summary>True when a reference panel at this offset ends above the middle of the dialogue area.</summary>
        public static bool GrowsUpward(double regionTop, double regionHeight, double pad, double referenceWindowHeight) =>
            regionTop + pad + referenceWindowHeight <= regionTop + Math.Max(0, regionHeight) / 2;

        /// <summary>The window's top for content of <paramref name="windowHeight"/>.</summary>
        public static double WindowTop(double regionTop, double regionHeight, double pad, double referenceWindowHeight, double windowHeight)
        {
            double referenceTop = regionTop + pad;
            return GrowsUpward(regionTop, regionHeight, pad, referenceWindowHeight)
                ? referenceTop + referenceWindowHeight - windowHeight
                : referenceTop;
        }

        /// <summary>The saved offset that puts a window of <paramref name="windowHeight"/> at <paramref name="windowTop"/> (after a drag).</summary>
        public static int PadFor(double windowTop, double windowHeight, double regionTop, double regionHeight, double referenceWindowHeight)
        {
            double upward = windowTop + windowHeight - referenceWindowHeight - regionTop;
            if (GrowsUpward(regionTop, regionHeight, upward, referenceWindowHeight)) return (int)Math.Round(upward);
            return (int)Math.Round(windowTop - regionTop);
        }

        /// <summary>
        /// The monitor the panel goes on: the one holding the game window's centre while Kaption keeps the frame on
        /// that window (a windowed game straddling two monitors is over the one its middle is on), else the one holding
        /// the frame's top-left corner. -1 when no monitor holds either.
        /// </summary>
        public static int MonitorFor(IReadOnlyList<Rectangle> monitors, Point frameTopLeft, Rectangle? gameClient)
        {
            if (monitors == null) return -1;
            if (gameClient is Rectangle c)
            {
                int onGame = IndexHolding(monitors, new Point(c.X + c.Width / 2, c.Y + c.Height / 2));
                if (onGame >= 0) return onGame;
            }
            return IndexHolding(monitors, frameTopLeft);
        }

        private static int IndexHolding(IReadOnlyList<Rectangle> monitors, Point p)
        {
            for (int i = 0; i < monitors.Count; i++)
                if (monitors[i].Contains(p)) return i;
            return -1;
        }

        /// <summary>
        /// The x the panel is centred on, in DIPs: the game window's centre when it is on this monitor, else the
        /// monitor's own centre. Saving a drag and applying the saved offset must both use this one value.
        /// </summary>
        public static double CentreDip(Rectangle monitor, double scale, Rectangle? gameClient)
        {
            if (gameClient is Rectangle c)
            {
                int centre = c.X + c.Width / 2;
                if (centre >= monitor.Left && centre < monitor.Right) return centre / scale;
            }
            return monitor.Left / scale + monitor.Width / scale / 2;
        }

        /// <summary>The window's left edge for a panel of <paramref name="width"/> centred on <paramref name="centre"/> and moved by the saved offset.</summary>
        public static double WindowLeft(double centre, double width, int padHorizontal) => centre - width / 2 + padHorizontal;

        /// <summary>The saved horizontal offset that puts a window of <paramref name="width"/> at <paramref name="left"/> (after a drag).</summary>
        public static int HorizontalPadFor(double left, double centre, double width) => (int)Math.Round(left - (centre - width / 2));
    }
}
