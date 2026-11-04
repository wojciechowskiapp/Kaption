using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using GI_Subtitles.Models;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.QuickTranslate;
using GI_Subtitles.Services.Rendering;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;
using Border = System.Windows.Controls.Border;

namespace GI_Test
{
    /// <summary>
    /// The screenshots kaption.one shows on its home page ("Tak wygląda Kaption 2.5") and ships in the media kit's app
    /// folder: Start while translating and while waiting for the game, Wygląd napisów, Postacie, the first setup step
    /// and the Ctrl+Q popup, Polish interface, both palettes, drawn at 200 % (a 1280×800 window is 2560×1600 px).
    /// Publishing renders, not checks (one guard on the UID row aside): they run only with KAPTION_RENDER_SHOWCASE=1 and
    /// write PNGs to KAPTION_SHOWCASE_DIR (default %TEMP%\kaption-showcase\renders\); landing/scripts/app-shots.mjs turns
    /// them into the site's JPEGs. Postacie and the popup read this machine's Genshin data and the pictures already on
    /// disk (read only, nothing is fetched), so they show what the app shows; without that data those two say so and skip.
    /// </summary>
    [TestClass]
    public class AppShowcaseShotTests
    {
        private const double Width = 1280, Height = 800, Scale = 2.0;
        private const string Lang = "pl-PL";

        private static string Dir =>
            Environment.GetEnvironmentVariable("KAPTION_SHOWCASE_DIR") is { Length: > 0 } dir
                ? dir
                : Path.Combine(Path.GetTempPath(), "kaption-showcase", "renders");

        private static void RequireOptIn()
        {
            if (Environment.GetEnvironmentVariable("KAPTION_RENDER_SHOWCASE") != "1")
                Assert.Inconclusive("Set KAPTION_RENDER_SHOWCASE=1 for the site's showcase renders.");
        }

