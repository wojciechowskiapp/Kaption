using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Today;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// Renders the farm card to PNG over a game-like backdrop: dark and light, with and without a UID, a weekday and
    /// Sunday, Polish and English. Output: %TEMP%\kaption-render\farm-card-*.png. Icons come from the app's image
    /// cache; KAPTION_RENDER_FETCH=1 downloads the missing ones first.
    /// </summary>
    [TestClass]
    public class FarmCardRenderTests
    {
        private static bool Fetch => Environment.GetEnvironmentVariable("KAPTION_RENDER_FETCH") == "1";

        [TestMethod]
        public void RendersTheCardInEveryState()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                var cases = new (string Name, DateTimeOffset Now, bool Uid, bool Empty, string Lang, bool Unknown)[]
                {
                    ("wednesday-uid", FarmCardSamples.Wednesday, true, false, "pl-PL", false),
                    ("wednesday-no-uid", FarmCardSamples.Wednesday, false, false, "pl-PL", false),
                    ("sunday-uid", FarmCardSamples.Sunday, true, false, "pl-PL", false),
                    ("sunday-no-uid", FarmCardSamples.Sunday, false, false, "pl-PL", false),
                    ("wednesday-uid-en", FarmCardSamples.Wednesday, true, false, "en-US", false),
                    ("empty-showcase", FarmCardSamples.Wednesday, true, true, "pl-PL", false),
                    ("unknown-uid", FarmCardSamples.Wednesday, true, false, "pl-PL", true),
                };
                foreach (var c in cases)
                foreach (bool dark in new[] { true, false })
                {
                    if (c.Name.EndsWith("-en", StringComparison.Ordinal) && !dark) continue;
                    var model = FarmCardSamples.Model(c.Now, c.Uid, c.Empty, c.Lang, c.Unknown);
                    ResolveIcons(model);
                    var (host, view) = Host(model, c.Lang, dark);
                    Layout(host, 404 + 2 * 28);
                    AssertFits(view, c.Name);
                    string name = $"farm-card-{c.Name}-{(dark ? "dark" : "light")}";
                    written.Add(Save(Snapshot(host), name + ".png"));
                    if (c.Name == "wednesday-uid") written.Add(Save(Snapshot(host, 1.5), name + "@150.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void RendersTheCharactersBlock()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (bool dark in new[] { true, false })
                {
                    var block = new FarmTodayBlock();
                    var model = FarmCardSamples.Model(FarmCardSamples.Wednesday, false);
                    ResolveIcons(model);
                    var host = new Border { Child = block, Padding = new Thickness(20), UseLayoutRounding = true };
                    host.Resources.MergedDictionaries.Add(Dictionary("Themes/Tokens.xaml"));
                    host.Resources.MergedDictionaries.Add(Dictionary($"Themes/Palette.{(dark ? "Dark" : "Light")}.xaml"));
                    host.Resources.MergedDictionaries.Add(Dictionary("Resources/ModernStyles.xaml"));
                    host.Resources.MergedDictionaries.Add(Dictionary($"Resources/CharacterPalette{(dark ? "Dark" : "Light")}.xaml"));
                    foreach (var d in Strings("pl-PL")) host.Resources.MergedDictionaries.Add(d);
                    host.SetResourceReference(Border.BackgroundProperty, "BgBrush");
                    block.Visibility = Visibility.Visible;
                    ((ItemsControl)block.FindName("Tiles")).ItemsSource = new FarmCardViewModel(model, "pl-PL", null).Books;
                    ((System.Windows.Documents.Run)block.FindName("DayRun")).Text = " · środa";
                    ((System.Windows.Controls.Primitives.ToggleButton)block.FindName("SettingsToggle")).IsChecked = true;
                    ((UIElement)block.FindName("SettingsPanel")).Visibility = Visibility.Visible;
                    ((TextBox)block.FindName("UidBox")).Text = "700000001";
                    ((ComboBox)block.FindName("RegionBox")).SelectedIndex = 0;
                    ((CheckBox)block.FindName("EnabledBox")).IsChecked = true;
                    Layout(host, 1000);
                    written.Add(Save(Snapshot(host), $"farm-block-{(dark ? "dark" : "light")}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        private static (Border Host, FarmCardView View) Host(FarmCardModel model, string lang, bool dark)
        {
            var view = new FarmCardView();
            var strings = Strings(lang).ToList();
            var backdrop = new Border
            {
                Padding = new Thickness(28),
                UseLayoutRounding = true,
                // A dusk scene stand-in, so the card's translucency reads as it will over the game.
                Background = new LinearGradientBrush(Color.FromRgb(0x2B, 0x3A, 0x55), Color.FromRgb(0x7A, 0x6A, 0x5A), 75),
                Child = view,
            };
            foreach (var d in strings) backdrop.Resources.MergedDictionaries.Add(d);
            view.ApplyAccent("Genshin");
            view.UsePalette(dark);
            string L(string key, string fallback) => strings.Select(d => d[key] as string).LastOrDefault(s => s != null) ?? fallback;
            view.Show(new FarmCardViewModel(model, lang, L));
            view.SetCountdown(0.62);
            return (backdrop, view);
        }

        // Nothing clipped: every text inside the card's width, the card at its fixed width.
        private static void AssertFits(FarmCardView view, string name)
        {
            Assert.AreEqual(404, view.ActualWidth, 0.5, name);
            foreach (var t in Descendants<TextBlock>(view).Where(t => t.IsVisible && t.ActualWidth > 0))
            {
                var box = t.TransformToAncestor(view).TransformBounds(new Rect(0, 0, t.ActualWidth, t.ActualHeight));
                Assert.IsTrue(box.Right <= view.ActualWidth + 0.5, $"{name}: \"{t.Text}\" runs past the card ({box.Right:0})");
            }
        }

        private static void ResolveIcons(FarmCardModel model)
        {
            var cache = CharacterImageCache.ForGame(FarmCardSettings.GameId);
            if (Fetch) Task.Run(() => Task.WhenAll(model.IconUrls().Select(u => cache.GetAsync(u)))).Wait(TimeSpan.FromSeconds(60));
            foreach (var b in model.Books) b.IconPath = cache.CachedPathOrNull(b.IconUrl);
            foreach (var r in model.Rows)
            {
                r.IconPath = cache.CachedPathOrNull(r.IconUrl);
                foreach (var s in r.Steps) s.IconPath = cache.CachedPathOrNull(s.IconUrl);
            }
        }
    }
}
