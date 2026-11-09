using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Themes;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;
using Border = System.Windows.Controls.Border;

namespace GI_Test
{
    /// <summary>
    /// The 2.6 shell refinements (P2): the status bar's "Więcej" menu, the rail's indicator in the game's colour, the
    /// micro-motion (and that it stays still with animations off), setup's "how it works" strip.
    /// </summary>
    [TestClass]
    public class ShellRefinementTests
    {
        [TestCleanup]
        public void MotionOffAgain() => Motion.Override = false;

        [TestMethod]
        public void StatusBar_DiscordAndAMoreMenu_WithTheFourActions()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string lang in new[] { "pl-PL", "en-US" })
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", lang, out var window, palette);
                    string L(string key) => host.TryFindResource(key) as string;
                    foreach (string gone in new[] { "BarAboutButton", "BarSetupButton", "BarXButton", "BarWebButton" })
                        Assert.IsNull(window.FindName(gone), $"{gone} moved into the menu or Pomoc");
                    Assert.IsNotNull(window.FindName("BarDiscordButton"), "Discord keeps its icon");
                    var more = (Button)window.FindName("BarMoreButton");
                    Assert.AreEqual(L("bar_More"), System.Windows.Automation.AutomationProperties.GetName(more));

                    // The menu as it opens. A context menu never has a parent, so it gets the window's resources and is laid
                    // out and drawn on its own, over the page's background.
                    var menu = (ContextMenu)window.FindName("BarMoreMenu");
                    foreach (var d in host.Resources.MergedDictionaries) menu.Resources.MergedDictionaries.Add(d);
                    ((MenuItem)window.FindName(lang == "pl-PL" ? "BarLangPl" : "BarLangEn")).IsChecked = true;
                    menu.Measure(new Size(400, 400));
                    menu.Arrange(new Rect(menu.DesiredSize));
                    menu.UpdateLayout();
                    var actions = menu.Items.OfType<MenuItem>().Where(m => m.Focusable).ToList();
                    CollectionAssert.AreEqual(new[] { L("bar_More_About"), L("bar_More_Report"), L("bar_More_Web"), "Polski", "English" },
                        actions.Select(m => m.Header as string).ToArray());
                    Assert.AreEqual(1, menu.Items.OfType<MenuItem>().Count(m => !m.Focusable), "one caption, Język");
                    written.Add(SaveReview(OnBackground(menu, (Brush)host.FindResource("BgBrush")),
                        $"statusbar/more-menu-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        private static System.Windows.Media.Imaging.BitmapSource OnBackground(FrameworkElement element, Brush background)
        {
            int w = (int)Math.Ceiling(element.ActualWidth) + 48, h = (int)Math.Ceiling(element.ActualHeight) + 48;
            var drawn = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            drawn.Render(element);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(background, null, new Rect(0, 0, w, h));
                dc.DrawImage(drawn, new Rect(24, 24, drawn.PixelWidth, drawn.PixelHeight));
            }
            var result = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            result.Render(visual);
            result.Freeze();
            return result;
        }

        [TestMethod]
        public void Rail_ActiveIndicator_TakesTheGamesColour_InBothPalettes_AndHighContrast()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string game in new[] { "Genshin", "StarRail", "ZZZ" })
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", "pl-PL", out var window, palette);
                    var rail = (TabControl)window.FindName("NavTabs");
                    Layout(host, 1280, 800);
                    var selected = (TabItem)rail.SelectedItem;
                    var bar = (Border)selected.Template.FindName("bar", selected);
                    Assert.AreEqual(((SolidColorBrush)host.FindResource("AccentBrush")).Color, ((SolidColorBrush)bar.Background).Color,
                        "no game yet: the app accent");

