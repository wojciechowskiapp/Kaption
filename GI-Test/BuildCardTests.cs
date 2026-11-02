using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GI_Subtitles.Services.Cards;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;
// GI_Test has a Config of its own; the build card reads and writes the app's.
using AppConfig = GI_Subtitles.Core.Config.Config;

namespace GI_Test
{
    /// <summary>
    /// "Karta buildu": the maker's URL (game and language, the UID only when there is one), the UID kept per game in
    /// Config.json, the click's flow (remembered, read off the screen, or none) and the Characters header button.
    /// </summary>
    [TestClass]
    public class BuildCardTests
    {
        [TestCleanup]
        public void ForgetEverything() => BuildCardMemory.ForgetAll();

        // ── the URL ───────────────────────────────────────────────────────

        [TestMethod]
        [DataRow("genshin", "735366201", "pl", "https://kaption.one/pl/moj-build/?game=genshin&uid=735366201")]
        [DataRow("genshin", "735366201", "pl-PL", "https://kaption.one/pl/moj-build/?game=genshin&uid=735366201")]
        [DataRow("starrail", "809162009", "en-US", "https://kaption.one/my-build/?game=starrail&uid=809162009")]
        [DataRow("zzz", "1300025292", "en", "https://kaption.one/my-build/?game=zzz&uid=1300025292")]
        [DataRow("zzz", "1300025292", "ja-JP", "https://kaption.one/my-build/?game=zzz&uid=1300025292")]
        [DataRow("zzz", "1300025292", null, "https://kaption.one/my-build/?game=zzz&uid=1300025292")]
        public void TheUrl_CarriesTheGameAndTheUid_InTheInterfacesLanguage(string game, string uid, string lang, string url)
        {
            Assert.AreEqual(url, BuildCardLink.Url(game, uid, lang));
        }

        [TestMethod]
        public void WithoutAUid_ThePageOpensOnItsForm()
        {
            Assert.AreEqual("https://kaption.one/pl/moj-build/?game=genshin", BuildCardLink.Url("genshin", null, "pl-PL"));
            Assert.AreEqual("https://kaption.one/my-build/?game=starrail", BuildCardLink.Url("starrail", "", "en-US"));
            Assert.AreEqual("https://kaption.one/my-build/?game=zzz", BuildCardLink.Url("zzz", "12345", "en-US"), "not a UID: left out");
            Assert.AreEqual("https://kaption.one/my-build/?game=zzz", BuildCardLink.Url("zzz", "735366201&x=1", "en-US"));
            Assert.AreEqual("https://kaption.one/my-build/", BuildCardLink.Url(null, null, "en-US"), "an unknown game: the bare page");
            Assert.AreEqual("https://kaption.one/pl/moj-build/?uid=735366201", BuildCardLink.Url("wuthering", "735366201", "pl"));
        }

        [TestMethod]
        [DataRow("Genshin", "genshin")]
        [DataRow("genshin", "genshin")]
        [DataRow("StarRail", "starrail")]
        [DataRow("ZZZ", "zzz")]
        [DataRow("zzz", "zzz")]
        [DataRow("Zenless", "zzz")]
        [DataRow("Zenless Zone Zero", "zzz")]
        [DataRow("WutheringWaves", null)]
        [DataRow("", null)]
        [DataRow((string)null, null)]
        public void EveryKaptionGame_MapsToTheMakersGame(string gameId, string card)
        {
            Assert.AreEqual(card, BuildCardLink.CardGame(gameId));
        }

        [TestMethod]
        public void AUid_IsNineOrTenDigitsWithoutALeadingZero()
        {
            foreach (string ok in new[] { "735366201", "618285856", "809162009", "1300025292", "1812345678" })
                Assert.IsTrue(BuildCardLink.IsUid(ok), ok);
            foreach (string bad in new[] { null, "", "12345678", "73536620123", "035366201", "73536620a", " 735366201", "٧٣٥٣٦٦٢٠١" })
                Assert.IsFalse(BuildCardLink.IsUid(bad), bad ?? "null");
        }

        // ── Config.json ───────────────────────────────────────────────────

