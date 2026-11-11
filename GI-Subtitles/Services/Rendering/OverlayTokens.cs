using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>
    /// The overlay's colours from Themes/Tokens.xaml. In the app they come from the application resources; where there
    /// is no WPF application (the promo renderer, tests) the dictionary is read once from the assembly and its colours
    /// copied out, so no thread holds on to it. The values in code are only a last resort when neither loads.
    /// </summary>
    public static class OverlayTokens
    {
        private static readonly Lazy<IReadOnlyDictionary<string, Color>> Own = new(LoadColors);

        public static Color Panel => Get("OverlayPanelColor", Colors.Black);
        public static Color Text => Get("OverlayTextColor", Colors.White);
        public static Color Outline => Get("OverlayOutlineColor", Color.FromArgb(0xD9, 0, 0, 0));
        public static Color Shadow => Get("OverlayShadowColor", Color.FromArgb(0x73, 0, 0, 0));
        public static Color NameFill => Get("OverlayNameFillColor", Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));
        public static Color NameText => Get("OverlayNameTextColor", Color.FromRgb(0xF2, 0xF4, 0xF8));
        public static Color Answer => Get("OverlayAnswerColor", Color.FromArgb(0xB0, 0xE8, 0xD8, 0xA0));
        public static Color Separator => Get("OverlaySeparatorColor", Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        public static Color PreviewBand => Get("OverlayPreviewBandColor", Color.FromArgb(0x8C, 0x0B, 0x0D, 0x17));
        public static Color PreviewBandLine => Get("OverlayPreviewBandLineColor", Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        public static Color Get(string key, Color fallback)
        {
            try
            {
                var app = Application.Current;
                if (app != null && app.Dispatcher.CheckAccess() && app.TryFindResource(key) is Color fromApp) return fromApp;
            }
            catch (Exception)
            {
                // Application resources unavailable here: the copy below has the same values.
            }
            return Own.Value.TryGetValue(key, out var c) ? c : fallback;
        }

        private static readonly Lazy<Drawing> OwnMark = new(LoadMark);

        /// <summary>The brand mark (Themes/Tokens.xaml, KaptionMark) as a frozen 32 x 32 drawing; null when it cannot load.</summary>
        public static Drawing Mark => OwnMark.Value;

        /// <summary>The side of <see cref="Mark"/>'s square, in its own units.</summary>
        public const double MarkUnits = 32;

        private static Drawing LoadMark()
        {
            try
            {
                var tokens = new ResourceDictionary { Source = new Uri(OverlayFonts.PackBase, "Themes/Tokens.xaml") };
                if (tokens["KaptionMark"] is DrawingImage image && image.Drawing is Drawing drawing)
                {
                    var copy = drawing.CloneCurrentValue();
                    copy.Freeze();
                    return copy;
                }
            }
            catch (Exception)
            {
                // No mark: the panel is drawn without it.
            }
            return null;
        }

        public static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static SolidColorBrush Brush(Color c, byte alpha) => Brush(Color.FromArgb(alpha, c.R, c.G, c.B));

        private static IReadOnlyDictionary<string, Color> LoadColors()
        {
            var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
            try
            {
                var tokens = new ResourceDictionary { Source = new Uri(OverlayFonts.PackBase, "Themes/Tokens.xaml") };
                foreach (object key in tokens.Keys)
                    if (key is string k && (k.StartsWith("Overlay", StringComparison.Ordinal) || k.StartsWith("Game", StringComparison.Ordinal))
                        && tokens[k] is Color c)
                        colors[k] = c;
            }
            catch (Exception)
            {
                // No tokens: callers fall back to the documented values.
            }
            return colors;
        }
    }
}
