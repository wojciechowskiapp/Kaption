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

        private sealed class AutoStartHost : IAutoStartHost
        {
            private readonly MainWindow _w;
            public AutoStartHost(MainWindow w) { _w = w; }

            public bool IsTranslationRunning => _w.OCRTimer.IsEnabled;

            public StartGate FirstClosedGate() => _w.SilentFirstClosedGate();

            public bool StartThroughGates()
            {
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

            bool enabled = Config.Get(SettingsWindow.AutoStartTranslationKey, true);
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
            data.AutoStartSettingChanged = on => _autoStart?.SetEnabled(on);

            _autoStartWatch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
            _autoStartWatch.Tick += (_, __) => WatchGame();
            _autoStartWatch.Start();
            Closed += (_, __) => _autoStartWatch?.Stop();
            Logger.Log.Info($"Auto-start: {(enabled ? "on" : "off")} at launch.");
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
                string[] names = SelectedGameProcessNames();
                bool requireFocus = Config.Get(SettingsWindow.AutoStartFocusKey, true);
                var probe = await Task.Run(() => _gameProbe.Probe(names, DateTime.UtcNow));
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

            bool genshin = string.Equals(Config.Get("Game", Game ?? "Genshin"), "Genshin", StringComparison.OrdinalIgnoreCase);
            return AutoStartGates.FirstClosed(new GateSnapshot(
                SessionActive: App.LicenseService?.IsActivated == true,
                InitialSyncRunning: App.StartupStatus == App.InitialStartupStatus.DownloadingTranslations,
                EngineReady: data?.Engine == SettingsWindow.EngineStatus.Ready && _engineReady,
                MatcherReady: data?.IsMatcherReady == true,
                RegionSet: HasConfiguredRegion(),
                GameRunning: gameOpen,
                DisplayModeTipPending: genshin && !Config.Get("FullscreenTipAcknowledged", false),
                OverlayOverlapsRegion: overlap));
        }
    }
}
