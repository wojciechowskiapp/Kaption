using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GI_Subtitles.Services.Codes;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// The Start page's codes card, filled through the card's own Apply with a list shaped like the live answer: on its
    /// own (dark and light, Polish and English, folded, Wuthering Waves with its in-game path) and in place on the Start
    /// page. Output: %TEMP%\kaption-render\codes-card-*.png and codes-start-*.png.
    /// </summary>
    [TestClass]
    public class GameCodesCardRenderTests
    {
        private static readonly DateTimeOffset Now = CodesSamples.Now;

        private static RedeemCodeList Genshin() => new("genshin", Now.AddMinutes(-14), new[]
        {
            new RedeemCode("CDOF4X83H7X2", new[] { new CodeReward("Primogem", 60), new CodeReward("Mora", 10000) }, Now.AddDays(-1), null),
            new RedeemCode("MIJIQ23V2437", new[] { new CodeReward("Mora", 10000), new CodeReward("Adventurer's Experience", 10),
                new CodeReward("Fine Enhancement Ore", 5), new CodeReward("Stir-Fried Fish Noodles", 5), new CodeReward("Jueyun Chili Chicken", 5) }, Now.AddDays(-5), Now.AddHours(7.25)),
            new RedeemCode("GENSHINGIFT", new[] { new CodeReward("Primogem", 50), new CodeReward("Hero's Wit", 3) }, Now.AddDays(-30), Now.AddDays(12)),
            new RedeemCode("XS6KFK8XRBB5", new[] { new CodeReward("Mora", 5000) }, Now.AddDays(-40), null),
        });

        private static RedeemCodeList Zzz() => new("zzz", Now.AddMinutes(-3), new[]
        {
            new RedeemCode("HOLLOWRUN33", new[] { new CodeReward("Polychrome", 20), new CodeReward("Denny", 2222) }, Now.AddDays(-2), null),
            new RedeemCode("ZENLESSGIFT", new[] { new CodeReward("Polychrome", 50), new CodeReward("Official Investigator Log", 3) }, Now.AddDays(-9), null),
        });

        private static RedeemCodeList WuWa() => new("wuwa", Now.AddMinutes(-8), new[]
        {
            new RedeemCode("WUTHERINGGIFT", new[] { new CodeReward("Astrite", 50), new CodeReward("Premium Resonance Potion", 2), new CodeReward("Shell Credit", 10000) }, Now.AddDays(-5), null),
        });

        /// <summary>The date words of a language, from its own dictionary (call on the STA thread).</summary>
        private static CodeDateWords Words(string lang)
        {
            var host = new Border();
            foreach (var d in Strings(lang)) host.Resources.MergedDictionaries.Add(d);
            return GameCodesCard.DateWords((key, fallback) => host.TryFindResource(key) as string ?? fallback);
        }

        private static CodesCardView View(string game, RedeemCodeList list, string lang) =>
            CodesCardModel.Build(CodesSamples.Game(game), list, list.CheckedAt, Now, CodesSchedule.Default.StaleAfter, lang, new CultureInfo(lang),
                words: Words(lang), zone: TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"));

        [TestMethod]
        public void RendersTheCardInEveryState()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                var cases = new (string Name, string Game, Func<RedeemCodeList> List, string Lang, bool Folded)[]
                {
                    ("genshin-pl", "genshin", Genshin, "pl-PL", false),
                    ("genshin-en", "genshin", Genshin, "en-US", false),
                    ("genshin-folded-pl", "genshin", Genshin, "pl-PL", true),
                    ("zzz-en", "zzz", Zzz, "en-US", false),
                    ("wuwa-pl", "wuwa", WuWa, "pl-PL", false),
                };
                foreach (var c in cases)
                foreach (string palette in WindowRenderTests.Palettes)
                {
                    var card = new GameCodesCard();
                    var host = new Border { Child = card, Padding = new Thickness(24, 8, 24, 24), UseLayoutRounding = true, SnapsToDevicePixels = true };
                    host.Resources.MergedDictionaries.Add(Dictionary("Themes/Tokens.xaml"));
                    host.Resources.MergedDictionaries.Add(Dictionary($"Themes/Palette.{palette}.xaml"));
                    host.Resources.MergedDictionaries.Add(Dictionary("Resources/ModernStyles.xaml"));
                    foreach (var d in Strings(c.Lang)) host.Resources.MergedDictionaries.Add(d);
                    host.SetResourceReference(Border.BackgroundProperty, "BgBrush");
                    string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;

                    var view = View(c.Game, c.List(), c.Lang);
                    Assert.IsTrue(view.Visible, c.Name);
                    card.Apply(view, c.Folded, L, new CultureInfo(c.Lang));
                    Assert.AreEqual(Visibility.Visible, card.Visibility, c.Name);
                    Assert.AreEqual(c.Folded, card.IsFolded, c.Name);
                    Layout(host, 912 + 48);
                    AssertFits(card, c.Name);
                    written.Add(Save(Snapshot(host), $"codes-card-{c.Name}-{palette.ToLowerInvariant()}.png"));
                    if (c.Name == "genshin-pl" && palette == "Dark") written.Add(Save(Snapshot(host, 1.5), $"codes-card-{c.Name}-{palette.ToLowerInvariant()}@150.png"));
                }

                // Text checks on one Polish render: title, plural count, the link with the remainder, the in-game path.
                var probe = new GameCodesCard();
                var strings = new Border { Child = probe };
                foreach (var d in Strings("pl-PL")) strings.Resources.MergedDictionaries.Add(d);
                string Lpl(string key, string fallback) => strings.TryFindResource(key) as string ?? fallback;
                probe.Apply(View("genshin", Genshin(), "pl-PL"), false, Lpl, new CultureInfo("pl-PL"));
                var title = ((System.Windows.Documents.Run)probe.FindName("TitleRun")).Text;
                var count = ((System.Windows.Documents.Run)probe.FindName("CountRun")).Text;
                Assert.AreEqual("Kody do Genshin Impact", title);
                StringAssert.EndsWith(count, "4 aktywne");
                Assert.AreEqual("Wszystkie kody (+1)", ((Button)probe.FindName("AllCodesButton")).Content);
                // The rows: the code ending soon leads with its mark, then the new one; each says its dates in Polish.
                var rows = probe.Current.Rows;
                CollectionAssert.AreEqual(new[] { "MIJIQ23V2437", "CDOF4X83H7X2", "GENSHINGIFT" }, rows.Select(r => r.Code).ToArray());
                Assert.AreEqual("Kończy się za 7 h", rows[0].EndsIn);
                Assert.IsTrue(rows[1].IsNew);
                Assert.AreEqual("Dodany 8 listopada · bez podanej daty końca", rows[1].Dates);
                StringAssert.StartsWith(rows[2].Dates, "Dodany 10 października · ważny do 21 listopada, ");
                // The same codes with a countdown an hour on: the row objects stay (focus and "Copied" with them), their text moves.
                var list = (ItemsControl)probe.FindName("Rows");
                var shownBefore = list.ItemsSource.Cast<GameCodesCard.CodeRowView>().ToList();
                var changed = new List<string>();
                shownBefore[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName);
                var later = CodesCardModel.Build(CodesSamples.Game("genshin"), Genshin(), Now, Now.AddHours(1), TimeSpan.FromDays(1), "pl-PL", new CultureInfo("pl-PL"),
                    words: Words("pl-PL"), zone: TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"));
                probe.Apply(later, false, Lpl, new CultureInfo("pl-PL"));
                var shownAfter = list.ItemsSource.Cast<GameCodesCard.CodeRowView>().ToList();
                Assert.IsTrue(shownBefore.SequenceEqual(shownAfter), "the same row objects after a countdown change");
                Assert.AreEqual("Kończy się za 6 h", shownAfter[0].EndsIn);
                CollectionAssert.AreEqual(new[] { "EndsIn" }, changed, "only what changed is announced");
                // Another list of codes replaces the rows.
                probe.Apply(View("zzz", Zzz(), "pl-PL"), false, Lpl, new CultureInfo("pl-PL"));
                Assert.IsFalse(list.ItemsSource.Cast<GameCodesCard.CodeRowView>().Any(r => shownBefore.Contains(r)));
                probe.Apply(View("wuwa", WuWa(), "pl-PL"), false, Lpl, new CultureInfo("pl-PL"));
                StringAssert.StartsWith(((TextBlock)probe.FindName("InGameHint")).Text, "Kod wpiszesz w grze: Terminal › ");
                probe.Apply(CodesCardView.Hidden(CodesCardHidden.NeverChecked), false, Lpl, null);
                Assert.AreEqual(Visibility.Collapsed, probe.Visibility, "never checked: no card, no 'no codes' line");
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
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", lang, out var window, palette);
                    ShellRenderTests.Fill(host, window, ShellRenderTests.Scene.Stopped, lang);
                    string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                    var card = (GameCodesCard)window.FindName("CodesCard");
                    Assert.IsNotNull(card, "the Start page holds the card");
                    Assert.AreEqual(Visibility.Collapsed, card.Visibility, "hidden until a list with active codes arrives");
                    card.Apply(View("genshin", Genshin(), lang), false, L, new CultureInfo(lang));
                    Layout(host, 1280, 1100);
                    written.Add(Save(Snapshot(host), $"codes-start-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        /// <summary>
        /// The clock moves a row (a code starts ending soon and goes first) while the keyboard is on another row's Copy:
        /// that row keeps its object and the card gives the keyboard back to its Copy; a countdown that only changes text
        /// asks for nothing; a code that leaves hands the keyboard to the first row.
        /// </summary>
        [TestMethod]
        public void KeepsTheKeyboardOnTheSameCodeWhenTheClockReordersTheRows()
        {
            RunSta(() =>
            {
                var game = CodesSamples.Game("genshin");
                var warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
                var pl = new CultureInfo("pl-PL");
                RedeemCodeList List(params RedeemCode[] codes) => new("genshin", Now.AddMinutes(-5), codes, 0, Now.AddDays(-40));
                var stays = new RedeemCode("STAYSFOCUSED", new[] { new CodeReward("Primogem", 60) }, Now.AddHours(-3), null);
                var other = new RedeemCode("OTHERCODE", new[] { new CodeReward("Mora", 10000) }, Now.AddDays(-5), null);
                var rises = new RedeemCode("RISESLATER", new[] { new CodeReward("Primogem", 100) }, Now.AddDays(-9), Now + CodeDates.EndsSoonWithin + TimeSpan.FromMinutes(10));
                CodesCardView At(DateTimeOffset now, params RedeemCode[] codes) =>
                    CodesCardModel.Build(game, List(codes), Now, now, TimeSpan.FromDays(1), "pl-PL", pl, words: Words("pl-PL"), zone: warsaw);

                var card = new GameCodesCard();
                var host = new Border { Child = card, Padding = new Thickness(24) };
                host.Resources.MergedDictionaries.Add(Dictionary("Themes/Tokens.xaml"));
                host.Resources.MergedDictionaries.Add(Dictionary("Themes/Palette.Dark.xaml"));
                host.Resources.MergedDictionaries.Add(Dictionary("Resources/ModernStyles.xaml"));
                foreach (var d in Strings("pl-PL")) host.Resources.MergedDictionaries.Add(d);
                string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                var asked = new List<UIElement>();
                card.FocusControl = control => { asked.Add(control); return true; };

                card.Apply(At(Now, stays, other, rises), false, L, pl);
                Layout(host, 960);
                var list = (ItemsControl)card.FindName("Rows");
                var rows = list.ItemsSource.Cast<GameCodesCard.CodeRowView>().ToList();
                CollectionAssert.AreEqual(new[] { "STAYSFOCUSED", "OTHERCODE", "RISESLATER" }, rows.Select(r => r.Code).ToArray());
                Button copy = card.RowButton(rows[0], GameCodesCard.CopyPart);
                Assert.IsNotNull(copy, "the row's Copy button is on screen");
                Assert.IsNotNull(card.RowButton(rows[0], GameCodesCard.RedeemPart), "and its Redeem button");
                // The keyboard lands on that Copy.
                copy.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, copy) { RoutedEvent = Keyboard.GotKeyboardFocusEvent });

                // Five minutes on nothing moves: nothing is asked.
                card.Apply(At(Now.AddMinutes(5), stays, other, rises), false, L, pl);
                Assert.AreEqual(0, asked.Count, "the same rows in the same order: the keyboard is left alone");

                // Twenty minutes on RISESLATER ends soon and goes first; the focused code is second now.
                card.Apply(At(Now.AddMinutes(20), stays, other, rises), false, L, pl);
                var after = list.ItemsSource.Cast<GameCodesCard.CodeRowView>().ToList();
                CollectionAssert.AreEqual(new[] { "RISESLATER", "STAYSFOCUSED", "OTHERCODE" }, after.Select(r => r.Code).ToArray());
                Assert.AreSame(rows[0], after[1], "the focused code keeps its row object");
                Assert.AreSame(rows[1], after[2]);
                Assert.AreEqual(1, asked.Count, "the keyboard is given back once");
                var back = asked[0] as Button;
                Assert.IsNotNull(back);
                Assert.AreEqual(GameCodesCard.CopyPart, back.Name);
                Assert.AreEqual("STAYSFOCUSED", ((GameCodesCard.CodeRowView)back.DataContext).Code);
                Assert.AreSame(card.RowButton(after[1], GameCodesCard.CopyPart), back, "the button of the row as it is on screen now");

                // The focused code leaves the list: the keyboard goes to the first row's Copy, not into nowhere.
                card.Apply(At(Now.AddMinutes(21), other, rises), false, L, pl);
                Assert.AreEqual(2, asked.Count);
                var fallback = (Button)asked[1];
                Assert.AreEqual(GameCodesCard.CopyPart, fallback.Name);
                Assert.AreEqual("RISESLATER", ((GameCodesCard.CodeRowView)fallback.DataContext).Code);

                // The player tabs out of the rows: a later reorder gives nothing back.
                var elsewhere = (Button)card.FindName("AllCodesButton");
                fallback.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, fallback, elsewhere) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                card.Apply(At(Now.AddMinutes(22), stays, other, rises), false, L, pl);
                Assert.AreEqual(2, asked.Count, "focus that left the rows is not pulled back");
            });
        }

        private static void AssertFits(GameCodesCard card, string name)
        {
            foreach (var t in Descendants<TextBlock>(card).Where(t => t.IsVisible && t.ActualWidth > 0))
            {
                var box = t.TransformToAncestor(card).TransformBounds(new Rect(0, 0, t.ActualWidth, t.ActualHeight));
                Assert.IsTrue(box.Right <= card.ActualWidth + 0.5, $"{name}: \"{t.Text}\" runs past the card ({box.Right:0} > {card.ActualWidth:0})");
            }
        }
    }
}