        private static JsonObject SavedFile() =>
            JsonNode.Parse(File.ReadAllText(Path.Combine(TestEnvironment.ConfigDir, "Config.json"))) as JsonObject;

        [TestMethod]
        public void TheLastUidPerGame_RoundTripsThroughConfigJson()
        {
            Assert.IsTrue(BuildCardMemory.Remember("Genshin", "735366201"));
            Assert.IsTrue(BuildCardMemory.Remember("Zenless", "1300025292"), "an alias lands on the game's own key");
            Assert.IsTrue(BuildCardMemory.Remember("StarRail", "809162009"));

            var file = SavedFile();
            Assert.AreEqual("735366201", (string)file["BuildCard.Uid.Genshin"], "written to Config.json as a string");
            Assert.AreEqual("809162009", (string)file["BuildCard.Uid.StarRail"]);
            Assert.AreEqual("1300025292", (string)file["BuildCard.Uid.ZZZ"]);

            Assert.AreEqual("735366201", BuildCardMemory.Recall("genshin"));
            Assert.AreEqual("1300025292", BuildCardMemory.Recall("ZZZ"));
            CollectionAssert.AreEqual(new[] { ("Genshin", "735366201"), ("StarRail", "809162009"), ("ZZZ", "1300025292") },
                BuildCardMemory.All().ToArray(), "every game's, in the app's order");

            // A newer read replaces the old one.
            Assert.IsTrue(BuildCardMemory.Remember("Genshin", "618285856"));
            Assert.AreEqual("618285856", (string)SavedFile()["BuildCard.Uid.Genshin"]);

            // "Zapomnij UID" forgets all three, keys and all.
            Assert.AreEqual(3, BuildCardMemory.ForgetAll());
            var after = SavedFile();
            Assert.IsFalse(after.ContainsKey("BuildCard.Uid.Genshin") || after.ContainsKey("BuildCard.Uid.StarRail")
                           || after.ContainsKey("BuildCard.Uid.ZZZ"), "no key is left behind");
            Assert.IsNull(BuildCardMemory.Recall("Genshin"));
            Assert.AreEqual(0, BuildCardMemory.All().Count);
            Assert.AreEqual(0, BuildCardMemory.ForgetAll(), "nothing left to forget");
        }

        [TestMethod]
        public void OnlyAUid_IsKept_AndAnEditedValueIsIgnored()
        {
            Assert.IsFalse(BuildCardMemory.Remember("Genshin", "12345"));
            Assert.IsFalse(BuildCardMemory.Remember("Genshin", null));
            Assert.IsFalse(BuildCardMemory.Remember("WutheringWaves", "735366201"), "a game the maker has no cards for");
            Assert.IsFalse(AppConfig.Has("BuildCard.Uid.Genshin"), "nothing written for a bad value");
            Assert.IsFalse(AppConfig.Has("BuildCard.Uid.WutheringWaves"));

            AppConfig.Set("BuildCard.Uid.StarRail", "not a uid"); // a hand-edited file
            Assert.IsNull(BuildCardMemory.Recall("StarRail"), "never sent to the page");
            Assert.AreEqual(1, BuildCardMemory.ForgetAll(), "but still forgotten");
        }

        // ── the click ─────────────────────────────────────────────────────

        /// <summary>A read off the screen that found <paramref name="uid"/> (null: none), ending as <paramref name="outcome"/>.</summary>
        internal static Task<UidReadResult> OnScreen(string uid, UidReadOutcome? outcome = null) => Task.FromResult(
            new UidReadResult(uid, outcome ?? (uid == null ? UidReadOutcome.NotFound : UidReadOutcome.Read),
                uid == null ? UidCorner.None : UidCorner.BottomRight, TimeSpan.FromMilliseconds(40)));

        [TestMethod]
        public async Task TheClick_ReadsOnce_RemembersTheUid_AndOpensWithItAfterwards()
        {
            int reads = 0;
            Func<Task<UidReadResult>> screen = () => { reads++; return OnScreen("735366201"); };

            var first = await BuildCardEntry.ResolveAsync("Genshin", "pl-PL", screen);
            Assert.AreEqual("https://kaption.one/pl/moj-build/?game=genshin&uid=735366201", first.Url);
            Assert.AreEqual(UidSource.Screen, first.Source);
            Assert.AreEqual("735366201", BuildCardMemory.Recall("Genshin"), "remembered for the next click");

            var second = await BuildCardEntry.ResolveAsync("Genshin", "en-US", screen);
            Assert.AreEqual("https://kaption.one/my-build/?game=genshin&uid=735366201", second.Url);
            Assert.AreEqual(UidSource.Remembered, second.Source);
            Assert.AreEqual(1, reads, "the second click opens straight away, without reading the screen");
        }

