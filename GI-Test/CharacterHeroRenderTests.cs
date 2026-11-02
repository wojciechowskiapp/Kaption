using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;
using Border = System.Windows.Controls.Border;

namespace GI_Test
{
    /// <summary>
    /// The Characters page header over the character's ambience image, and "Gra też w drużynach". The header's text is
    /// measured against what is drawn behind it in both palettes, over a deliberately harsh picture (black, white and
    /// saturated blobs), so a bright or a dark scene from the updater cannot take the name or the small print below
    /// 4.5:1. Review renders go to %TEMP%\kaption-w26\renders\&lt;run&gt;\characters\.
    /// </summary>
    [TestClass]
    public class CharacterHeroRenderTests
    {
        private const double PageWidth = 980;

        [TestMethod]
        public void Header_TextKeepsItsContrast_OverTheAmbience_InBothPalettes()
        {
            var report = new List<string>();
            RunSta(() =>
            {
                foreach (bool dark in new[] { true, false })
                foreach (var (name, path) in new[] { ("busy", BusyFixture()), ("night", NightFixture()) })
                {
                    var model = FlinsWithAmbience(path);
                    var (host, view) = Host(model, dark);
                    var vm = (CharacterOverviewViewModel)view.DataContext;
                    Assert.IsTrue(vm.HasHeroAmbience, "the picture on disk is the header's backdrop");
                    Assert.IsTrue(Shown((FrameworkElement)view.FindName("HeroAmbienceLayer")));
                    string palette = dark ? "dark" : "light";
                    SaveReview(Snapshot(host), $"characters/hero-{name}-{palette}.png");

                    var texts = new[] { "HeroName", "HeroGloss", "HeroStars", "HeroFooter" }
                        .Select(n => (Name: n, Text: (TextBlock)view.FindName(n))).ToList();
                    foreach (var t in texts) Assert.IsTrue(Shown(t.Text), $"{t.Name} shows");
                    var colours = texts.ToDictionary(t => t.Name, t => Fg(t.Text.Foreground));
                    foreach (var t in texts) t.Text.Opacity = 0;
                    Layout(host, PageWidth);
                    var backdrop = Snapshot(host);
                    foreach (var t in texts)
                    {
                        double ratio = ContrastRatio(RelativeLuminance(colours[t.Name]), BrightestLuminance(backdrop, InkBox(t.Text, host), 0.95));
                        report.Add($"{palette,-5} {name,-5} {t.Name,-10} {ratio,5:0.0}:1");
                        // Text needs 4.5:1; the stars are a picture of the rarity (a graphic: 3:1).
                        double needed = t.Name == "HeroStars" ? 3 : 4.5;
                        Assert.IsTrue(ratio >= needed, $"{palette} over the {name} picture: {t.Name} has {ratio:0.0}:1 (needs {needed}:1)");
                    }
                }
            });
            foreach (string line in report) Console.WriteLine(line);
        }

        [TestMethod]
        public void Header_WithoutThePicture_KeepsThePlainWash()
        {
            RunSta(() =>
            {
                foreach (bool dark in new[] { true, false })
                {
                    var model = FlinsWithAmbience(null);
                    var (host, view) = Host(model, dark);
                    Assert.IsFalse(((CharacterOverviewViewModel)view.DataContext).HasHeroAmbience);
                    Assert.IsFalse(Shown((FrameworkElement)view.FindName("HeroAmbienceLayer")), "missing or offline: no picture layer");
                    SaveReview(Snapshot(host), $"characters/hero-none-{(dark ? "dark" : "light")}.png");
                }
            });
        }

