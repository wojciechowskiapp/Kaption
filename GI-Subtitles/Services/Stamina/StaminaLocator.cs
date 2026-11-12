using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GI_Subtitles.Services.Games;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;
using CvSize = OpenCvSharp.Size;

namespace GI_Subtitles.Services.Stamina
{
    /// <summary>The stamina icon found in a strip: its box in the strip's own pixels and how well it matched.</summary>
    public readonly record struct StaminaAnchor(CvRect Icon, double Score, double Scale);

    /// <summary>
    /// Finds the game's stamina icon in the top band of the game picture and cuts out the number beside it. Plain
    /// OpenCV on a small strip: a colour template at a few sizes (one game draws the icon larger on its map than in
    /// its dialogs), then the bright glyphs to the right of it up to the first wide gap, which is the "+" button.
    /// The strip is first brought to the size a 1920x1080 client would give, so the cost is the same at 4K.
    /// Not thread-safe; one sample at a time uses it.
    /// </summary>
    public sealed class StaminaLocator : IDisposable
    {
        /// <summary>The client size the icon templates are drawn for.</summary>
        public const int ReferenceWidth = 1920, ReferenceHeight = 1080;

        /// <summary>LogicalName prefix of the embedded icon templates (Assets/Stamina/&lt;icon&gt;.png).</summary>
        public const string ResourcePrefix = "Kaption.Stamina.";

        /// <summary>The number is far brighter than its pill; a strip flatter than this (0-255) holds no text.</summary>
        private const double MinContrast = 60;
        /// <summary>The digits are at least this share of the icon's height in every measured game (0.40 to 0.82).</summary>
        private const double MinGlyphHeight = 0.25;
        /// <summary>The shortest run that can be a number, in digit heights ("0/200" is about three; a lone "9" is not one).</summary>
        private const double MinRunWidth = 2;
        /// <summary>How much taller than the icon the strip read for the number is, on each side.</summary>
        private const double VerticalSlack = 0.15;

        /// <summary>
        /// The first pass runs on a picture this much smaller. Measured on real frames: at half size the real icons
        /// still score 0.93 or more and nothing else passes 0.81, for about a quarter of the work.
        /// </summary>
        private const double CoarseFactor = 0.5;
        /// <summary>How far under the spec's score a half-size match may be and still get the full-size check.</summary>
        private const double CoarseSlack = 0.06;
        /// <summary>The full-size check looks this many pixels around where the half-size pass pointed.</summary>
        private const int RefineMargin = 3;

        private readonly GameStaminaScreen _spec;
        private readonly Mat[] _templates, _coarse;
        private readonly double[] _scales;

        /// <param name="template">The icon as a 1920x1080 client shows it, BGR; copied.</param>
        public StaminaLocator(GameStaminaScreen spec, Mat template)
        {
            _spec = spec ?? throw new ArgumentNullException(nameof(spec));
            if (template == null || template.Empty()) throw new ArgumentException("An icon template is required.", nameof(template));
            using Mat bgr = ToBgr(template);
            _scales = (spec.Scales ?? Array.Empty<double>()).Where(s => s > 0).Distinct().ToArray();
            if (_scales.Length == 0) _scales = new[] { 1.0 };
            _templates = new Mat[_scales.Length];
            _coarse = new Mat[_scales.Length];
            for (int i = 0; i < _scales.Length; i++)
            {
                _templates[i] = Scaled(bgr, _scales[i]);
                _coarse[i] = Scaled(bgr, _scales[i] * CoarseFactor);
            }
        }

