using System;
using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using GI_Subtitles.Core.Config;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Themes
{
    public enum ThemePreference { Auto, Dark, Light }

    public enum AppTheme { Dark, Light, HighContrast }

    /// <summary>
    /// Owns the app-level semantic palette: Palette.Dark.xaml or Palette.Light.xaml sits right after Tokens.xaml in
    /// Application.Resources and is swapped in place, so every DynamicResource follows without a restart. Auto follows
    /// Windows' app theme live; Windows high-contrast mode wins over everything and maps the keys to system colours.
    /// </summary>
    public static class ThemeService
    {
        public const string ConfigKey = "Appearance.Theme";
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private static ResourceDictionary _dark, _light;
        private static bool _listening;

        public static ThemePreference Preference { get; private set; } = ThemePreference.Auto;
        public static AppTheme Current { get; private set; } = AppTheme.Dark;
        public static bool IsDark => Current != AppTheme.Light;

        /// <summary>Raised on the UI thread after the palette changed.</summary>
        public static event EventHandler ThemeChanged;

        public static void Initialize()
        {
            Preference = Parse(Config.Get<string>(ConfigKey, null));
            Refresh();
            if (_listening) return;
            _listening = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemParameters.StaticPropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SystemParameters.HighContrast)) Refresh();
            };
        }

        public static void SetPreference(ThemePreference preference)
        {
            Preference = preference;
            Config.Set(ConfigKey, preference.ToString());
            Refresh();
        }

        public static ThemePreference Parse(string value) =>
            Enum.TryParse(value, true, out ThemePreference p) && Enum.IsDefined(p) ? p : ThemePreference.Auto;

        /// <summary>Windows without an app-theme value (pre-1809, some editions) gets Kaption's own look: dark.</summary>
        public static AppTheme Resolve(ThemePreference preference, bool? windowsAppsUseLight, bool highContrast)
        {
            if (highContrast) return AppTheme.HighContrast;
            return preference switch
            {
                ThemePreference.Dark => AppTheme.Dark,
                ThemePreference.Light => AppTheme.Light,
                _ => windowsAppsUseLight == true ? AppTheme.Light : AppTheme.Dark,
            };
        }

        public static bool? ReadWindowsAppsUseLightTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("AppsUseLightTheme") is int v ? v != 0 : null;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"ThemeService: reading the Windows app theme failed: {ex.Message}");
                return null;
            }
        }

        public static Uri PaletteUri(bool dark) =>
            new Uri($"pack://application:,,,/Themes/Palette.{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute);

        public static bool IsPalette(ResourceDictionary d) =>
            d?.Source?.OriginalString.Contains("Themes/Palette.", StringComparison.OrdinalIgnoreCase) == true
            || (d != null && d.Contains(HighContrastMarker));

        /// <summary>Puts <paramref name="palette"/> in the palette slot (replacing the old one, else after Tokens).</summary>
        public static void ApplyTo(ResourceDictionary resources, ResourceDictionary palette)
        {
            Collection<ResourceDictionary> merged = resources.MergedDictionaries;
            for (int i = 0; i < merged.Count; i++)
            {
                if (!IsPalette(merged[i])) continue;
                if (!ReferenceEquals(merged[i], palette)) merged[i] = palette;
                return;
            }
            int at = 0;
            for (int i = 0; i < merged.Count; i++)
                if (merged[i].Source?.OriginalString.Contains("Tokens.xaml", StringComparison.OrdinalIgnoreCase) == true) at = i + 1;
            merged.Insert(at, palette);
        }

        /// <summary>Re-resolves the theme from the preference, Windows and high contrast, and applies it.</summary>
        public static void Refresh()
        {
            var app = Application.Current;
            if (app == null) return;
            if (!app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(Refresh));
                return;
            }
            AppTheme next = Resolve(Preference, ReadWindowsAppsUseLightTheme(), SystemParameters.HighContrast);
            ResourceDictionary palette = next switch
            {
                AppTheme.Light => _light ??= new ResourceDictionary { Source = PaletteUri(false) },
                AppTheme.HighContrast => HighContrastPalette(_light ??= new ResourceDictionary { Source = PaletteUri(false) }),
                _ => _dark ??= new ResourceDictionary { Source = PaletteUri(true) },
            };
            bool changed = next != Current;
            try
            {
                ApplyTo(app.Resources, palette);
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"ThemeService: applying the {next} palette failed: {ex}");
                return;
            }
            Current = next;
            if (changed)
            {
                Logger.Log.Info($"Theme: {next} (preference {Preference}).");
                ThemeChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        private const string HighContrastMarker = "Kaption_HighContrastPalette";

        private static ResourceDictionary _highContrast;
        private static string _highContrastColours;

        /// <summary>
        /// The high-contrast palette, built again only when the system colours it maps to change. Windows raises a
        /// preference change for many reasons; re-applying an identical new palette each time made every DynamicResource
        /// in every open window re-resolve (the "palette churn" seen in high contrast).
        /// </summary>
        public static ResourceDictionary HighContrastPalette(ResourceDictionary template)
        {
            string colours = string.Join(",", SystemColors.WindowColor, SystemColors.WindowTextColor, SystemColors.HighlightColor,
                SystemColors.HighlightTextColor, SystemColors.HotTrackColor, SystemColors.GrayTextColor, SystemColors.InfoColor);
            if (_highContrast == null || colours != _highContrastColours)
            {
                _highContrast = BuildHighContrastPalette(template);
                _highContrastColours = colours;
            }
            return _highContrast;
        }

        /// <summary>
        /// Every key of the light palette re-pointed at the matching Windows system colour, so high-contrast themes
        /// (black, white or custom) carry through. Tints and hairlines become the system window/text colours.
        /// </summary>
        public static ResourceDictionary BuildHighContrastPalette(ResourceDictionary template)
        {
            var hc = new ResourceDictionary { [HighContrastMarker] = true };
            foreach (object key in template.Keys)
            {
                if (!(key is string name)) continue;
                System.Windows.Media.Color color = HighContrastColorFor(name);
                if (name.EndsWith("Color", StringComparison.Ordinal)) hc[name] = color;
                else hc[name] = new System.Windows.Media.SolidColorBrush(color);
            }
            return hc;
        }

        public static System.Windows.Media.Color HighContrastColorFor(string key)
        {
            string k = key.Replace("Brush", "").Replace("Color", "");
            if (k is "TextOnAccent") return SystemColors.HighlightTextColor;
            if (k.StartsWith("AccentFill", StringComparison.Ordinal) || k is "Accent" or "AccentHover" or "AccentPressed"
                || k is "FocusRing" or "BorderFocus" || k.StartsWith("RailIndicator", StringComparison.Ordinal))
                return SystemColors.HighlightColor;
            if (k is "AccentText" || k.StartsWith("Error", StringComparison.Ordinal) && !k.EndsWith("Light", StringComparison.Ordinal)
                || k is "Warn" or "Info" or "Success" or "Debug")
                return SystemColors.HotTrackColor;
            if (k.StartsWith("Text", StringComparison.Ordinal) || k is "TooltipText")
                return k is "TextDisabled" ? SystemColors.GrayTextColor : SystemColors.WindowTextColor;
            if (k.Contains("Border", StringComparison.Ordinal) || k is "Hairline" or "Divider" or "ScrollThumb" or "ScrollThumbHover"
                || k is "OverlayBorder" or "ElevationBorder" or "WindowBorder")
                return SystemColors.WindowTextColor;
            if (k is "TooltipBg") return SystemColors.InfoColor;
            if (k is "CaptionHover" or "CaptionCloseHover") return SystemColors.HighlightColor;
            return SystemColors.WindowColor;
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // General carries the app-theme flip; Color/Accessibility carry high-contrast toggles.
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)
                Refresh();
        }
    }
}