                    NavRail.UseGameAccent(rail, game);
                    Layout(host, 1280, 800);
                    var expected = (SolidColorBrush)host.FindResource(NavRail.AccentKeyFor(game));
                    Assert.AreEqual(expected.Color, ((SolidColorBrush)bar.Background).Color, $"{game}'s colour on the {palette} rail");
                    // The indicator is a 3 px graphic: 3:1 against the selected item's own surface.
                    var behind = ((SolidColorBrush)host.FindResource("Surface2Brush")).Color;
                    double ratio = ContrastRatio(RelativeLuminance(expected.Color), RelativeLuminance(behind));
                    Assert.IsTrue(ratio >= 3, $"{game} on {palette}: {ratio:0.0}:1");
                    written.Add(SaveReview(Snapshot(host), $"rail/rail-{game.ToLowerInvariant()}-{palette.ToLowerInvariant()}.png"));
                }
            });
            foreach (string key in new[] { "RailIndicatorGenshinBrush", "RailIndicatorStarRailBrush", "RailIndicatorZzzBrush" })
                Assert.AreEqual(SystemColors.HighlightColor, ThemeService.HighContrastColorFor(key), $"{key} is the system highlight in high contrast");
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void Motion_OnlyWhenAllowed_AndNeverHeldAfterwards()
        {
            RunSta(() =>
            {
                var still = new Border { Width = 40, Height = 20, Opacity = 0.9 };
                Motion.Override = false;
                Motion.Enter(still);
                Assert.IsFalse(still.HasAnimatedProperties, "animations off: nothing moves");

                var moving = new Border { Width = 40, Height = 20, Opacity = 0.9 };
                Motion.Override = true;
                Motion.Enter(moving, rise: 8);
                Assert.IsTrue(moving.HasAnimatedProperties, "the element fades in");
                Assert.IsTrue(((TranslateTransform)moving.RenderTransform).HasAnimatedProperties, "and rises");
                Assert.AreEqual(0.9, (double)moving.GetAnimationBaseValue(UIElement.OpacityProperty), 1e-9, "its own opacity is untouched");
            });
        }

        [TestMethod]
        public void Switch_KnobSlidesToItsSide()
        {
            RunSta(() =>
            {
                var host = LoadLooseWindow("Views/SettingsWindow.xaml", "pl-PL", out _, "Dark");
                var box = new CheckBox { Style = (Style)host.FindResource("ToggleSwitch"), Content = "Auto-start" };
                host.Child = new StackPanel { Children = { box } };
                Layout(host, 320, 80);
                var knob = (FrameworkElement)box.Template.FindName("knob", box);

                Motion.Override = false;
                box.IsChecked = true;
                Layout(host, 320, 80);
                Motion.Toggled(box);
                Assert.IsFalse(knob.RenderTransform is TranslateTransform t0 && t0.HasAnimatedProperties, "animations off: the knob just sits on its side");

                Motion.Override = true;
                box.IsChecked = false;
                Layout(host, 320, 80);
                Motion.Toggled(box);
                var slide = (TranslateTransform)knob.RenderTransform;
                Assert.IsTrue(slide.HasAnimatedProperties, "the knob slides from where it was");
                Assert.IsTrue(Motion.GetSlideKnob(box), "every switch in the shell carries the slide");
            });
        }

        [TestMethod]
        public void Rail_PageRisesIn_AndTheHeroFadesOnlyForANewState()
        {
            RunSta(() =>
            {
                var host = LoadLooseWindow("Views/SettingsWindow.xaml", "pl-PL", out var window, "Dark");
                Layout(host, 1280, 800);
                var rail = (TabControl)window.FindName("NavTabs");
                var page = (UIElement)rail.Template.FindName("PART_SelectedContentHost", rail);
                Motion.Override = true;
                NavRail.PageEntered(rail);
                Assert.IsTrue(page.HasAnimatedProperties, "the opened page rises in");

                var text = (UIElement)window.FindName("StateCardText");
                string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                void Show(StartCardInputs inputs) =>
                    SettingsWindow.ApplyStartCardTo(window.FindName, StartCardModel.Compute(inputs), inputs, L, CultureInfo.GetCultureInfo("pl-PL"));

                Show(new StartCardInputs { Reader = ReaderState.Ready, PackOnDisk = true });
                Show(new StartCardInputs { Reader = ReaderState.Ready, PackOnDisk = true });
                Assert.IsFalse(text.HasAnimatedProperties, "the first fill and the same state again stay still");
                Show(new StartCardInputs { Translating = true, LinesThisSession = 3, Reader = ReaderState.Ready, PackOnDisk = true });
                Assert.IsTrue(text.HasAnimatedProperties, "a new state fades in");

                var steady = (UIElement)window.FindName("StateCardText");
                steady.BeginAnimation(UIElement.OpacityProperty, null);
                Show(new StartCardInputs { Translating = true, LinesThisSession = 4, Reader = ReaderState.Ready, PackOnDisk = true });
                Assert.IsFalse(steady.HasAnimatedProperties, "a new line count is not a new state");
            });
        }

        /// <summary>
        /// The icon weight audit (P3): every line icon in the app's windows and themes is drawn at the rail's 1.6 px; only
        /// the check mark is heavier (1.8, or 2 on a filled disc).
        /// </summary>
        [TestMethod]
        public void Icons_OneLineWeight_EverywhereButTheCheckMark()
        {
            string root = RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            var files = System.IO.Directory.GetFiles(System.IO.Path.Combine(root, "GI-Subtitles", "Views"), "*.xaml")
                .Concat(System.IO.Directory.GetFiles(System.IO.Path.Combine(root, "GI-Subtitles", "Themes"), "*.xaml"))
                .Append(System.IO.Path.Combine(root, "GI-Subtitles", "Resources", "ModernStyles.xaml"));
            var path = new System.Text.RegularExpressions.Regex(@"<Path\b[^>]*>", System.Text.RegularExpressions.RegexOptions.Singleline);
            var icon = new System.Text.RegularExpressions.Regex(@"Data=""\{(?:StaticResource|DynamicResource) ((?:Nav)?Icon\w+)\}""");
            var weight = new System.Text.RegularExpressions.Regex(@"StrokeThickness=""([0-9.]+)""");
            var off = new List<string>();
            int checkedIcons = 0;
            foreach (string file in files)
            {
                string xaml = System.IO.File.ReadAllText(file);
                foreach (System.Text.RegularExpressions.Match m in path.Matches(xaml))
                {
                    var name = icon.Match(m.Value);
                    var w = weight.Match(m.Value);
                    if (!name.Success || !w.Success) continue;
                    checkedIcons++;
                    bool ok = name.Groups[1].Value == "IconCheck" ? w.Groups[1].Value is "1.8" or "2" : w.Groups[1].Value == "1.6";
                    if (!ok) off.Add($"{System.IO.Path.GetFileName(file)}: {name.Groups[1].Value} at {w.Groups[1].Value}");
                }
            }
            Assert.IsTrue(checkedIcons > 25, "the audit sees the icons");
            Assert.AreEqual(0, off.Count, "line icons at 1.6 px: " + string.Join("; ", off));
        }

        /// <summary>
        /// The high-contrast check (P3): every page under the palette Windows' high-contrast mode builds (system colours
        /// for every key, the new rail indicators included), rendered for review; and the palette is built once for the
        /// same system colours instead of on every preference change.
        /// </summary>
        [TestMethod]
        public void HighContrast_EveryPageOnSystemColours_AndThePaletteIsNotRebuiltEachTime()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                var light = Dictionary("Themes/Palette.Light.xaml");
                var hc = ThemeService.HighContrastPalette(light);
                Assert.AreSame(hc, ThemeService.HighContrastPalette(light), "same system colours: the same palette, nothing re-resolves");
                foreach (object key in light.Keys)
                    Assert.IsTrue(hc.Contains(key), $"{key} has a high-contrast value");

                var host = LoadLooseWindow("Views/SettingsWindow.xaml", "pl-PL", out var window, "Light");
                ThemeService.ApplyTo(host.Resources, hc);
                Assert.AreEqual(SystemColors.WindowTextColor, ((SolidColorBrush)host.FindResource("TextBrush")).Color);
                Assert.AreEqual(SystemColors.HighlightColor, ((SolidColorBrush)host.FindResource("AccentBrush")).Color);
                Assert.AreEqual(SystemColors.HighlightColor, ((SolidColorBrush)host.FindResource("RailIndicatorZzzBrush")).Color);
                // The Start page's primary action follows the scheme as well: on a real High Contrast Black the brand
                // gradient had stayed under the system's black highlight text (2.5.0 live look).
                var primary = (Button)window.FindName("DashToggleOcrButton");
                Assert.AreEqual(SystemColors.HighlightColor, ((SolidColorBrush)primary.Background).Color, "Start's primary action in high contrast");
                foreach (string page in SettingsNavigation.Pages)
                {
                    if (page == SettingsNavigation.Characters) continue; // W8's page; its own tests render it
                    var tab = ((TabControl)window.FindName("NavTabs")).Items.OfType<TabItem>().First(t => (t.Tag as string) == page);
                    tab.IsSelected = true;
                    Layout(host, 1280, 800);
                    written.Add(SaveReview(Snapshot(host), $"hc/page-{page.ToLowerInvariant()}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        /// <summary>
        /// Empty and error states (P3): the same component everywhere (Characters: nothing matches, no data, unreadable
        /// data; Gra i napisy: no packs; Pomoc: an empty log), each one sentence and one action.
        /// </summary>
        [TestMethod]
        public void States_OneSentenceOneAction_SameComponentEverywhere()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", "pl-PL", out var window, palette);
                    string L(string key) => host.TryFindResource(key) as string;
                    var packs = (StateMessage)window.FindName("TranslationsEmptyState");
                    var logs = (StateMessage)window.FindName("LogsEmptyState");
                    Assert.AreEqual(L("state_Packs_Empty"), packs.Text);
                    Assert.AreEqual(L("state_CheckAgain"), packs.ActionText);
                    Assert.AreEqual(L("state_Logs_Empty"), logs.Text);
                    Assert.AreEqual(L("state_OpenAppLog"), logs.ActionText);
                    packs.Visibility = Visibility.Visible;
                    ((FrameworkElement)window.FindName("TranslationsLoading")).Visibility = Visibility.Collapsed; // the scan has answered
                    var tabs = (TabControl)window.FindName("NavTabs");
                    tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => (t.Tag as string) == SettingsNavigation.Game);
                    Layout(host, 1280, 800);
                    written.Add(SaveReview(Snapshot(host), $"states/packs-empty-{palette.ToLowerInvariant()}.png"));

                    int clicks = 0;
                    packs.ActionClick += (s, e) => clicks++;
                    packs.Action.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Assert.AreEqual(1, clicks, "the one action is the button");
                    Assert.AreEqual(Visibility.Visible, packs.Action.Visibility);
                }

                foreach (bool dark in new[] { false, true })
                foreach (var state in new[] { CharactersPage.PageState.Empty, CharactersPage.PageState.Error })
                {
                    var page = CharacterOverviewRenderTests.Page(GI_Subtitles.Services.Characters.CharacterCatalog.Empty, null, out var host, dark);
                    page.ShowState(state);
                    Layout(host, 804, 721);
                    var shown = (StateMessage)page.FindName(state == CharactersPage.PageState.Empty ? "EmptyState" : "ErrorState");
                    Assert.AreEqual(state == CharactersPage.PageState.Error ? StateTone.Problem : StateTone.Neutral, shown.Tone);
                    Assert.IsFalse(string.IsNullOrEmpty(shown.ActionText), $"{state}: one action");
                    written.Add(SaveReview(Snapshot(host), $"states/characters-{state.ToString().ToLowerInvariant()}-{(dark ? "dark" : "light")}.png"));
                }
            });

            // One sentence each, in every locale (the scan-failed summary is a status line, not a state).
            foreach (string file in new[] { "Strings.pl-PL.xaml", "Strings.en-US.xaml" })
            {
                var dict = Dictionary("Resources/" + file);
                foreach (string key in new[] { "state_Char_NoResults", "state_Char_Empty", "state_Char_Error", "state_Packs_Empty", "state_Logs_Empty" })
                {
                    string text = (dict[key] as string ?? "").Trim();
                    int ends = text.Count(c => c is '.' or '!' or '?' or '。' or '！' or '？');
                    Assert.IsTrue(ends <= 1, $"{file} {key}: one sentence (\"{text}\")");
                }
            }
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void Setup_HowItWorks_ThreeFramesWithCaptions()
        {
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                {
                    var host = LoadLooseWindow("Views/OnboardingWindow.xaml", "pl-PL", out var window, palette);
                    Layout(host, 1000, 720);
                    var strip = (Grid)window.FindName("HowItWorksStrip");
                    var frames = Descendants<HowItWorksFrame>(strip).ToList();
                    CollectionAssert.AreEqual(new[] { 1, 2, 3 }, frames.Select(f => f.Stage).ToArray());
                    var captions = Descendants<TextBlock>(strip).Select(t => t.Text).ToArray();
                    CollectionAssert.AreEqual(new[] { "onb_How_1", "onb_How_2", "onb_How_3" }.Select(k => host.TryFindResource(k) as string).ToArray(), captions);
                    foreach (var f in frames) Assert.IsFalse(f.IsHitTestVisible || f.Focusable, "a picture, not a control");
                }
            });
        }
    }
}
