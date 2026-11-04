using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Themes
{
    /// <summary>
    /// Per-game colours as resource keys derived from the GameId: Game&lt;Stem&gt;Brush, Game&lt;Stem&gt;Color,
    /// Game&lt;Stem&gt;GlowColor (Tokens.xaml) and RailIndicator&lt;Stem&gt;Brush (Palette.*.xaml). Genshin, Star Rail
    /// and ZZZ keep the keys those files define; any other listed game gets them from its manifest at startup, so
    /// views can bind every game with DynamicResource and a new game needs no XAML.
    /// </summary>
    public static class GameLook
    {
        /// <summary>The key stem for a GameId: as written, with an all-capitals id title-cased (ZZZ → Zzz).</summary>
        public static string KeyStem(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return string.Empty;
            bool allCaps = gameId.Length > 1 && gameId.All(c => !char.IsLetter(c) || char.IsUpper(c));
            return allCaps ? gameId[0] + gameId.Substring(1).ToLowerInvariant() : gameId;
        }

        public static string AccentBrushKey(string game) => "Game" + StemOf(game) + "Brush";
        public static string AccentColorKey(string game) => "Game" + StemOf(game) + "Color";
        public static string GlowColorKey(string game) => "Game" + StemOf(game) + "GlowColor";
        public static string RailIndicatorKey(string game) => "RailIndicator" + StemOf(game) + "Brush";

        // Any spelling of a listed game finds it; anything else borrows the first game's keys, as before.
        private static string StemOf(string game)
        {
            var registry = GameRegistry.Current;
            return KeyStem((registry.FindLoose(game) ?? registry.Default)?.GameId ?? "Genshin");
        }

        /// <summary>Adds the theme-independent keys (accent colour and brush, glow) each listed game lacks.</summary>
        public static void AddMissingTokens(ResourceDictionary target, GameRegistry registry = null)
        {
            foreach (GameManifest m in (registry ?? GameRegistry.Current).Listed)
            {
                if (!TryParse(m.Look?.Accent, out Color accent)) continue;
                string stem = KeyStem(m.GameId);
                if (!Has(target, $"Game{stem}Color")) target[$"Game{stem}Color"] = accent;
                if (!Has(target, $"Game{stem}Brush")) target[$"Game{stem}Brush"] = Frozen(accent);
                if (!Has(target, $"Game{stem}GlowColor")) target[$"Game{stem}GlowColor"] = TryParse(m.Look.Glow, out Color glow) ? glow : accent;
            }
        }

        /// <summary>Adds each listed game's missing rail indicator to a palette: the accent on dark, accentDark on light.</summary>
        public static ResourceDictionary AddMissingPaletteKeys(ResourceDictionary palette, bool dark, GameRegistry registry = null)
        {
            foreach (GameManifest m in (registry ?? GameRegistry.Current).Listed)
            {
                string key = $"RailIndicator{KeyStem(m.GameId)}Brush";
                if (Has(palette, key)) continue;
                string hex = dark ? m.Look?.Accent : m.Look?.AccentDark ?? m.Look?.Accent;
                if (TryParse(hex, out Color c)) palette[key] = Frozen(c);
            }
            return palette;
        }

        /// <summary>The manifest accent of <paramref name="game"/> (any spelling); the first game's for anything else.</summary>
        public static Color AccentOf(string game)
        {
            var registry = GameRegistry.Current;
            string hex = (registry.FindLoose(game) ?? registry.Default)?.Look?.Accent;
            return TryParse(hex, out Color c) ? c : Color.FromRgb(0xF2, 0xB8, 0x4B);
        }

        private static bool Has(ResourceDictionary d, string key) =>
            d.Contains(key) || d.MergedDictionaries.Any(m => Has(m, key));

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static bool TryParse(string hex, out Color color)
        {
            color = default;
            if (string.IsNullOrEmpty(hex)) return false;
            try
            {
                color = (Color)ColorConverter.ConvertFromString(hex);
                return true;
            }
            catch (Exception ex) when (ex is FormatException || ex is NotSupportedException)
            {
                return false;
            }
        }
    }
}
