using System.Windows;
using System.Windows.Media;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// One frame of setup's "how it works" strip, drawn rather than shipped as a picture: a game scene with its dialogue box
    /// (1), the box framed while Kaption reads it (2), the Polish subtitle above the box (3). The scene is the game's, so it
    /// looks the same in both palettes; only the frame's hairline follows the palette. Decorative: the captions carry the
    /// meaning.
    /// </summary>
    public sealed class HowItWorksFrame : FrameworkElement
    {
        public static readonly DependencyProperty StageProperty = DependencyProperty.Register(
            nameof(Stage), typeof(int), typeof(HowItWorksFrame), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>1: the game shows a line; 2: Kaption reads it; 3: the Polish line above it.</summary>
        public int Stage
        {
            get => (int)GetValue(StageProperty);
            set => SetValue(StageProperty, value);
        }

        public const double FrameWidth = 176, FrameHeight = 104;

        public HowItWorksFrame()
        {
            Width = FrameWidth;
            Height = FrameHeight;
            SnapsToDevicePixels = true;
            IsHitTestVisible = false;
            Focusable = false;
        }

        private Color Token(string key, Color fallback) => TryFindResource(key) is Color c ? c : fallback;

        private static Brush Solid(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth > 0 ? ActualWidth : FrameWidth, h = ActualHeight > 0 ? ActualHeight : FrameHeight;
            var frame = new Rect(0, 0, w, h);
            var clip = new RectangleGeometry(frame, 8, 8);
            clip.Freeze();

            dc.PushClip(clip);
            // The scene: dusk sky, a range of hills.
            var sky = new LinearGradientBrush(Token("IllustrationSkyTopColor", Color.FromRgb(0x26, 0x30, 0x4A)),
                Token("IllustrationSkyBottomColor", Color.FromRgb(0x5C, 0x6F, 0x8F)), 90);
            sky.Freeze();
            dc.DrawRectangle(sky, null, frame);
            var hills = new StreamGeometry();
            using (var g = hills.Open())
            {
                g.BeginFigure(new Point(0, h * 0.62), true, true);
                g.BezierTo(new Point(w * 0.18, h * 0.46), new Point(w * 0.32, h * 0.5), new Point(w * 0.46, h * 0.58), true, true);
                g.BezierTo(new Point(w * 0.62, h * 0.44), new Point(w * 0.8, h * 0.48), new Point(w, h * 0.56), true, true);
                g.LineTo(new Point(w, h), true, false);
                g.LineTo(new Point(0, h), true, false);
            }
            hills.Freeze();
            dc.DrawGeometry(Solid(Token("IllustrationHillColor", Color.FromRgb(0x1C, 0x23, 0x33))), null, hills);

            // The game's dialogue box: a name and two lines of its own (English) text.
            var box = new Rect(10, h - 34, w - 20, 26);
            dc.DrawRoundedRectangle(Solid(Token("IllustrationDialogueColor", Color.FromArgb(0xCC, 0x0F, 0x12, 0x20))), null, box, 4, 4);
            var text = Solid(Token("IllustrationTextColor", Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));
            dc.DrawRoundedRectangle(Solid(Token("IllustrationNameColor", Color.FromRgb(0xF2, 0xC4, 0x6D))), null,
                new Rect(box.X + 8, box.Y + 6, 26, 3), 1.5, 1.5);
            dc.DrawRoundedRectangle(text, null, new Rect(box.X + 8, box.Y + 13, box.Width * 0.72, 3), 1.5, 1.5);
            dc.DrawRoundedRectangle(text, null, new Rect(box.X + 8, box.Y + 19, box.Width * 0.46, 3), 1.5, 1.5);

            if (Stage == 2)
            {
                // Kaption reads the box: the area it watches, outlined in the brand's light indigo, with a reading line.
                var reading = Token("Indigo300", Color.FromRgb(0xA5, 0xB4, 0xFC));
                var pen = new Pen(Solid(reading), 1.5) { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) };
                pen.Freeze();
                dc.DrawRoundedRectangle(null, pen, Rect.Inflate(box, 4, 4), 6, 6);
                dc.DrawRectangle(Solid(Color.FromArgb(0x66, reading.R, reading.G, reading.B)), null,
                    new Rect(box.X + box.Width * 0.58, box.Y - 2, 2, box.Height + 4));
            }
            else if (Stage == 3)
            {
                // The Polish subtitle above the box: Kaption's own panel and line, the overlay's tokens.
                var panel = Token("OverlayPanelColor", Colors.Black);
                var line = new Rect(w * 0.16, box.Y - 22, w * 0.68, 16);
                dc.DrawRoundedRectangle(Solid(Color.FromArgb(0xC8, panel.R, panel.G, panel.B)), null, line, 5, 5);
                dc.DrawRoundedRectangle(Solid(Token("OverlayTextColor", Colors.White)), null,
                    new Rect(line.X + 10, line.Y + 6.5, line.Width - 20, 3), 1.5, 1.5);
            }
            dc.Pop();

            // The frame's hairline follows the palette.
            if (TryFindResource("CardBorderBrush") is Brush border)
            {
                var edge = new Pen(border, 1);
                edge.Freeze();
                dc.DrawRoundedRectangle(null, edge, new Rect(0.5, 0.5, w - 1, h - 1), 8, 8);
            }
        }
    }
}
