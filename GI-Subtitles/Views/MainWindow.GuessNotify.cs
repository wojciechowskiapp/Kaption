using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Guess;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// "Someone passed you in Gendle": the guess ranking notice (design section 6, R4). A background poller reads the
    /// player's standing; a drop on the selected game's board or the global one becomes one tray balloon a day at most,
    /// never while subtitles run, a game is in front or Windows says not now. Settings › Start shows the same text in a
    /// strip while the balloon waits and after it; the strip never stands in for the balloon.
    /// Never over the game, and never anything that could end the session: the feature has no path to the licence.
    /// </summary>
    public partial class MainWindow
    {
        private GuessStandingPoller _guessPoller;
        private DispatcherTimer _guessGateTicker;
        private readonly GuessNoticeDelivery _guessDelivery = new();
        /// <summary>The delivery step last logged for the waiting notice, so a re-check logs only a change.</summary>
        private GuessDeliveryStep? _guessLoggedStep;

        private void InitGuessNotify()
        {
            var api = new KaptionApiClient();
            _guessPoller = new GuessStandingPoller(ct => FetchGuessStandingAsync(api, ct), HasGuessSession, GuessNotifySettings.Schedule);
            _guessPoller.StandingRead += standing =>
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => OnGuessStanding(standing)));
            };
            _guessGateTicker = new DispatcherTimer(DispatcherPriority.Background) { Interval = _guessPoller.Schedule.GateRetry };
            _guessGateTicker.Tick += (_, __) => TryDeliverGuessNotice();
            GuessNotifySettings.EnabledChanged += OnGuessNotifyEnabledChanged;
            if (data != null) data.GuessLobbyOpened = OnGuessLobbyOpened;
            if (GuessNotifySettings.Enabled) _guessPoller.Start();
            Closed += (_, __) =>
            {
                GuessNotifySettings.EnabledChanged -= OnGuessNotifyEnabledChanged;
                _guessGateTicker?.Stop();
                _guessPoller?.Dispose();
            };
        }

        /// <summary>The selected game's board id (its wire id, "genshin"); null for a game the registry does not list.</summary>
        private static string CurrentGuessBoard() => GameRegistry.Current.Find(Config.Get("Game", "Genshin"))?.Id;

        private static bool HasGuessSession() =>
            GuessNotifySettings.StandingUrlOverride != null
            || !string.IsNullOrEmpty(App.LicenseService?.CurrentActivation?.DeviceSessionJwt);

        /// <summary>
        /// One standing request. <c>since</c> carries each watched board's stored server time (GuessOvertakeRule.Since): the server
        /// adds the place after the last score only for a board scored on later, so a pass made while Kaption was
        /// closed still counts.
        /// </summary>
        private static Task<GuessStanding> FetchGuessStandingAsync(KaptionApiClient api, CancellationToken ct)
        {
            string board = CurrentGuessBoard();
            return api.GetGuessStandingAsync(App.LicenseService?.CurrentActivation?.DeviceSessionJwt, board, ct,
                GuessNotifySettings.StandingUrlOverride, GuessOvertakeRule.Since(GuessNotifySettings.Baselines, board));
        }

        private void OnGuessNotifyEnabledChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(OnGuessNotifyEnabledChanged));
                return;
            }
            if (GuessNotifySettings.Enabled)
            {
                if (_guessPoller?.IsRunning == false) _guessPoller.Start();
                return;
            }
            _guessPoller?.Stop();
            ClearGuessNotice();
        }

        private void OnGuessStanding(GuessStanding standing)
        {
            if (standing == null || !GuessNotifySettings.Enabled) return;
            GuessNotifySettings.Baselines = _guessDelivery.Read(GuessNotifySettings.Baselines, standing, CurrentGuessBoard(),
                GuessNotifySettings.Today(DateTime.Now), GuessNotifySettings.LastShownDay, c => GuessNoticeText.Build(c, GuessL), out var candidate);
            // One line per read: each watched board, what it was measured from, the notice it found, the next read.
            Logger.Log.Info(GuessOvertakeRule.Describe(standing, _guessDelivery.LastOutcome, _guessPoller.DelayAfter(standing)));
            if (candidate != null) _guessLoggedStep = null; // a fresh notice: its first delivery decision is logged
            // A read without a drop replaces what the strip said.
            data?.ShowGuessRankNotice(_guessDelivery.Notice);
            TryDeliverGuessNotice();
        }

        private void TryDeliverGuessNotice()
        {
            if (_guessDelivery.Notice == null || !_guessDelivery.Undelivered)
            {
                _guessGateTicker?.Stop();
                return;
            }
            string today = GuessNotifySettings.Today(DateTime.Now);
            var decision = _guessDelivery.Next(today, GuessNotifySettings.Enabled, GuessNotifySettings.LastShownDay,
                OCRTimer.IsEnabled, GuessNoticeGate.AnyGameInFront(), GuessNoticeGate.QueryNotificationState());
            switch (decision.Step)
            {
                case GuessDeliveryStep.ShowBalloon:
                    var notice = _guessDelivery.Notice;
                    notify?.ShowBalloonTip(notice.Title, notice.Body, onClick: OpenGuessLobbyFromNotice);
                    MarkGuessDelivered(today);
                    break;
                case GuessDeliveryStep.Disabled:
                    LogGuessDecision(decision.Step, "Guess ranking notice dropped: notices are turned off.");
                    ClearGuessNotice();
                    break;
                case GuessDeliveryStep.Expired:
                    LogGuessDecision(decision.Step, $"Guess ranking notice from {_guessDelivery.PendingDay} not shown as a tray notice today; "
                                                    + "the Start strip keeps it and the next read finds the drop again.");
                    _guessGateTicker?.Stop();
                    break;
                case GuessDeliveryStep.CappedToday:
                    LogGuessDecision(decision.Step, $"Guess ranking notice waits for tomorrow: one already reached you today ({GuessNotifySettings.LastShownDay}); "
                                                    + "the Start strip shows it.");
                    _guessGateTicker?.Stop();
                    break;
                case GuessDeliveryStep.Idle:
                    _guessGateTicker?.Stop();
                    break;
                default:
                    if (_guessGateTicker?.IsEnabled == false)
                    {
                        _guessGateTicker.Interval = _guessPoller.Schedule.GateRetry;
                        Logger.Log.Info($"Guess ranking notice waits ({decision.Gate}); the Start strip shows it meanwhile; checking again every {_guessGateTicker.Interval}.");
                        _guessGateTicker.Start();
                    }
                    break;
            }
        }

        /// <summary>Logs a delivery decision once per notice and step: the 60-second re-checks and strip events repeat them.</summary>
        private void LogGuessDecision(GuessDeliveryStep step, string line)
        {
            if (_guessLoggedStep == step) return;
            _guessLoggedStep = step;
            Logger.Log.Info(line);
        }

        private void MarkGuessDelivered(string today)
        {
            GuessNotifySettings.LastShownDay = today;
            GuessNotifySettings.Baselines = _guessDelivery.Delivered(GuessNotifySettings.Baselines, CurrentGuessBoard());
            _guessGateTicker?.Stop();
            _guessLoggedStep = null;
            Logger.Log.Info($"Guess ranking notice shown as a tray notice ({_guessDelivery.Notice?.Board}).");
        }

        private void ClearGuessNotice()
        {
            _guessDelivery.Clear();
            _guessGateTicker?.Stop();
            data?.ShowGuessRankNotice(null);
        }

        /// <summary>The ranking was opened (balloon or strip): the next drop is measured from the place seen now.</summary>
        private void OnGuessLobbyOpened()
        {
            GuessNotifySettings.Baselines = _guessDelivery.LobbyOpened(GuessNotifySettings.Baselines, CurrentGuessBoard());
            ClearGuessNotice();
        }

        private void OpenGuessLobbyFromNotice()
        {
            string url = GuessLinks.RankedLobby(UiLanguage.Current);
            if (GuessLinks.IsSitePage(url))
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception ex) { Logger.Log.Warn($"Guess ranking: lobby not opened: {ex.Message}"); }
            }
            OnGuessLobbyOpened();
        }

        private string GuessL(string key, string fallback) => TryFindResource(key) as string ?? fallback;
    }
}