        [TestMethod]
        public async Task NoUidOnScreen_OpensThePageWithoutOne_AndRemembersNothing()
        {
            foreach (string read in new[] { null, "", "12:45", "UID 735366201" })
            {
                var target = await BuildCardEntry.ResolveAsync("StarRail", "pl", () => OnScreen(read));
                Assert.AreEqual("https://kaption.one/pl/moj-build/?game=starrail", target.Url, read ?? "null");
                Assert.IsNull(target.Uid);
                Assert.AreEqual(UidSource.None, target.Source);
            }
            Assert.AreEqual(0, BuildCardMemory.All().Count);
        }

        [TestMethod]
        public async Task TheUidIsKeptPerGame()
        {
            BuildCardMemory.Remember("Genshin", "735366201");
            var zzz = await BuildCardEntry.ResolveAsync("ZZZ", "en-US", () => OnScreen(null));
            Assert.AreEqual("https://kaption.one/my-build/?game=zzz", zzz.Url, "Genshin's UID is not Zenless Zone Zero's");
        }

        [TestMethod]
        public async Task TheTarget_SaysHowTheReadEnded_OnlyWhenThereWasOne()
        {
            var loading = await BuildCardEntry.ResolveAsync("Genshin", "pl", () => OnScreen(null, UidReadOutcome.NoReader));
            Assert.AreEqual(UidReadOutcome.NoReader, loading.ReadOutcome);
            Assert.IsNull(loading.Uid);

            var read = await BuildCardEntry.ResolveAsync("Genshin", "pl", () => OnScreen("618285856"));
            Assert.AreEqual(UidReadOutcome.Read, read.ReadOutcome);
            Assert.AreEqual(UidSource.Screen, read.Source);

            int reads = 0;
            var remembered = await BuildCardEntry.ResolveAsync("Genshin", "pl", () => { reads++; return OnScreen(null); });
            Assert.IsNull(remembered.ReadOutcome, "a remembered UID needs no read");
            Assert.AreEqual(UidSource.Remembered, remembered.Source);
            var other = await BuildCardEntry.ResolveAsync("WutheringWaves", "pl", () => { reads++; return OnScreen(null); });
            Assert.IsNull(other.ReadOutcome, "nor does a game the maker has no cards for");
            Assert.AreEqual(0, reads);
        }

        // ── what the read blacks out ──────────────────────────────────────

