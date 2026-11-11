using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;
using GI_Subtitles.Core.Input;
using GI_Subtitles.Services.Community;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;
using AppConfig = GI_Subtitles.Core.Config.Config;
using Decision = GI_Subtitles.Views.SuggestFixWindow.FixDecision;

namespace GI_Test
{
    /// <summary>The fix window: what it shows, when it saves, what saving does, and how it looks in both palettes.</summary>
    [TestClass]
    public class SuggestFixWindowTests
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 3, 18, 0, 0, TimeSpan.Zero);

        private static ShownLine Line(string shown = "Ruszajmy!", string pack = "Ruszajmy!") =>
            new ShownLine("Genshin", "pl", "Let's go!", shown, pack, "Paimon", "Wait for me!", Now.UtcDateTime);

        private static T Find<T>(FrameworkElement root, string name) where T : class =>
            root.FindName(name) as T ?? throw new AssertFailedException($"no {name}");

        /// <summary>Sends nothing, so what was queued stays visible to the test.</summary>
        private sealed class OfflineSender : ISuggestionSender
        {
            public System.Threading.Tasks.Task<SendResult> SendAsync(PendingSuggestion s, System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.Task.FromResult(new SendResult(SendOutcome.RetryLater, "offline"));
        }

        [TestMethod]
        public void Decide_SavesAChange_RevertsToTheSharedText_IgnoresTheRest()
        {
            const string en = "Let's go!";
            Assert.AreEqual(Decision.Unchanged, SuggestFixWindow.Decide(en, "Ruszajmy!", "Ruszajmy!", "Ruszajmy!"));
            Assert.AreEqual(Decision.Unchanged, SuggestFixWindow.Decide(en, "  Ruszajmy!\r\n", "Ruszajmy!", "Ruszajmy!"), "spacing alone is no change");
            Assert.AreEqual(Decision.Unchanged, SuggestFixWindow.Decide(en, "   ", "Ruszajmy!", "Ruszajmy!"), "an empty fix");
            Assert.AreEqual(Decision.Unchanged, SuggestFixWindow.Decide(en, "#Ruszajmy!", "Ruszajmy!", "Ruszajmy!"), "what the server strips is no change");
            Assert.AreEqual(Decision.Save, SuggestFixWindow.Decide(en, "No to w drogę!", "Ruszajmy!", "Ruszajmy!"));
            Assert.AreEqual(Decision.Revert, SuggestFixWindow.Decide(en, "Ruszajmy!", "No to w drogę!", "Ruszajmy!"),
                "the player's own fix is shown and they typed the shared text back");
            Assert.AreEqual(Decision.Save, SuggestFixWindow.Decide("One\ntwo", "Raz\r\ndwa", "Raz\ntrzy", "Raz\ntrzy"));
            Assert.AreEqual("Raz\ndwa", SuggestFixWindow.Clean(" Raz\r\ndwa\r"));
            Assert.AreEqual(Decision.Unchanged, SuggestFixWindow.Decide(en, "Rusz#ajmy! <b>", "Rusz#ajmy! <b>", "Rusz#ajmy! <b>"),
                "a pack text the server would clean is not a change while the box is untouched");
        }

        [TestMethod]
        public void Problem_NamesWhatTheServerWouldRefuse()
        {
            Assert.IsNull(SuggestFixWindow.Problem("One\ntwo", "Raz\ndwa"));
            StringAssert.Contains(SuggestFixWindow.Problem("One\ntwo", "Raz dwa"), "(1)", "the English count of line breaks");
            StringAssert.Contains(SuggestFixWindow.Problem("Deals 1[i]% DMG.", "Zadaje obrażenia."), "1[i]");
            StringAssert.Contains(SuggestFixWindow.Problem("Deals DMG.", "Zadaje 5[i] obrażeń."), "5[i]");
            Assert.IsNotNull(SuggestFixWindow.Problem("Hi.", new string('a', 60)));
        }

        [TestMethod]
        public void Apply_SavesTheFix_AndQueuesTheSuggestion()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            var queue = new SuggestionQueue(dir.File("community-queue.json"));
            var submitter = new SuggestionSubmitter(queue, new OfflineSender(), scheduleRetries: false);

            SuggestFixWindow.Apply(Decision.Save, Line(), layers, submitter, " No to w #drogę!\r\n", " brzmi naturalniej ", "Ruszajmy!", Now);

            Assert.AreEqual("No to w drogę!", layers.Resolve("Let's go!", "Ruszajmy!"), "applies at once, cleaned as the server cleans it");
            var fix = layers.UserFixes.Single();
            Assert.AreEqual("Ruszajmy!", fix.Was);
            Assert.AreEqual("Paimon", fix.Speaker);
            Assert.AreEqual("brzmi naturalniej", fix.Note);
            Assert.IsTrue(File.Exists(dir.File("overrides.pl.json")), "and is on disk");

            var queued = queue.Items.Single();
            Assert.AreEqual("genshin", queued.Game);
            Assert.AreEqual("pl", queued.Lang);
            Assert.AreEqual("Let's go!", queued.EnText);
            Assert.AreEqual("Ruszajmy!", queued.CurrentText, "what every player sees now, not the player's own fix");
            Assert.AreEqual("No to w drogę!", queued.SuggestedText);
            Assert.AreEqual("Paimon", queued.Speaker);
            Assert.AreEqual("Wait for me!", queued.ContextPrev);
            Assert.AreEqual("brzmi naturalniej", queued.Note);
        }

        [TestMethod]
        public void Apply_SendsTheExactPackKey_EvenWithEdgeWhitespace()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            var queue = new SuggestionQueue(dir.File("community-queue.json"));
            var submitter = new SuggestionSubmitter(queue, new OfflineSender(), scheduleRetries: false);
            var line = new ShownLine("Genshin", "pl", "Hmm... ", "Hmm...", "Hmm...", "", null, Now.UtcDateTime);

            SuggestFixWindow.Apply(Decision.Save, line, layers, submitter, "Hmm, no nie wiem...", "", "Hmm...", Now);
            Assert.AreEqual("Hmm... ", queue.Items.Single().EnText, "the server keys the line by the pack key as sent");
            Assert.AreEqual("Hmm, no nie wiem...", layers.Resolve("Hmm... ", "Hmm..."));

            SuggestFixWindow.Apply(Decision.Revert, new ShownLine("Genshin", "pl", "Hmm... ", "Hmm, no nie wiem...", "Hmm...", "", null, Now.UtcDateTime),
                layers, submitter, "Hmm...", "", "Hmm...", Now);
            Assert.AreEqual(0, queue.Count);
        }

        [TestMethod]
        public void Window_ExplainsAFixTheServerWouldRefuse_AndKeepsSaveOff()
        {
            RunSta(() =>
            {
                using var dir = new TempFolder();
                var line = new ShownLine("Genshin", "pl", "One more thing.\nListen.", "Jeszcze jedno.\nSłuchaj.", "Jeszcze jedno.\nSłuchaj.",
                    "Paimon", null, Now.UtcDateTime);
                var w = new SuggestFixWindow(line, dir.Layers(), null, IntPtr.Zero);
                try
                {
                    var box = Find<TextBox>(w, "FixBox");
                    var save = Find<Button>(w, "BtnSave");
                    var error = Find<TextBlock>(w, "ErrorText");
                    box.Text = "Jeszcze jedno, słuchaj.";
                    Assert.IsFalse(save.IsEnabled, "one line break short");
                    Assert.AreEqual(Visibility.Visible, error.Visibility);
                    StringAssert.Contains(error.Text, "(1)");
                    box.Text = "Jeszcze jedno.\nPosłuchaj.";
                    Assert.IsTrue(save.IsEnabled);
                    Assert.AreEqual(Visibility.Collapsed, error.Visibility);
                }
                finally
                {
                    w.Close();
                }
            });
        }

        [TestMethod]
        public void Apply_Revert_RemovesTheOwnFix_AndTakesBackTheWaitingSuggestion()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            var queue = new SuggestionQueue(dir.File("community-queue.json"));
            var submitter = new SuggestionSubmitter(queue, new OfflineSender(), scheduleRetries: false);
            SuggestFixWindow.Apply(Decision.Save, Line(), layers, submitter, "No to w drogę!", "", "Ruszajmy!", Now);

            SuggestFixWindow.Apply(Decision.Revert, Line(shown: "No to w drogę!"), layers, submitter, "Ruszajmy!", "", "Ruszajmy!", Now);
            Assert.AreEqual(0, layers.UserFixCount);
            Assert.AreEqual("Ruszajmy!", layers.Resolve("Let's go!", "Ruszajmy!"));
            Assert.AreEqual(0, queue.Count, "nothing to review any more");
        }

        [TestMethod]
        public void Window_ShowsTheLine_PrefillsTheFix_AndSaveWaitsForAChange()
        {
            RunSta(() =>
            {
                using var dir = new TempFolder();
                var w = new SuggestFixWindow(Line(), dir.Layers(), submitter: null, returnFocusTo: IntPtr.Zero);
                try
                {
                    Assert.AreEqual("Let's go!", Find<TextBlock>(w, "EnglishText").Text);
                    Assert.AreEqual("Ruszajmy!", Find<TextBlock>(w, "CurrentText").Text);
                    var speaker = Find<TextBlock>(w, "SpeakerText");
                    Assert.AreEqual("Paimon", speaker.Text);
                    Assert.AreEqual(Visibility.Visible, speaker.Visibility);
                    var box = Find<TextBox>(w, "FixBox");
                    Assert.AreEqual("Ruszajmy!", box.Text, "prefilled with the Polish in use");
                    Assert.AreEqual(SuggestFixWindow.MaxFixLength, box.MaxLength);
                    Assert.AreEqual(SuggestFixWindow.MaxNoteLength, Find<TextBox>(w, "NoteBox").MaxLength);
                    var save = Find<Button>(w, "BtnSave");
                    Assert.IsFalse(save.IsEnabled, "nothing changed yet");
                    box.Text = "No to w drogę!";
                    Assert.IsTrue(save.IsEnabled);
                    box.Text = "   ";
                    Assert.IsFalse(save.IsEnabled, "an empty fix cannot be saved");
                    Assert.IsTrue(Find<Button>(w, "BtnCancel").IsCancel, "Esc cancels");
                    Assert.IsFalse(w.IsUntouched, "edited");
                    box.Text = "Ruszajmy!";
                    Assert.IsTrue(w.IsUntouched, "back as it opened: a newer line may replace it");
                    Find<TextBox>(w, "NoteBox").Text = "uwaga";
                    Assert.IsFalse(w.IsUntouched, "a note counts as work too");
                }
                finally
                {
                    w.Close();
                }
            });
        }

        [TestMethod]
        public void Window_ReadsTheLineAsItIsNow_NotAsItWasRecorded()
        {
            RunSta(() =>
            {
                using var dir = new TempFolder();
                var layers = dir.Layers();
                layers.SaveUserFix(new UserFix("Let's go!", "No to w drogę!", "Ruszajmy!", "Paimon", "", Now));
                // Recorded before the fix was saved: the overlay showed the pack.
                var w = new SuggestFixWindow(Line(shown: "Ruszajmy!", pack: "Ruszajmy!"), layers, null, IntPtr.Zero);
                try
                {
                    Assert.AreEqual("No to w drogę!", Find<TextBlock>(w, "CurrentText").Text);
                    var box = Find<TextBox>(w, "FixBox");
                    Assert.AreEqual("No to w drogę!", box.Text);
                    box.Text = "Ruszajmy!";
                    Assert.IsTrue(Find<Button>(w, "BtnSave").IsEnabled, "typing the shared text back reverts the fix");
                }
                finally
                {
                    w.Close();
                }
            });
        }

        [TestMethod]
        public void Apply_ForALineTheServerDoesNotTake_SavesOnThisPcOnly()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            var queue = new SuggestionQueue(dir.File("community-queue.json"));
            var submitter = new SuggestionSubmitter(queue, new OfflineSender(), scheduleRetries: false);
            string longEnglish = new string('x', SuggestionRules.MaxEnglishLength + 1);
            var line = new ShownLine("Genshin", "pl", longEnglish, "y", "y", "", null, Now.UtcDateTime);
            SuggestFixWindow.Apply(Decision.Save, line, layers, submitter, "z", "", "y", Now);
            Assert.AreEqual("z", layers.Resolve(longEnglish, "y"));
            Assert.AreEqual(0, queue.Count, "nothing queued that the server would refuse");
        }

        [TestMethod]
        public void Apply_ForALanguageTheServerDoesNotReview_SavesOnThisPcOnly()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers(lang: "de");
            var queue = new SuggestionQueue(dir.File("community-queue.json"));
            var submitter = new SuggestionSubmitter(queue, new OfflineSender(), scheduleRetries: false);
            var line = new ShownLine("Genshin", "de", "Let's go!", "Los geht's.", "Los geht's.", "Paimon", null, Now.UtcDateTime);
            Assert.IsFalse(SuggestFixWindow.Apply(Decision.Save, line, layers, submitter, "Auf geht's.", "", "Los geht's.", Now));
            Assert.AreEqual("Auf geht's.", layers.Resolve("Let's go!", "Los geht's."), "it still works on this PC");
            Assert.AreEqual(0, queue.Count, "nothing queued that the server would answer with 400 and the queue would drop");
        }

        [TestMethod]
        public void Hint_SaysWhereTheFixGoes()
        {
            Assert.AreEqual("fix_Hint", SuggestFixWindow.HintKey("genshin", "pl", "Let's go!"));
            Assert.AreEqual("fix_Hint_OtherLanguage", SuggestFixWindow.HintKey("genshin", "de", "Let's go!"));
            Assert.AreEqual("fix_Hint_LocalOnly", SuggestFixWindow.HintKey("genshin", "pl", new string('x', SuggestionRules.MaxEnglishLength + 1)));
            Assert.AreEqual("fix_Hint_LocalOnly", SuggestFixWindow.HintKey("unknown", "pl", "Let's go!"));

            RunSta(() =>
            {
                using var dir = new TempFolder();
                foreach (var (lang, layers, expected) in new[]
                {
                    ("pl-PL", dir.Layers(), "Trafi też do sprawdzenia"),
                    ("en-US", dir.Layers(), "goes to the community for review"),
                    ("en-US", dir.Layers(lang: "de"), "only reviews Polish"),
                    ("pl-PL", dir.Layers(lang: "de"), "wyłącznie polskie"),
                    ("en-US", dir.Layers("Unknown", "unknown"), "can't be sent for review"),
                })
                {
                    var w = new SuggestFixWindow(Line(), layers, null, IntPtr.Zero);
                    try
                    {
                        Layout(Host(w, "Dark", lang), 560);
                        StringAssert.Contains(Find<TextBlock>(w, "SendHint").Text, expected, $"{lang} {layers.WireGame}/{layers.Lang}");
                    }
                    finally
                    {
                        w.Close();
                    }
                }
            });
        }

        [TestMethod]
        public void Window_WithoutAPackText_FallsBackToWhatWasShown()
        {
            RunSta(() =>
            {
                using var dir = new TempFolder();
                var w = new SuggestFixWindow(Line(shown: "Ruszajmy!", pack: ""), dir.Layers(), null, IntPtr.Zero);
                try { Assert.AreEqual("Ruszajmy!", Find<TextBlock>(w, "CurrentText").Text); }
                finally { w.Close(); }
            });
        }

        [TestMethod]
        public void Window_WithoutASpeaker_HidesTheNameRow()
        {
            RunSta(() =>
            {
                using var dir = new TempFolder();
                var line = new ShownLine("ZZZ", "pl", "Hm.", "Hm.", "Hm.", "", null, Now.UtcDateTime);
                var w = new SuggestFixWindow(line, dir.Layers("ZZZ", "zzz"), null, IntPtr.Zero);
                try { Assert.AreEqual(Visibility.Collapsed, Find<TextBlock>(w, "SpeakerText").Visibility); }
                finally { w.Close(); }
            });
        }

        [TestMethod]
        public void RendersInBothPalettes_WithThePaletteColours()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                using var dir = new TempFolder();
                const string pl = "Podróżniku, musimy się pospieszyć. Zakon Otchłani nie będzie na nas czekał.";
                var line = new ShownLine("Genshin", "pl", "Traveler, we should hurry. The Abyss Order won't wait for us.",
                    pl, pl, "Paimon", null, Now.UtcDateTime);
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string lang in new[] { "pl-PL", "en-US" })
                {
                    var w = new SuggestFixWindow(line, dir.Layers(), null, IntPtr.Zero);
                    Assert.AreEqual(pl, Find<TextBlock>(w, "CurrentText").Text, "the render shows a real line");
                    Assert.AreEqual(pl, Find<TextBox>(w, "FixBox").Text);
                    var host = Host(w, palette, lang);
                    Layout(host, 560);
                    double text = RelativeLuminance(Fg(Find<TextBlock>(w, "CurrentText").Foreground));
                    double bg = RelativeLuminance(((SolidColorBrush)host.Background).Color);
                    if (palette == "Dark") Assert.IsTrue(bg < 0.05 && text > 0.4, $"dark: bg {bg:F2}, text {text:F2}");
                    else Assert.IsTrue(bg > 0.85 && text < 0.2, $"light: bg {bg:F2}, text {text:F2}");
                    Assert.IsTrue(ContrastRatio(text, bg) >= 4.5, $"{palette}: readable");
                    written.Add(Save(Snapshot(host), $"window-SuggestFixWindow-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));
                    w.Close();
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        /// <summary>The window's content in a host carrying the app-level palette, as the app's resources would.</summary>
        private static Border Host(Window w, string palette, string lang)
        {
            var content = (UIElement)w.Content;
            var resources = w.Resources;
            w.Content = null;
            w.Resources = new ResourceDictionary();
            var host = new Border { Child = content, UseLayoutRounding = true, SnapsToDevicePixels = true };
            host.Resources.MergedDictionaries.Add(Dictionary("Themes/Tokens.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary($"Themes/Palette.{palette}.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary("Themes/Shell.xaml"));
            foreach (var d in Strings(lang)) host.Resources.MergedDictionaries.Add(d);
            host.Resources.MergedDictionaries.Add(resources);
            host.SetResourceReference(Border.BackgroundProperty, "BgBrush");
            host.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            return host;
        }

        [TestMethod]
        public void MyFixRows_FlattenLineBreaks()
        {
            var rows = SettingsWindow.MyFixRows(new[]
            {
                new UserFix("Line one\nline two", "Raz\r\ndwa", "", "", "", Now),
            });
            Assert.AreEqual("Raz dwa", rows.Single().Text);
            Assert.AreEqual("Line one line two", rows.Single().English);
            Assert.AreEqual("Line one\nline two", rows.Single().Key, "the key stays exact for removal");
            Assert.AreEqual(0, SettingsWindow.MyFixRows(null).Count);
        }
    }

    /// <summary>Moje poprawki on Gra i napisy, built with its code-behind, against the test run's temporary data folder.</summary>
    [TestClass]
    public class MyFixesSettingsTests
    {
        private static void Invoke(SettingsWindow w, string handler, object sender) =>
            typeof(SettingsWindow).GetMethod(handler, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(w, new[] { sender, new RoutedEventArgs() });

        [TestMethod]
        public void MyFixes_ListsTheGamesFixes_AndRemovesOne()
        {
            StringAssert.StartsWith(Path.GetFullPath(TranslationLayerRegistry.DataRoot), Path.GetFullPath(Path.GetTempPath()),
                "tests never touch a real profile");
            RunSta(() =>
            {
                string game = GI_Subtitles.Core.Config.Config.Get("Game", "Genshin") ?? "Genshin";
                string output = GI_Subtitles.Core.Config.Config.Get("Output", "PL") ?? "PL";
                var layers = TranslationLayerRegistry.For(game, output);
                layers.EnsureLoaded();
                foreach (var old in layers.UserFixes) layers.RemoveUserFix(old.En);
                layers.SaveUserFix(new UserFix("Settings test line.", "Linia z testu\nustawień.", "", "", "", DateTimeOffset.UtcNow));

                using var license = TestProtection.UseFakeLicense();
                var w = new SettingsWindow("2.5.0", null, 1.0);
                try
                {
                    Invoke(w, "MyFixesCard_Loaded", null);
                    var list = (ListView)w.FindName("MyFixesList");
                    var empty = (TextBlock)w.FindName("MyFixesEmpty");
                    // The card reads its files on a pool thread and fills in on the dispatcher.
                    for (int i = 0; i < 1000 && list.ItemsSource == null; i++)
                    {
                        DoEvents();
                        System.Threading.Thread.Sleep(20);
                    }
                    var row = ((IEnumerable<SettingsWindow.MyFixRow>)list.ItemsSource).Single();
                    Assert.AreEqual("Linia z testu ustawień.", row.Text);
                    Assert.AreEqual(Visibility.Visible, list.Visibility);
                    Assert.AreEqual(Visibility.Collapsed, empty.Visibility);

                    Invoke(w, "MyFixDelete_Click", new Button { Tag = row.Key });
                    for (int i = 0; i < 1000 && list.Visibility == Visibility.Visible; i++)
                    {
                        DoEvents();
                        System.Threading.Thread.Sleep(20);
                    }
                    Assert.AreEqual(0, layers.UserFixCount, "removed from the layers the overlay reads");
                    Assert.AreEqual(Visibility.Collapsed, list.Visibility);
                    Assert.AreEqual(Visibility.Visible, empty.Visibility);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(empty.Text));
                    Assert.IsFalse(empty.Text.Contains("{0}"), "the hotkey is filled in: " + empty.Text);
                    Invoke(w, "MyFixesCard_Unloaded", null);
                }
                finally
                {
                    w.Close();
                }
            });
        }

        [TestMethod]
        public void MyFixes_FollowsTheGameAndLanguagePickedOnThePage()
        {
            RunSta(() =>
            {
                string game = AppConfig.Has("Game") ? AppConfig.Get<string>("Game", null) : null;
                string output = AppConfig.Has("Output") ? AppConfig.Get<string>("Output", null) : null;
                var genshinPl = TranslationLayerRegistry.For("Genshin", "PL");
                var genshinDe = TranslationLayerRegistry.For("Genshin", "DE");
                var railDe = TranslationLayerRegistry.For("StarRail", "DE");
                var all = new[] { genshinPl, genshinDe, railDe };
                void Clear()
                {
                    foreach (var layers in all)
                    {
                        layers.EnsureLoaded();
                        foreach (var old in layers.UserFixes) layers.RemoveUserFix(old.En);
                    }
                }
                try
                {
                    AppConfig.Set("Game", "Genshin");
                    AppConfig.Set("Output", "PL");
                    Clear();
                    genshinPl.SaveUserFix(new UserFix("Polish line.", "Polska linia.", "", "", "", DateTimeOffset.UtcNow));
                    genshinDe.SaveUserFix(new UserFix("German line.", "Deutsche Zeile.", "", "", "", DateTimeOffset.UtcNow));
                    railDe.SaveUserFix(new UserFix("Rail line.", "Zeile im Zug.", "", "", "", DateTimeOffset.UtcNow));

                    using var license = TestProtection.UseFakeLicense();
                    var w = new SettingsWindow("2.5.0", null, 1.0);
                    try
                    {
                        Invoke(w, "MyFixesCard_Loaded", null);
                        var list = (ListView)w.FindName("MyFixesList");
                        Assert.AreEqual("Polska linia.", RowsWhen(list, "Polska linia.").Single().Text);

                        // The page's language picker: Config["Output"] changes, the card follows.
                        var combo = (ComboBox)w.FindName("OutputLanguageCombo");
                        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(i => (string)i.Tag == "DE");
                        Assert.AreEqual("Deutsche Zeile.", RowsWhen(list, "Deutsche Zeile.").Single().Text);

                        // The game: pills and pack rows all end in SyncTranslationPickerState.
                        AppConfig.Set("Game", "StarRail");
                        SyncPicker(w);
                        var row = RowsWhen(list, "Zeile im Zug.").Single();

                        Invoke(w, "MyFixDelete_Click", new Button { Tag = row.Key });
                        for (int i = 0; i < 1000 && list.Visibility == Visibility.Visible; i++)
                        {
                            DoEvents();
                            System.Threading.Thread.Sleep(20);
                        }
                        Assert.AreEqual(0, railDe.UserFixCount, "Remove acts on the pair on screen");
                        Assert.AreEqual(1, genshinDe.UserFixCount, "and on nothing else");
                        Assert.AreEqual(1, genshinPl.UserFixCount);

                        // Off screen the card neither listens nor rebinds; it binds again when it loads.
                        Invoke(w, "MyFixesCard_Unloaded", null);
                        AppConfig.Set("Game", "Genshin");
                        SyncPicker(w);
                        for (int i = 0; i < 25; i++)
                        {
                            DoEvents();
                            System.Threading.Thread.Sleep(20);
                        }
                        Assert.IsFalse(Rows(list).Any(r => r.Text == "Deutsche Zeile."), "not rebound while unloaded");
                        Invoke(w, "MyFixesCard_Loaded", null);
                        Assert.AreEqual("Deutsche Zeile.", RowsWhen(list, "Deutsche Zeile.").Single().Text);
                        Invoke(w, "MyFixesCard_Unloaded", null);
                    }
                    finally
                    {
                        w.Close();
                    }
                }
                finally
                {
                    Clear();
                    if (game == null) AppConfig.Remove("Game"); else AppConfig.Set("Game", game);
                    if (output == null) AppConfig.Remove("Output"); else AppConfig.Set("Output", output);
                }
            });
        }

        private static void SyncPicker(SettingsWindow w) =>
            typeof(SettingsWindow).GetMethod("SyncTranslationPickerState", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(w, null);

        private static IReadOnlyList<SettingsWindow.MyFixRow> Rows(ListView list) =>
            (list.ItemsSource as IEnumerable<SettingsWindow.MyFixRow>)?.ToList() ?? new List<SettingsWindow.MyFixRow>();

        // The card fills in from a pool thread; waits until it lists the expected text.
        private static IReadOnlyList<SettingsWindow.MyFixRow> RowsWhen(ListView list, string text)
        {
            for (int i = 0; i < 1000; i++)
            {
                var rows = Rows(list);
                if (rows.Any(r => r.Text == text)) return rows;
                DoEvents();
                System.Threading.Thread.Sleep(20);
            }
            throw new AssertFailedException($"My fixes never listed \"{text}\"");
        }
    }

    /// <summary>
    /// Hotkey 9007 through the real overlay window: WndProc, the last shown line, and the rule that a newer line replaces
    /// an untouched fix window but never one the player is typing in.
    /// </summary>
    [TestClass]
    public class FixLineHotkeyWiringTests
    {
        private const int WmHotkey = 0x0312;

        private static bool PressFixHotkey(MainWindow main)
        {
            var wndProc = typeof(MainWindow).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic);
            var args = new object[] { IntPtr.Zero, WmHotkey, (IntPtr)HotkeySettingsManager.FixLineHotkeyId, IntPtr.Zero, false };
            wndProc.Invoke(main, args);
            DoEvents();
            return (bool)args[4];
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct LastInputInfo { public uint Size; public uint Time; }

        // The window's state, who has the foreground and when the desktop last had input: the window takes the focus,
        // so on a shared desktop a keystroke meant for another app can land in it (it did: a "/" in the fix box).
        private static string Describe(SuggestFixWindow w, IReadOnlyList<string> typed)
        {
            string owner = "none";
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                GetWindowThreadProcessId(fg, out uint pid);
                try { owner = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch (ArgumentException) { owner = "exited"; }
                if (pid == Environment.ProcessId) owner += " (this test process)";
            }
            var last = new LastInputInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInputInfo>() };
            string idle = GetLastInputInfo(ref last) ? $"{unchecked((uint)Environment.TickCount - last.Time)} ms ago" : "unknown";
            return $"untouched={w.IsUntouched} fix=\"{((TextBox)w.FindName("FixBox")).Text}\" note=\"{((TextBox)w.FindName("NoteBox")).Text}\"" +
                $" active={w.IsActive} foreground={owner} text input=[{string.Join(", ", typed.Select(t => "\"" + t + "\""))}]" +
                $" last desktop input {idle}";
        }

        [TestMethod]
        public void TheHotkey_OpensTheLastLine_ReplacesAnUntouchedWindow_AndKeepsAnEdit()
        {
            RunSta(() =>
            {
                var at = new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc);
                var main = new MainWindow();
                var notices = new List<(string Title, string Body)>();
                // The app shows a modal dialog here; a press that comes in while it is up must not stack another.
                main.NothingToFixNotice = (title, body) =>
                {
                    notices.Add((title, body));
                    if (notices.Count == 1) PressFixHotkey(main);
                };
                var open = new List<SuggestFixWindow>();
                SuggestFixWindow Opened()
                {
                    var w = main.OpenFixWindow;
                    if (w != null && !open.Contains(w))
                    {
                        open.Add(w);
                        w.Closed += (s, e) => open.Remove((SuggestFixWindow)s);
                    }
                    return w;
                }
                try
                {
                    Assert.IsTrue(PressFixHotkey(main), "9007 is handled, with no start gates in the way");
                    Assert.IsNull(Opened(), "nothing shown yet: no window");
                    var notice = notices.Single();
                    Assert.AreEqual("No line to fix yet", notice.Title, "said in a dialog, which a full-screen game cannot hide");
                    StringAssert.StartsWith(notice.Body, "Start translating.");
                    Assert.IsFalse(notice.Body.Contains("{0}"), notice.Body);

                    main.LastShownLine.Record("Genshin", "pl", "Hotkey line A.", "Linia A.", "Linia A.", "Paimon", at);
                    PressFixHotkey(main);
                    var first = Opened();
                    Assert.IsNotNull(first);
                    var typed = new List<string>(); // the test never types: anything here came from the desktop
                    first.PreviewTextInput += (s, e) => typed.Add(e.Text);
                    Assert.AreEqual("Hotkey line A.", first.Line.EnKey);
                    Assert.IsTrue(first.IsVisible);
                    PressFixHotkey(main);
                    Assert.AreSame(first, Opened(), "the same line: the open window comes forward");

                    main.LastShownLine.Record("Genshin", "pl", "Hotkey line B.", "Linia B.", "Linia B.", "Paimon", at.AddSeconds(4));
                    string before = Describe(first, typed);
                    PressFixHotkey(main);
                    var second = Opened();
                    Assert.AreNotSame(first, second, "the newer line kept the first window; before the press: " + before);
                    Assert.IsFalse(open.Contains(first), "an untouched window gives way to the newer line");
                    Assert.AreEqual("Hotkey line B.", second.Line.EnKey);
                    Assert.AreEqual("Hotkey line A.", second.Line.PrevEn, "the line before goes along as context");

                    ((TextBox)second.FindName("FixBox")).Text = "Linia B, poprawiona.";
                    main.LastShownLine.Record("Genshin", "pl", "Hotkey line C.", "Linia C.", "Linia C.", "", at.AddSeconds(8));
                    PressFixHotkey(main);
                    Assert.AreSame(second, Opened(), "an edit in progress is never thrown away");
                    Assert.IsTrue(second.IsVisible);

                    second.Close();
                    DoEvents();
                    Assert.IsNull(main.OpenFixWindow, "closing frees the slot");
                    PressFixHotkey(main);
                    Assert.AreEqual("Hotkey line C.", Opened().Line.EnKey, "the next press opens the newest line");
                }
                finally
                {
                    foreach (var w in open.ToList()) w.Close();
                    main.Close();
                }
            });
        }
    }

    /// <summary>The fix feature's copy: English and Polish, the house rules, and every key the code asks for.</summary>
    [TestClass]
    public class FixLineCopyTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
        private static readonly Regex FeatureKey = new Regex(@"^(fix_\w+|Tray_FixLine|Hotkey_9007_\w+)$");

        private static Dictionary<string, string> Copy(string file)
        {
            string path = Path.Combine(RepoRoot(), "GI-Subtitles", "Resources", file);
            return XDocument.Load(path).Root.Elements()
                .Select(e => (Key: (string)e.Attribute(X + "Key"), Value: e.Value))
                .Where(e => e.Key != null && FeatureKey.IsMatch(e.Key))
                .ToDictionary(e => e.Key, e => e.Value);
        }

        [TestMethod]
        public void EnglishAndPolish_HaveTheSameKeys_Translated_WithTheHouseRules()
        {
            var en = Copy("Strings.en-US.xaml");
            var pl = Copy("Strings.pl-PL.xaml");
            Assert.IsTrue(en.Count >= 20, "the feature's keys are there");
            CollectionAssert.AreEquivalent(en.Keys.OrderBy(k => k).ToList(), pl.Keys.OrderBy(k => k).ToList());
            var problems = new List<string>();
            foreach (var (file, copy) in new[] { ("en", en), ("pl", pl) })
            foreach (var (key, value) in copy)
            {
                if (value.Contains((char)0x2014)) problems.Add($"{file} {key}: em dash");
                if (value.Contains('!')) problems.Add($"{file} {key}: exclamation mark");
                if (Regex.IsMatch(value, @"\bAI\b") || value.IndexOf("sztuczn", StringComparison.OrdinalIgnoreCase) >= 0) problems.Add($"{file} {key}: mentions AI");
                if (Regex.IsMatch(value, @"\bOCR\b")) problems.Add($"{file} {key}: says OCR");
                if (value.Contains("{0}") != en[key].Contains("{0}")) problems.Add($"{file} {key}: placeholder differs from English");
            }
            foreach (var (key, value) in pl)
                if (value == en[key] && Regex.IsMatch(value, "[a-z]{4,}")) problems.Add($"pl {key}: copies English");
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        }

        [TestMethod]
        public void EveryKeyTheFeatureUses_IsInBothLanguages()
        {
            var en = Copy("Strings.en-US.xaml");
            var pl = Copy("Strings.pl-PL.xaml");
            string root = Path.Combine(RepoRoot(), "GI-Subtitles");
            var sources = new[]
            {
                "Views/SuggestFixWindow.xaml", "Views/SuggestFixWindow.xaml.cs", "Views/MainWindow.FixLine.cs",
                "Views/SettingsWindow.MyFixes.cs", "Views/SettingsWindow.xaml", "Core/UI/INotifyIcon.cs", "Core/Input/HotkeySettings.cs",
            };
            var used = new HashSet<string>();
            foreach (string source in sources)
            foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(root, source)), @"\b(fix_\w+|Tray_FixLine|Hotkey_9007_\w+)\b"))
                used.Add(m.Value);
            Assert.IsTrue(used.Count >= 15, "the scan found the feature's keys");
            var missing = used.Where(k => !en.ContainsKey(k) || !pl.ContainsKey(k)).ToList();
            Assert.AreEqual(0, missing.Count, "missing: " + string.Join(", ", missing));
        }
    }
}
