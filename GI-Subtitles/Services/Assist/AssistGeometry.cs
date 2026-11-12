using System;
using System.Drawing;
using System.Globalization;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Assist
{
    /// <summary>
    /// Canvas pixels and a window edge to screen pixels. A game draws its interface for one size (the canvas) and
    /// pins each piece to an edge or a corner of the window, so a HUD place is "this far from that corner", scaled,
    /// and never a fraction of the picture. Pure: no window is asked anything here.
    /// </summary>
    public static class AssistGeometry
    {
        /// <summary>How far a window's shape may be from a named one ("21:9" covers 2560x1080 and 3440x1440) and still be it.</summary>
        public const double AspectTolerance = 0.03;

        /// <summary>How much larger than on its canvas the game draws its interface in this client; 0 when it cannot be told.</summary>
        public static double Scale(GameAssistCanvas canvas, Size client)
        {
            if (canvas == null || canvas.Width <= 0 || canvas.Height <= 0 || client.Width <= 0 || client.Height <= 0) return 0;
            double byWidth = client.Width / (double)canvas.Width, byHeight = client.Height / (double)canvas.Height;
            return canvas.Scale switch
            {
                GameAssistCanvas.ScaleMin => Math.Min(byWidth, byHeight),
                GameAssistCanvas.ScaleHeight => byHeight,
                GameAssistCanvas.ScaleWidth => byWidth,
                _ => 0, // a rule this build does not know is not guessed
            };
        }

        /// <summary>The point of a <paramref name="width"/> x <paramref name="height"/> area an edge name stands for.</summary>
        public static bool TryEdgePoint(string edge, double width, double height, out double x, out double y)
        {
            x = y = 0;
            switch (edge)
            {
                case "topLeft": return true;
                case "top": x = width / 2; return true;
                case "topRight": x = width; return true;
                case "left": y = height / 2; return true;
                case "center": x = width / 2; y = height / 2; return true;
                case "right": x = width; y = height / 2; return true;
                case "bottomLeft": y = height; return true;
                case "bottom": x = width / 2; y = height; return true;
                case "bottomRight": x = width; y = height; return true;
                default: return false;
            }
        }

        /// <summary>Whether the box lies inside its canvas: what the manifest check asks of every fixed place.</summary>
        public static bool FitsCanvas(GameAssistBox box, GameAssistCanvas canvas)
        {
            if (box == null || canvas == null || box.W <= 0 || box.H <= 0) return false;
            if (!TryEdgePoint(box.Edge, canvas.Width, canvas.Height, out double ax, out double ay)) return false;
            double left = ax + box.X, top = ay + box.Y;
            return left >= 0 && top >= 0 && left + box.W <= canvas.Width && top + box.H <= canvas.Height;
        }

        /// <summary>
        /// The box in screen pixels for a game whose client area is <paramref name="client"/>, grown by
        /// <paramref name="slack"/> canvas pixels on every side. False when the scale is unknown or the result leaves
        /// the client (a window smaller than the interface needs).
        /// </summary>
        public static bool TryPlace(GameAssistBox box, GameAssistCanvas canvas, Rectangle client, int slack, out Rectangle placed)
        {
            placed = Rectangle.Empty;
            double scale = Scale(canvas, client.Size);
            if (!(scale > 0) || box == null || box.W <= 0 || box.H <= 0) return false;
            if (!TryEdgePoint(box.Edge, client.Width, client.Height, out double ax, out double ay)) return false;
            int left = client.Left + (int)Math.Round(ax + (box.X - slack) * scale);
            int top = client.Top + (int)Math.Round(ay + (box.Y - slack) * scale);
            int width = Math.Max(1, (int)Math.Round((box.W + slack * 2) * scale));
            int height = Math.Max(1, (int)Math.Round((box.H + slack * 2) * scale));
            var rect = new Rectangle(left, top, width, height);
            if (!client.Contains(rect)) return false;
            placed = rect;
            return true;
        }

        /// <summary>"16:9" as a number; 0 for anything that is not two positive integers around a colon.</summary>
        public static double AspectValue(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            int colon = name.IndexOf(':');
            if (colon <= 0 || colon == name.Length - 1) return 0;
            return int.TryParse(name.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out int w)
                   && int.TryParse(name.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int h)
                   && w > 0 && h > 0
                ? w / (double)h
                : 0;
        }

        /// <summary>
        /// Whether a window of this size has a shape the places were measured on. The refusal rule as data: a shape
        /// nobody measured gets no fixed place, however close the guess would be.
        /// </summary>
        public static bool IsChecked(GameAssistChecked measured, Size client)
        {
            if (measured?.Aspects == null || client.Width <= 0 || client.Height <= 0) return false;
            double shape = client.Width / (double)client.Height;
            foreach (string name in measured.Aspects)
            {
                double value = AspectValue(name);
                if (value > 0 && Math.Abs(shape - value) <= value * AspectTolerance) return true;
            }
            return false;
        }
    }
}
