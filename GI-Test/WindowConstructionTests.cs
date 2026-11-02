using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;
// GI_Test has a Config of its own; these windows read and write the app's.
using AppConfig = GI_Subtitles.Core.Config.Config;

namespace GI_Test
{
    /// <summary>
    /// Windows built with their code-behind, as the app builds them. The render tests load the XAML loose, without
    /// handlers, so they cannot see a handler that runs while InitializeComponent applies the XAML defaults: in 2.5
    /// RC that turned a saved "off" for auto-start back on at every launch and threw before Settings could open.
    /// </summary>
    [TestClass]
    public class WindowConstructionTests
    {
        private static T Find<T>(System.Windows.FrameworkElement window, string name) where T : class =>
            window.FindName(name) as T ?? throw new AssertFailedException($"no {name} in {window.GetType().Name}");

        private static string ConfigFile => Path.Combine(TestEnvironment.ConfigDir, "Config.json");

        /// <summary>The saved settings, key by key, as the app wrote them to disk.</summary>
        private static Dictionary<string, string> Saved() =>
            (JsonNode.Parse(File.ReadAllText(ConfigFile)) as JsonObject)?
                .ToDictionary(kv => kv.Key, kv => kv.Value?.ToJsonString() ?? "null") ?? new Dictionary<string, string>();

        private static void AssertUnchanged(Dictionary<string, string> before, string what)
        {
            var after = Saved();
            var changed = before.Keys.Union(after.Keys)
                .Where(k => !before.TryGetValue(k, out var b) || !after.TryGetValue(k, out var a) || a != b)
                .Select(k => $"{k}: {(before.TryGetValue(k, out var b) ? b : "(none)")} -> {(after.TryGetValue(k, out var a) ? a : "(none)")}")
                .ToList();
            Assert.AreEqual(0, changed.Count, $"{what} changed saved settings: {string.Join("; ", changed)}");
        }

        [TestMethod]
        public void TheAppsSettings_GoToATemporaryProfile()
        {
            AppConfig.Set("Tests.Probe", 1);
            StringAssert.StartsWith(Path.GetFullPath(ConfigFile), Path.GetFullPath(Path.GetTempPath()));
            StringAssert.Contains(File.ReadAllText(ConfigFile), "Tests.Probe", "tests never read or write a real profile");
        }