        /// <summary>
        /// The overlay window is the dialogue area's width plus 200 DIP and see-through beside its panel, so the UID read
        /// blacks out the panel it draws, not the whole window: beside a short line the game (and its UID) stays readable,
        /// a long line is covered whole, and so is a panel still shrinking from a longer one.
        /// </summary>
        [TestMethod]
        public void TheOverlaysMaskedArea_IsItsPanel_NotTheWholeWindow()
        {
            RunSta(() =>
            {
                var card = new GI_Subtitles.Services.Rendering.SubtitleCard { SmoothResize = false };
                card.SubtitleText.Text = "Hej.";
                // As MainWindow is built: a see-through strip of a window with the panel centred in it. Opacity 0 and never
                // activated, so nothing shows on the screen of whoever runs the tests.
                var window = new Window
                {
                    WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0, ShowInTaskbar = false,
                    ShowActivated = false, ResizeMode = ResizeMode.NoResize, Left = 0, Top = 0, Width = 1736, Height = 110,
                    Background = System.Windows.Media.Brushes.Transparent,
                    Content = new Grid { Background = System.Windows.Media.Brushes.Transparent, Children = { card } },
                };
                try
                {
                    window.Show();
                    DoEvents();
                    static System.Drawing.Rectangle Box(FrameworkElement e)
                    {
                        var a = e.PointToScreen(new Point(0, 0));
                        var b = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
                        return System.Drawing.Rectangle.FromLTRB((int)Math.Floor(a.X), (int)Math.Floor(a.Y), (int)Math.Ceiling(b.X), (int)Math.Ceiling(b.Y));
                    }
                    var whole = Box(window);

                    var shortLine = MainWindow.PanelScreenArea(window, card);
                    Assert.IsTrue(whole.Contains(shortLine), $"inside the window: {shortLine} in {whole}");
                    Assert.IsTrue(shortLine.Contains(Box(card)), "the whole panel");
                    Assert.IsTrue(shortLine.Width < whole.Width / 3, $"a short line leaves the window's sides readable ({shortLine.Width} of {whole.Width} px)");

                    card.SubtitleText.Text = "Chodźmy najpierw do Gildii Poszukiwaczy Przygód, a potem coś zjemy w Dobrym Myśliwym.";
                    DoEvents();
                    var longLine = MainWindow.PanelScreenArea(window, card);
                    Assert.IsTrue(longLine.Width > shortLine.Width * 4 && longLine.Contains(Box(card)), $"a long line: {longLine}");

                    // Right after a long line the panel shrinks to the short one over SubtitleOverlayDefaults.ResizeMs, drawn
                    // wider than the control (PanelWidth): the mask follows what is drawn.
                    card.SubtitleText.Text = "Hej.";
                    DoEvents();
                    card.PanelWidth = 1500;
                    var shrinking = MainWindow.PanelScreenArea(window, card);
                    double scale = whole.Width / 1736.0;
                    Assert.IsTrue(shrinking.Width >= 1500 * scale && whole.Contains(shrinking), $"the panel as drawn: {shrinking}");
                }
                finally
                {
                    window.Close();
                }
            });
        }

        // ── the line under the entry ──────────────────────────────────────

        /// <summary>The app's string lookup for a language: its dictionary over the English one.</summary>
        private static Func<string, string, string> Lookup(string lang)
        {
            var host = new Border();
            foreach (var d in Strings(lang)) host.Resources.MergedDictionaries.Add(d);
            return (key, fallback) => host.TryFindResource(key) as string ?? fallback;
        }

        private static BuildCardTarget NoUid(UidReadOutcome? outcome) => new BuildCardTarget("https://kaption.one/pl/moj-build/", null, UidSource.None, outcome);