        [TestMethod]
        public void PlaysIn_ListsTheHosts_SixThenTheRest_AndOpensAHost()
        {
            RunSta(() =>
            {
                foreach (bool dark in new[] { true, false })
                {
                    string palette = dark ? "dark" : "light";
                    var roster = CharacterTeamIndexTests.Roster();
                    var flins = CharacterOverviewBuilder.Build(roster.Find(CharacterTeamIndexTests.Flins), roster, null);
                    var (host, view) = Host(flins, dark, height: 1500);
                    string opened = null;
                    view.EnableCharacterLinks(id => opened = id);
                    Layout(host, PageWidth);
                    view.ScrollToSection("Teams");
                    Layout(host, PageWidth);
                    SaveReview(Snapshot(host), $"characters/plays-in-flins-{palette}.png");
                    var rows = Descendants<Button>((FrameworkElement)view.FindName("TeamHostsList")).Where(Shown).ToList();
                    Assert.AreEqual(1, rows.Count, "Columbina's team");
                    Assert.AreEqual("Columbina", AutomationName(rows[0]));
                    rows[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.AreEqual(CharacterTeamIndexTests.Columbina, opened, "a row opens its host's page");

                    var many = ManyHosts();
                    var (host2, view2) = Host(CharacterOverviewBuilder.Build(many.Find(CharacterTeamIndexTests.Bennett), many, null), dark, height: 1700);
                    view2.EnableCharacterLinks(_ => { });
                    view2.ScrollToSection("Teams");
                    Layout(host2, PageWidth);
                    SaveReview(Snapshot(host2), $"characters/plays-in-cap-{palette}.png");
                    var list = (ItemsControl)view2.FindName("TeamHostsList");
                    var more = (Button)view2.FindName("MoreTeamHostsButton");
                    Assert.AreEqual(CharacterOverviewViewModel.TeamHostCap, list.Items.Count);
                    Assert.IsTrue(Shown(more), "\"Pokaż więcej\" under the six");
                    StringAssert.Contains(string.Concat(Descendants<TextBlock>(more).SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Run>()).Select(r => r.Text)), "(2)");
                    more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Layout(host2, PageWidth);
                    Assert.AreEqual(8, list.Items.Count);
                    Assert.IsFalse(Shown(more));
                    SaveReview(Snapshot(host2), $"characters/plays-in-cap-all-{palette}.png");

                    // Where a host cannot switch pages (the Ctrl+Q popup), the rows are plain cards, not buttons.
                    var (host3, view3) = Host(flins, dark, height: 1500);
                    Layout(host3, PageWidth);
                    Assert.AreEqual(0, Descendants<Button>((FrameworkElement)view3.FindName("TeamHostsList")).Count(Shown));
                }
            });
        }

        [TestMethod]
        public void CharactersTab_AHostRow_SwitchesThePage()
        {
            RunSta(() =>
            {
                var page = CharacterOverviewRenderTests.Page(CharacterTeamIndexTests.Roster(), "Flins", out var host);
                Layout(host, 1264, 761);
                var overview = (ContentControl)page.FindName("OverviewHost");
                var view = (CharacterOverviewView)overview.Content;
                view.ScrollToSection("Teams");
                Layout(host, 1264, 761);
                var row = Descendants<Button>((FrameworkElement)view.FindName("TeamHostsList")).First(Shown);
                row.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                CharacterOverviewRenderTests.Pump(() => (overview.Content as FrameworkElement)?.DataContext is CharacterOverviewViewModel vm && vm.Name == "Columbina");
                Assert.AreEqual("Columbina", ((CharacterOverviewViewModel)((FrameworkElement)overview.Content).DataContext).Name);
                Assert.AreEqual("Columbina", (((ListBox)page.FindName("CharacterList")).SelectedItem as CharacterListItem)?.Name, "the list follows");

                // A search that hides the host is cleared first.
                var search = (TextBox)page.FindName("SearchBox");
                search.Text = "Flins";
                Assert.IsTrue(page.OpenCharacter(CharacterTeamIndexTests.Nefer));
                Assert.AreEqual("", search.Text);
                Assert.AreEqual("Nefer", (((ListBox)page.FindName("CharacterList")).SelectedItem as CharacterListItem)?.Name);
                Assert.IsFalse(page.OpenCharacter("404"), "not in the list");
            });
        }

