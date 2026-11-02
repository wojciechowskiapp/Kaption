using System;
using System.Collections.Generic;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// The six rail pages and where every older tab id lands. Settings had nine tabs up to 2.4; callers (and any
    /// stored deep link) that still say "Translations" or "Logs" open the page that now holds that content, scrolled
    /// to the section.
    /// </summary>
    public static class SettingsNavigation
    {
        public const string Home = "Dashboard", Game = "Game", Look = "Appearance", Hotkeys = "Hotkeys",
                            Characters = "Characters", Help = "Help";

        /// <summary>Rail order; also the TabItem order in SettingsWindow.xaml.</summary>
        public static readonly IReadOnlyList<string> Pages = new[] { Home, Game, Look, Hotkeys, Characters, Help };

        /// <summary>The nine tabs of 2.4 in their SelectedIndex order.</summary>
        public static readonly IReadOnlyList<string> LegacyTabs = new[]
            { "Dashboard", "Regions", "Appearance", "General", "Hotkeys", "Translations", "Characters", "Community", "Logs" };

        public const string SectionPacks = "Packs", SectionRegion = "Region", SectionAdvanced = "Advanced",
                            SectionCommunity = "Community", SectionLog = "Log";

        private static readonly Dictionary<string, (string Page, string Section)> Map = new(StringComparer.OrdinalIgnoreCase)
        {
            [Home] = (Home, null),
            ["Home"] = (Home, null),
            ["Start"] = (Home, null),
            [Game] = (Game, null),
            ["Translations"] = (Game, SectionPacks),
            ["Regions"] = (Game, SectionRegion),
            ["General"] = (Game, SectionAdvanced),
            [Look] = (Look, null),
            [Hotkeys] = (Hotkeys, null),
            [Characters] = (Characters, null),
            [Help] = (Help, null),
            ["Community"] = (Help, SectionCommunity),
            ["Logs"] = (Help, SectionLog),
        };

        /// <summary>Page and optional section for a page id or an old tab id; null page when unknown.</summary>
        public static (string Page, string Section) Resolve(string id) =>
            !string.IsNullOrWhiteSpace(id) && Map.TryGetValue(id.Trim(), out var hit) ? hit : (null, null);

        /// <summary>An old SelectedIndex (0-8) mapped the same way.</summary>
        public static (string Page, string Section) ResolveLegacyIndex(int index) =>
            index >= 0 && index < LegacyTabs.Count ? Resolve(LegacyTabs[index]) : (null, null);
    }
}
