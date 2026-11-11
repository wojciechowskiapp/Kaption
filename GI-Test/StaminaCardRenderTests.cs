using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Stamina;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// The Start page's stamina card, drawn through the card's own Apply: switched off (the sentence that says what
    /// would be read), waiting for the first number, tracking and full, in Polish and English, dark and light, on its
    /// own and in place on Start; and the tray notice's words. Output: %TEMP%\kaption-render\stamina-*.png.
    /// </summary>
    [TestClass]
    public class StaminaCardRenderTests
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 10, 13, 4, 0, TimeSpan.Zero);
        private static readonly TimeSpan Lead = TimeSpan.FromMinutes(StaminaSettings.DefaultNoticeLeadMinutes);

        private static GameManifest Game(string id) => GameRegistry.Current.FindByWireId(id);

        private static Border Host(UIElement child, string palette, string lang)
        {
            var host = new Border { Child = child, Padding = new Thickness(24, 8, 24, 24), UseLayoutRounding = true, SnapsToDevicePixels = true };
            host.Resources.MergedDictionaries.Add(Dictionary("Themes/Tokens.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary($"Themes/Palette.{palette}.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary("Resources/ModernStyles.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary("Themes/Shell.xaml"));
            foreach (var d in Strings(lang)) host.Resources.MergedDictionaries.Add(d);
            host.SetResourceReference(Border.BackgroundProperty, "BgBrush");
            return host;
        }

        private static StaminaCardView View(string game, string state, string lang, Func<string, string, string> l)
        {
            var culture = new CultureInfo(lang);
            GameManifest g = Game(game);
            return state switch
            {
                "off" => StaminaCardModel.Build(g, false, true, Lead, null, null, Now, lang, l, culture),
                "waiting" => StaminaCardModel.Build(g, true, true, Lead, null, null, Now, lang, l, culture),
                "full" => StaminaCardModel.Build(g, true, true, Lead, new StaminaReading(g.Stamina.Max - 3, Now.AddHours(-9), 200), null, Now, lang, l, culture),
                _ => StaminaCardModel.Build(g, true, true, Lead, new StaminaReading(143, Now.AddMinutes(-5), 310), null, Now, lang, l, culture),
            };
        }

        [TestMethod]
        public void RendersTheCardInEveryState()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                var cases = new (string Game, string State, string Lang)[]
                {
                    ("genshin", "off", "pl-PL"), ("genshin", "off", "en-US"),
                    ("genshin", "waiting", "pl-PL"), ("starrail", "waiting", "en-US"),
                    ("genshin", "tracking", "pl-PL"), ("genshin", "tracking", "en-US"), ("wuwa", "tracking", "pl-PL"),
                    ("starrail", "full", "en-US"), ("genshin", "full", "pl-PL"),
                };
                foreach (var c in cases)
                foreach (string palette in WindowRenderTests.Palettes)
                {
                    var card = new StaminaCard();
                    Border host = Host(card, palette, c.Lang);
                    string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                    StaminaCardView view = View(c.Game, c.State, c.Lang, L);
                    Assert.IsTrue(view.Visible, $"{c.Game} {c.State}");
                    card.Apply(view);
                    Assert.AreEqual(Visibility.Visible, card.Visibility);
                    Layout(host, 912 + 48);
                    AssertFits(card, $"{c.Game}-{c.State}-{c.Lang}");

                    var toggle = (CheckBox)card.FindName("ReadToggle");
                    Assert.AreEqual(c.State != "off", toggle.IsChecked == true, "the switch shows the player's choice");
                    var notice = (CheckBox)card.FindName("NoticeToggle");
                    Assert.AreEqual(c.State == "off" ? Visibility.Collapsed : Visibility.Visible, notice.Visibility, "the notice choice only once reading is on");
                    var fresh = (FrameworkElement)card.FindName("FreshRow");
                    Assert.AreEqual(c.State is "tracking" or "full" ? Visibility.Visible : Visibility.Collapsed, fresh.Visibility);
                    written.Add(Save(Snapshot(host), $"stamina-card-{c.Game}-{c.State}-{c.Lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));
                }

                // The switches report only the player's own flip, and a game without a measured screen has no card.
                var probe = new StaminaCard();
                Border probeHost = Host(probe, "Dark", "en-US");
                string Len(string key, string fallback) => probeHost.TryFindResource(key) as string ?? fallback;
                int flips = 0;
                probe.ReadChanged += _ => flips++;
                probe.Apply(View("genshin", "off", "en-US", Len));
                probe.Apply(View("genshin", "tracking", "en-US", Len));
                Assert.AreEqual(0, flips, "drawing the card is not a flip");
                ((CheckBox)probe.FindName("ReadToggle")).IsChecked = false;
                Assert.AreEqual(1, flips);
                probe.Apply(StaminaCardModel.Build(Game("zzz"), true, true, Lead, null, null, Now, "en-US", Len, CultureInfo.InvariantCulture));
                Assert.AreEqual(Visibility.Collapsed, probe.Visibility, "Zenless Zone Zero is not measured: no card");
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void RendersTheCardOnTheStartPage()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string lang in new[] { "pl-PL", "en-US" })
                foreach (string state in new[] { "off", "tracking" })
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", lang, out var window, palette);
                    ShellRenderTests.Fill(host, window, ShellRenderTests.Scene.Stopped, lang);
                    string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                    var card = (StaminaCard)window.FindName("StaminaCard");
                    Assert.IsNotNull(card, "the Start page holds the card");
                    card.Apply(View("genshin", state, lang, L));
                    Layout(host, 1280, 1100);
                    written.Add(Save(Snapshot(host), $"stamina-start-{state}-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        /// <summary>The tray notice is drawn by Windows; this renders its two texts, as the app hands them over.</summary>
        [TestMethod]
        public void RendersTheNoticeWords()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string lang in new[] { "pl-PL", "en-US" })
                {
                    var stack = new StackPanel();
                    Border host = Host(stack, "Dark", lang);
                    string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                    var culture = new CultureInfo(lang);
                    GameManifest game = Game("genshin");
                    var reading = new StaminaReading(143, Now.AddHours(-7), 310);
                    DateTimeOffset due = StaminaNotice.DueAt(game.Stamina, reading, Lead);
                    foreach (DateTimeOffset at in new[] { due, StaminaClock.FullAt(game.Stamina, reading).AddHours(1) })
                    {
                        StaminaNoticeText text = StaminaText.Notice(game.Stamina, reading, at, L, culture);
                        Assert.IsFalse(string.IsNullOrWhiteSpace(text.Title) || string.IsNullOrWhiteSpace(text.Body));
                        var box = new Border { Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(8), Width = 380, HorizontalAlignment = HorizontalAlignment.Left };
                        box.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
                        var lines = new StackPanel();
                        var app = new TextBlock { Text = "Kaption", FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
                        app.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
                        var title = new TextBlock { Text = text.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                        var body = new TextBlock { Text = text.Body, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                        body.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                        lines.Children.Add(app);
                        lines.Children.Add(title);
                        lines.Children.Add(body);
                        box.Child = lines;
                        stack.Children.Add(box);
                    }
                    Layout(host, 380 + 48);
                    written.Add(Save(Snapshot(host), $"stamina-notice-{lang.Substring(0, 2)}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        private static void AssertFits(StaminaCard card, string name)
        {
            foreach (var t in Descendants<TextBlock>(card).Where(t => t.IsVisible && t.ActualWidth > 0))
            {
                var box = t.TransformToAncestor(card).TransformBounds(new Rect(0, 0, t.ActualWidth, t.ActualHeight));
                Assert.IsTrue(box.Right <= card.ActualWidth + 0.5, $"{name}: \"{t.Text}\" runs past the card ({box.Right:0} > {card.ActualWidth:0})");
            }
        }
    }
}