        [TestMethod]
        public void TheLine_SaysTheReaderIsLoading_AndWhereStarRailAndZzzShowTheUid()
        {
            RunSta(() =>
            {
                var pl = Lookup("pl-PL");
                const string notRead = "Nie udało się odczytać UID z ekranu gry. Wpisz go na stronie.";
                const string menu = "W Star Rail i ZZZ otwórz menu gry (Esc), tam widać UID.";
                const string loading = "Czytnik tekstu jeszcze się ładuje. Spróbuj za chwilę.";

                // Genshin prints the UID the whole time: the plain line.
                Assert.AreEqual(notRead, SettingsWindow.BuildCardLine(NoUid(UidReadOutcome.NotFound), "Genshin", false, pl));
                Assert.AreEqual(notRead, SettingsWindow.BuildCardLine(NoUid(UidReadOutcome.TimedOut), "Genshin", false, pl));
                Assert.AreEqual(notRead, SettingsWindow.BuildCardLine(NoUid(UidReadOutcome.NoGameWindow), "Genshin", false, pl));
                // Star Rail and ZZZ show it in their menus: the line says so.
                Assert.AreEqual(notRead + " " + menu, SettingsWindow.BuildCardLine(NoUid(UidReadOutcome.NotFound), "StarRail", false, pl));
                Assert.AreEqual(notRead + " " + menu, SettingsWindow.BuildCardLine(NoUid(UidReadOutcome.TimedOut), "ZZZ", false, pl));
                Assert.AreEqual(notRead + " " + menu, SettingsWindow.BuildCardLine(NoUid(UidReadOutcome.NoGameWindow), "Zenless", false, pl));
                // The text reader still loading (the first start): its own line, for every game.
                Assert.AreEqual(loading, SettingsWindow.BuildCardLine(NoUid(UidReadOutcome.NoReader), "Genshin", false, pl));
                Assert.AreEqual(loading, SettingsWindow.BuildCardLine(NoUid(UidReadOutcome.NoReader), "StarRail", false, pl));
                // A reader that failed to start is not loading: the plain line is the true one.
                Assert.AreEqual(notRead, SettingsWindow.BuildCardLine(NoUid(UidReadOutcome.NoReader), "Genshin", true, pl));
                Assert.AreEqual(notRead + " " + menu, SettingsWindow.BuildCardLine(NoUid(UidReadOutcome.NoReader), "ZZZ", true, pl));
                // No read at all (a game the maker has no cards for): the plain line, nothing about menus.
                Assert.AreEqual(notRead, SettingsWindow.BuildCardLine(NoUid(null), "WutheringWaves", false, pl));
                // With a UID the page needs nothing: no line.
                var opened = new BuildCardTarget("https://kaption.one/pl/moj-build/?game=starrail&uid=809162009", "809162009", UidSource.Screen, UidReadOutcome.Read);
                Assert.IsNull(SettingsWindow.BuildCardLine(opened, "StarRail", false, pl));
                Assert.IsNull(SettingsWindow.BuildCardLine(opened with { Source = UidSource.Remembered, ReadOutcome = null }, "StarRail", false, pl));

                var en = Lookup("en-US");
                Assert.AreEqual("Couldn't read the UID from the game screen. Type it on the page. " +
                                "In Star Rail and ZZZ open the game menu (Esc), the UID is shown there.",
                    SettingsWindow.BuildCardLine(NoUid(UidReadOutcome.NotFound), "StarRail", false, en));
                Assert.AreEqual("The text reader is still loading. Try again in a moment.",
                    SettingsWindow.BuildCardLine(NoUid(UidReadOutcome.NoReader), "ZZZ", false, en));

                // Japanese runs the two sentences on after its full stop, without a space.
                var ja = Lookup("ja-JP");
                string jaLine = SettingsWindow.BuildCardLine(NoUid(UidReadOutcome.NotFound), "ZZZ", false, ja);
                Assert.AreEqual(ja("card_Uid_NotRead", null) + ja("card_Uid_MenuHint", null), jaLine);
                Assert.IsFalse(jaLine.Contains(" " + ja("card_Uid_MenuHint", null)), jaLine);
            });
        }

        // ── the Characters header ─────────────────────────────────────────