        [TestMethod]
        public void Showcase_StartAndLook()
        {
            RequireOptIn();
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                {
                    string tag = palette.ToLowerInvariant();
                    foreach (var (scene, slug) in new[]
                             {
                                 (ShellRenderTests.Scene.Translating, "start-translating"),
                                 (ShellRenderTests.Scene.WaitingForGame, "start-ready"),
                             })
                    {
                        var host = Shell(palette, scene, out var window);
                        Select(window, SettingsNavigation.Home);
                        Layout(host, Width, Height);
                        written.Add(Write(Snapshot(host, Scale), $"{slug}-{tag}.png"));
                    }

                    var lookHost = Shell(palette, ShellRenderTests.Scene.Translating, out var lookWindow);
                    Select(lookWindow, SettingsNavigation.Look);
                    ApplyLook(lookHost, lookWindow, OverlayStyle.Default);
                    Layout(lookHost, Width, Height);
                    written.Add(Write(Snapshot(lookHost, Scale), $"subtitles-{tag}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void Showcase_Setup()
        {
            RequireOptIn();
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                {
                    var host = LoadLooseWindow("Views/OnboardingWindow.xaml", Lang, out var window, palette);
                    OnboardingTests.Fill(host, window, OnboardingStep.Welcome, Lang);
                    Layout(host, 1000, 720);
                    var shot = Snapshot(host, Scale);
                    written.Add(Write(OnStage(shot, palette == "Dark", radius: 8, clip: true), $"setup-{palette.ToLowerInvariant()}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void Showcase_Characters()
        {
            RequireOptIn();
            var catalog = LiveCatalog();
            string name = Environment.GetEnvironmentVariable("KAPTION_SHOWCASE_CHARACTER") is { Length: > 0 } n ? n : "Columbina";
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                {
                    var host = Shell(palette, ShellRenderTests.Scene.Translating, out var window);
                    Select(window, SettingsNavigation.Characters);
                    var list = ShowCharacter(window, catalog, name);
                    Layout(host, Width, Height);
                    ScrollToRow(list, rowsAbove: 4);
                    Layout(host, Width, Height);
                    written.Add(Write(Snapshot(host, Scale), $"characters-{palette.ToLowerInvariant()}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        /// <summary>
        /// Ctrl+Q over Bennett's first constellation, the screen of the owner's own capture: the two lines the game shows
        /// there, with the Polish pack's text for them (TextMap 519686444 and 2073824575), and the strip that recognises
        /// Bennett and offers his card.
        /// </summary>
        [TestMethod]
        public void Showcase_QuickTranslatePopup()
        {
            RequireOptIn();
            var catalog = LiveCatalog();
            var record = catalog.Characters.FirstOrDefault(c => c.Name == "Bennett")
                         ?? throw new AssertInconclusiveException("Bennett is not in this machine's character data.");
            var written = new List<string>();
            RunSta(() =>
            {
                var model = CharacterOverviewBuilder.Build(record, catalog, null);
                CharacterIcons.ApplyCached(model, CharacterImageCache.ForGame("Genshin"));
                CharacterOverviewViewModel.PrepareVisuals(model);
                var constellationScreen = new[]
                {
                    new QuickTranslateSegment("Grand Expectation", "519686444", "Wielkie oczekiwanie", true),
                    new QuickTranslateSegment(
                        "Fantastic Voyage's ATK increase no longer has an HP restriction, and gains an additional 20% of Bennett's Base ATK.",
                        "2073824575", "Zwiększenie ATK Fantastic Voyage nie ma już ograniczenia związanego z HP i zyskuje dodatkowe 20% Bazowego ATK Bennetta.", true),
                };
                var popup = new QuickTranslatePopup(constellationScreen, QuickTranslateSettings.FromValues(null, 8, 16),
                    new System.Drawing.Rectangle(400, 300, 520, 160));
                var chrome = (FrameworkElement)popup.Content;
                var resources = popup.Resources;
                popup.Content = null;
                popup.Resources = new ResourceDictionary();
                var host = new Border { Child = chrome, UseLayoutRounding = true, Background = Brushes.Transparent };
                host.Resources.MergedDictionaries.Add(resources);
                foreach (var d in Strings(Lang)) host.Resources.MergedDictionaries.Add(d);
                var context = new QuickTranslateContext(model, record, constellationScreen.Select(s => s.Source).ToList());
                popup.SetCharacter(new CharacterOverviewView(model), context, () => model.ToPlainText());
                Layout(host, 520);
                // A close-up: the popup at 1.6× its size (drawn at that scale, not enlarged), alone on the stage. It has
                // one palette, the dark one, over any game.
                const double zoom = 1.6;
                written.Add(Write(OnStage(Snapshot(host, Scale * zoom), dark: true, radius: 12 * zoom, clip: false), "quick-translate-popup-dark.png"));
                popup.Close();
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        // ── the shell, filled as the app fills it ─────────────────────────────

        private static Border Shell(string palette, ShellRenderTests.Scene scene, out Window window)
        {
            var host = LoadLooseWindow("Views/SettingsWindow.xaml", Lang, out window, palette);
            ShellRenderTests.Fill(host, window, scene, Lang);
            CurrentPacks(host, window);
            RememberedUid(host, window);
            return host;
        }

        /// <summary>
        /// Test data for Start › Zaawansowane, whose build card row prints the remembered UID in full: the site's shots fill
        /// it from here, never from a Config.json, so no player's UID (the owners' showcase accounts included) reaches a
        /// published render. 123456789 is the plain placeholder, not an account the cards come from.
        /// </summary>
        internal const string ShowcaseUid = "123456789";

        private static void RememberedUid(Border host, Window window)
        {
            string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
            SettingsWindow.ApplyBuildCardMemoryTo(window.FindName, new[] { ("Genshin", ShowcaseUid) }, L, new CultureInfo(Lang));
        }

        /// <summary>
        /// Always on, unlike the renders: the shell the showcase draws holds the test UID in the build card row and nothing
        /// else, and the test UID is none of the accounts behind the site's sample cards (tools/fetch-build-card-samples.mjs).
        /// </summary>
        [TestMethod]
        public void TheShowcaseShell_HoldsTestDataInTheUidRow_NeverARealUid()
        {
            string samples = Path.Combine(RepoRoot(), "tools", "fetch-build-card-samples.mjs");
            if (File.Exists(samples))
            {
                string accounts = File.ReadAllLines(samples).First(l => l.TrimStart().StartsWith("const UIDS"));
                var real = Regex.Matches(accounts, "'([1-9][0-9]{8,9})'").Select(m => m.Groups[1].Value).ToList();
                Assert.IsTrue(real.Count >= 3, "the sample accounts are listed: " + accounts);
                CollectionAssert.DoesNotContain(real, ShowcaseUid, "the showcase's UID is test data, not an account");
            }
            RunSta(() =>
            {
                var host = Shell("Dark", ShellRenderTests.Scene.Translating, out var window);
                string L(string key) => host.TryFindResource(key) as string;
                string row = ((TextBlock)window.FindName("BuildCardMemoryText")).Text;
                Assert.AreEqual(string.Format(new CultureInfo(Lang), L("card_Memory_Format"), "Genshin Impact " + ShowcaseUid), row,
                    "the row shows the test UID and nothing else");
            });
        }

        /// <summary>
        /// The packs as the live feed had them on the afternoon of 2 November 2026 (Genshin 7.2, whose character data
        /// the Postacie shot shows, and its 551,847-line pack): Genshin up to date, a newer Star Rail pack offered, ZZZ
        /// not installed yet. ShellRenderTests' fixture keeps its older numbers, which the guide screenshots match.
        /// </summary>
        private static void CurrentPacks(Border host, Window window)
        {
            string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
            var culture = new CultureInfo(Lang);
            Brush B(string key) => host.TryFindResource(key) as Brush;
            SettingsWindow.PackCardRow Row(string name, string accent, string installed, string latest, bool active)
            {
                var (text, brush, isDownload, sub) = SettingsWindow.PackRowStatus(PackStatus.Compare(installed, latest), false, active,
                    installed, latest, L, culture);
                return new SettingsWindow.PackCardRow { Name = name, Version = sub, Status = text, StatusBrush = B(brush), Accent = B(accent),
                    IsDownload = isDownload, Tooltip = isDownload ? L("shell_Packs_Download_Tip", "") : null };
            }
            ((ItemsControl)window.FindName("PacksCardList")).ItemsSource = new[]
            {
                Row("Genshin Impact", "GameGenshinBrush", "7.2.0-r5", "7.2.0-r5", active: true),
                Row("Honkai: Star Rail", "GameStarRailBrush", "4.6.0-r8", "4.6.0-r10", active: false),
                Row("Zenless Zone Zero", "GameZzzBrush", null, "3.3.0-r7", active: false),
            };
            ((TextBlock)window.FindName("DashDictStatus")).Text = EngineLabels.SubtitlesChip(loaded: true, packMissing: false, packComing: false, L);
        }

        private static void Select(Window window, string page)
        {
            var tabs = (TabControl)window.FindName("NavTabs");
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => (string)t.Tag == page);
        }

        /// <summary>The page as the app fills it, plus the sliders (code-behind keeps them in step in the app).</summary>
        private static void ApplyLook(Border host, Window window, OverlayStyle look)
        {
            string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
            SettingsWindow.ApplyLookTo(window.FindName, look, "Genshin", ShellRenderTests.LookFixture, -140, 900, ShellRenderTests.Art("Genshin"), L);
            ((Slider)window.FindName("FontSizeSlider")).Value = look.Size;
            ((Slider)window.FindName("PadVerticalSlider")).Value = look.PadVertical ?? -140;
            ((Slider)window.FindName("PadHorizontalSlider")).Value = look.PadHorizontal;
        }

        // ── Postacie: this machine's data, read only ──────────────────────────

        private static CharacterCatalog LiveCatalog()
        {
            var catalog = CharacterCatalogService.GetAsync("Genshin").GetAwaiter().GetResult();
            if (catalog.IsEmpty) Assert.Inconclusive("No Genshin character data on this machine.");
            return catalog;
        }

        // The page as the app fills it: this machine's catalog, pictures already on disk, no downloads.
        private static ListBox ShowCharacter(Window window, CharacterCatalog catalog, string name)
        {
            var page = (CharactersPage)window.FindName("CharactersPage");
            page.GameSource = () => "Genshin";
            page.PackSource = () => null;
            page.PackReady = () => true;
            page.FetchImages = false;
            var load = page.LoadAsync("Genshin", _ => Task.FromResult(catalog));
            CharacterOverviewRenderTests.Pump(() => load.IsCompleted);
            var list = (ListBox)page.FindName("CharacterList");
            var item = list.Items.Cast<CharacterListItem>().FirstOrDefault(i => i.Name == name)
                       ?? throw new AssertInconclusiveException($"{name} is not in this machine's character data.");
            list.SelectedItem = item;
            var overview = (ContentControl)page.FindName("OverviewHost");
            CharacterOverviewRenderTests.Pump(() => (overview.Content as FrameworkElement)?.DataContext is CharacterOverviewViewModel vm && vm.Name == name);
            // The header's picture decodes off the UI thread.
            CharacterOverviewRenderTests.Pump(() => (overview.Content as FrameworkElement)?.DataContext is CharacterOverviewViewModel { HasHeroAmbience: true });
            return list;
        }

        /// <summary>
        /// Scrolls the list so its top row starts on the list's top edge (no half-cut row) with <paramref name="rowsAbove"/>
        /// rows above the selected one. The list scrolls by pixel and virtualises, so the row is found, measured and
        /// scrolled to a few times until it sits exactly at the top.
        /// </summary>
        private static void ScrollToRow(ListBox list, int rowsAbove)
        {
            var scroller = Descendants<ScrollViewer>(list).First();
            var viewport = Descendants<ScrollContentPresenter>(scroller).First();
            int top = Math.Max(0, list.SelectedIndex - rowsAbove);
            list.ScrollIntoView(list.Items[top]);
            for (int pass = 0; pass < 4; pass++)
            {
                list.UpdateLayout();
                DoEvents();
                if (!(list.ItemContainerGenerator.ContainerFromIndex(top) is FrameworkElement row)) break;
                double y = row.TranslatePoint(new Point(0, 0), viewport).Y;
                if (Math.Abs(y) < 0.5) break;
                scroller.ScrollToVerticalOffset(scroller.VerticalOffset + y);
            }
        }

        // ── composition ───────────────────────────────────────────────────────

        /// <summary>
        /// Something smaller than the frame, centred on a 1280×800 stage as it sits on a desktop: a soft shadow and a
        /// hairline edge over a quiet ink backdrop with one blue light, the site's own stage. Setup is a fixed 1000×720
        /// window, so it gets the corners Windows 11 gives a window (<paramref name="clip"/>); the Ctrl+Q popup draws
        /// its own. <paramref name="radius"/> is in DIP of what is drawn.
        /// </summary>
        private static BitmapSource OnStage(BitmapSource content, bool dark, double radius, bool clip)
        {
            int w = (int)(Width * Scale), h = (int)(Height * Scale);
            double cw = content.PixelWidth, ch = content.PixelHeight;
            double x = Math.Round((w - cw) / 2), y = Math.Round((h - ch) / 2);
            double r = radius * Scale;
            var visual = new ContainerVisual();

            var backdrop = new DrawingVisual();
            using (var dc = backdrop.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x17)), null, new Rect(0, 0, w, h));
                var light = new RadialGradientBrush(Color.FromArgb(0x70, 0x4F, 0x5B, 0xD5), Color.FromArgb(0, 0x4F, 0x5B, 0xD5))
                {
                    Center = new Point(0.5, 0.7), GradientOrigin = new Point(0.5, 0.7), RadiusX = 0.8, RadiusY = 0.9,
                };
                dc.DrawRectangle(light, null, new Rect(0, 0, w, h));
            }
            visual.Children.Add(backdrop);

            var shadow = new DrawingVisual
            {
                Effect = new DropShadowEffect { BlurRadius = 32 * Scale, ShadowDepth = 10 * Scale, Direction = 270, Opacity = 0.6, Color = Colors.Black },
            };
            using (var dc = shadow.RenderOpen())
                dc.DrawRoundedRectangle(Brushes.Black, null, new Rect(x, y, cw, ch), r, r);
            visual.Children.Add(shadow);

            var frame = new DrawingVisual();
            using (var dc = frame.RenderOpen())
            {
                if (clip) dc.PushClip(new RectangleGeometry(new Rect(x, y, cw, ch), r, r));
                dc.DrawImage(content, new Rect(x, y, cw, ch));
                if (clip) dc.Pop();
                var edge = new Pen(new SolidColorBrush(dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x29, 0, 0, 0)), Scale);
                if (clip) dc.DrawRoundedRectangle(null, edge, new Rect(x + Scale / 2, y + Scale / 2, cw - Scale, ch - Scale), r, r);
            }
            visual.Children.Add(frame);

            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private static string Write(BitmapSource bitmap, string name)
        {
            AssertNotBlank(bitmap, name);
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, name);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var fs = File.Create(path)) encoder.Save(fs);
            return path;
        }
    }
}