        /// <summary>
        /// Review only (KAPTION_RENDER_LIVE=1, with KAPTION_RENDER_FETCH=1 to fetch missing pictures): the Characters tab
        /// with this machine's own Genshin bundle and its real ambience pictures (kept in the game's icon folder as the
        /// app keeps them), in both palettes. Reads the bundle only; nothing is written to the profile but those pictures.
        /// </summary>
        [TestMethod]
        public void Live_CharactersTab_ReviewRenders()
        {
            if (Environment.GetEnvironmentVariable("KAPTION_RENDER_LIVE") != "1") Assert.Inconclusive("Set KAPTION_RENDER_LIVE=1 for the live review renders.");
            var catalog = CharacterCatalogService.GetAsync("Genshin").GetAwaiter().GetResult();
            if (catalog.IsEmpty) Assert.Inconclusive("No Genshin character data on this machine.");
            var report = new List<string>();
            RunSta(() =>
            {
                foreach (bool dark in new[] { true, false })
                foreach (string name in new[] { "Flins", "Columbina", "Chiori", "Kamisato Ayaka" })
                {
                    if (catalog.Characters.All(c => c.Name != name)) continue;
                    // With KAPTION_RENDER_FETCH=1 the page fetches missing pictures as the app does; wait for the backdrop.
                    var page = CharacterOverviewRenderTests.Page(catalog, name, out var host, dark);
                    var overview = (ContentControl)page.FindName("OverviewHost");
                    for (int i = 0; i < 300 && !((overview.Content as FrameworkElement)?.DataContext is CharacterOverviewViewModel { HasHeroAmbience: true }); i++)
                    {
                        DoEvents();
                        System.Threading.Thread.Sleep(50);
                    }
                    Layout(host, 1264, 761);
                    string slug = name.ToLowerInvariant().Replace(' ', '-');
                    SaveReview(Snapshot(host), $"characters/live-{slug}-{(dark ? "dark" : "light")}.png");
                    if (overview.Content is CharacterOverviewView view)
                    {
                        view.ScrollToSection("Teams");
                        Layout(host, 1264, 761);
                        SaveReview(Snapshot(host), $"characters/live-{slug}-teams-{(dark ? "dark" : "light")}.png");
                        report.Add($"{name}: picture {(((CharacterOverviewViewModel)view.DataContext).HasHeroAmbience ? "shown" : "missing")}, " +
                                   $"plays in {((CharacterOverviewViewModel)view.DataContext).TeamHosts.Count} other teams");
                    }
                }
            });
            foreach (string line in report.Distinct()) Console.WriteLine(line);
        }

        // ── fixtures and hosts ──────────────────────────────────────────

        private static CharacterOverviewModel FlinsWithAmbience(string path)
        {
            var roster = CharacterTeamIndexTests.Roster();
            var model = CharacterOverviewBuilder.Build(roster.Find(CharacterTeamIndexTests.Flins), roster, null);
            model.AmbiencePath = path;
            model.NameGloss = "W napisach: Flins"; // a gloss line, to measure it too
            model.Rarity = 5;
            model.RarityText = "★★★★★";
            CharacterOverviewViewModel.PrepareVisuals(model);
            return model;
        }

        internal static CharacterCatalog ManyHosts()
        {
            var records = new List<string>();
            string[] names = { "Varesa", "Iansan", "Chasca", "Mavuika", "Xilonen", "Kinich", "Emilie", "Clorinde" };
            for (int i = 0; i < names.Length; i++)
                records.Add(CharacterTeamIndexTests.Record("2" + i, names[i], $"2024-0{i + 1}-01", "5." + i,
                    CharacterTeamIndexTests.Team("Team " + names[i], "Drużyna: " + names[i], "ai",
                        new[] { "2" + i, CharacterTeamIndexTests.Bennett }, (CharacterTeamIndexTests.Bennett, "Healing and ATK buff", "Leczenie i wzmocnienie ATK"))));
            records.Add(CharacterTeamIndexTests.Record(CharacterTeamIndexTests.Bennett, "Bennett", "2020-09-28", "1.0"));
            return CharacterTeamIndexTests.Catalog(records.ToArray());
        }