        private static (CharacterOverviewView View, Border Host) Hero(string lang = "pl-PL")
        {
            var model = new CharacterOverviewModel
            {
                Name = "Chiori", EnglishName = "Chiori", Game = "genshin", ElementKey = "Geo", Element = "Geo", Rarity = 5,
                PageUrlPl = "https://kaption.one/pl/postacie/genshin-impact/chiori/",
                PageUrlEn = "https://kaption.one/characters/genshin-impact/chiori/",
            };
            var view = new CharacterOverviewView(model);
            var host = new Border { Child = view };
            host.Resources.MergedDictionaries.Add(Dictionary("Themes/Palette.Light.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary("Resources/CharacterPaletteLight.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary("Resources/CharacterLayoutPage.xaml"));
            foreach (var d in Strings(lang)) host.Resources.MergedDictionaries.Add(d);
            return (view, host);
        }

        [TestMethod]
        public void TheCharactersHeader_HasTheButton_OnlyWhereTheHostEnablesIt()
        {
            RunSta(() =>
            {
                // The Ctrl+Q popup builds the same view and never enables it.
                var (popup, popupHost) = Hero();
                Layout(popupHost, 900);
                Assert.AreEqual(Visibility.Collapsed, ((Button)popup.FindName("BuildCardButton")).Visibility);

                var (view, host) = Hero();
                var notes = new List<string>();
                int opened = 0;
                view.EnableBuildCard(async showNote =>
                {
                    opened++;
                    showNote("Nie udało się odczytać UID z ekranu gry. Wpisz go na stronie.");
                    await Task.CompletedTask;
                });
                Layout(host, 900);
                var button = (Button)view.FindName("BuildCardButton");
                var note = (TextBlock)view.FindName("BuildCardNote");
                Assert.AreEqual(Visibility.Visible, button.Visibility);
                Assert.AreEqual("Karta buildu", AutomationProperties.GetName(button));
                Assert.AreEqual(Visibility.Collapsed, note.Visibility, "no line before a click");

                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
                DoEvents();
                Assert.AreEqual(1, opened);
                Assert.AreEqual(Visibility.Visible, note.Visibility);
                Assert.AreEqual("Nie udało się odczytać UID z ekranu gry. Wpisz go na stronie.", note.Text);
                Assert.IsTrue(button.IsEnabled, "enabled again once the page is open");

                view.ShowBuildCardNote(null);
                Assert.AreEqual(Visibility.Collapsed, note.Visibility);
                view.EnableBuildCard(null);
                Assert.AreEqual(Visibility.Collapsed, button.Visibility);
            });
        }

        [TestMethod]
        public void TheCharactersPage_KeepsItsLine_WhenAnotherCharacterOpens()
        {
            RunSta(() =>
            {
                var catalog = CharacterCatalog.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(CharacterCatalogTests.MinimalJson)), "starrail");
                Assert.AreEqual(2, catalog.Characters.Count, "the fixture catalog loads");
                int opened = 0;
                var page = new CharactersPage
                {
                    GameSource = () => "StarRail", PackSource = () => null, PackReady = () => true, FetchImages = false,
                    BuildCardSource = showNote => { opened++; showNote("line"); return Task.CompletedTask; },
                };
                // As in Settings: the app palette, the control styles and the strings come from the host.
                var host = new Border();
                host.Resources.MergedDictionaries.Add(Dictionary("Themes/Palette.Light.xaml"));
                host.Resources.MergedDictionaries.Add(Dictionary("Resources/ModernStyles.xaml"));
                host.Resources.MergedDictionaries.Add(Dictionary("Themes/Tokens.xaml"));
                host.Resources.MergedDictionaries.Add(Dictionary("Themes/Shell.xaml"));
                foreach (var d in Strings("pl-PL")) host.Resources.MergedDictionaries.Add(d);
                host.Child = page;
                var load = page.LoadAsync("StarRail", _ => Task.FromResult(catalog));
                CharacterOverviewRenderTests.Pump(() => load.IsCompleted);
                var overview = (ContentControl)page.FindName("OverviewHost");
                CharacterOverviewRenderTests.Pump(() => overview.Content is CharacterOverviewView);
                Layout(host, 1100, 760);
                var first = (CharacterOverviewView)overview.Content;
                var button = (Button)first.FindName("BuildCardButton");
                Assert.AreEqual(Visibility.Visible, button.Visibility, "the Characters tab shows the button");
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
                DoEvents();
                Assert.AreEqual(1, opened);
                Assert.AreEqual("line", ((TextBlock)first.FindName("BuildCardNote")).Text);

                // Another character: a new page, the same line under its buttons.
                var list = (ListBox)page.FindName("CharacterList");
                var shownName = ((CharacterOverviewViewModel)first.DataContext).Name;
                list.SelectedItem = list.Items.Cast<CharacterListItem>().First(i => i.Name != shownName);
                CharacterOverviewRenderTests.Pump(() => overview.Content is CharacterOverviewView v && !ReferenceEquals(v, first));
                var second = (CharacterOverviewView)overview.Content;
                Assert.AreNotSame(first, second);
                var note = (TextBlock)second.FindName("BuildCardNote");
                Assert.AreEqual(Visibility.Visible, note.Visibility, "the page's line stays");
                Assert.AreEqual("line", note.Text);
                Assert.AreEqual(Visibility.Visible, ((Button)second.FindName("BuildCardButton")).Visibility);

                // A theme change redraws the page through the same path: the button and the line stay.
                page.UsePalette(dark: true);
                DoEvents();
                var redrawn = (CharacterOverviewView)overview.Content;
                Assert.AreNotSame(second, redrawn, "redrawn on the dark palette");
                Assert.AreEqual(Visibility.Visible, ((Button)redrawn.FindName("BuildCardButton")).Visibility);
                Assert.AreEqual("line", ((TextBlock)redrawn.FindName("BuildCardNote")).Text);
            });
        }
    }
}
