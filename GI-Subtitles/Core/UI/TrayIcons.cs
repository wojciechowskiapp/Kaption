using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GI_Subtitles.Core.UI
{
    /// <summary>
    /// What the tray icon says at a glance, in the status chip's colours: grey and quiet when stopped; the brand tile with
    /// a green dot while translating; with an amber dot while it waits for the game or gets ready; grey with a red dot when
    /// something needs attention.
    /// </summary>
    public enum TrayState { Idle, Translating, Busy, Waiting, Problem }

    /// <summary>
    /// The Kaption mark drawn for the tray in each state, at the system's small-icon size so it is sharp at any DPI (16,
    /// 20, 24, 32 px...). Built once per state and size and kept for the process lifetime (HICONs are not freed on purpose:
    /// a handful of them).
    /// </summary>
    public static class TrayIcons
    {
        private static readonly Dictionary<(TrayState, int), Icon> Cache = new();

        // The K of landing/public/favicon.svg on its 32×32 grid.
        private static readonly PointF[] Glyph =
        {
            new(9, 7), new(12, 7), new(12, 14.2f), new(17.6f, 7), new(21.2f, 7), new(15.4f, 15), new(21.8f, 25),
            new(18, 25), new(13.2f, 17.4f), new(11, 20), new(11, 25), new(8, 25), new(8, 7),
        };

        // The status chip's dot colours (Palette.Dark: SuccessColor, WarnColor, ErrorColor), vivid enough at 16 px on a
        // light or a dark taskbar; the tile's grey is the chip's "stopped".
        internal static readonly Color Idle = Color.FromArgb(0x6B, 0x72, 0x80);
        internal static readonly Color BrandFrom = Color.FromArgb(0x25, 0x63, 0xEB), BrandTo = Color.FromArgb(0x7C, 0x3A, 0xED);
        internal static readonly Color Success = Color.FromArgb(0x34, 0xD3, 0x99), Warn = Color.FromArgb(0xF5, 0x9E, 0x0B),
            Error = Color.FromArgb(0xF8, 0x71, 0x71), Ring = Color.FromArgb(0x0B, 0x0D, 0x17);

        /// <summary>The dot's colour for a state; null when the state shows no dot (stopped).</summary>
        public static Color? DotFor(TrayState state) => state switch
        {
            TrayState.Translating => Success,
            TrayState.Busy or TrayState.Waiting => Warn,
            TrayState.Problem => Error,
            _ => null,
        };

        /// <summary>The brand tile while Kaption is on (translating, waiting, getting ready); grey when stopped or stuck.</summary>
        public static bool BrandTile(TrayState state) => state is TrayState.Translating or TrayState.Busy or TrayState.Waiting;

        public static Icon For(TrayState state)
        {
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            lock (Cache)
            {
                if (Cache.TryGetValue((state, size), out var icon)) return icon;
                icon = Draw(state, size);
                Cache[(state, size)] = icon;
                return icon;
            }
        }

        public static Bitmap Render(TrayState state, int size)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float s = size / 32f;
            g.ScaleTransform(s, s);
            using (var body = RoundedRect(new RectangleF(0, 0, 32, 32), 7))
            using (Brush fill = BrandTile(state)
                       ? new LinearGradientBrush(new PointF(0, 0), new PointF(32, 32), BrandFrom, BrandTo)
                       : new SolidBrush(Idle))
                g.FillPath(fill, body);
            using (var glyph = new GraphicsPath())
            using (var ink = new SolidBrush(Color.White))
            {
                glyph.AddPolygon(Glyph);
                g.FillPath(ink, glyph);
            }
            if (DotFor(state) is Color dot)
            {
                // The chip's dot in the corner, ringed in ink so it stands off the tile and off any taskbar.
                var centre = new PointF(24.5f, 24.5f);
                const float radius = 6.5f, ring = 2.2f;
                using (var edge = new SolidBrush(Ring))
                    g.FillEllipse(edge, centre.X - radius - ring, centre.Y - radius - ring, 2 * (radius + ring), 2 * (radius + ring));
                using (var fill = new SolidBrush(dot))
                    g.FillEllipse(fill, centre.X - radius, centre.Y - radius, 2 * radius, 2 * radius);
            }
            return bmp;
        }

        /// <summary>The dot's centre at <paramref name="size"/> px, for tests and checks.</summary>
        public static Point DotCentre(int size) => new((int)Math.Round(24.5 * size / 32.0), (int)Math.Round(24.5 * size / 32.0));

        private static Icon Draw(TrayState state, int size)
        {
            using var bmp = Render(state, size);
            IntPtr h = bmp.GetHicon();
            using var temp = Icon.FromHandle(h);
            var copy = (Icon)temp.Clone();
            DestroyIcon(h);
            return copy;
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
