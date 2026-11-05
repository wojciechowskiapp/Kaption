using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Services.Guess;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// The Start page's "Nowość w Kaption" card and the ranking strip, and the Help page's guess row, filled through the
    /// window's own static code, dark and light, Polish and English. Output: %TEMP%\kaption-render\guess-*.png.
    /// </summary>
    [TestClass]
    public class GuessCardsRenderTests
    {
        [TestMethod]
        public void RendersTheSpotlightTheStripAndTheHelpRow()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                string repo = RepoRoot();
                ImageSource Art(string asset) => AmbienceArt.LoadFile(Path.Combine(repo, "GI-Subtitles", "Assets", "Ambience", asset + ".jpg"));
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string lang in new[] { "pl-PL", "en-US" })
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", lang, out var window, palette);
                    ShellRenderTests.Fill(host, window, ShellRenderTests.Scene.Stopped, lang);
                    string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                    object Find(string n) => window.FindName(n);
                    Brush B(string key) => host.TryFindResource(key) as Brush;

                    var rows = SpotlightRules.Rows(new SpotlightInputs { SetupCompleted = true, WuWaPolishListed = true, WuWaGameVersion = "3.7" });
                    Assert.IsTrue(SettingsWindow.ApplySpotlightTo(Find, rows, "Gendle", Art("wuwa"), Art("genshin"), L, B("GameWuWaBrush"),
                        B("GameGenshinBrush")), "both rows show");
                    var notice = GuessNoticeText.Build(new GuessOvertake("genshin", 3, 5, new GuessNeighbour(4, "Ayvruu", null, false, 160, 20)), L);
                    Assert.IsTrue(SettingsWindow.ApplyGuessRankStripTo(Find, notice));
                    StringAssert.Contains(((TextBlock)Find("GuessRankStripText")).Text, "Ayvruu");
                    StringAssert.Contains(((TextBlock)Find("SpotlightWuWaBody")).Text, "3.7");
                    StringAssert.StartsWith(((TextBlock)Find("SpotlightGuessTitle")).Text, "Gendle:");

                    Select(window, SettingsNavigation.Home);
                    Layout(host, 1280, 1180);
                    string tag = $"{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}";
                    written.Add(Save(Snapshot(host), $"guess-start-{tag}.png"));

                    // One row left after the other was closed, WuWa already selected.
                    var one = SpotlightRules.Rows(new SpotlightInputs { SetupCompleted = true, WuWaPolishListed = true, WuWaSelected = true, SeenGuess = true });
                    SettingsWindow.ApplySpotlightTo(Find, one, "Wuwdle", Art("wuwa"), Art("wuwa"), L, B("GameWuWaBrush"), B("GameWuWaBrush"));
                    Assert.AreEqual(Visibility.Collapsed, ((UIElement)Find("SpotlightGuessRow")).Visibility);
                    Assert.IsFalse(SettingsWindow.ApplyGuessRankStripTo(Find, null), "no notice, no strip");
                    Layout(host, 1280, 1000);
                    written.Add(Save(Snapshot(host), $"guess-start-one-row-{tag}.png"));

                    ((TextBlock)Find("GuessCommunityBody")).Text = SettingsWindow.GuessCommunityText(L);
                    StringAssert.Contains(((TextBlock)Find("GuessCommunityBody")).Text, lang == "pl-PL" ? "Zendle i Wuwdle" : "Zendle and Wuwdle");
                    Select(window, SettingsNavigation.Help);
                    Layout(host, 1280, 1000);
                    written.Add(Save(Snapshot(host), $"guess-help-{tag}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        private static void Select(Window window, string page)
        {
            var tabs = (TabControl)window.FindName("NavTabs");
            foreach (var item in tabs.Items)
                if (item is TabItem t && (string)t.Tag == page) tabs.SelectedItem = t;
        }
    }
}
