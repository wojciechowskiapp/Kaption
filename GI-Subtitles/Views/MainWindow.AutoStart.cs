using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Automation;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Auto-start wiring: the controller (Services/Automation/AutoStartController.cs) decides; this file feeds it the
    /// game's running and foreground edges, answers its silent gate check and runs the same start/stop path as the
    /// Start button. It never opens a gate dialog and never bypasses a gate.
    /// </summary>
    public partial class MainWindow
    {
        private AutoStartController _autoStart;
        private DispatcherTimer _autoStartWatch;
        private Action _toggleCore;
        private bool _watchPrimed, _lastGameRunning, _lastGameForeground, _probing;
        /// <summary>The controller's own start is running the Start gates (TryGateGameRunning trusts the probe then).</summary>
        private bool _autoStartStarting;
        private readonly GameProbe _gameProbe = new();
        /// <summary>This session's one try at filling a missing dialogue area when the game shows up.</summary>
        private readonly RegionAutoFill _regionAutoFill = new();
        private bool _gameSwitchHeld;

        private sealed class AutoStartHost : IAutoStartHost
        {
            private readonly MainWindow _w;
            public AutoStartHost(MainWindow w) { _w = w; }

            public bool IsTranslationRunning => _w.OCRTimer.IsEnabled;

            public StartGate FirstClosedGate() => _w.SilentFirstClosedGate();

            public bool StartThroughGates()
            {
                // A switch waiting for the restart: the loaded data belongs to the other game.
                if (_w.GameSwitchPending()) return false;
                _w._autoStartStarting = true;
                try
                {
                    if (!_w.OCRTimer.IsEnabled) _w._toggleCore?.Invoke();
                }
                finally
                {
                    _w._autoStartStarting = false;
                }
                return _w.OCRTimer.IsEnabled;
            }

            public void StopTranslation()
            {
                if (_w.OCRTimer.IsEnabled) _w._toggleCore?.Invoke();
            }
        }

        /// <summary>After the dashboard actions are wired: wraps the toggle so real user starts and stops reach the controller.</summary>
        private void InitAutoStart()
        {
            _toggleCore = data.OnToggleOCR;
            data.OnToggleOCR = () =>
            {
                bool was = OCRTimer.IsEnabled;
                _toggleCore();
                // Button and tray come through here, the hotkey calls NotifyUserToggled itself; the controller's own
                // start and stop use _toggleCore, so they are never taken for the user's.
                NotifyUserToggled(was);
            };

            bool setting = Config.Get(SettingsWindow.AutoStartTranslationKey, true);
            bool enabled = GameSwitchHold.AutoStartEnabled(setting, GameSwitchPending());
            _autoStart = new AutoStartController(new AutoStartHost(this), enabled);
            _autoStart.StateChanged += (_, __) =>
            {
                string gate = _autoStart.BlockingGate != StartGate.None ? $" ({_autoStart.BlockingGate})" : string.Empty;
                Logger.Log.Info($"Auto-start: {_autoStart.State}{gate}.");
                data?.UpdateDashboardStatus();
            };
            data.IsWaitingForGame = () => _autoStart != null && AutoStartGates.IsWaitingForGame(_autoStart.State, _autoStart.BlockingGate);
            data.AutoStartBlockedGate = AutoStartBlockingGateForCard;
            data.OnResolveGate = ResolveGate;
            data.GatesChanged = () => _autoStart?.OnGatesChanged();
            data.AutoStartSettingChanged = on => _autoStart?.SetEnabled(GameSwitchHold.AutoStartEnabled(on, GameSwitchPending()));
            data.GameSwitchAwaitsRestart = GameSwitchPending;
            data.LoadedGame = () => Game;
            _regionAutoFill.Restore(Config.Get<string>(RegionAutoFill.FilledRegionKey, null));
            data.GetRegionAutoFillNote = () => _regionAutoFill.NoteFor(Config.Get<string>("Region", ""), HasConfiguredRegion());

            _autoStartWatch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
            _autoStartWatch.Tick += (_, __) => WatchGame();
            _autoStartWatch.Start();
            Closed += (_, __) => _autoStartWatch?.Stop();
            Logger.Log.Info($"Auto-start: {(enabled ? "on" : "off")} at launch.");
        }

        /// <summary>Config["Game"] names another game than the one this process loaded (a switch waiting for the restart).</summary>
        private bool GameSwitchPending() => GameSwitchHold.AwaitsRestart(Game, Config.Get<string>("Game", "Genshin"));

        // Every tick: a switch made on any page (pill, pack row, setup) holds auto-start until the restart.
        private void ApplyGameSwitchHold()
        {
            bool pending = GameSwitchPending();
            if (pending != _gameSwitchHeld)
            {
                _gameSwitchHeld = pending;
                Logger.Log.Info(pending
                    ? $"Auto-start: held, {Config.Get<string>("Game", "Genshin")} is selected but {Game} stays loaded until the restart."
                    : "Auto-start: the loaded game is selected again.");
                data?.UpdateDashboardStatus();
            }
            _autoStart.SetEnabled(GameSwitchHold.AutoStartEnabled(Config.Get(SettingsWindow.AutoStartTranslationKey, true), pending));
        }

        /// <summary>
        /// For the Start card: the gate, other than the game itself, that would hold auto-start back right now. Checked
        /// whether or not the game is in front, so the card can say it while the user looks at Kaption.
        /// </summary>
        private StartGate AutoStartBlockingGateForCard()
        {
            if (_autoStart == null) return StartGate.None;
            if (_autoStart.State is AutoStartState.Disabled or AutoStartState.PausedByUser or AutoStartState.Translating) return StartGate.None;
            if (OCRTimer.IsEnabled) return StartGate.None;
            var gate = SilentFirstClosedGate(assumeGameRunning: true);
            return gate == StartGate.GameRunning ? StartGate.None : gate;
        }

        /// <summary>"Pokaż wskazówkę", "Popraw obszar", ...: the Start button's own dialog or page for that gate.</summary>
        private void ResolveGate(StartGate gate)
        {
            try
            {
                switch (gate)
                {
                    case StartGate.Session: TryGateOcrStart(); break;
                    case StartGate.InitialSync: TryGateInitialDictionarySync(); break;
                    case StartGate.Engine: TryGateEngineReady(); break;
                    case StartGate.Matcher: TryGateMatcherReady(); break;
                    case StartGate.Region: data?.OnSelectRegion?.Invoke(); break;
                    case StartGate.DisplayModeTip: TryGateFullscreenTip(); break;
                    case StartGate.OverlayOverlap: TryGateOverlayNotInRegion(); break;
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Auto-start: resolving {gate} failed: {ex.Message}");
            }
            _autoStart?.OnGatesChanged();
            data?.UpdateDashboardStatus();
        }

        /// <summary>A start or stop the user made: a manual stop pauses auto-start until the user starts again.</summary>
        private void NotifyUserToggled(bool wasRunning)
        {
            bool now = OCRTimer.IsEnabled;
            if (!wasRunning && now) _autoStart?.OnUserStarted();
            else if (wasRunning && !now) _autoStart?.OnUserStopped();
        }

        /// <summary>The app stopped translation itself (region cleared, sign-in lost): a failed start, not a pause.</summary>
        private void NotifyStoppedByApp() => _autoStart?.OnTranslationStoppedExternally();

        // Off the UI thread: listing processes costs tens of milliseconds and the overlay animates on this thread.
        private async void WatchGame()
        {
            if (_autoStart == null || _probing) return;
            _probing = true;
            try
            {
                ApplyGameSwitchHold();
                var profile = Services.Detection.GameRegionProfile.Get(Config.Get<string>("Game", "Genshin"));
                string[] names = profile?.ProcessNames ?? Array.Empty<string>();
                // A generic executable (WuWa's Unreal shell) counts only with one of the game's window titles.
                string[] titles = profile?.RequiresWindowTitle == true ? profile.WindowTitles ?? Array.Empty<string>() : null;
                bool requireFocus = Config.Get(SettingsWindow.AutoStartFocusKey, true);
                var probe = await Task.Run(() => _gameProbe.Probe(names, DateTime.UtcNow, titles));
                if (_autoStart == null) return;
                bool running = probe.Running;
                bool foreground = requireFocus ? probe.Foreground : probe.Running;
                if (!_watchPrimed || running != _lastGameRunning)
                {
                    _lastGameRunning = running;
                    _autoStart.OnGameRunningChanged(running);
                }
                if (!_watchPrimed || foreground != _lastGameForeground)
                {
                    _lastGameForeground = foreground;
                    _autoStart.OnGameForeground(foreground);
                }
                _watchPrimed = true;
                // The probe's own answer: with "running counts" on, a covered window still is not in front.
                await TryAutoFillRegionAsync(profile, probe.Foreground);
                if (_autoStart == null) return;
                // Reader, pack, region and sign-in changes reach the controller here; its cooldown spaces the retries.
                if (_autoStart.State == AutoStartState.Blocked) _autoStart.OnGatesChanged();
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Auto-start watch failed: {ex.Message}");
            }
            finally
            {
                _probing = false;
            }
        }

        /// <summary>
        /// Auto-start waits on the dialogue area alone and the game is in front: once the game window has kept one size
        /// for a few probes, save the area from the profile's ratios. Never replaces a saved area; a failure shows on the
        /// Start card with the picker as its action. Logs stay Info/Warn: a best-effort step is not a Sentry event.
        /// </summary>
        private async Task TryAutoFillRegionAsync(Services.Detection.GameRegionProfile profile, bool gameInFront)
        {
            bool blockedOnRegion = _autoStart.State == AutoStartState.Blocked && _autoStart.BlockingGate == StartGate.Region;
            bool busy = ChooseRegion || OCRTimer.IsEnabled || _quickTranslateBusy;
            if (!RegionAutoFill.ShouldTry(HasConfiguredRegion(), blockedOnRegion, gameInFront, _regionAutoFill.Decided, busy))
            {
                if (!gameInFront) _regionAutoFill.Interrupt();
                return;
            }

            RegionAutoFill.WindowRect? window = null;
            try
            {
                // Lists processes and windows (tens of ms): off the UI thread, and only while the area is missing.
                window = await Task.Run(() => Services.Detection.GameWindowDetector.FindGameWindow(profile) is { } w
                    ? new RegionAutoFill.WindowRect(w.GameX, w.GameY, w.GameW, w.GameH)
                    : (RegionAutoFill.WindowRect?)null);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Auto-fill dialogue area: window lookup failed: {ex.Message}");
            }

            RegionAutoFillStep step = _regionAutoFill.Observe(window);
            if (step == RegionAutoFillStep.Wait) return;

            // Back on the UI thread: the player may have drawn an area while the lookup ran.
            bool savedNow = HasConfiguredRegion()
                            || Services.Detection.CaptureRegionValidator.TryParse(Config.Get<string>("Region", ""), out _, out _, out _, out _);
            if (savedNow) return;

            string region = null;
            if (step == RegionAutoFillStep.Fill && window is RegionAutoFill.WindowRect rect)
            {
                var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
                region = RegionAutoFill.RegionFor(profile, rect, desktop.Left, desktop.Top, desktop.Width, desktop.Height);
            }

            if (RegionAutoFill.MaySave(savedNow, region))
            {
                Config.Set("Region", region);
                notify.Region = region.Split(',');
                _regionAutoFill.MarkSet(region);
                Config.Set(RegionAutoFill.FilledRegionKey, region);
                // No overlap dialog over the game: an overlap shows as the silent OverlayOverlap gate on the card.
                NotifyRegionChanged();
                ResetOcrPipelineState(clearImageCaches: true);
                data?.UpdateDashboardRegionInfo();
                Logger.Log.Info($"Auto-fill dialogue area: saved {region} from the {profile.GameId} profile ratios (game window {window?.Width}x{window?.Height}).");
            }
            else
            {
                _regionAutoFill.MarkFailed();
                Logger.Log.Warn(window is RegionAutoFill.WindowRect last
                    ? $"Auto-fill dialogue area: no steady, usable game window ({last.Width}x{last.Height}); the Start card offers the picker."
                    : "Auto-fill dialogue area: the game window was not found; the Start card offers the picker.");
            }
            _autoStart?.OnGatesChanged();
            data?.UpdateDashboardStatus();
        }

        private static string[] SelectedGameProcessNames() =>
            Services.Detection.GameRegionProfile.Get(Config.Get<string>("Game", "Genshin"))?.ProcessNames ?? Array.Empty<string>();

        /// <summary>The eight Start gates, read without dialogs (TryGateOcrStart … TryGateOverlayNotInRegion).</summary>
        private StartGate SilentFirstClosedGate(bool assumeGameRunning = false)
        {
            bool overlap = false;
            try { overlap = !_overlapOverrideAccepted && notify != null && EvaluateOverlayRegionOverlap().HasOverlap; }
            catch (Exception ex) { Logger.Log.Debug($"Auto-start: overlap check failed: {ex.Message}"); }

            // Same as TryGateGameRunning: an unknown game, the dev switch or this session's "continue anyway" pass. The
            // watcher's last probe says whether the game runs (no process listing on the UI thread).
            bool gameOpen = assumeGameRunning || SelectedGameProcessNames().Length == 0 || _lastGameRunning
                            || Config.Get("DevSkipGameGate", false) || _gameGateBypassedThisSession;

            bool displayModeTip = Services.Games.GameRegistry.ShowsDisplayModeTip(Config.Get("Game", Game ?? "Genshin"));
            return AutoStartGates.FirstClosed(new GateSnapshot(
                SessionActive: App.LicenseService?.IsActivated == true,
                InitialSyncRunning: App.StartupStatus == App.InitialStartupStatus.DownloadingTranslations,
                EngineReady: data?.Engine == SettingsWindow.EngineStatus.Ready && _engineReady,
                MatcherReady: data?.IsMatcherReady == true,
                RegionSet: HasConfiguredRegion(),
                GameRunning: gameOpen,
                DisplayModeTipPending: displayModeTip && !Config.Get("FullscreenTipAcknowledged", false),
                OverlayOverlapsRegion: overlap));
        }
    }
}
