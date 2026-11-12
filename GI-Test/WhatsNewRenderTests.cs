using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Notices;
using GI_Subtitles.Services.Notices.WhatsNew;
using GI_Subtitles.Services.Today;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// Start's "New in Kaption" card, drawn through the card's own Apply with the shipped entries: what a player on
    /// each game sees after an update, a row turned on, the list opened from Help, in Polish and English, dark and
    /// light, on its own and in place on Start; and the words of the two tray notices. What its buttons report is
    /// checked here too. Output: %TEMP%\kaption-render\whatsnew-*.png, day-notice-*.png.
    /// </summary>
    [TestClass]
    public class WhatsNewRenderTests
    {
        private static readonly string[] Langs = { "pl-PL", "en-US" };

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

        /// <summary>A player on <paramref name="game"/> who just updated: nothing answered, only the day notice on.</summary>
        private static WhatsNewInputs Updated(GameManifest game, Func<string, bool> featureOn = null) => new()
        {
            Game = game.Id,
            Meets = r => SettingsWindow.WhatsNewMeets(r, game),
            FeatureOn = featureOn ?? (f => f == WhatsNewFeatures.DayNotice),
        };

        private static IReadOnlyList<WhatsNewRow> LeadRows(GameManifest game, string lang, Func<string, string, string> l, Func<string, bool> featureOn = null)
        {
            WhatsNewInputs inputs = Updated(game, featureOn);
            WhatsNewPlan plan = WhatsNewPlanner.Lead(WhatsNewCatalog.Default, inputs);
            Assert.AreEqual(WhatsNewPlanState.Show, plan.State, game.Id);
            return WhatsNewRows.Build(plan.Rows, lang == "pl-PL", SettingsWindow.WhatsNewValues(game, l, new CultureInfo(lang)), inputs.FeatureOn);
        }

        private static IReadOnlyList<WhatsNewRow> ListRows(GameManifest game, string lang, Func<string, string, string> l, Func<string, bool> featureOn) =>
            WhatsNewRows.Build(WhatsNewPlanner.List(WhatsNewCatalog.Default, Updated(game, featureOn)), lang == "pl-PL",
                SettingsWindow.WhatsNewValues(game, l, new CultureInfo(lang)), featureOn);

        [TestMethod]
        public void RendersTheCardAfterAnUpdate_ForEveryGame()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string game in new[] { "genshin", "starrail", "zzz", "wuwa" })
                foreach (string lang in Langs)
                foreach (string palette in WindowRenderTests.Palettes)
                {
                    var card = new WhatsNewLead();
                    Border host = Host(card, palette, lang);
                    string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                    var rows = LeadRows(Game(game), lang, L);
                    Assert.IsTrue(rows.Count is >= 2 and <= WhatsNewPlanner.MaxRows, $"{game}: {rows.Count} rows");
                    // Genshin has all of it; a game without a measured stamina screen is never told about the timer.
                    Assert.AreEqual(game != "zzz", rows.Any(r => r.Entry.Feature == WhatsNewFeatures.StaminaNotice), game);
                    Assert.IsTrue(rows.Any(r => r.Entry.Feature == WhatsNewFeatures.DayNotice), $"{game}: every game has a day's notice to hear of");

                    card.Apply(rows, list: false, L);
                    Assert.AreEqual(Visibility.Visible, card.Visibility);
                    Layout(host, 912 + 48);
                    AssertFits(card, $"{game}-{lang}-{palette}");
                    foreach (WhatsNewLead.RowModel row in card.Shown)
                    {
                        Assert.AreEqual(row.Row.IsAsk ? Visibility.Visible : Visibility.Collapsed, row.AskVisibility, row.Row.Entry.Id);
                        Assert.AreEqual(Visibility.Collapsed, row.SwitchVisibility, "no switches on the card after an update");
                    }
                    Assert.AreEqual(L("WhatsNew_Eyebrow", null), ((TextBlock)card.FindName("Heading")).Text);
                    written.Add(Save(Snapshot(host), $"whatsnew-lead-{game}-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void RendersARowTurnedOn_AndTheListFromHelp()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string lang in Langs)
                foreach (string palette in WindowRenderTests.Palettes)
                {
                    // "Turn on" on the stamina row: its buttons give way to what happens next; the others still ask.
                    var card = new WhatsNewLead();
                    Border host = Host(card, palette, lang);
                    string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                    var rows = LeadRows(Game("genshin"), lang, L);
                    card.Apply(rows, list: false, L);
                    card.MarkDone(rows[0]);
                    Layout(host, 912 + 48);
                    AssertFits(card, $"done-{lang}-{palette}");
                    Assert.AreEqual(Visibility.Visible, card.Shown[0].DoneVisibility);
                    Assert.AreEqual(Visibility.Collapsed, card.Shown[0].AskVisibility);
                    written.Add(Save(Snapshot(host), $"whatsnew-turned-on-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));

                    // The list: every entry for the game, a live switch per row, no questions.
                    var list = new WhatsNewLead();
                    Border listHost = Host(list, palette, lang);
                    string LL(string key, string fallback) => listHost.TryFindResource(key) as string ?? fallback;
                    Func<string, bool> on = f => f is WhatsNewFeatures.DayNotice or WhatsNewFeatures.StaminaNotice;
                    var all = ListRows(Game("genshin"), lang, LL, on);
                    Assert.AreEqual(WhatsNewCatalog.Default.Entries.Count(e => e.Games.Count == 0 || e.Games.Contains("genshin")), all.Count,
                        "Genshin has every entry that is not another game's");
                    list.Apply(all, list: true, LL);
                    Layout(listHost, 912 + 48);
                    AssertFits(list, $"list-{lang}-{palette}");
                    Assert.IsTrue(list.IsList);
                    foreach (WhatsNewLead.RowModel row in list.Shown)
                    {
                        Assert.AreEqual(Visibility.Collapsed, row.AskVisibility, "the list asks nothing");
                        Assert.AreEqual(Visibility.Visible, row.SwitchVisibility, row.Row.Entry.Id);
                        Assert.AreEqual(on(row.Row.Entry.Feature), row.SwitchOn, row.Row.Entry.Id);
                    }
                    Assert.AreEqual(LL("WhatsNew_List_Eyebrow", null), ((TextBlock)list.FindName("Heading")).Text);
                    written.Add(Save(Snapshot(listHost), $"whatsnew-list-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void TheCardReportsWhatThePlayerClicked_AndNothingElse()
        {
            RunSta(() =>
            {
                var card = new WhatsNewLead();
                Border host = Host(card, "Dark", "en-US");
                string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                var rows = LeadRows(Game("genshin"), "en-US", L);
                var log = new List<string>();
                card.TurnOnRequested += r => log.Add("on:" + r.Entry.Feature);
                card.NotNowRequested += r => log.Add("later:" + r.Entry.Feature);
                card.NeverRequested += r => log.Add("never:" + r.Entry.Feature);
                card.ActionRequested += r => log.Add("action:" + r.Entry.Action);
                card.CloseRequested += () => log.Add("close");
                card.Emptied += () => log.Add("empty");
                card.Apply(rows, list: false, L);
                Layout(host, 960);
                Assert.AreEqual(0, log.Count, "drawing the card answers nothing");

                Button Find(string text, int nth = 0) => Descendants<Button>(card).Where(b => OnScreen(b) && Equals(b.Content, text)).ElementAt(nth);
                void Click(ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                // Three rows: the stamina question, today's books (its own action), start with Windows.
                Click(Find(L("WhatsNew_TurnOn", null)));
                Click(Find(L("WhatsNew_NotNow", null), 1));
                Click(Find(rows[1].ActionLabel));
                CollectionAssert.AreEqual(new[] { "on:" + WhatsNewFeatures.StaminaNotice, "later:" + WhatsNewFeatures.StartWithWindows, "action:" + WhatsNewActions.TodayCard }, log);

                // The host answers: the first row is on, the third leaves; the card stays for the rest.
                card.MarkDone(rows[0]);
                card.Remove(rows[2]);
                Layout(host, 960);
                Assert.AreEqual(2, card.Shown.Count);
                Assert.AreEqual(0, Descendants<Button>(card).Count(b => OnScreen(b) && Equals(b.Content, L("WhatsNew_TurnOn", null))), "no question is left");
                Assert.AreEqual(Visibility.Visible, card.Visibility);

                // The first row leaving hands "first" to the next: no rule above the top row.
                card.Remove(rows[0]);
                Assert.AreEqual(Visibility.Collapsed, card.Shown[0].RuleVisibility);
                card.Remove(rows[1]);
                Assert.AreEqual(Visibility.Collapsed, card.Visibility);
                Assert.AreEqual("empty", log.Last());

                // "Got it" only reports; the host decides what closing means.
                card.Apply(rows, list: false, L);
                Layout(host, 960);
                log.Clear();
                Click((Button)card.FindName("DoneButton"));
                CollectionAssert.AreEqual(new[] { "close" }, log);

                // The list: a switch shows what the host says is true, not what was clicked.
                bool? asked = null;
                card.SwitchRequested += (r, on) => asked = on;
                card.Apply(ListRows(Game("genshin"), "en-US", L, _ => false), list: true, L);
                Layout(host, 960);
                CheckBox toggle = Descendants<CheckBox>(card).First(OnScreen);
                // What a click or the space bar does: the switch flips itself (OnToggle), keeping its binding.
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Assert.AreEqual(true, asked, "the click asks for on");
                Assert.AreEqual(false, toggle.IsChecked, "and stays off until the host says it took");
                card.SetSwitch(card.Shown[0].Row, true);
                Assert.AreEqual(true, toggle.IsChecked);

                // The thing was flipped elsewhere (its own switch in Settings) while the list was open: every row
                // follows, so the next click asks for the right thing.
                card.SyncSwitches(f => f == WhatsNewFeatures.DayNotice);
                Assert.AreEqual(false, toggle.IsChecked, "the stamina row follows the setting");
                foreach (WhatsNewLead.RowModel row in card.Shown)
                    Assert.AreEqual(row.Row.Entry.Feature == WhatsNewFeatures.DayNotice, row.SwitchOn, row.Row.Entry.Id);

                card.Apply(Array.Empty<WhatsNewRow>(), list: false, L);
                Assert.AreEqual(Visibility.Collapsed, card.Visibility, "nothing to say: no card");
            });
        }

        [TestMethod]
        public void TheListsSwitches_AnswerToAScreenReadersToggle_AndStayQuietWhenOnlyDrawnOrSynced()
        {
            RunSta(() =>
            {
                var card = new WhatsNewLead();
                Border host = Host(card, "Dark", "en-US");
                string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                var requests = new List<(string Feature, bool On)>();
                card.SwitchRequested += (r, on) => requests.Add((r.Entry.Feature, on));

                // Drawn with two things already on: showing a switch as it is asks for nothing.
                card.Apply(ListRows(Game("genshin"), "en-US", L, f => f is WhatsNewFeatures.DayNotice or WhatsNewFeatures.StaminaNotice), list: true, L);
                Layout(host, 960);
                var toggles = Descendants<CheckBox>(card).Where(OnScreen).ToList();
                Assert.AreEqual(card.Shown.Count, toggles.Count, "a switch per row");
                Assert.AreEqual(0, requests.Count, "drawing the list is not the player asking");
                // A setting changed elsewhere (its own switch in Settings): the list follows, and asks for nothing.
                card.SyncSwitches(f => f == WhatsNewFeatures.DayNotice);
                Assert.AreEqual(0, requests.Count, "a switch following its setting is not the player asking");

                // A screen reader flips "Start Kaption with Windows" through UI Automation's Toggle pattern: no click.
                CheckBox windows = toggles.Single(t => ((WhatsNewLead.RowModel)t.DataContext).Row.Entry.Feature == WhatsNewFeatures.StartWithWindows);
                var provider = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(windows).GetPattern(PatternInterface.Toggle);
                Assert.AreEqual(ToggleState.Off, provider.ToggleState);
                provider.Toggle();
                CollectionAssert.AreEqual(new[] { (WhatsNewFeatures.StartWithWindows, true) }, requests, "the host is asked to turn it on");
                Assert.AreEqual(ToggleState.Off, provider.ToggleState, "announced as on only once the host says the setting took");

                // The host turned it on: every switch shows what is true, and that is not another request.
                card.SyncSwitches(f => f is WhatsNewFeatures.DayNotice or WhatsNewFeatures.StartWithWindows);
                Assert.AreEqual(ToggleState.On, provider.ToggleState);
                Assert.AreEqual(1, requests.Count);

                // Off again the same way; a host that refuses (the setting stays on) leaves the switch on, askable again.
                provider.Toggle();
                Assert.AreEqual((WhatsNewFeatures.StartWithWindows, false), requests.Last());
                Assert.AreEqual(ToggleState.On, provider.ToggleState);
                provider.Toggle();
                Assert.AreEqual(3, requests.Count);
                Assert.AreEqual((WhatsNewFeatures.StartWithWindows, false), requests.Last());
            });
        }

        [TestMethod]
        public void RendersTheCardOnTheStartPage()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string lang in Langs)
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", lang, out var window, palette);
                    ShellRenderTests.Fill(host, window, ShellRenderTests.Scene.Stopped, lang);
                    string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                    var card = (WhatsNewLead)window.FindName("WhatsNewLeadCard");
                    Assert.IsNotNull(card, "the Start page holds the card");
                    Assert.IsNotNull(window.FindName("WhatsNewToggle"), "Gra i napisy holds its switch");
                    Assert.IsNotNull(window.FindName("DayNoticeToggle"), "Gra i napisy holds the day notice's switch");
                    Assert.IsNotNull(window.FindName("WhatsNewListButton"), "Help opens the list");
                    card.Apply(LeadRows(Game("genshin"), lang, L), list: false, L);
                    Layout(host, 1280, 1100);
                    // It leads the once-cards: nothing of theirs sits above it.
                    var stack = (StackPanel)window.FindName("StartOnceCards");
                    Assert.AreSame(card, stack.Children[0]);
                    written.Add(Save(Snapshot(host), $"whatsnew-start-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        /// <summary>The tray notices are drawn by Windows; this renders their words as the app hands them over.</summary>
        [TestMethod]
        public void RendersTheNoticeWords()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string lang in Langs)
                {
                    var stack = new StackPanel();
                    Border host = Host(stack, "Dark", lang);
                    string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                    var culture = new CultureInfo(lang);
                    var now = new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
                    GameManifest genshin = Game("genshin"), hsr = Game("starrail");
                    FarmCardModel mine = FarmCardSamples.Model(FarmCardSamples.Wednesday, hasUid: true, lang: lang);
                    FarmCardModel open = FarmCardSamples.Model(FarmCardSamples.Wednesday, hasUid: false, lang: lang);
                    string resinFull = DayNoticeContent.Stamina(TodayCardBuilder.Stamina(genshin, new(genshin.Stamina.Max - 10, now.AddHours(-20), 0), null, now, lang),
                        now, now.AddHours(18), L, culture);
                    string powerFull = DayNoticeContent.Stamina(TodayCardBuilder.Stamina(hsr, new(hsr.Stamina.Max - 5, now.AddHours(-9), 0), null, now, lang),
                        now, now.AddHours(18), L, culture);
                    string lastDay = DayNoticeContent.EndsToday(new[] { new TodayPeriod("banner", lang == "pl-PL" ? "Banner Castorice" : "Castorice's banner", "", now.AddHours(6), "https://kaption.one/news/x/") },
                        now, now.AddHours(18), L, culture);

                    var notices = new List<(string Title, string Body)>();
                    void Add(DayNoticeText t)
                    {
                        Assert.IsNotNull(t);
                        Assert.IsTrue(t.Title.Length <= DayNoticeBuilder.MaxTitle && t.Body.Length <= DayNoticeBuilder.MaxBody, t.Body);
                        notices.Add((t.Title, t.Body));
                    }
                    // With the UID saved: who needs what, and the pool that filled overnight.
                    Add(DayNoticeBuilder.Build(new[] { new DayNoticeSection("genshin", genshin.Name, new[] { DayNoticeContent.Books(mine, L, culture), resinFull }, true) },
                        mine.Weekday, L, culture));
                    // Without one: what is open, and how to get names.
                    Add(DayNoticeBuilder.Build(new[] { new DayNoticeSection("genshin", genshin.Name, new[] { DayNoticeContent.Books(open, L, culture) }, true) },
                        open.Weekday, L, culture));
                    // Two games.
                    Add(DayNoticeBuilder.Build(new[]
                    {
                        new DayNoticeSection("genshin", genshin.Name, new[] { DayNoticeContent.Books(mine, L, culture) }, true),
                        new DayNoticeSection("starrail", hsr.Name, new[] { powerFull, lastDay }, false),
                    }, mine.Weekday, L, culture));
                    // A start with Windows after an update: the one line that says there is news.
                    var titles = LeadRows(genshin, lang, L).Select(r => r.Title).ToList();
                    notices.Add((L("WhatsNew_Tray_Title", null),
                        string.Format(culture, L("WhatsNew_Tray_Body_More", null), titles[0], titles.Count - 1)));

                    foreach (var (title, body) in notices)
                    {
                        Assert.IsFalse(string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body));
                        var box = new Border { Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(8), Width = 380, HorizontalAlignment = HorizontalAlignment.Left };
                        box.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
                        var lines = new StackPanel();
                        var app = new TextBlock { Text = "Kaption", FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
                        app.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
                        var head = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                        head.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                        var text = new TextBlock { Text = body, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                        lines.Children.Add(app);
                        lines.Children.Add(head);
                        lines.Children.Add(text);
                        box.Child = lines;
                        stack.Children.Add(box);
                    }
                    Layout(host, 380 + 48);
                    written.Add(Save(Snapshot(host), $"day-notice-{lang.Substring(0, 2)}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        /// <summary>Laid out and not collapsed anywhere up the tree (IsVisible needs a window, which a render has not).</summary>
        private static bool OnScreen(FrameworkElement e)
        {
            if (e.ActualWidth <= 0 || e.ActualHeight <= 0) return false;
            for (DependencyObject d = e; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                if (d is UIElement u && u.Visibility != Visibility.Visible) return false;
            return true;
        }

        private static void AssertFits(FrameworkElement card, string name)
        {
            int texts = 0;
            foreach (var t in Descendants<TextBlock>(card).Where(OnScreen))
            {
                var box = t.TransformToAncestor(card).TransformBounds(new Rect(0, 0, t.ActualWidth, t.ActualHeight));
                Assert.IsTrue(box.Right <= card.ActualWidth + 0.5, $"{name}: \"{t.Text}\" runs past the card ({box.Right:0} > {card.ActualWidth:0})");
                texts++;
            }
            Assert.IsTrue(texts >= 5, $"{name}: the check looked at {texts} texts");
            foreach (var b in Descendants<ButtonBase>(card).Where(OnScreen))
            {
                var box = b.TransformToAncestor(card).TransformBounds(new Rect(0, 0, b.ActualWidth, b.ActualHeight));
                Assert.IsTrue(box.Right <= card.ActualWidth + 0.5 && box.Left >= -0.5, $"{name}: a button runs past the card");
            }
        }
    }
}
