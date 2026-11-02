using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using GI_Subtitles.Core.Config;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.QuickTranslate
{
    /// <summary>
    /// The last area picked with Ctrl+Q, in physical pixels, so "translate the last area again"
    /// survives restarts. An area no longer fully on screen is treated as missing.
    /// </summary>
    public static class QuickTranslateArea
    {
        public const string ConfigKey = "QuickTranslateLastArea";
        public const int MinSide = 8;

        public static string Format(Rectangle r) =>
            string.Join(",", new[] { r.X, r.Y, r.Width, r.Height }
                .Select(v => v.ToString(CultureInfo.InvariantCulture)));

        public static bool TryParse(string s, Rectangle screenBounds, out Rectangle area)
        {
            area = Rectangle.Empty;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string[] parts = s.Split(',');
            if (parts.Length != 4) return false;

            var v = new int[4];
            for (int i = 0; i < 4; i++)
            {
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]))
                    return false;
            }
            if (v[2] < MinSide || v[3] < MinSide) return false;

            var r = new Rectangle(v[0], v[1], v[2], v[3]);
            if (!screenBounds.Contains(r)) return false;
            area = r;
            return true;
        }

        /// <summary>
        /// The picker's physical-pixel rect, rounded. False for a cancel (<see cref="System.Windows.Rect.Empty"/>),
        /// a non-finite value, or a side under <see cref="MinSide"/> once rounded.
        /// </summary>
        public static bool TryFromPicker(System.Windows.Rect picked, out Rectangle selection)
        {
            selection = Rectangle.Empty;
            if (picked.IsEmpty
                || !double.IsFinite(picked.X) || !double.IsFinite(picked.Y)
                || !double.IsFinite(picked.Width) || !double.IsFinite(picked.Height))
                return false;

            var r = new Rectangle(
                (int)Math.Round(picked.X), (int)Math.Round(picked.Y),
                (int)Math.Round(picked.Width), (int)Math.Round(picked.Height));
            if (r.Width < MinSide || r.Height < MinSide) return false;
            selection = r;
            return true;
        }

        public static void Save(Rectangle area)
        {
            try { Config.Set(ConfigKey, Format(area)); }
            catch (Exception ex) { Logger.Log.Warn($"QuickTranslateArea save: {ex.Message}"); }
        }

        public static bool TryLoad(out Rectangle area)
        {
            area = Rectangle.Empty;
            try
            {
                return TryParse(Config.Get(ConfigKey, ""), System.Windows.Forms.SystemInformation.VirtualScreen, out area);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"QuickTranslateArea load: {ex.Message}");
                return false;
            }
        }
    }
}
