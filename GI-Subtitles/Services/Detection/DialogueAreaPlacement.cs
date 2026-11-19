using System.Collections.Generic;
using System.Drawing;
using GI_Subtitles.Services.Validation;

namespace GI_Subtitles.Services.Detection
{
    public enum PlacementStatus
    {
        Placed,
        /// <summary>The window is under the size of a game's window: a launcher, a splash or one still opening.</summary>
        Unusable,
        /// <summary>The desktop would cut the game's text: a window hanging off the screen. Never saved as a sliver.</summary>
        OffScreen,
    }

    /// <summary>The block on a window, clipped to the desktop, and the measured text placed the same way.</summary>
    public readonly record struct PlacementResult(PlacementStatus Status, Rectangle Area, Rectangle Text);

    /// <summary>
    /// The manifest's measured dialogue block (games/&lt;id&gt;.json) placed on a game's client area, in screen pixels.
    /// Arithmetic only, so the capture tick may call it. The block is fractions of the 16:9 band centred in the
    /// client (<see cref="GameWindowDetector.CalculateFromRatios"/>), so a move keeps its size and the reader's shape.
    /// </summary>
    public static class DialogueAreaPlacement
    {
        /// <summary>A client area smaller than this is a launcher, a splash or a window still opening.</summary>
        public const int MinClientWidth = 640, MinClientHeight = 360;

        public static bool IsUsable(Rectangle client) => client.Width >= MinClientWidth && client.Height >= MinClientHeight;

        /// <summary>The largest 16:9 rectangle centred in the client, as <see cref="GameWindowDetector.CalculateFromRatios"/> finds it.</summary>
        public static Rectangle Band(Rectangle client)
        {
            double aspect = (double)client.Width / client.Height;
            const double target = 16.0 / 9.0;
            int refX = 0, refY = 0, refW = client.Width, refH = client.Height;
            if (aspect > target + 0.01)
            {
                refW = (int)(client.Height * target);
                refX = (client.Width - refW) / 2;
            }
            else if (aspect < target - 0.01)
            {
                refH = (int)(client.Width / target);
                refY = (client.Height - refH) / 2;
            }
            return new Rectangle(client.X + refX, client.Y + refY, refW, refH);
        }

        private static Rectangle Apply(double x, double y, double w, double h, Rectangle band) =>
            new(band.X + (int)(x * band.Width), band.Y + (int)(y * band.Height), (int)(w * band.Width), (int)(h * band.Height));

        /// <summary>Every monitor's bounds (physical pixels), for <see cref="Place"/>. Asked once per placement, not per tick.</summary>
        public static Rectangle[] MonitorBounds()
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            var bounds = new Rectangle[screens.Length];
            for (int i = 0; i < screens.Length; i++) bounds[i] = screens[i].Bounds;
            return bounds;
        }

        /// <summary>
        /// The block on <paramref name="client"/>, clipped to <paramref name="desktop"/>. Refused for a window too small
        /// to be the game's, and when the clip would cut the game's measured text (a window mostly off the screen).
        /// With <paramref name="monitors"/>, the text must also be on a monitor: the desktop is their bounding box, and
        /// with monitors of different heights it has corners no monitor shows.
        /// </summary>
        public static PlacementResult Place(GameRegionProfile profile, Rectangle client, Rectangle desktop, IReadOnlyList<Rectangle> monitors = null)
        {
            if (profile == null || !IsUsable(client)) return new PlacementResult(PlacementStatus.Unusable, default, default);
            Rectangle band = Band(client);
            Rectangle block = Apply(profile.DialogueXPct, profile.DialogueYPct, profile.DialogueWPct, profile.DialogueHPct, band);
            Rectangle text = profile.DialogueText is RegionFraction t ? Apply(t.X, t.Y, t.W, t.H, band) : block;
            if (!CaptureRegionBounds.TryClamp(block.X, block.Y, block.Width, block.Height,
                    desktop.Left, desktop.Top, desktop.Width, desktop.Height, out int x, out int y, out int w, out int h))
                return new PlacementResult(PlacementStatus.OffScreen, default, text);
            var area = new Rectangle(x, y, w, h);
            // Margin may go; the text may not. Without a measured text the block itself must survive whole.
            return area.Contains(text) && OnMonitors(text, monitors)
                ? new PlacementResult(PlacementStatus.Placed, area, text)
                : new PlacementResult(PlacementStatus.OffScreen, default, text);
        }

        /// <summary>
        /// Every pixel of <paramref name="r"/> is on some monitor (no list: nothing to check). Each monitor is cut out of
        /// what is still uncovered; anything left is a gap no monitor shows. A handful of rectangles, once per placement.
        /// </summary>
        private static bool OnMonitors(Rectangle r, IReadOnlyList<Rectangle> monitors)
        {
            if (monitors == null || monitors.Count == 0) return true;
            var uncovered = new List<Rectangle> { r };
            foreach (Rectangle m in monitors)
            {
                var next = new List<Rectangle>();
                foreach (Rectangle u in uncovered)
                {
                    Rectangle cut = Rectangle.Intersect(u, m);
                    if (cut.IsEmpty)
                    {
                        next.Add(u);
                        continue;
                    }
                    // What of u lies above, below, left and right of the monitor.
                    if (cut.Top > u.Top) next.Add(Rectangle.FromLTRB(u.Left, u.Top, u.Right, cut.Top));
                    if (cut.Bottom < u.Bottom) next.Add(Rectangle.FromLTRB(u.Left, cut.Bottom, u.Right, u.Bottom));
                    if (cut.Left > u.Left) next.Add(Rectangle.FromLTRB(u.Left, cut.Top, cut.Left, cut.Bottom));
                    if (cut.Right < u.Right) next.Add(Rectangle.FromLTRB(cut.Right, cut.Top, u.Right, cut.Bottom));
                }
                uncovered = next;
                if (uncovered.Count == 0) return true;
            }
            return uncovered.Count == 0;
        }
    }
}
