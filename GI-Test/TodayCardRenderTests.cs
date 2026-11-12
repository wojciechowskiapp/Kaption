using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Stamina;
using GI_Subtitles.Services.Today;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// Renders the Today card of each game over a game-like backdrop, and the block on Start, in Polish and English,
    /// dark and light. Output: %TEMP%\kaption-render\today-card-*.png and today-block-*.png. The data is made up to
    /// look like a real evening: a stamina reading, the manifest's resets, two periods from the week file.
    /// </summary>
    [TestClass]
    public class TodayCardRenderTests
    {
        private static readonly DateTimeOffset Now = FarmCardSamples.Wednesday;

        private static GameManifest Game(string id) => GameRegistry.Current.FindByWireId(id);

        private static IReadOnlyList<WeekPeriod> Periods(string game, bool polish) => game switch
        {
            "genshin" => new[]
            {
                new WeekPeriod(game, "banner", polish ? "Życzenia okolicznościowe wersji Luna II" : "Version Luna II Event Wishes", "Phase I", Now.AddDays(-12), Now.AddHours(31), "/news/a/"),
                new WeekPeriod(game, "event", polish ? "Wydarzenie: Opowieści spod księżyca" : "Event: Tales under the Moon", null, Now.AddDays(-9), Now.AddDays(5).AddHours(3), "/news/b/"),
            },
            "starrail" => new[]
            {
                new WeekPeriod(game, "banner", polish ? "Warp wydarzenia wersji 4.6, faza 2" : "Version 4.6 Event Warp, Phase 2", null, Now.AddDays(-14), Now.AddMinutes(50), "/news/c/"),
                new WeekPeriod(game, "event", "Planar Fissure", null, Now.AddDays(-3), Now.AddDays(4), "/news/d/"),
            },
            "zzz" => new[]
            {
                new WeekPeriod(game, "banner", polish ? "Kanał ekskluzywny wersji 3.3" : "Version 3.3 Exclusive Channel", "Phase II", Now.AddDays(-10), Now.AddDays(2).AddHours(6), "/news/e/"),
            },
            _ => Array.Empty<WeekPeriod>(),
        };

        private static TodayCardModel Model(string game, string lang, string state = "filling")
        {
            GameManifest g = Game(game);
            StaminaReading reading = g.Stamina == null || !StaminaCardModel.Supports(g) ? null : state switch
            {
                "stale" => new StaminaReading(g.Stamina.Max / 2, Now.AddHours(-70), g.Stamina.RegenSeconds / 2.0),
                "full" => new StaminaReading(g.Stamina.Max - 10, Now.AddHours(-9), g.Stamina.RegenSeconds / 2.0),
                _ => new StaminaReading(g.Stamina.Max * 7 / 10, Now.AddMinutes(-24), g.Stamina.RegenSeconds / 2.0),
            };
            FarmCardModel farm = game == "genshin" ? FarmCardSamples.Model(Now, hasUid: true, lang: lang) : null;
            if (farm != null) ResolveIcons(farm);
            return TodayCardBuilder.Build(new TodayInputs(g, Now, "europe", true, reading, null, Periods(game, lang.StartsWith("pl", StringComparison.Ordinal)),
                Array.Empty<AssistStoredReading>(), farm, lang));
        }

        private static void ResolveIcons(FarmCardModel model)
        {
            var cache = CharacterImageCache.ForGame(FarmCardSettings.GameId);
            foreach (var b in model.Books) b.IconPath = cache.CachedPathOrNull(b.IconUrl);
            foreach (var r in model.Rows)
            {
                r.IconPath = cache.CachedPathOrNull(r.IconUrl);
                foreach (var s in r.Steps) s.IconPath = cache.CachedPathOrNull(s.IconUrl);
            }
        }

        private static (Border Host, FarmCardView View) CardHost(TodayCardModel model, string lang, bool dark)
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
            view.ApplyAccent(model.GameId);
            view.UsePalette(dark);
            string L(string key, string fallback) => strings.Select(d => d[key] as string).LastOrDefault(s => s != null) ?? fallback;
            view.Show(new TodayCardViewModel(model, lang, L));
            view.SetCountdown(0.62);
            return (backdrop, view);
        }

        // Laid out for real: the element and everything above it is visible (the test host has no window, so
        // IsVisible says nothing here).
        private static bool Shown(DependencyObject element, DependencyObject root)
        {
            for (var e = element; e != null && !ReferenceEquals(e, root); e = VisualTreeHelper.GetParent(e))
                if (e is UIElement { Visibility: not Visibility.Visible }) return false;
            return true;
        }

        // Nothing clipped: every text inside the card's width, the card at its fixed width.
        private static void AssertFits(FrameworkElement view, double width, string name)
        {
            Assert.AreEqual(width, view.ActualWidth, 0.5, name);
            foreach (var t in Descendants<TextBlock>(view).Where(t => Shown(t, view) && t.ActualWidth > 0))
            {
                var box = t.TransformToAncestor(view).TransformBounds(new Rect(0, 0, t.ActualWidth, t.ActualHeight));
                Assert.IsTrue(box.Right <= view.ActualWidth + 0.5, $"{name}: \"{t.Text}\" runs past the card ({box.Right:0})");
            }
        }

        [TestMethod]
        public void RendersTheCardOfEveryGame()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                var cases = new (string Game, string State, string Lang, bool Dark)[]
                {
                    ("genshin", "filling", "pl-PL", true), ("genshin", "filling", "en-US", false),
                    ("starrail", "full", "pl-PL", true), ("starrail", "full", "en-US", true), ("starrail", "filling", "pl-PL", false),
                    ("starrail", "stale", "en-US", false),
                    ("zzz", "filling", "pl-PL", true), ("zzz", "filling", "en-US", false),
                    ("wuwa", "filling", "pl-PL", true), ("wuwa", "filling", "en-US", false),
                };
                foreach (var c in cases)
                {
                    TodayCardModel model = Model(c.Game, c.Lang, c.State);
                    Assert.IsTrue(model.HasSections, c.Game);
                    var (host, view) = CardHost(model, c.Lang, c.Dark);
                    Layout(host, 404 + 2 * 28);
                    string name = $"today-card-{c.Game}-{c.State}-{c.Lang.Substring(0, 2)}-{(c.Dark ? "dark" : "light")}";
                    AssertFits(view, 404, name);

                    // The farm parts belong to Genshin alone; the other games' cards show none of them.
                    bool farm = c.Game == "genshin";
                    foreach (string part in new[] { "WeekStrip", "WeekBlock", "BooksBlock", "TipLine" })
                        Assert.AreEqual(farm, ((FrameworkElement)view.FindName(part)).Visibility == Visibility.Visible, $"{name}: {part}");
                    Assert.AreEqual(model.Resets != null, ((FrameworkElement)view.FindName("ResetChip")).Visibility == Visibility.Visible, $"{name}: the reset chip needs reset data");
                    Assert.AreEqual(Visibility.Visible, ((FrameworkElement)view.FindName("SectionList")).Visibility, name);
                    written.Add(Save(Snapshot(host), name + ".png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void TheFarmCardAlone_LaysOutNothingNew()
        {
            RunSta(() =>
            {
                FarmCardModel farm = FarmCardSamples.Model(Now, hasUid: true);
                var view = new FarmCardView();
                var host = new Border { Padding = new Thickness(28), Child = view };
                foreach (var d in Strings("pl-PL")) host.Resources.MergedDictionaries.Add(d);
                view.Show(new FarmCardViewModel(farm, "pl-PL", null));
                Layout(host, 404 + 2 * 28);
                var sections = (FrameworkElement)view.FindName("SectionList");
                Assert.AreEqual(Visibility.Collapsed, sections.Visibility, "no sections: the list takes no room");
                Assert.AreEqual(0, sections.ActualHeight);
                foreach (string part in new[] { "WeekStrip", "WeekBlock", "ResetChip", "BooksBlock" })
                    Assert.AreEqual(Visibility.Visible, ((FrameworkElement)view.FindName(part)).Visibility, part);
            });
        }

        /// <summary>
        /// What one showing costs, for the owner (no analytics): building the model and its texts, and the card's first
        /// layout. All of it happens on the card's own timer and UI work, never on the capture tick. Printed, not asserted.
        /// </summary>
        [TestMethod]
        public void OneShowing_PrintsWhatItCosts()
        {
            RunSta(() =>
            {
                foreach (string game in new[] { "genshin", "starrail" })
                {
                    Model(game, "pl-PL"); // warm the manifests and the sample data
                    var build = System.Diagnostics.Stopwatch.StartNew();
                    const int rounds = 200;
                    TodayCardModel model = null;
                    for (int i = 0; i < rounds; i++)
                    {
                        model = Model(game, "pl-PL");
                        _ = new TodayCardViewModel(model, "pl-PL", null);
                    }
                    double perBuild = build.Elapsed.TotalMilliseconds / rounds;
                    var layout = System.Diagnostics.Stopwatch.StartNew();
                    var (host, view) = CardHost(model, "pl-PL", true);
                    Layout(host, 404 + 2 * 28);
                    Console.WriteLine($"Today card, {game}: model and texts {perBuild:0.00} ms, view and first layout {layout.Elapsed.TotalMilliseconds:0} ms, {view.ActualHeight:0} px tall.");
                    Assert.IsTrue(view.ActualHeight > 0);
                }
            });
        }

        [TestMethod]
        public void RendersTheStartBlock()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (var c in new (string Game, string Lang)[] { ("genshin", "pl-PL"), ("starrail", "pl-PL"), ("starrail", "en-US"), ("zzz", "en-US") })
                foreach (string palette in WindowRenderTests.Palettes)
                {
                    var block = new TodayBlock();
                    var host = new Border { Child = block, Padding = new Thickness(24, 8, 24, 24), UseLayoutRounding = true, SnapsToDevicePixels = true };
                    host.Resources.MergedDictionaries.Add(Dictionary("Themes/Tokens.xaml"));
                    host.Resources.MergedDictionaries.Add(Dictionary($"Themes/Palette.{palette}.xaml"));
                    host.Resources.MergedDictionaries.Add(Dictionary("Resources/ModernStyles.xaml"));
                    host.Resources.MergedDictionaries.Add(Dictionary("Themes/Shell.xaml"));
                    foreach (var d in Strings(c.Lang)) host.Resources.MergedDictionaries.Add(d);
                    host.SetResourceReference(Border.BackgroundProperty, "BgBrush");
                    string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;

                    TodayCardModel model = Model(c.Game, c.Lang);
                    block.Apply(new TodayCardViewModel(model, c.Lang, L), string.Format(L("TodayCard_Eyebrow_Format", "Today in {0}"), model.GameName));
                    Assert.AreEqual(Visibility.Visible, block.Visibility, c.Game);
                    // The reset times are the server's: the choice sits with them, named in the interface language.
                    var servers = TodayCardSettings.Servers(Game(c.Game), L);
                    block.SetServers(servers, "europe");
                    var box = (ComboBox)block.FindName("ServerBox");
                    Assert.AreEqual(3, box.Items.Count, c.Game);
                    Assert.AreEqual("europe", ((TodayServerChoice)box.SelectedItem).Id);
                    Assert.AreEqual(c.Lang == "pl-PL" ? "Europa" : "Europe", ((TodayServerChoice)box.SelectedItem).Name);
                    Layout(host, 912 + 48);
                    AssertFits(block, 912, $"block-{c.Game}-{c.Lang}");
                    written.Add(Save(Snapshot(host), $"today-block-{c.Game}-{c.Lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));
                    // A saved choice is the one selected; an id the game does not have falls back to its first server.
                    block.SetServers(servers, "america");
                    Assert.AreEqual("america", ((TodayServerChoice)box.SelectedItem).Id);
                    block.SetServers(servers, "mars");
                    Assert.AreEqual("europe", ((TodayServerChoice)box.SelectedItem).Id);
                }

                // Nothing to say: no block.
                var empty = new TodayBlock();
                empty.Apply(new TodayCardViewModel(TodayCardBuilder.Build(new TodayInputs(Game("wuwa"), Now, "europe", true, null, null, null, null, null, "en-US")), "en-US", null), "x");
                Assert.AreEqual(Visibility.Collapsed, empty.Visibility);
                empty.Apply(null, null);
                // A game without reset data has no server to choose.
                Assert.AreEqual(0, TodayCardSettings.Servers(Game("wuwa"), null).Count);
                empty.SetServers(TodayCardSettings.Servers(Game("wuwa"), null), null);
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)empty.FindName("ServerRow")).Visibility);
                Assert.AreEqual(Visibility.Collapsed, empty.Visibility);
            });
            foreach (string p in written) Console.WriteLine(p);
        }
    }
}
