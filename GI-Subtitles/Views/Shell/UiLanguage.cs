using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using GI_Subtitles.Core.Config;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// The interface language, Polish or English. The zh-CN and ja-JP dictionaries were removed on 2026-11-09; a
    /// config that still names one reads as English (ConfigMigrations v5 rewrites it once).
    /// Applies live: the string dictionaries are swapped at app level, so every DynamicResource follows.
    /// </summary>
    public static class UiLanguage
    {
        public const string Polish = "pl-PL", English = "en-US";

        public static event EventHandler Changed;

        /// <summary>Polish when Windows speaks Polish, English otherwise.</summary>
        public static string FromWindows(string windowsDisplayLanguage) =>
            windowsDisplayLanguage != null && windowsDisplayLanguage.StartsWith("pl", StringComparison.OrdinalIgnoreCase) ? Polish : English;

        /// <summary>pl-PL for Polish, en-US for anything else (null, an old zh-CN or ja-JP value, a typo).</summary>
        public static string Normalize(string culture) =>
            string.Equals(culture, Polish, StringComparison.OrdinalIgnoreCase) ? Polish : English;

        public static string Current => Normalize(Config.Get<string>("UILang", null));

        public static void Apply(string culture)
        {
            string tag = Normalize(culture);
            Config.Set("UILang", tag);
            var app = Application.Current;
            if (app == null) return;
            try
            {
                var ci = new CultureInfo(tag);
                CultureInfo.DefaultThreadCurrentCulture = ci;
                CultureInfo.DefaultThreadCurrentUICulture = ci;
                var merged = app.Resources.MergedDictionaries;
                foreach (var d in merged.Where(d => d.Source != null && d.Source.OriginalString.Contains("Resources/Strings")).ToList())
                    merged.Remove(d);
                merged.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Resources/Strings.en-US.xaml", UriKind.Absolute) });
                if (tag == Polish)
                    merged.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Resources/Strings.pl-PL.xaml", UriKind.Absolute) });
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"UiLanguage: switching to {tag} failed: {ex.Message}");
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
