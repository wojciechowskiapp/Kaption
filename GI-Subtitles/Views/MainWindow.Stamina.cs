using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows.Threading;
using GI_Subtitles.Common;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Notices;
using GI_Subtitles.Services.Stamina;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The stamina number read off the game screen (Original Resin, Trailblaze Power, Waveplates), for a player who
    /// switched it on. The screen watch does the looking (MainWindow.Assist.cs, Services/Assist): while subtitles run
    /// and the game is in front it copies one small strip of the top of the game picture every few seconds, and the
    /// stamina reader finds the icon in it and reads the number beside it (Services/Stamina). This file is what
    /// happens to a value: the tracker decides whether it is believed, the timer that follows lives in Config on this
    /// PC, and one tray notice says when the pool is nearly full, never over a game. Nothing is sent anywhere, and
    /// the game's process, memory and files are never touched.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>The longest the notice timer sleeps before it looks again (a clock change, a new reading elsewhere).</summary>
        private static readonly TimeSpan StaminaNoticeMaxSleep = TimeSpan.FromHours(1);

        /// <summary>How often "the number on screen is what the timer expected" is written to Config (the card's "read … ago").</summary>
        private static readonly TimeSpan StaminaSeenSaveEvery = TimeSpan.FromMinutes(1);

        private volatile bool _staminaOn;
        /// <summary>The game the tracker belongs to; both are made when that game's first value arrives.</summary>
        private GameManifest _staminaGame;
        private StaminaTracker _staminaTracker;
        private DateTimeOffset _staminaSavedAt;
        private DispatcherTimer _staminaNoticeTimer;

        private void InitStamina()
        {
            _staminaOn = StaminaSettings.Enabled;
            StaminaSettings.Changed += OnStaminaSettingsChanged;
            _staminaNoticeTimer = new DispatcherTimer(DispatcherPriority.Background);
            _staminaNoticeTimer.Tick += (_, __) => CheckStaminaNotice();
            // A notice that fell due while Kaption was closed is not shown late: the Start card says where things stand.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (GameManifest game in GameRegistry.Current.Listed.Where(g => g.Stamina != null))
            {
                StaminaReading reading = StaminaSettings.LoadReading(game.Id);
                if (reading != null && StaminaSettings.LoadArmed(game.Id) && now >= StaminaNotice.DueAt(game.Stamina, reading, StaminaSettings.NoticeLead))
                    StaminaSettings.SaveArmed(game.Id, false);
            }
            CheckStaminaNotice();
            AssistStore.Default.Deleted += OnStaminaForgotten;
            Closed += (_, __) =>
            {
                AssistStore.Default.Deleted -= OnStaminaForgotten;
                StaminaSettings.Changed -= OnStaminaSettingsChanged;
                _staminaNoticeTimer?.Stop();
                _staminaOn = false;
            };
        }

        private void OnStaminaSettingsChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(OnStaminaSettingsChanged));
                return;
            }
            _staminaOn = StaminaSettings.Enabled;
            if (_staminaOn) Logger.Log.Info("Stamina reader switched on: the number beside the stamina icon is read while subtitles run.");
            else Logger.Log.Info("Stamina reader switched off.");
            CheckStaminaNotice();
        }

        /// <summary>"Delete what was read" (Settings): the timer's reading is gone, so the tracker starts from nothing.</summary>
        private void OnStaminaForgotten(string gameId)
        {
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => OnStaminaForgotten(gameId)));
                return;
            }
            if (_staminaGame != null && string.Equals(_staminaGame.GameId, gameId, StringComparison.Ordinal)) _staminaTracker = null;
            CheckStaminaNotice();
            data?.RefreshStaminaCard();
        }

        /// <summary>UI thread: a value read off the screen goes to the tracker; a believed one moves the timer.</summary>
        private void OnStaminaValue(string gameId, int value, DateTimeOffset at)
        {
            if (!_staminaOn) return;
            GameManifest game = _staminaGame;
            StaminaTracker tracker = _staminaTracker;
            if (game == null || tracker == null || !string.Equals(game.Id, gameId, StringComparison.Ordinal))
            {
                // The first value of this game (or the first after "delete what was read"). A late value of a game
                // that is no longer selected is dropped.
                game = GameRegistry.Current.Find(Config.Get<string>("Game", Game ?? "Genshin"));
                GameStaminaScreen spec = game?.Desktop?.StaminaScreen;
                if (game?.Stamina == null || spec == null || !string.Equals(game.Id, gameId, StringComparison.Ordinal)) return;
                tracker = new StaminaTracker(game.Stamina, spec.Confirmations, spec.RefillConfirmations,
                    TimeSpan.FromSeconds(spec.SampleSeconds * (spec.RefillConfirmations + 1)), StaminaSettings.LoadReading(game.Id));
                _staminaGame = game;
                _staminaTracker = tracker;
            }

            StaminaVerdict verdict = tracker.Offer(value, at);
            if (verdict == StaminaVerdict.Waiting) return;
            StaminaReading reading = tracker.Reading;
            if (verdict == StaminaVerdict.Accepted)
            {
                // Spending, a refill or the first reading starts a new fill, so the notice is owed again (or not, when
                // the pool is already that close). A one-point correction is the same fill: what is owed stays owed.
                // One write for all of it: a reading on disk without its "owed" would lose that fill's notice.
                bool? armed = tracker.LastChange != StaminaChange.Corrected
                    ? StaminaNotice.Arms(game.Stamina, reading, StaminaSettings.NoticeLead)
                    : null;
                StaminaSettings.SaveAccepted(game.Id, reading, armed, at);
                RememberStaminaRead(game, value, at);
                _staminaSavedAt = at;
                Logger.Log.Info($"Stamina reader: {game.Stamina.Name} {reading.Value}/{game.Stamina.Max} read off the screen; full at "
                                + $"{StaminaClock.FullAt(game.Stamina, reading).ToLocalTime():HH:mm}.");
                CheckStaminaNotice();
            }
            else if (at - _staminaSavedAt >= StaminaSeenSaveEvery)
            {
                // The number on screen is what the timer expected: only "last read" moves. The reading stays, so a pool
                // seen full again keeps the moment it filled.
                StaminaSettings.SaveSeen(game.Id, at);
                RememberStaminaRead(game, value, at);
                _staminaSavedAt = at;
            }
            data?.RefreshStaminaCard();
        }

        /// <summary>The number as it stood on screen, for the "what Kaption has read" list in Settings.</summary>
        private static void RememberStaminaRead(GameManifest game, int value, DateTimeOffset at) =>
            AssistStore.Default.Record(game.GameId, AssistStore.LocalAccount, AssistSettings.StaminaReader,
                string.Create(CultureInfo.InvariantCulture, $"{value}/{game.Stamina.Max}"), "", at);

        /// <summary>Shows the "nearly full" notice of any game whose turn it is, or sets the timer for the next look.</summary>
        private void CheckStaminaNotice()
        {
            if (_staminaNoticeTimer == null) return;
            _staminaNoticeTimer.Stop();
            if (!StaminaSettings.Enabled || !StaminaSettings.Notice)
            {
                NoticeGate.Default.Withdraw(NoticeKind.StaminaNearlyFull);
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan lead = StaminaSettings.NoticeLead;
            TimeSpan? sleep = null;
            // The app's one gate (Services/Notices/NoticeGate), asked once per look and only when something is due.
            NoticeGateResult? gate = null;
            bool anyDue = false;
            foreach (GameManifest game in GameRegistry.Current.Listed.Where(g => g.Stamina != null))
            {
                if (!StaminaSettings.LoadArmed(game.Id)) continue;
                StaminaReading reading = StaminaSettings.LoadReading(game.Id);
                // Asked only once something is due: the answer needs the foreground process.
                bool due = reading != null && now >= StaminaNotice.DueAt(game.Stamina, reading, lead);
                if (due)
                {
                    anyDue = true;
                    gate ??= NoticeGate.Default.Ask(NoticeKind.StaminaNearlyFull, true, false, now, () => NoticeScreen.Read(OCRTimer.IsEnabled));
                    if (gate == NoticeGateResult.CapReached)
                    {
                        // The app has sent its notices for today: this fill gets none. The Start card says where it stands.
                        StaminaSettings.SaveArmed(game.Id, false);
                        Logger.Log.Info($"Stamina notice for {game.Id} not sent: the day's notices are used up; the Start card shows the timer.");
                        continue;
                    }
                }
                StaminaNoticeDecision next = StaminaNotice.Next(game.Stamina, reading, true, true, lead, now, gate == NoticeGateResult.Show);
                switch (next.Step)
                {
                    case StaminaNoticeStep.Show:
                        StaminaNoticeText text = StaminaText.Notice(game.Stamina, reading, now, StaminaL, CultureInfo.CurrentUICulture);
                        string url = StaminaLinks.TimerUrl(game, UiLanguage.Current, reading, StaminaSettings.LoadSeen(game.Id));
                        notify?.ShowBalloonTip(text.Title, text.Body, onClick: url == null ? null : () => OpenStaminaTimer(url));
                        StaminaSettings.SaveArmed(game.Id, false);
                        Logger.Log.Info($"Stamina notice shown for {game.Id}.");
                        NoticeGate.Default.Shown(NoticeKind.StaminaNearlyFull, now);
                        gate = NoticeGateResult.TooSoon; // one balloon at a time; another game's waits for the next look
                        sleep = Shorter(sleep, StaminaSettings.GateRetry);
                        break;
                    case StaminaNoticeStep.Wait:
                        sleep = Shorter(sleep, StaminaSettings.GateRetry);
                        break;
                    case StaminaNoticeStep.Sleep:
                        sleep = Shorter(sleep, next.At.Value - now);
                        break;
                }
            }
            if (!anyDue) NoticeGate.Default.Withdraw(NoticeKind.StaminaNearlyFull);
            if (sleep is not TimeSpan wait) return;
            _staminaNoticeTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 1, StaminaNoticeMaxSleep.TotalSeconds));
            _staminaNoticeTimer.Start();
        }

        private static TimeSpan Shorter(TimeSpan? a, TimeSpan b) => a is TimeSpan x && x < b ? x : b;

        private static void OpenStaminaTimer(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Log.Warn($"Stamina timer page not opened: {ex.Message}"); }
        }

        private string StaminaL(string key, string fallback) => TryFindResource(key) as string ?? fallback;
    }
}