        private static (Border Host, CharacterOverviewView View) Host(CharacterOverviewModel model, bool dark, double height = double.NaN)
        {
            var view = new CharacterOverviewView(model);
            var host = new Border { Child = view, UseLayoutRounding = true };
            host.Background = new SolidColorBrush(dark ? Color.FromRgb(0x0B, 0x0D, 0x17) : Colors.White);
            foreach (var d in Strings("pl-PL")) host.Resources.MergedDictionaries.Add(d);
            host.Resources.MergedDictionaries.Add(Dictionary($"Themes/Palette.{(dark ? "Dark" : "Light")}.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary($"Resources/CharacterPalette{(dark ? "Dark" : "Light")}.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary("Resources/CharacterLayoutPage.xaml"));
            if (dark) TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
            Layout(host, PageWidth, double.IsNaN(height) ? 760 : height);
            return (host, view);
        }

        // In a render test nothing is on screen (IsVisible stays false): visible means no collapsed or hidden ancestor.
        private static bool Shown(UIElement e)
        {
            for (DependencyObject d = e; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is UIElement u && u.Visibility != Visibility.Visible) return false;
            return true;
        }

        private static string AutomationName(DependencyObject d) => System.Windows.Automation.AutomationProperties.GetName(d);

        /// <summary>A harsh stand-in for an ambience picture: black, white and saturated blobs everywhere.</summary>
        private static string BusyFixture() => Fixture("ambience-busy.png", dc =>
        {
            var rnd = new Random(11);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), null, new Rect(0, 0, 1216, 688));
            Color[] colours = { Colors.White, Colors.Black, Color.FromRgb(0xFF, 0xE0, 0x40), Color.FromRgb(0x40, 0xC0, 0xFF), Color.FromRgb(0xFF, 0x50, 0xA0) };
            for (int i = 0; i < 260; i++)
            {
                var c = colours[rnd.Next(colours.Length)];
                double r = rnd.Next(12, 90);
                dc.DrawEllipse(new SolidColorBrush(c), null, new Point(rnd.Next(0, 1216), rnd.Next(0, 688)), r, r * 0.8);
            }
        });

        /// <summary>A scene like the updater's: a dark sky, a lit horizon, a bright glow on the right.</summary>
        private static string NightFixture() => Fixture("ambience-night.png", dc =>
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x14, 0x18, 0x3A), Color.FromRgb(0x06, 0x07, 0x0E), 90), null, new Rect(0, 0, 1216, 688));
            dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, 0xFF, 0xA0, 0x60), Color.FromArgb(0xB0, 0xFF, 0xA0, 0x60), 90), null, new Rect(0, 180, 1216, 120));
            dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(0xFF, 0xF4, 0xE8, 0xFF), Color.FromArgb(0, 0xA0, 0x80, 0xFF)), null, new Point(900, 260), 220, 180);
            var rnd = new Random(3);
            for (int i = 0; i < 120; i++)
                dc.DrawEllipse(Brushes.White, null, new Point(rnd.Next(0, 1216), rnd.Next(0, 240)), 1.2, 1.2);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x0C, 0x14)), null, new Rect(0, 470, 1216, 218));
        });

        private static string Fixture(string name, Action<DrawingContext> draw)
        {
            string path = Path.Combine(OutputDir, "fixtures", name);
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) draw(dc);
            var bmp = new RenderTargetBitmap(1216, 688, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) encoder.Save(fs);
            return path;
        }
    }
}
