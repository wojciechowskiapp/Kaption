using System;

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
    }
}
