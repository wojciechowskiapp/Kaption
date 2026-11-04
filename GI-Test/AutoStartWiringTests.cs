using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Services.Automation;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>The app side of auto-start: the silent gate order, the waiting state, and the setting on Gra i napisy.</summary>
    [TestClass]
    public class AutoStartWiringTests
    {
        private static readonly GateSnapshot AllOpen = new(
            SessionActive: true, InitialSyncRunning: false, EngineReady: true, MatcherReady: true,
            RegionSet: true, GameRunning: true, DisplayModeTipPending: false, OverlayOverlapsRegion: false);

        [TestMethod]
        public void AllGatesOpen_StartPasses()
        {
            Assert.AreEqual(StartGate.None, AutoStartGates.FirstClosed(AllOpen));
        }

        [TestMethod]
        public void EachGateCloses_InTheStartButtonsOrder()
        {
            Assert.AreEqual(StartGate.Session, AutoStartGates.FirstClosed(AllOpen with { SessionActive = false }));
            Assert.AreEqual(StartGate.InitialSync, AutoStartGates.FirstClosed(AllOpen with { InitialSyncRunning = true }));
            Assert.AreEqual(StartGate.Engine, AutoStartGates.FirstClosed(AllOpen with { EngineReady = false }));
            Assert.AreEqual(StartGate.Matcher, AutoStartGates.FirstClosed(AllOpen with { MatcherReady = false }));
            Assert.AreEqual(StartGate.Region, AutoStartGates.FirstClosed(AllOpen with { RegionSet = false }));
            Assert.AreEqual(StartGate.GameRunning, AutoStartGates.FirstClosed(AllOpen with { GameRunning = false }));
            Assert.AreEqual(StartGate.DisplayModeTip, AutoStartGates.FirstClosed(AllOpen with { DisplayModeTipPending = true }));
            Assert.AreEqual(StartGate.OverlayOverlap, AutoStartGates.FirstClosed(AllOpen with { OverlayOverlapsRegion = true }));
        }

        [TestMethod]
        public void SeveralClosed_ReportsTheFirst()
        {
            var s = AllOpen with { EngineReady = false, RegionSet = false, OverlayOverlapsRegion = true };
            Assert.AreEqual(StartGate.Engine, AutoStartGates.FirstClosed(s));
            Assert.AreEqual(StartGate.Session, AutoStartGates.FirstClosed(s with { SessionActive = false }));
        }

        [TestMethod]
        [DataRow(AutoStartState.WaitingForGame, StartGate.None, true)]
        [DataRow(AutoStartState.Blocked, StartGate.GameRunning, true)]
        [DataRow(AutoStartState.Blocked, StartGate.Engine, false)]
        [DataRow(AutoStartState.Blocked, StartGate.DisplayModeTip, false)]
        [DataRow(AutoStartState.Translating, StartGate.None, false)]
        [DataRow(AutoStartState.PausedByUser, StartGate.None, false)]
        [DataRow(AutoStartState.Disabled, StartGate.None, false)]
        public void WaitingForGame_OnlyWhenTheGameIsWhatIsMissing(AutoStartState state, StartGate gate, bool waiting)
        {
            Assert.AreEqual(waiting, AutoStartGates.IsWaitingForGame(state, gate));
        }

        [TestMethod]
        public void TheSetupsPromise_IsKeptByTheController()
        {
            string views = System.IO.Path.Combine(RepoRoot(), "GI-Subtitles", "Views");
            string setup = System.IO.File.ReadAllText(System.IO.Path.Combine(views, "OnboardingWindow.xaml.cs"));
            string wiring = System.IO.File.ReadAllText(System.IO.Path.Combine(views, "MainWindow.AutoStart.cs"));
            string page = System.IO.File.ReadAllText(System.IO.Path.Combine(views, "SettingsWindow.Shell.cs"));
            const string key = "SettingsWindow.AutoStartTranslationKey";
            StringAssert.Contains(setup, "Config.Set(" + key, "the setup's game step writes the setting");
            StringAssert.Contains(setup, "onb_Done_AutoStart", "Gotowe says Kaption starts by itself when the setting is on");
            StringAssert.Contains(wiring, "Config.Get(" + key + ", true)", "the auto-start controller reads the same setting, default on");
            StringAssert.Contains(wiring, "new AutoStartController(", "something consumes it");
            StringAssert.Contains(page, "Config.Set(AutoStartTranslationKey", "the switch on Gra i napisy writes the same setting");
        }

        [TestMethod]
        public void GameProbe_FindsARunningProcess_ThenKeepsItCheaply_AndForgetsItWhenTheGameChanges()
        {
            string self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            var probe = new GameProbe();
            var t0 = System.DateTime.UtcNow;
            Assert.IsTrue(probe.Probe(new[] { self }, t0).Running, "the first probe lists processes and finds it");
            Assert.IsTrue(probe.Probe(new[] { self }, t0.AddMilliseconds(500)).Running, "the next one asks about the known process only");
            Assert.IsFalse(probe.Probe(new[] { "kaption-no-such-game" }, t0.AddSeconds(1)).Running, "another game: looked up again at once");
            Assert.IsFalse(probe.Probe(System.Array.Empty<string>(), t0.AddSeconds(2)).Running, "an unknown game is never running");
            Assert.IsTrue(GameProbe.Matches("GenshinImpact", new[] { "YuanShen", "GenshinImpact" }));
            Assert.IsFalse(GameProbe.Matches(null, new[] { "GenshinImpact" }));
        }

        [TestMethod]
        public void The25Card_IsForReturningUsersOnly()
        {
            Assert.IsTrue(SettingsWindow.ShouldShowIntro25(seen: false));
            Assert.IsFalse(SettingsWindow.ShouldShowIntro25(seen: true));
            string setup = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "GI-Subtitles", "Views", "OnboardingWindow.xaml.cs"));
            StringAssert.Contains(setup, "Config.Set(SettingsWindow.Intro25SeenKey, true)", "a new install finishing the setup never gets the card");
            RunSta(() =>
            {
                LoadLooseWindow("Views/SettingsWindow.xaml", "pl-PL", out var window);
                Assert.IsNotNull(window.FindName("Intro25Card"));
                Assert.IsNotNull(window.FindName("Intro25AutoToggle"), "the card carries the auto-start switch");
            });
        }

        [TestMethod]
        public void TheSwitchIsOnTheGamePage_NotBehindZaawansowane_AndOnByDefault()
        {
            RunSta(() =>
            {
                LoadLooseWindow("Views/SettingsWindow.xaml", "pl-PL", out var window);
                var toggle = (CheckBox)window.FindName("AutoTranslateToggle");
                Assert.IsNotNull(toggle, "AutoTranslateToggle");
                Assert.AreEqual(true, toggle.IsChecked, "on unless the user turned it off");
                bool inGamePage = false;
                for (DependencyObject d = toggle; d != null; d = LogicalTreeHelper.GetParent(d) ?? VisualTreeHelper.GetParent(d))
                {
                    Assert.IsFalse(d is Expander, "the auto-start switch must not sit inside an expander");
                    if (d is TabItem tab) inGamePage = (string)tab.Tag == SettingsNavigation.Game;
                }
                Assert.IsTrue(inGamePage, "the switch lives on Gra i napisy");
                var focus = (CheckBox)window.FindName("AutoTranslateFocusCheck");
                Assert.IsNotNull(focus, "the focus option exists (under Zaawansowane)");
                Assert.AreEqual(SettingsWindow.AutoStartTranslationKey, "AutoStartTranslation", "the key the setup window writes");
            });
        }

        // ── ux-01: the dialogue area filled when the game shows up ───────────

        private static string Wiring() =>
            System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "GI-Subtitles", "Views", "MainWindow.AutoStart.cs"));

        private static string Body(string code, string signature)
        {
            int start = code.IndexOf(signature, System.StringComparison.Ordinal);
            Assert.IsTrue(start > 0, signature);
            int end = code.IndexOf("\n        }", start, System.StringComparison.Ordinal);
            return code.Substring(start, end - start);
        }

        [TestMethod]
        public void TheWatcher_FillsAMissingArea_FromTheProbesOwnForeground_OnlyAtTheRegionGate()
        {
            string code = Wiring();
            string watch = Body(code, "private async void WatchGame()");
            StringAssert.Contains(watch, "TryAutoFillRegionAsync(profile, probe.Foreground)",
                "the real foreground: with 'running counts' a covered window would still be filled from");

            string fill = Body(code, "private async Task TryAutoFillRegionAsync(");
            StringAssert.Contains(fill, "_autoStart.State == AutoStartState.Blocked && _autoStart.BlockingGate == StartGate.Region");
            StringAssert.Contains(fill, "RegionAutoFill.ShouldTry(HasConfiguredRegion(),");
            StringAssert.Contains(fill, "ChooseRegion || OCRTimer.IsEnabled || _quickTranslateBusy", "never while a picker, translation or Ctrl+Q runs");
            StringAssert.Contains(fill, "await Task.Run(", "the window lookup lists processes: off the UI thread");
            StringAssert.Contains(fill, "_autoStart?.OnGatesChanged()", "the gate re-opens at once after the save");
        }

        [TestMethod]
        public void TheFill_NeverReplacesASavedArea_AndSavesItWhereTheGateLooks()
        {
            string fill = Body(Wiring(), "private async Task TryAutoFillRegionAsync(");
            int recheck = fill.IndexOf("bool savedNow = HasConfiguredRegion()", System.StringComparison.Ordinal);
            int config = fill.IndexOf("Config.Get<string>(\"Region\", \"\")", recheck, System.StringComparison.Ordinal);
            int save = fill.IndexOf("Config.Set(\"Region\", region)", System.StringComparison.Ordinal);
            int notify = fill.IndexOf("notify.Region = region.Split(',')", System.StringComparison.Ordinal);
            Assert.IsTrue(recheck > 0 && config > recheck && save > config, "both the gate's copy and Config are checked again after the lookup, before saving");
            Assert.IsTrue(notify > save, "HasConfiguredRegion reads notify.Region: the gate opens without a restart");
            StringAssert.Contains(fill, "RegionAutoFill.MaySave(savedNow, region)");
            StringAssert.Contains(fill, "_regionAutoFill.MarkFailed()", "a failed try reaches the card");

            int mark = fill.IndexOf("_regionAutoFill.MarkSet(region)", System.StringComparison.Ordinal);
            int keep = fill.IndexOf("Config.Set(RegionAutoFill.FilledRegionKey, region)", System.StringComparison.Ordinal);
            Assert.IsTrue(mark > save && keep > mark, "the filled area is remembered, so it never reads as hand-drawn after a restart");
            StringAssert.Contains(Wiring(), "_regionAutoFill.Restore(Config.Get<string>(RegionAutoFill.FilledRegionKey, null))",
                "the next launch keeps the note while the area is unchanged");
        }

        [TestMethod]
        public void TheFill_StaysQuiet_OverTheGame()
        {
            string fill = Body(Wiring(), "private async Task TryAutoFillRegionAsync(");
            Assert.IsFalse(fill.Contains("AnswerRegion"), "the answer area has no reader (FeatureAnswerTranslationEnabled is off)");
            Assert.IsFalse(fill.Contains("OnCaptureRegionUserChange") || fill.Contains("ShowOverlapWarnIfNeeded"), "no overlap dialog over the game");
            Assert.IsFalse(fill.Contains("AutoRegionService.Detect"), "no OCR on a splash or login screen; the measured ratios instead");
            Assert.IsFalse(fill.Contains("Logger.Log.Error"), "a best-effort step is not a GlitchTip event");
        }

        // ── "Later" on the game-switch restart prompt ────────────────────────

        [TestMethod]
        public void AGameSwitch_WaitsForTheRestart_UntilTheSavedGameIsTheLoadedOne()
        {
            Assert.IsFalse(GameSwitchHold.AwaitsRestart("Genshin", "Genshin"));
            Assert.IsFalse(GameSwitchHold.AwaitsRestart("Genshin", "genshin"), "case is the same game");
            Assert.IsFalse(GameSwitchHold.AwaitsRestart("ZZZ", "Zenless"), "an alias is the same game");
            Assert.IsFalse(GameSwitchHold.AwaitsRestart("Genshin", null), "no saved game reads as the default");
            Assert.IsTrue(GameSwitchHold.AwaitsRestart("Genshin", "StarRail"), "Star Rail picked, Genshin still loaded");
            Assert.IsTrue(GameSwitchHold.AwaitsRestart("StarRail", "WuWa"));

            Assert.IsTrue(GameSwitchHold.AutoStartEnabled(settingOn: true, switchPending: false));
            Assert.IsFalse(GameSwitchHold.AutoStartEnabled(settingOn: true, switchPending: true), "no start with the old game's data");
            Assert.IsFalse(GameSwitchHold.AutoStartEnabled(settingOn: false, switchPending: false));
        }

        [TestMethod]
        public void AGameSwitch_HoldsAutoStart_AndTheCardOffersTheRestart()
        {
            var s = StartCardModel.Compute(new StartCardInputs { Reader = ReaderState.Ready, PackOnDisk = true, WaitingForGame = true, GameSwitchPending = true });
            Assert.AreEqual(StartCardKind.GameSwitchPending, s.Kind, "not 'waiting for the game': it would not start");
            Assert.AreEqual(StartCardAction.RestartApp, s.Action);
            var downloading = StartCardModel.Compute(new StartCardInputs { Downloading = true, GameSwitchPending = true });
            Assert.AreEqual(StartCardKind.GameSwitchPending, downloading.Kind, "the restart comes before the new game's downloads");
            var manual = StartCardModel.Compute(new StartCardInputs { Translating = true, Reader = ReaderState.Ready, GameSwitchPending = true });
            Assert.AreEqual(StartCardKind.Translating, manual.Kind, "a session the player started stays stoppable");

            string code = Wiring();
            StringAssert.Contains(Body(code, "private async void WatchGame()"), "ApplyGameSwitchHold();", "every tick, whichever page made the switch");
            StringAssert.Contains(Body(code, "public bool StartThroughGates()"), "if (_w.GameSwitchPending()) return false;",
                "a start between two ticks is refused too");
            StringAssert.Contains(code, "GameSwitchHold.AwaitsRestart(Game, Config.Get<string>(\"Game\", \"Genshin\"))",
                "the loaded game is the one MainWindow read at launch");
            StringAssert.Contains(code, "data.AutoStartSettingChanged = on => _autoStart?.SetEnabled(GameSwitchHold.AutoStartEnabled(on, GameSwitchPending()));",
                "turning the switch on during a pending switch does not undo the hold");
        }
    }
}