        [TestMethod]
        public void Settings_KeepsASavedOff_ForBothAutoStartSettings_AndTheUsersChangesStillWrite()
        {
            RunSta(() =>
            {
                AppConfig.Set(SettingsWindow.AutoStartTranslationKey, false);
                AppConfig.Set(SettingsWindow.AutoStartFocusKey, false);
                AppConfig.Set(SettingsWindow.Intro25SeenKey, false); // a returning user who has not dismissed the 2.5 card

                // Settings builds its file protection while it is constructed; see TestProtection.UseFakeLicense.
                using var license = TestProtection.UseFakeLicense();
                var w = new SettingsWindow("2.5.0", null, 1.0);
                try
                {
                    Assert.IsFalse(AppConfig.Get(SettingsWindow.AutoStartTranslationKey, true), "building the window keeps a saved off");
                    Assert.IsFalse(AppConfig.Get(SettingsWindow.AutoStartFocusKey, true), "the same for the focus setting");
                    var toggle = Find<CheckBox>(w, "AutoTranslateToggle");
                    var card = Find<CheckBox>(w, "Intro25AutoToggle");
                    var focus = Find<CheckBox>(w, "AutoTranslateFocusCheck");
                    Assert.AreEqual(false, toggle.IsChecked, "the switch shows the saved setting, not the XAML default");
                    Assert.AreEqual(false, card.IsChecked);
                    Assert.AreEqual(false, focus.IsChecked);

                    // The user's own changes still write, from either switch, and both show them.
                    toggle.IsChecked = true;
                    Assert.IsTrue(AppConfig.Get(SettingsWindow.AutoStartTranslationKey, false));
                    Assert.AreEqual(true, card.IsChecked);
                    card.IsChecked = false;
                    Assert.IsFalse(AppConfig.Get(SettingsWindow.AutoStartTranslationKey, true));
                    Assert.AreEqual(false, toggle.IsChecked);
                    focus.IsChecked = true;
                    Assert.IsTrue(AppConfig.Get(SettingsWindow.AutoStartFocusKey, false));

                    // "Rozumiem" on the 2.5 card writes only its own key.
                    var before = Saved();
                    typeof(SettingsWindow).GetMethod("Intro25Ok_Click", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(w, new object[] { null, null });
                    Assert.IsTrue(AppConfig.Get(SettingsWindow.Intro25SeenKey, false));
                    before[SettingsWindow.Intro25SeenKey] = "true";
                    AssertUnchanged(before, "Rozumiem");
                }
                finally
                {
                    w.Close();
                }
            });
        }

        [TestMethod]
        public void Settings_DefaultsToOn_WhenNothingWasSaved_WithoutWritingIt()
        {
            RunSta(() =>
            {
                AppConfig.Remove(SettingsWindow.AutoStartTranslationKey);
                AppConfig.Remove(SettingsWindow.AutoStartFocusKey);
                // Settings builds its file protection while it is constructed; see TestProtection.UseFakeLicense.
                using var license = TestProtection.UseFakeLicense();
                var w = new SettingsWindow("2.5.0", null, 1.0);
                try
                {
                    Assert.AreEqual(true, Find<CheckBox>(w, "AutoTranslateToggle").IsChecked);
                    Assert.AreEqual(true, Find<CheckBox>(w, "Intro25AutoToggle").IsChecked);
                    Assert.AreEqual(true, Find<CheckBox>(w, "AutoTranslateFocusCheck").IsChecked);
                    Assert.IsFalse(AppConfig.Has(SettingsWindow.AutoStartTranslationKey), "showing the default is not a choice");
                    Assert.IsFalse(AppConfig.Has(SettingsWindow.AutoStartFocusKey));
                }
                finally
                {
                    w.Close();
                }
            });
        }

        [TestMethod]
        public void Settings_BuildingTheWindow_ChangesNoSavedSetting()
        {
            RunSta(() =>
            {
                // Each unlike its control's XAML default, so a write while the XAML loads shows up here.
                AppConfig.Set(SettingsWindow.AutoStartTranslationKey, false);
                AppConfig.Set(SettingsWindow.AutoStartFocusKey, false);
                AppConfig.Set("AutoShrinkText", false);
                AppConfig.Set("FontFamily", "Consolas");
                AppConfig.Set("Size", 30);
                var before = Saved();
                // Settings builds its file protection while it is constructed; see TestProtection.UseFakeLicense.
                using var license = TestProtection.UseFakeLicense();
                var w = new SettingsWindow("2.5.0", null, 1.0);
                try
                {
                    AssertUnchanged(before, "building Settings");
                    Assert.AreEqual(false, Find<CheckBox>(w, "AutoShrinkCheckBox").IsChecked);
                    Assert.AreEqual("Consolas", (Find<ComboBox>(w, "FontFamilyComboBox").SelectedItem as ComboBoxItem)?.Tag);
                }
                finally
                {
                    w.Close();
                }
            });
        }

        [TestMethod]
        public void Setup_ShowsASavedOff_AndItsSkipBoxWritesTheSameSetting()
        {
            RunSta(() =>
            {
                AppConfig.Set(SettingsWindow.AutoStartTranslationKey, false);
                var before = Saved();
                var w = new OnboardingWindow(null, OnboardingMode.Rerun);
                try
                {
                    AssertUnchanged(before, "building the setup");
                    var skip = Find<CheckBox>(w, "SkipAutoStartCheck");
                    Assert.AreEqual(true, skip.IsChecked, "the setup shows the saved off as \"don't start by itself\"");

                    // Leaving the Gra step commits the box: unticked turns auto-start on, ticked turns it off.
                    var leave = typeof(OnboardingWindow).GetMethod("LeaveStep", BindingFlags.Instance | BindingFlags.NonPublic);
                    skip.IsChecked = false;
                    leave.Invoke(w, new object[] { OnboardingStep.Game });
                    Assert.IsTrue(AppConfig.Get(SettingsWindow.AutoStartTranslationKey, false));
                    skip.IsChecked = true;
                    leave.Invoke(w, new object[] { OnboardingStep.Game });
                    Assert.IsFalse(AppConfig.Get(SettingsWindow.AutoStartTranslationKey, true));
                }
                finally
                {
                    w.Close();
                }
            });
        }

        [TestMethod]
        public void Looks_KinoMovesTheSubtitle_AndAnotherLookPutsItBack()
        {
            RunSta(() =>
            {
                foreach (string k in new[] { "Size", "SubtitleBgOpacity", "Overlay.Outline", "Overlay.LetterSpacing", "Overlay.Font", "FontFamily",
                             SettingsWindow.PadBeforeCinemaKey })
                    AppConfig.Remove(k);
                AppConfig.Set("Pad", new[] { -187, -318 });
                using var license = TestProtection.UseFakeLicense();
                var w = new SettingsWindow("2.5.0", null, 1.0);
                try
                {
                    Assert.AreEqual(false, Find<RadioButton>(w, "PresetCinema").IsChecked, "building the page picks nothing");
                    Find<RadioButton>(w, "PresetCinema").IsChecked = true;
                    var kino = GI_Subtitles.Services.Rendering.OverlayStyle.FromConfig();
                    Assert.AreEqual(GI_Subtitles.Services.Rendering.OverlayPreset.Cinema, kino.MatchingPreset());
                    Assert.AreEqual(0, kino.PadHorizontal, "Kino centres the subtitle");
                    Assert.AreNotEqual(-187, kino.PadVertical, "and moves it down to the dialogue");
                    Assert.AreEqual(26, (int)Find<Slider>(w, "FontSizeSlider").Value, "the size slider follows the look");

                    Find<RadioButton>(w, "PresetClassic").IsChecked = true;
                    var back = GI_Subtitles.Services.Rendering.OverlayStyle.FromConfig();
                    Assert.AreEqual(GI_Subtitles.Services.Rendering.OverlayPreset.Classic, back.MatchingPreset());
                    Assert.AreEqual((-187, -318), (back.PadVertical, back.PadHorizontal), "the position from before Kino comes back");
                    Assert.IsFalse(AppConfig.Has(SettingsWindow.PadBeforeCinemaKey));
                }
                finally
                {
                    w.Close();
                    AppConfig.Remove("Pad");
                    foreach (string k in new[] { "Size", "SubtitleBgOpacity", "Overlay.Outline", "Overlay.LetterSpacing" }) AppConfig.Remove(k);
                }
            });
        }

        /// <summary>
        /// "Karta buildu" in the real window: the Skróty tile on Start opens the maker in the browser (a recorder here),
        /// reads the UID off the screen only when none is remembered, says so in one quiet line when it could not read
        /// one, and "Zapomnij UID" under Start › Zaawansowane forgets it, touching no other setting.
        /// </summary>
        [TestMethod]
        public void Settings_BuildCardTile_OpensTheMaker_AndForgetUidClearsWhatItKept()
        {
            RunSta(() =>
            {
                string game = AppConfig.Has("Game") ? AppConfig.Get<string>("Game", null) : null;
                string lang = AppConfig.Has("UILang") ? AppConfig.Get<string>("UILang", null) : null;
                GI_Subtitles.Services.Cards.BuildCardMemory.ForgetAll();
                AppConfig.Set("Game", "Genshin");
                AppConfig.Set("UILang", "pl-PL");
                using var license = TestProtection.UseFakeLicense();
                var w = new SettingsWindow("2.5.0", null, 1.0);
                try
                {
                    var opened = new List<string>();
                    string onScreen = null;
                    int reads = 0;
                    w.OpenUrl = opened.Add;
                    w.ReadUidFromScreen = g => { reads++; return BuildCardTests.OnScreen(onScreen); };
                    var tile = Find<Button>(w, "BuildCardTile");
                    var note = Find<TextBlock>(w, "BuildCardNote");
                    var forget = Find<Button>(w, "BuildCardForgetButton");
                    void Click(Button b)
                    {
                        b.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, b));
                        DoEvents();
                    }
                    Assert.IsFalse(forget.IsEnabled, "nothing to forget yet");

                    // Nothing on screen: the page without a UID, and one line under the tile instead of a message box.
                    Click(tile);
                    Assert.AreEqual("https://kaption.one/pl/moj-build/?game=genshin", opened.Last());
                    Assert.AreEqual(System.Windows.Visibility.Visible, note.Visibility);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(note.Text));
                    Assert.IsFalse(AppConfig.Has("BuildCard.Uid.Genshin"));

                    // The UID on screen: read, opened with it, kept; the line goes away.
                    onScreen = "735366201";
                    Click(tile);
                    Assert.AreEqual("https://kaption.one/pl/moj-build/?game=genshin&uid=735366201", opened.Last());
                    Assert.AreEqual(System.Windows.Visibility.Collapsed, note.Visibility);
                    Assert.AreEqual("735366201", AppConfig.Get<string>("BuildCard.Uid.Genshin", null));
                    Assert.AreEqual(2, reads);

                    // The next click opens with it straight away; English opens the English page.
                    AppConfig.Set("UILang", "en-US");
                    Click(tile);
                    Assert.AreEqual("https://kaption.one/my-build/?game=genshin&uid=735366201", opened.Last());
                    Assert.AreEqual(2, reads, "no second read");
                    Assert.IsTrue(forget.IsEnabled);
                    StringAssert.Contains(Find<TextBlock>(w, "BuildCardMemoryText").Text, "735366201");

                    // Zapomnij UID: the key goes, nothing else changes, and the next click reads the screen again.
                    var before = Saved();
                    Click(forget);
                    before.Remove("BuildCard.Uid.Genshin");
                    AssertUnchanged(before, "Zapomnij UID");
                    Assert.IsFalse(forget.IsEnabled);
                    Click(tile);
                    Assert.AreEqual(3, reads);
                }
                finally
                {
                    w.Close();
                    GI_Subtitles.Services.Cards.BuildCardMemory.ForgetAll();
                    if (game == null) AppConfig.Remove("Game"); else AppConfig.Set("Game", game);
                    if (lang == null) AppConfig.Remove("UILang"); else AppConfig.Set("UILang", lang);
                }
            });
        }

        /// <summary>
        /// The app's own read under the tile, with the screen access replaced by a recorder: it blacks out the subtitle
        /// panel and the Ctrl+Q popup that MainWindow reports, gets 3 s while translating and 1.5 s otherwise, and the
        /// line under the tile says what happened: the reader still loading, or for Star Rail where the game shows the UID.
        /// </summary>
        [TestMethod]
        public void Settings_BuildCardRead_MasksTheOverlay_WaitsLongerWhileTranslating_AndSaysWhy()
        {
            RunSta(() =>
            {
                string game = AppConfig.Has("Game") ? AppConfig.Get<string>("Game", null) : null;
                string lang = AppConfig.Has("UILang") ? AppConfig.Get<string>("UILang", null) : null;
                GI_Subtitles.Services.Cards.BuildCardMemory.ForgetAll();
                AppConfig.Set("Game", "Genshin");
                AppConfig.Set("UILang", "pl-PL");
                using var license = TestProtection.UseFakeLicense();
                var w = new SettingsWindow("2.5.0", null, 1.0);
                try
                {
                    var opened = new List<string>();
                    var asked = new List<(string Game, IReadOnlyList<System.Drawing.Rectangle> Exclude, System.TimeSpan Budget)>();
                    var answer = new GI_Subtitles.Services.Cards.UidReadResult(null, GI_Subtitles.Services.Cards.UidReadOutcome.NotFound,
                        GI_Subtitles.Services.Cards.UidCorner.None, System.TimeSpan.FromMilliseconds(30));
                    bool translating = true;
                    var panel = new System.Drawing.Rectangle(980, 1032, 900, 48);
                    var popup = new System.Drawing.Rectangle(1400, 300, 520, 160);
                    w.OpenUrl = opened.Add;
                    w.IsOcrRunning = () => translating;
                    w.OverlayScreenAreas = () => new[] { panel, popup };
                    w.ReadGameScreen = (g, reader, exclude, budget) =>
                    {
                        lock (asked) asked.Add((g, exclude, budget));
                        return answer;
                    };
                    var tile = Find<Button>(w, "BuildCardTile");
                    var note = Find<TextBlock>(w, "BuildCardNote");
                    void Click()
                    {
                        int before = opened.Count;
                        tile.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, tile));
                        CharacterOverviewRenderTests.Pump(() => opened.Count > before);
                        Assert.AreEqual(before + 1, opened.Count, "the page opened");
                    }
                    // The line the app picks, through the window's own string lookup (this harness has no app dictionaries,
                    // so the code's English wording; BuildCardTests check the words in each language).
                    string Line(GI_Subtitles.Services.Cards.UidReadOutcome outcome, string gameId) => SettingsWindow.BuildCardLine(
                        new GI_Subtitles.Services.Cards.BuildCardTarget("", null, GI_Subtitles.Services.Cards.UidSource.None, outcome),
                        gameId, false, (k, f) => System.Windows.Application.Current?.TryFindResource(k) as string ?? f);

                    // Translating: the longer budget, and the overlay's two areas blacked out with this window.
                    Click();
                    Assert.AreEqual(1, asked.Count);
                    Assert.AreEqual("Genshin", asked[0].Game);
                    Assert.AreEqual(System.TimeSpan.FromSeconds(3), asked[0].Budget, "3 s while translating");
                    CollectionAssert.Contains(asked[0].Exclude.ToList(), panel, "the subtitle panel is blacked out");
                    CollectionAssert.Contains(asked[0].Exclude.ToList(), popup, "the Ctrl+Q popup too");
                    Assert.AreEqual(Line(GI_Subtitles.Services.Cards.UidReadOutcome.NotFound, "Genshin"), note.Text, "Genshin: the plain line");

                    // Not translating: the budget stays 1.5 s.
                    translating = false;
                    Click();
                    Assert.AreEqual(System.TimeSpan.FromSeconds(1.5), asked[1].Budget, "1.5 s when idle");

                    // The reader is still loading in this window (no engine was started): its own line.
                    answer = answer with { Outcome = GI_Subtitles.Services.Cards.UidReadOutcome.NoReader };
                    Click();
                    Assert.AreEqual(SettingsWindow.EngineStatus.Loading, w.Engine);
                    Assert.AreEqual(Line(GI_Subtitles.Services.Cards.UidReadOutcome.NoReader, "Genshin"), note.Text);
                    Assert.AreNotEqual(Line(GI_Subtitles.Services.Cards.UidReadOutcome.NotFound, "Genshin"), note.Text, "loading has its own line");

                    // Star Rail shows the UID in its menu: the plain line says where.
                    AppConfig.Set("Game", "StarRail");
                    answer = answer with { Outcome = GI_Subtitles.Services.Cards.UidReadOutcome.NotFound };
                    Click();
                    Assert.AreEqual("StarRail", asked.Last().Game);
                    Assert.AreEqual(Line(GI_Subtitles.Services.Cards.UidReadOutcome.NotFound, "StarRail"), note.Text);
                    string plain = Line(GI_Subtitles.Services.Cards.UidReadOutcome.NotFound, "Genshin");
                    Assert.IsTrue(note.Text.StartsWith(plain) && note.Text.Length > plain.Length, "the plain line and where the menu shows the UID");

                    // A UID on screen: opened with it, no line.
                    answer = new GI_Subtitles.Services.Cards.UidReadResult("809162009", GI_Subtitles.Services.Cards.UidReadOutcome.Read,
                        GI_Subtitles.Services.Cards.UidCorner.BottomLeft, System.TimeSpan.FromMilliseconds(30));
                    Click();
                    Assert.AreEqual("https://kaption.one/pl/moj-build/?game=starrail&uid=809162009", opened.Last());
                    Assert.AreEqual(System.Windows.Visibility.Collapsed, note.Visibility);
                }
                finally
                {
                    w.Close();
                    GI_Subtitles.Services.Cards.BuildCardMemory.ForgetAll();
                    if (game == null) AppConfig.Remove("Game"); else AppConfig.Set("Game", game);
                    if (lang == null) AppConfig.Remove("UILang"); else AppConfig.Set("UILang", lang);
                }
            });
        }

        [TestMethod]
        public void Overlay_BuildingTheWindow_ChangesNoSavedSetting()
        {
            RunSta(() =>
            {
                AppConfig.Set(SettingsWindow.AutoStartTranslationKey, false);
                var before = Saved();
                var w = new MainWindow();
                try
                {
                    AssertUnchanged(before, "building the overlay");
                }
                finally
                {
                    w.Close();
                }
            });
        }
    }
}
