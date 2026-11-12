using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Notices;
using GI_Subtitles.Services.Notices.WhatsNew;
using GI_Subtitles.Services.Stamina;
using GI_Subtitles.Services.Today;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// What Kaption says on its own at a start, one thing at most (Services/Notices/DayNotice.cs decides):
    /// after an update the news card on Start is that thing, or one tray line when Kaption started with Windows and no
    /// window opened; on every other start it is the notice of the day, once per game day: what is worth doing today
    /// in the games the player has, built from the Today card's own model (Services/Notices/DayNoticeLoader.cs).
    /// Both tray notices go through the app's one gate (Services/Notices/NoticeGate.cs): never while subtitles run or
    /// a game is in front, inside the day's cap for every notice together, one at a time. A day is only remembered
    /// once its notice really went out, and only for the game, server, account and day it was built for.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>The longest the timer sleeps: a clock change or a wake from sleep is noticed within the hour.</summary>
        private static readonly TimeSpan StartNoticeMaxSleep = TimeSpan.FromHours(1);
        /// <summary>The pause after a day with nothing to say yet (the site not reached): the data may still come.</summary>
        private static readonly TimeSpan StartNoticeNoContentRetry = TimeSpan.FromMinutes(30);

        private DispatcherTimer _startNoticeTimer;
        private bool _startNoticeBusy, _startNoticePicked, _startWindowShown, _startNewsTrayOwed;
        /// <summary>The game day on which this run gave its one notice; null while it has not.</summary>
        private string _startSpentOn;
        private NoticeGateResult? _startLoggedGate;
        /// <summary>The game day "nothing to say yet" was last logged for: once a day, not on every look.</summary>
        private string _startLoggedNothingOn;

        /// <summary>
        /// Testing aid (Debug builds only, where the end-to-end mode exists): lets the start's tray notices out in that
        /// mode too. Without it a test run says nothing by the clock of whoever is using the PC.
        /// </summary>
        public const string InE2EKey = "StartNotices.InE2E";

        private void InitStartupNotices(bool windowShown)
        {
            if (Common.E2EMode.IsActive && !Config.Get(InE2EKey, false))
            {
                Logger.Log.Info($"Start notice: none in the end-to-end mode ({InE2EKey} lets them out).");
                return;
            }
            _startWindowShown = windowShown;
            _startNoticeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = DayNoticeSettings.Delay };
            _startNoticeTimer.Tick += (_, __) => StartNoticeTick();
            _startNoticeTimer.Start();
            DayNoticeSettings.Changed += OnDayNoticeSettingsChanged;
            Closed += (_, __) =>
            {
                DayNoticeSettings.Changed -= OnDayNoticeSettingsChanged;
                _startNoticeTimer.Stop();
            };
        }

        // Switched on: look soon instead of at the end of a long sleep. Switched off: the next look finds nothing to do.
        private void OnDayNoticeSettingsChanged()
        {
            if (Dispatcher.HasShutdownStarted) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnDayNoticeSettingsChanged));
                return;
            }
            if (_startNoticeTimer == null || _startNoticeBusy || !_startNoticePicked) return;
            _startNoticeTimer.Stop();
            _startNoticeTimer.Interval = DayNoticeSettings.Delay;
            _startNoticeTimer.Start();
        }

        private async void StartNoticeTick()
        {
            _startNoticeTimer.Stop();
            if (_startNoticeBusy || Dispatcher.HasShutdownStarted) return;
            _startNoticeBusy = true;
            TimeSpan next = StartNoticeMaxSleep;
            try
            {
                next = await StartNoticeStepAsync();
            }
            catch (Exception ex)
            {
                // A notice less is the whole cost: never a dialog.
                Logger.Log.Warn($"Start notice: a look failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _startNoticeBusy = false;
            }
            if (Dispatcher.HasShutdownStarted) return;
            _startNoticeTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(next.TotalSeconds, 1, StartNoticeMaxSleep.TotalSeconds));
            _startNoticeTimer.Start();
        }

        /// <summary>One look; returns how long until the next.</summary>
        private async Task<TimeSpan> StartNoticeStepAsync()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            // Everything the day's notice depends on, read once: the waits below can outlast any of it.
            DayNoticeRequest request = CaptureDayNotice(now);
            string dayKey = request.DayKey;
            TimeSpan untilNextDay = request.DayEnds - now + DayNoticeSettings.Delay;

            if (!_startNoticePicked)
            {
                _startNoticePicked = true;
                // What the start looked like, not what is left of it now: a card the player already closed was still
                // this start's notice.
                Version level = data?.WhatsNewLevel;
                bool led = data?.WhatsNewLedThisStart == true;
                StartNoticeKind kind = StartNoticePlanner.Pick(newsWaiting: led || level != null, windowShown: _startWindowShown && led,
                    trayToldNews: level != null && ConfigWhatsNewMemory.Default.TrayTold == level);
                if (kind == StartNoticeKind.NewsCard)
                {
                    _startSpentOn = dayKey;
                    Logger.Log.Info("Start notice: the news card led Start; the notice of the day waits for the next start.");
                }
                _startNewsTrayOwed = kind == StartNoticeKind.NewsTray;
            }

            if (_startNewsTrayOwed)
            {
                Version level = data?.WhatsNewLevel;
                IReadOnlyList<string> titles = data?.WhatsNewTitles ?? Array.Empty<string>();
                StartNoticePlanner.NewsTrayStep news = StartNoticePlanner.NewsTray(data?.WhatsNewPresentedThisRun == true, level == null ? 0 : titles.Count);
                if (news != StartNoticePlanner.NewsTrayStep.Send)
                {
                    _startNewsTrayOwed = false;
                    NoticeGate.Default.Withdraw(NoticeKind.KaptionNews);
                    if (news == StartNoticePlanner.NewsTrayStep.CardSaidIt)
                    {
                        // The player opened the window while the line waited: the card was this start's notice, and
                        // no tray line (or slot of the day's notices) is spent on saying it again.
                        _startSpentOn = dayKey;
                        Logger.Log.Info("Start notice: the news card was opened before the tray could say it; no tray line.");
                    }
                }
                else
                {
                    NoticeGateResult gate = AskStartGate(NoticeKind.KaptionNews, "the news line", now);
                    if (gate == NoticeGateResult.CapReached) return StartNoticeMaxSleep; // the card on Start still says it
                    if (gate != NoticeGateResult.Show) return DayNoticeSettings.Retry;
                    CultureInfo culture = DayNoticeContent.CultureFor(UiLanguage.Current);
                    string body = titles.Count == 1
                        ? string.Format(culture, StartL("WhatsNew_Tray_Body_One", "New: {0}. Click to see."), titles[0])
                        : string.Format(culture, StartL("WhatsNew_Tray_Body_More", "New: {0}, and {1} more. Click to see."), titles[0], titles.Count - 1);
                    if (notify?.ShowBalloonTip(StartL("WhatsNew_Tray_Title", "Kaption has been updated"), body, onClick: () => data?.ShowStartPage()) != true)
                        return DayNoticeSettings.Retry;
                    NoticeGate.Default.Shown(NoticeKind.KaptionNews, now);
                    // The tray said it once; the card itself stays on Start until the player has dealt with it.
                    ConfigWhatsNewMemory.Default.TrayTold = level;
                    _startNewsTrayOwed = false;
                    _startSpentOn = dayKey;
                    Logger.Log.Info($"Start notice: the tray said Kaption {level} has news ({titles.Count}).");
                    return untilNextDay;
                }
            }

            // The notice's own rule first (switched off, this day told, this start spent); only one that could show
            // asks the app's gate, where it takes its place among the other notices.
            DayNoticeStep step = DayNoticeRule.Next(DayNoticeSettings.Enabled, DayNoticeSettings.LastDay, dayKey, _startSpentOn,
                otherWaits: false, mayInterrupt: true);
            if (step != DayNoticeStep.Show)
            {
                NoticeGate.Default.Withdraw(NoticeKind.Today);
                return untilNextDay;
            }
            NoticeGateResult before = AskStartGate(NoticeKind.Today, "the notice of the day", now);
            if (before == NoticeGateResult.CapReached) return StartNoticeMaxSleep;
            if (before != NoticeGateResult.Show) return DayNoticeSettings.Retry;

            // Built, checked and sent in one step: the check comes after the last wait and nothing waits between it
            // and the tray call, so a game, server or UID picked meanwhile cannot slip a stale notice through.
            DayNoticeOutcome outcome = await DayNoticeLoader.PrepareAndDeliverAsync(request,
                r => DayNoticeLoader.BuildAsync(r, TodayCardSources.Live, TodayCardSettings.HasCard, StartL),
                DayNoticeStillCurrent, DeliverDayNotice);
            switch (outcome)
            {
                case DayNoticeOutcome.Delivered:
                    return untilNextDay;
                case DayNoticeOutcome.Stale:
                    Logger.Log.Info("Notice of the day: the game, the server, the UID or the day changed, or a Today card came up, while it was being built; not sent, building it again.");
                    NoticeGate.Default.Withdraw(NoticeKind.Today);
                    return DayNoticeSettings.Delay;
                case DayNoticeOutcome.Nothing:
                    NoticeGate.Default.Withdraw(NoticeKind.Today);
                    if (!string.Equals(_startLoggedNothingOn, dayKey, StringComparison.Ordinal))
                    {
                        _startLoggedNothingOn = dayKey;
                        Logger.Log.Info($"Notice of the day: nothing to say yet for {dayKey}; looking again every {StartNoticeNoContentRetry}.");
                    }
                    return StartNoticeNoContentRetry < untilNextDay ? StartNoticeNoContentRetry : untilNextDay;
                default:
                    return DayNoticeSettings.Retry;
            }
        }

        /// <summary>The settings the day's notice is built for, read at one moment (see <see cref="DayNoticeRequest"/>).</summary>
        private static DayNoticeRequest CaptureDayNotice(DateTimeOffset now) =>
            DayNoticeRequest.Capture(GameRegistry.Current.Listed, TodayCardSettings.ActiveGame(), TodayCardSettings.ServerId,
                FarmCardSettings.Uid, LastPlayedWithKaption, TodayCardSettings.LastShownDay, UiLanguage.Current, now);

        /// <summary>After the last wait: still switched on, and still the game, server, UID and day the notice was built for.</summary>
        private static bool DayNoticeStillCurrent(DayNoticeRequest request) =>
            DayNoticeSettings.Enabled
            && request.StillCurrent(TodayCardSettings.ActiveGame(), TodayCardSettings.ServerId, FarmCardSettings.Uid,
                TodayCardSettings.LastShownDay, DateTimeOffset.UtcNow);

        /// <summary>
        /// Sends a notice whose request still holds: the gate once more (a game can have come to the front while the
        /// data loaded), the tray, and only then the day. Waits for nothing. False keeps the day for the next look.
        /// </summary>
        private bool DeliverDayNotice(DayNoticeRequest request, DayNoticeText text)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (AskStartGate(NoticeKind.Today, "the notice of the day", now) != NoticeGateResult.Show) return false;
            bool opensCard = text.OpensCard;
            string game = request.Selected?.Id;
            string dayKey = request.DayKey;
            if (notify?.ShowBalloonTip(text.Title, text.Body, onClick: () => OpenDayNotice(opensCard, game)) != true)
            {
                // No tray icon to say it with: the day is not spent, the next look tries again.
                Logger.Log.Info("Notice of the day: the tray could not show it; trying again.");
                return false;
            }
            NoticeGate.Default.Shown(NoticeKind.Today, now);
            DayNoticeSettings.LastDay = dayKey;
            _startSpentOn = dayKey;
            Logger.Log.Info($"Notice of the day shown as a tray notice for {dayKey}: \"{text.Title}\".");
            return true;
        }

        /// <summary>
        /// Asks the app's one gate (Services/Notices/NoticeGate.cs) for a start notice: not while subtitles run, not
        /// over a game, only when Windows accepts a notification, inside the day's cap for every notice together and
        /// after any kind that goes first. The one place the start's notices ask; an answer is logged once.
        /// </summary>
        private NoticeGateResult AskStartGate(NoticeKind kind, string what, DateTimeOffset now)
        {
            NoticeGateResult gate = NoticeGate.Default.Ask(kind, kindEnabled: true, kindShownToday: false, now,
                () => NoticeScreen.Read(OCRTimer.IsEnabled));
            if (gate == NoticeGateResult.Show)
            {
                _startLoggedGate = null;
                return gate;
            }
            if (_startLoggedGate != gate)
            {
                _startLoggedGate = gate;
                Logger.Log.Info(gate == NoticeGateResult.CapReached
                    ? $"Start notice: {what} is not sent today: the day's notices are used up; Start shows it."
                    : $"Start notice: {what} waits ({gate}); checking again every {DayNoticeSettings.Retry}.");
            }
            return gate;
        }

        /// <summary>
        /// When Kaption last read a game's stamina off the screen: the sign that the player plays it with Kaption. It
        /// stays a sign after the reader is switched off; that switch only decides whether a pool is spoken of.
        /// </summary>
        internal static DateTimeOffset? LastPlayedWithKaption(string wireId) =>
            StaminaSettings.LoadSeen(wireId) ?? StaminaSettings.LoadReading(wireId)?.At;

        /// <summary>
        /// A click on the notice opens the selected game's card of the day for the rest. The click can come long
        /// after the notice: when another game is selected by then, or the notice promised no card, it opens Start,
        /// where the Today block is.
        /// </summary>
        /// <param name="noticeGame">The wire id of the game that was selected when the notice was built.</param>
        private void OpenDayNotice(bool opensCard, string noticeGame)
        {
            GameManifest selected = TodayCardSettings.ActiveGame();
            if (!DayNoticeContent.ClickOpensCard(opensCard, noticeGame, selected?.Id))
            {
                data?.ShowStartPage();
                return;
            }
            // The player sees the card that opens now, for the server chosen now: that game day is the one it does
            // not come up by itself again on.
            TodayCardSettings.SetLastShownDay(selected, TodayCardSettings.DayKey(selected, DateTimeOffset.UtcNow, TodayCardSettings.ServerId));
            ShowFarmCardOnDemand();
        }

        private string StartL(string key, string fallback) => TryFindResource(key) as string ?? fallback;
    }
}
