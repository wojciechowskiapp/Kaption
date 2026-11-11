using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Services.Notices;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// The come-back loop's three pieces of interface, rendered: the "new story is in Polish" line on Start, its switch
    /// on Gra i napisy, and "Start Kaption with Windows" on the setup's last step. Both palettes, both languages.
    /// </summary>
    [TestClass]
    public class NewStoryRenderTests
    {
        private static readonly NewStoryNotice Genshin72 = new("genshin", new GameVersion(7, 2), new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));

        [TestMethod]
        public void StartShowsOneLine_InTheGamesColour_AndHidesWithoutANotice()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string lang in new[] { "pl-PL", "en-US" })
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", lang, out var window, palette);
                    string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                    ShellRenderTests.Fill(host, window, ShellRenderTests.Scene.WaitingForGame, lang);
                    var tabs = (TabControl)window.FindName("NavTabs");
                    foreach (TabItem t in tabs.Items) if ((string)t.Tag == SettingsNavigation.Home) tabs.SelectedItem = t;

                    var card = (UIElement)window.FindName("NewStoryCard");
                    Assert.AreEqual(Visibility.Collapsed, card.Visibility, "no notice, no line");

                    SettingsWindow.ApplyNewStoryLineTo(window.FindName, Genshin72, L, key => host.TryFindResource(key) as Brush);
                    Layout(host, 1280, 800);

                    Assert.AreEqual(Visibility.Visible, card.Visibility);
                    var line = (TextBlock)window.FindName("NewStoryLine");
                    Assert.AreEqual(lang == "pl-PL" ? "Genshin Impact 7.2: nowa fabuła jest już po polsku" : "Genshin Impact 7.2: the new story is in Polish now", line.Text);
                    Assert.IsTrue(line.ActualHeight > 0 && line.ActualHeight < 30, $"one line at this width, got {line.ActualHeight:F0} px");
                    Assert.AreSame(host.TryFindResource("GameGenshinBrush"), ((Border)window.FindName("NewStoryAccent")).Background, "the game's accent on the edge");
                    written.Add(SaveReview(Snapshot(host), $"newstory/start-line-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));

                    SettingsWindow.ApplyNewStoryLineTo(window.FindName, null, L, key => host.TryFindResource(key) as Brush);
                    Assert.AreEqual(Visibility.Collapsed, card.Visibility, "closed again");
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void SettingsCarriesTheSwitch_OnByDefault()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string lang in new[] { "pl-PL", "en-US" })
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", lang, out var window, palette);
                    ShellRenderTests.Fill(host, window, ShellRenderTests.Scene.Stopped, lang);
                    var tabs = (TabControl)window.FindName("NavTabs");
                    foreach (TabItem t in tabs.Items) if ((string)t.Tag == SettingsNavigation.Game) tabs.SelectedItem = t;
                    Layout(host, 1280, 1400);

                    var toggle = (CheckBox)window.FindName("NewStoryToggle");
                    Assert.AreEqual(true, toggle.IsChecked, "the notice is on unless the player turns it off");
                    Assert.IsTrue(toggle.ActualHeight > 0, "on the page, not behind Zaawansowane");
                    written.Add(SaveReview(Snapshot(host), $"newstory/switch-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void SetupOffersStartWithWindows_Unticked()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string lang in new[] { "pl-PL", "en-US" })
                {
                    var host = LoadLooseWindow("Views/OnboardingWindow.xaml", lang, out var window, palette);
                    OnboardingTests.Fill(host, window, OnboardingStep.Done, lang);
                    Layout(host, 1000, 720);

                    var check = (CheckBox)window.FindName("StartWithWindowsCheck");
                    Assert.AreNotEqual(true, check.IsChecked, "opt-in: setup never ticks it for the player");
                    Assert.IsTrue(check.ActualHeight > 0 && check.ActualWidth > 0, "visible on the last step");
                    var finish = (FrameworkElement)window.FindName("FinishButton");
                    Assert.IsTrue(finish.ActualHeight > 0, "the step still fits its window with the checkbox on it");
                    written.Add(SaveReview(Snapshot(host), $"newstory/setup-done-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }
    }
}
