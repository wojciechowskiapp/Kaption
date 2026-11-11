using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Threading;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Guess;
using GI_Subtitles.Services.Notices;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// "Genshin Impact 7.2: the new story is in Polish now": the tray balloon for a notice the pack sync raised
    /// (Services/Notices/NewStoryNotices.cs decides; Settings › Start shows the same line). One balloon per game and
    /// version, never over the game or running subtitles: it waits until Windows would show it.
    /// </summary>
    public partial class MainWindow
    {
        private readonly NewStoryBalloonQueue _newStoryBalloons = new();
        private DispatcherTimer _newStoryRetry;

        private void InitNewStoryNotice()
        {
            // The pace while a balloon waits (for the game to leave the front, or for the one before it): the ranking notice's own.
            _newStoryRetry = new DispatcherTimer(DispatcherPriority.Background) { Interval = GuessNotifySettings.Schedule(null).GateRetry };
            _newStoryRetry.Tick += (_, __) => DeliverNewStoryBalloons();
            NewStoryNotices.Default.Raised += OnNewStoryRaised;
            NewStoryNotices.Default.Changed += OnNewStoryChanged;
            Closed += (_, __) =>
            {
                NewStoryNotices.Default.Raised -= OnNewStoryRaised;
                NewStoryNotices.Default.Changed -= OnNewStoryChanged;
                _newStoryRetry.Stop();
            };
            // The startup sync may have finished before the tray icon existed.
            CollectNewStoryNotices();
        }

        // Raised on the sync's thread. BeginInvoke, not Invoke: it does not throw once the dispatcher is shutting down.
        private void OnNewStoryRaised()
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(CollectNewStoryNotices));
        }

        private void OnNewStoryChanged()
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(DropClosedNewStoryNotices));
        }

        private void CollectNewStoryNotices()
        {
            _newStoryBalloons.Add(NewStoryNotices.Default.TakeUndelivered());
            DeliverNewStoryBalloons();
        }

        /// <summary>A line closed on Start, or the switch turned off, takes its waiting balloon with it; the others stay.</summary>
        private void DropClosedNewStoryNotices()
        {
            var now = DateTimeOffset.UtcNow;
            _newStoryBalloons.KeepOnly(n => NewStoryNotices.Default.IsPending(n, now));
            if (_newStoryBalloons.Count == 0) _newStoryRetry?.Stop();
        }

        private void DeliverNewStoryBalloons()
        {
            // A balloon may have waited a long time (a play session): what expired or was closed meanwhile is not shown.
            var now = DateTimeOffset.UtcNow;
            _newStoryBalloons.KeepOnly(n => NewStoryNotices.Default.IsPending(n, now));
            if (_newStoryBalloons.Count == 0)
            {
                _newStoryRetry?.Stop();
                return;
            }
            // The ranking notice's gate, minus its once-a-day cap: not while translating, not over a game, and only
            // when Windows would show a notification now.
            var gate = GuessNoticeGate.Check(enabled: true, lastShownDay: null, today: null, translating: OCRTimer.IsEnabled,
                gameInFront: GuessNoticeGate.AnyGameInFront(), quns: GuessNoticeGate.QueryNotificationState());
            if (gate != GuessGateResult.Show)
            {
                if (!_newStoryRetry.IsEnabled)
                {
                    Logger.Log.Info($"New story notice waits ({gate}); Start shows the line meanwhile.");
                    _newStoryRetry.Start();
                }
                return;
            }
            // One balloon per tick: Windows shows one at a time, so a second game's notice follows on the next tick.
            var notice = _newStoryBalloons.TakeNext();
            string game = GameRegionProfile.DisplayNameOf(notice.Game);
            string title = NewStoryText.Line(LocalizedString("NewStory_Balloon_Title_Format", "{0} {1}"), game, notice.Version);
            string body = LocalizedString("NewStory_Balloon_Body", "The new story is in Polish now. Click to see what's in it.");
            notify?.ShowBalloonTip(title, body, onClick: () => OpenNewStoryPage(notice));
            Logger.Log.Info($"New story notice shown as a tray notice: {notice.Id}.");
            if (_newStoryBalloons.Count > 0) _newStoryRetry.Start();
            else _newStoryRetry.Stop();
        }

        /// <summary>
        /// Opens the version's page on the site. Only when it really opened has the player seen the news: then the
        /// Start line closes for good. A launch that failed leaves the line (and its link) to try again.
        /// </summary>
        internal static bool OpenNewStoryPage(NewStoryNotice notice)
        {
            string url = NewStoryText.PageUrl(LocalizedString("shell_Url_ReleaseNote_Format", "https://kaption.one/pl/aktualizacje/{0}/"), notice);
            if (!TryOpenNewStoryPage(url, u => Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }))) return false;
            NewStoryNotices.Default.Dismiss(notice);
            return true;
        }

        /// <summary>True when <paramref name="launch"/> ran for a real link without throwing.</summary>
        internal static bool TryOpenNewStoryPage(string url, Action<string> launch)
        {
            if (string.IsNullOrEmpty(url)) return false;
            try
            {
                launch(url);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"New story notice: page not opened: {ex.Message}");
                return false;
            }
        }
    }
}