        private static Mat Scaled(Mat src, double factor)
        {
            var size = new CvSize(Math.Max(4, (int)Math.Round(src.Width * factor)), Math.Max(4, (int)Math.Round(src.Height * factor)));
            var dst = new Mat();
            Cv2.Resize(src, dst, size, 0, 0, factor < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
            return dst;
        }

        /// <summary>The locator for a game's spec with its embedded icon; null when the build carries no such icon.</summary>
        public static StaminaLocator ForSpec(GameStaminaScreen spec)
        {
            if (spec == null || string.IsNullOrEmpty(spec.Icon)) return null;
            using Stream stream = typeof(StaminaLocator).Assembly.GetManifestResourceStream(ResourcePrefix + spec.Icon + ".png");
            if (stream == null) return null;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            using Mat template = Cv2.ImDecode(bytes.ToArray(), ImreadModes.Color);
            return template.Empty() ? null : new StaminaLocator(spec, template);
        }

        /// <summary>The part of the game's client area (screen pixels) the icon can be in.</summary>
        public static System.Drawing.Rectangle SearchArea(System.Drawing.Rectangle client, GameStaminaScreen spec)
        {
            if (spec?.Search == null || client.Width <= 0 || client.Height <= 0) return System.Drawing.Rectangle.Empty;
            int x = client.Left + (int)Math.Floor(client.Width * spec.Search.X);
            int y = client.Top + (int)Math.Floor(client.Height * spec.Search.Y);
            int w = Math.Min(client.Right - x, (int)Math.Ceiling(client.Width * spec.Search.W));
            int h = Math.Min(client.Bottom - y, (int)Math.Ceiling(client.Height * spec.Search.H));
            return w > 0 && h > 0 ? new System.Drawing.Rectangle(x, y, w, h) : System.Drawing.Rectangle.Empty;
        }

        /// <summary>How much larger than at 1920x1080 a client of this size draws its interface.</summary>
        public static double InterfaceScale(System.Drawing.Size client) =>
            Math.Min(client.Width / (double)ReferenceWidth, client.Height / (double)ReferenceHeight);

        /// <summary>
        /// The icons in <paramref name="strip"/> (the search area's pixels, BGR or BGRA), best first, at most
        /// <paramref name="max"/>. A game can draw a look-alike beside the real one, so the caller reads each in turn.
        /// </summary>
        public IReadOnlyList<StaminaAnchor> FindIcons(Mat strip, System.Drawing.Size client, int max = 3)
        {
            var found = new List<StaminaAnchor>();
            if (strip == null || strip.Empty() || client.Width <= 0 || client.Height <= 0) return found;
            double ui = InterfaceScale(client);
            if (!(ui > 0)) return found;

            using Mat bgr = ToBgr(strip);
            Mat norm = bgr;
            // Within 2 % of the reference the strip is searched as it is (the sizes tried cover that); what is found is
            // then already in the strip's own pixels.
            bool scaled = Math.Abs(ui - 1) > 0.02;
            double back = scaled ? ui : 1;
            try
            {
                if (scaled)
                {
                    norm = new Mat();
                    Cv2.Resize(bgr, norm, new CvSize(Math.Max(1, (int)Math.Round(bgr.Width / ui)), Math.Max(1, (int)Math.Round(bgr.Height / ui))),
                        0, 0, ui > 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
                }
                using var small = new Mat();
                Cv2.Resize(norm, small, new CvSize(Math.Max(1, (int)Math.Round(norm.Width * CoarseFactor)), Math.Max(1, (int)Math.Round(norm.Height * CoarseFactor))),
                    0, 0, InterpolationFlags.Area);
                var frame = new CvRect(0, 0, norm.Width, norm.Height);
                for (int i = 0; i < _templates.Length; i++)
                {
                    Mat t = _templates[i], c = _coarse[i];
                    if (c.Width > small.Width || c.Height > small.Height || t.Width > norm.Width || t.Height > norm.Height) continue;
                    using var result = new Mat();
                    Cv2.MatchTemplate(small, c, result, TemplateMatchModes.CCoeffNormed);
                    for (int n = 0; n < max; n++)
                    {
                        Cv2.MinMaxLoc(result, out _, out double rough, out _, out Point at);
                        if (!(rough >= _spec.MinIconScore - CoarseSlack) || rough > 1.001) break;
                        // Blank this peak's neighbourhood so the next pass finds another icon, not the same one a pixel over.
                        var around = new CvRect(at.X - c.Width / 2, at.Y - c.Height / 2, c.Width, c.Height) & new CvRect(0, 0, result.Width, result.Height);
                        if (around.Width > 0 && around.Height > 0)
                        {
                            using var hole = new Mat(result, around);
                            hole.SetTo(new Scalar(-1));
                        }

                        // The full-size check, only around where the small picture pointed.
                        var near = new CvRect((int)Math.Round(at.X / CoarseFactor) - RefineMargin, (int)Math.Round(at.Y / CoarseFactor) - RefineMargin,
                            t.Width + RefineMargin * 2, t.Height + RefineMargin * 2) & frame;
                        if (near.Width < t.Width || near.Height < t.Height) continue;
                        using var patch = new Mat(norm, near);
                        using var exact = new Mat();
                        Cv2.MatchTemplate(patch, t, exact, TemplateMatchModes.CCoeffNormed);
                        Cv2.MinMaxLoc(exact, out _, out double score, out _, out Point shift);
                        if (!(score >= _spec.MinIconScore) || score > 1.001) continue;
                        var box = new CvRect((int)Math.Round((near.X + shift.X) * back), (int)Math.Round((near.Y + shift.Y) * back),
                            (int)Math.Round(t.Width * back), (int)Math.Round(t.Height * back));
                        found.Add(new StaminaAnchor(box, score, _scales[i]));
                    }
                }
            }
            finally
            {
                if (scaled) norm.Dispose();
            }

            // The same icon answers at neighbouring sizes: keep the best of each overlapping group.
            var best = new List<StaminaAnchor>();
            foreach (var a in found.OrderByDescending(a => a.Score))
            {
                if (best.Any(b => (b.Icon & a.Icon).Width > 0 && (b.Icon & a.Icon).Height > 0)) continue;
                best.Add(a);
                if (best.Count == max) break;
            }
            return best;
        }

        /// <summary>
        /// The icon at a place it was found before, without the search: only the strip around the remembered box is
        /// looked at, at the remembered size. False sends the caller back to <see cref="FindIcons"/>, so a place
        /// that stopped matching costs one small comparison and changes nothing.
        /// </summary>
        public bool TryConfirmAt(Mat strip, System.Drawing.Size client, StaminaAnchor remembered, out StaminaAnchor anchor)
        {
            anchor = default;
            if (strip == null || strip.Empty()) return false;
            int size = Array.IndexOf(_scales, remembered.Scale);
            double ui = InterfaceScale(client);
            if (size < 0 || !(ui > 0)) return false;
            bool scaled = Math.Abs(ui - 1) > 0.02;
            double back = scaled ? ui : 1;
            int margin = (int)Math.Ceiling(RefineMargin * back);
            var near = new CvRect(remembered.Icon.X - margin, remembered.Icon.Y - margin, remembered.Icon.Width + margin * 2, remembered.Icon.Height + margin * 2)
                & new CvRect(0, 0, strip.Width, strip.Height);
            if (near.Width <= 0 || near.Height <= 0) return false;

            using var cut = new Mat(strip, near);
            using Mat bgr = ToBgr(cut);
            Mat norm = bgr;
            try
            {
                if (scaled)
                {
                    norm = new Mat();
                    Cv2.Resize(bgr, norm, new CvSize(Math.Max(1, (int)Math.Round(bgr.Width / ui)), Math.Max(1, (int)Math.Round(bgr.Height / ui))),
                        0, 0, ui > 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
                }
                Mat t = _templates[size];
                if (norm.Width < t.Width || norm.Height < t.Height) return false;
                using var result = new Mat();
                Cv2.MatchTemplate(norm, t, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double score, out _, out Point shift);
                if (!(score >= _spec.MinIconScore) || score > 1.001) return false;
                anchor = new StaminaAnchor(new CvRect(near.X + (int)Math.Round(shift.X * back), near.Y + (int)Math.Round(shift.Y * back),
                    (int)Math.Round(t.Width * back), (int)Math.Round(t.Height * back)), score, _scales[size]);
                return true;
            }
            finally
            {
                if (scaled) norm.Dispose();
            }
        }

        /// <summary>
        /// The box of the number to the right of <paramref name="anchor"/>, in the strip's pixels: the run of bright
        /// glyphs that starts beside the icon and ends at the first gap wider than the spec allows.
        /// </summary>
        public bool TryCutNumber(Mat strip, StaminaAnchor anchor, out CvRect line)
        {
            line = default;
            if (strip == null || strip.Empty() || _spec.Digits == null) return false;
            int iconH = anchor.Icon.Height;
            // A little taller than the icon: one game's digits end level with the icon's lower edge.
            int slack = (int)Math.Ceiling(iconH * VerticalSlack);
            var window = new CvRect(anchor.Icon.Right, anchor.Icon.Top - slack, (int)Math.Round(iconH * _spec.Digits.Width), iconH + slack * 2)
                & new CvRect(0, 0, strip.Width, strip.Height);
            if (window.Width < iconH || window.Height < iconH / 2) return false;

            using var roi = new Mat(strip, window);
            using Mat bgr = ToBgr(roi);
            using var gray = new Mat();
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.MinMaxLoc(gray, out double darkest, out double brightest);
            if (brightest - darkest < MinContrast) return false;
            using var ink = new Mat();
            Cv2.Threshold(gray, ink, brightest * _spec.Digits.InkLevel, 255, ThresholdTypes.Binary);

            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            int count = Cv2.ConnectedComponentsWithStats(ink, labels, stats, centroids, PixelConnectivity.Connectivity8, MatType.CV_32S);
            var glyphs = new List<CvRect>();
            for (int i = 1; i < count; i++)
            {
                var box = new CvRect(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2), stats.At<int>(i, 3));
                // Tall enough to be a digit. No upper limit on purpose: two digits that touch are one wide blob, and
                // dropping it would leave the rest of the number to be read as a smaller one.
                if (box.Height >= iconH * MinGlyphHeight) glyphs.Add(box);
            }
            if (glyphs.Count == 0) return false;
            glyphs.Sort((a, b) => a.X.CompareTo(b.X));

            int[] heights = glyphs.Select(g => g.Height).OrderBy(h => h).ToArray();
            int textH = heights[heights.Length / 2];
            double maxGap = textH * _spec.Digits.MaxGap;
            CvRect first = glyphs[0];
            // The number starts beside the icon; something further away is another pill's text.
            if (first.X > iconH * _spec.Digits.MaxStart) return false;
            CvRect run = first;
            for (int i = 1; i < glyphs.Count; i++)
            {
                if (glyphs[i].X - run.Right > maxGap) break;
                run |= glyphs[i];
            }
            // "0/200" is the shortest number there is: about three digit heights wide.
            if (run.Width < textH * MinRunWidth) return false;

            int padX = (int)Math.Ceiling(textH * 0.5), padY = (int)Math.Ceiling(textH * 0.4);
            line = new CvRect(window.X + run.X - padX, window.Y + run.Y - padY, run.Width + padX * 2, run.Height + padY * 2)
                & new CvRect(0, 0, strip.Width, strip.Height);
            return line.Width > 0 && line.Height > 0;
        }

        private static Mat ToBgr(Mat src) => src.Channels() switch
        {
            4 => src.CvtColor(ColorConversionCodes.BGRA2BGR),
            1 => src.CvtColor(ColorConversionCodes.GRAY2BGR),
            _ => src.Clone(),
        };

        public void Dispose()
        {
            foreach (Mat t in _templates) t?.Dispose();
            foreach (Mat t in _coarse) t?.Dispose();
        }
    }
}
