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
    /// never while subtitles run, a game is in front or Windows says not now. Settings › Start repeats it in a strip.
    /// Never over the game, and never anything that could end the session: the feature has no path to the licence.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly TimeSpan GuessGateRetry = TimeSpan.FromSeconds(60);
        private GuessStandingPoller _guessPoller;
        private DispatcherTimer _guessGateTicker;
        private readonly GuessNoticeDelivery _guessDelivery = new();

        private void InitGuessNotify()
        {
            var api = new KaptionApiClient();
            _guessPoller = new GuessStandingPoller(ct => FetchGuessStandingAsync(api, ct), HasGuessSession);
            _guessPoller.StandingRead += standing =>
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => OnGuessStanding(standing)));
            };
            _guessGateTicker = new DispatcherTimer(DispatcherPriority.Background) { Interval = GuessGateRetry };
            _guessGateTicker.Tick += (_, __) => TryDeliverGuessNotice();
            GuessNotifySettings.EnabledChanged += OnGuessNotifyEnabledChanged;
            if (data != null)
            {
                data.GuessLobbyOpened = OnGuessLobbyOpened;
                // Settings brought to the front on Start while a notice waits: the strip has shown it, no balloon follows.
                data.GuessStripMayBeSeen = TryDeliverGuessNotice;
            }
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

        private static Task<GuessStanding> FetchGuessStandingAsync(KaptionApiClient api, CancellationToken ct) =>
            api.GetGuessStandingAsync(App.LicenseService?.CurrentActivation?.DeviceSessionJwt, CurrentGuessBoard(), ct,
                GuessNotifySettings.StandingUrlOverride);

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
                GuessNotifySettings.Today(DateTime.Now), c => GuessNoticeText.Build(c, GuessL), out var candidate);
            if (candidate != null)
                Logger.Log.Info($"Guess ranking: passed on the {candidate.Board} board ({candidate.From} → {candidate.To}).");
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
            var decision = _guessDelivery.Next(today, data?.GuessRankStripOnScreen == true, GuessNotifySettings.Enabled,
                GuessNotifySettings.LastShownDay, OCRTimer.IsEnabled, GuessNoticeGate.AnyGameInFront(),
                GuessNoticeGate.QueryNotificationState());
            switch (decision.Step)
            {
                case GuessDeliveryStep.SeenOnStrip:
                    MarkGuessDelivered(today, "on the Start strip");
                    break;
                case GuessDeliveryStep.ShowBalloon:
                    var notice = _guessDelivery.Notice;
                    notify?.ShowBalloonTip(notice.Title, notice.Body, onClick: OpenGuessLobbyFromNotice);
                    MarkGuessDelivered(today, "as a tray notice");
                    break;
                case GuessDeliveryStep.Disabled:
                    ClearGuessNotice();
                    break;
                case GuessDeliveryStep.Expired:   // yesterday's balloon is dropped; the strip keeps it until the next read
                case GuessDeliveryStep.CappedToday: // the cap is for today; the strip still carries it
                case GuessDeliveryStep.Idle:
                    _guessGateTicker?.Stop();
                    break;
                default:
                    if (_guessGateTicker?.IsEnabled == false)
                    {
                        Logger.Log.Info($"Guess ranking notice waits ({decision.Gate}).");
                        _guessGateTicker.Start();
                    }
                    break;
            }
        }

        private void MarkGuessDelivered(string today, string how)
        {
            GuessNotifySettings.LastShownDay = today;
            GuessNotifySettings.Baselines = _guessDelivery.Delivered(GuessNotifySettings.Baselines, CurrentGuessBoard());
            _guessGateTicker?.Stop();
            Logger.Log.Info($"Guess ranking notice delivered {how}.");
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
