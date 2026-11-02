using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Element colours keyed by the English element name. Mid-tones that read as solid fills on both the dark
    /// popup and the light Settings page; text on a fill picks dark or white by contrast.
    /// </summary>
    public static class ElementPalette
    {
        private static readonly Color Neutral = Color.FromRgb(0x9C, 0xA3, 0xAF);
        private static readonly Color DarkInk = Color.FromRgb(0x15, 0x16, 0x1B);

        // Order = the games' own element order, used to sort the filter.
        private static readonly (string Name, Color Hue)[] Shared =
        {
            ("Pyro", Rgb(0xEC, 0x6A, 0x3F)), ("Hydro", Rgb(0x3E, 0x92, 0xE6)), ("Anemo", Rgb(0x35, 0xB8, 0x9F)),
            ("Electro", Rgb(0xA4, 0x6B, 0xD8)), ("Dendro", Rgb(0x86, 0xB5, 0x32)), ("Cryo", Rgb(0x6C, 0xC4, 0xDE)),
            ("Geo", Rgb(0xDD, 0xA7, 0x33)),
            ("Physical", Rgb(0xA3, 0xAB, 0xB5)), ("Fire", Rgb(0xEC, 0x6A, 0x3F)), ("Ice", Rgb(0x6C, 0xC4, 0xDE)),
            ("Lightning", Rgb(0xA4, 0x6B, 0xD8)), ("Wind", Rgb(0x35, 0xB8, 0x9F)), ("Quantum", Rgb(0x74, 0x66, 0xE0)),
            ("Imaginary", Rgb(0xE6, 0xC5, 0x40)),
            ("Electric", Rgb(0x3E, 0x9B, 0xF0)), ("Ether", Rgb(0xE8, 0x55, 0x8E)), ("Frost", Rgb(0x9A, 0xD8, 0xEC)),
            ("Auric Ink", Rgb(0xC9, 0xA1, 0x4A)), ("Honed Edge", Rgb(0xB8, 0xBE, 0xC8)), ("Lumiflux", Rgb(0xD9, 0x8A, 0xE8)),
        };

        // ZZZ draws Physical in yellow, not Star Rail's grey.
        private static readonly Dictionary<string, Color> ZzzHues = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            ["Physical"] = Rgb(0xE6, 0xB8, 0x3A),
        };

        private static readonly Dictionary<string, (int Order, Color Hue)> ByName = Index();

        public static Color For(string game, string element)
        {
            if (string.IsNullOrWhiteSpace(element)) return Neutral;
            element = Canon(element);
            if (string.Equals(game, "zzz", StringComparison.OrdinalIgnoreCase) && ZzzHues.TryGetValue(element, out var z)) return z;
            return ByName.TryGetValue(element, out var e) ? e.Hue : Neutral;
        }

        /// <summary>Sort position in the game's own element order; unknown elements go last.</summary>
        public static int Order(string element) =>
            element != null && ByName.TryGetValue(Canon(element), out var e) ? e.Order : int.MaxValue;

        /// <summary>The game's own text spells "Auric Ink" with a no-break space; the hue tables are keyed with plain spaces.</summary>
        private static string Canon(string element) => element.Replace(' ', ' ').Trim();

        /// <summary>Dark or white text on <paramref name="fill"/>, whichever contrasts more.</summary>
        public static Color InkOn(Color fill)
        {
            double l = Luminance(fill);
            double onDark = (l + 0.05) / (Luminance(DarkInk) + 0.05);
            double onWhite = 1.05 / (l + 0.05);
            return onDark >= onWhite ? DarkInk : Colors.White;
        }

        /// <summary>The hue darkened for a light surface or lightened for a dark one, for text and line icons.</summary>
        public static Color Readable(Color hue, bool darkSurface)
        {
            Color target = darkSurface ? Colors.White : Color.FromRgb(0x11, 0x18, 0x27);
            double mix = darkSurface ? 0.25 : 0.35;
            return Color.FromRgb(
                (byte)(hue.R + (target.R - hue.R) * mix),
                (byte)(hue.G + (target.G - hue.G) * mix),
                (byte)(hue.B + (target.B - hue.B) * mix));
        }

        public static SolidColorBrush Brush(Color c, byte alpha = 0xFF)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }

        private static Dictionary<string, (int, Color)> Index()
        {
            var d = new Dictionary<string, (int, Color)>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Shared.Length; i++)
                if (!d.ContainsKey(Shared[i].Name)) d[Shared[i].Name] = (i, Shared[i].Hue);
            return d;
        }

        private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

        private static double Luminance(Color c)
        {
            double Channel(byte v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
    }

    /// <summary>
    /// Line glyphs per skill kind on a 16×16 grid, shown when the game data names no skill icon (ZZZ today).
    /// Our own shapes, no game artwork.
    /// </summary>
    public static class SkillGlyphs
    {
        private static readonly Dictionary<string, Geometry> Glyphs = new Dictionary<string, Geometry>(StringComparer.OrdinalIgnoreCase)
        {
            ["basic"] = G("M3,13 L11.5,4.5 M9.5,2.5 L13.5,6.5 M2.5,10.5 L5.5,13.5 M11.5,4.5 L13.5,2.5"),
            ["skill"] = G("M8,1.5 V4.5 M8,11.5 V14.5 M1.5,8 H4.5 M11.5,8 H14.5 M3.4,3.4 L5.5,5.5 M10.5,10.5 L12.6,12.6 M12.6,3.4 L10.5,5.5 M5.5,10.5 L3.4,12.6"),
            ["ultimate"] = G("M8,1.5 L9.9,6.1 L14.5,8 L9.9,9.9 L8,14.5 L6.1,9.9 L1.5,8 L6.1,6.1 Z"),
            ["passive"] = G("M3.5,9 L8,4.5 L12.5,9 M3.5,13 L8,8.5 L12.5,13"),
            ["talent"] = G("M8,1.5 L14.5,8 L8,14.5 L1.5,8 Z M9.5,8 A1.5,1.5 0 1 1 6.5,8 A1.5,1.5 0 1 1 9.5,8 Z"),
            ["technique"] = G("M13,8 A5,5 0 1 1 3,8 A5,5 0 1 1 13,8 Z M8,1.5 V4.5 M8,11.5 V14.5 M1.5,8 H4.5 M11.5,8 H14.5"),
            ["dodge"] = G("M2,5 H8 M2,8 H10 M2,11 H8 M10.5,4.5 L14,8 L10.5,11.5"),
            ["assist"] = G("M3,5.5 H12 L9.5,3 M13,10.5 H4 L6.5,13"),
            ["chain"] = G("M7,9 L9,7 M6.2,6 L4.4,7.8 A2.4,2.4 0 0 0 7.8,11.2 L9.6,9.4 M9.8,10 L11.6,8.2 A2.4,2.4 0 0 0 8.2,4.8 L6.4,6.6"),
            ["special"] = G("M9.5,1.5 L3.5,9 H8 L6.5,14.5 L12.5,7 H8 Z"),
            ["core"] = G("M8,1.5 L13.6,4.75 V11.25 L8,14.5 L2.4,11.25 V4.75 Z M9.5,8 A1.5,1.5 0 1 1 6.5,8 A1.5,1.5 0 1 1 9.5,8 Z"),
        };

        private static readonly Geometry Unknown = G("M4,4 H12 V12 H4 Z");

        public static Geometry For(string kind) =>
            !string.IsNullOrEmpty(kind) && Glyphs.TryGetValue(kind, out var g) ? g : Unknown;

        private static Geometry G(string data)
        {
            var g = Geometry.Parse(data);
            g.Freeze();
            return g;
        }
    }

    /// <summary>Per-game accents from kaption.one (landing/src/data/games.ts), used only on game items.</summary>
    public static class GameAccents
    {
        private static readonly Color Genshin = Color.FromRgb(0xF2, 0xB8, 0x4B);
        private static readonly Color StarRail = Color.FromRgb(0x6F, 0xD6, 0xE8);
        private static readonly Color Zzz = Color.FromRgb(0xC9, 0xCE, 0xDC);

        /// <param name="game">Profile id ("StarRail") or wire id ("starrail"); unknown gives Genshin's gold.</param>
        public static Color For(string game)
        {
            switch ((game ?? "").Trim().ToLowerInvariant())
            {
                case "starrail":
                case "hsr":
                    return StarRail;
                case "zzz":
                case "zenless":
                case "zenlesszonezero":
                    return Zzz;
                default:
                    return Genshin;
            }
        }
    }

    /// <summary>Line icons for gear slots, drawn on a 16×16 grid. Our own shapes, no game artwork.</summary>
    public static class SlotIcons
    {
        private static readonly Dictionary<string, Geometry> Icons = new Dictionary<string, Geometry>(StringComparer.OrdinalIgnoreCase)
        {
            ["sands"] = G("M4,2.5 H12 M4,13.5 H12 M5,2.5 C5,6.2 8,6.6 8,8 C8,9.4 5,9.8 5,13.5 M11,2.5 C11,6.2 8,6.6 8,8 C8,9.4 11,9.8 11,13.5 M6.4,12.4 H9.6"),
            ["goblet"] = G("M4.5,2.5 H11.5 C11.5,6.6 10,8.6 8,8.6 C6,8.6 4.5,6.6 4.5,2.5 Z M8,8.6 V12.8 M5.6,13.5 H10.4"),
            ["circlet"] = G("M2.6,12 L3.6,5.6 L6.2,8.4 L8,4 L9.8,8.4 L12.4,5.6 L13.4,12 Z M5.2,12 H10.8"),
            ["body"] = G("M5.2,2.5 L2.8,4.2 L3.6,8.2 L5,8.6 V13.5 H11 V8.6 L12.4,8.2 L13.2,4.2 L10.8,2.5 C10,3.9 6,3.9 5.2,2.5 Z"),
            ["feet"] = G("M5.5,2.5 H9 V9.2 L12.4,10.6 C13.1,10.9 13.5,11.5 13.5,12.2 V13.5 H4.5 V12.4 C4.9,11 5.5,10 5.5,8.6 Z"),
            ["sphere"] = G("M12,8 A4,4 0 1 1 4,8 A4,4 0 1 1 12,8 Z M14.58,5.61 A7,2.2 -20 1 1 1.42,10.39 A7,2.2 -20 1 1 14.58,5.61 Z"),
            ["rope"] = G("M1.8,8 A3.6,2.5 0 1 0 9,8 A3.6,2.5 0 1 0 1.8,8 Z M7,8 A3.6,2.5 0 1 0 14.2,8 A3.6,2.5 0 1 0 7,8 Z"),
            ["disc"] = G("M14.2,8 A6.2,6.2 0 1 1 1.8,8 A6.2,6.2 0 1 1 14.2,8 Z M9.7,8 A1.7,1.7 0 1 1 6.3,8 A1.7,1.7 0 1 1 9.7,8 Z M4.4,5.6 A4.3,4.3 0 0 1 7.6,3.7"),
        };

        private static readonly Geometry Unknown = G("M3.5,3.5 H12.5 V12.5 H3.5 Z");

        public static Geometry For(string slot)
        {
            if (string.IsNullOrEmpty(slot)) return Unknown;
            if (slot.StartsWith("disc", StringComparison.OrdinalIgnoreCase)) return Icons["disc"];
            return Icons.TryGetValue(slot, out var g) ? g : Unknown;
        }

        private static Geometry G(string data)
        {
            var g = Geometry.Parse(data);
            g.Freeze();
            return g;
        }
    }
}
