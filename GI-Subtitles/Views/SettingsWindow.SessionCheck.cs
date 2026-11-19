using System;
using System.Globalization;
using System.Windows.Threading;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Hints;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The first-session check on the Start card: while translation runs and no line shows, say why (the game looks
    /// like full screen, lines are read but not found, no dialogue seen yet), once where the player is looking too:
    /// on the in-game hint card, or as a tray balloon when the card cannot say it; and say once that it works when
    /// a new install shows its first line. The decision is SessionCheckModel's; this file feeds it the reading
    /// loop's counters once a second and never touches the capture path.
    /// </summary>
    public partial class SettingsWindow
    {
        /// <summary>The setup window finished and no line has shown yet; cleared by the first line, for good.</summary>
        public const string FirstLinePendingKey = "Ui.FirstLinePending";

        private static readonly TimeSpan SessionCheckInterval = TimeSpan.FromSeconds(1);

        /// <summary>The reading loop's counters since translation started (MainWindow).</summary>
        public Func<SessionEvidenceSnapshot> GetSessionEvidence;
        /// <summary>Translation stopped (the argument: a session check was shown in this run): the dialogue area's local counters for the stop line.</summary>
        public Func<bool, string> DescribeDialogueAreaAtStop;
        /// <summary>A session check was shown in this run of translation (first-run counter).</summary>
        private bool _checkRaisedThisRun;
        /// <summary>False in a cloud session or with the dev switch: no local game window to call full screen (MainWindow).</summary>
        public Func<bool> HasLocalGameWindow;
        /// <summary>
        /// Asks the in-game hint card whether it says this check in the game, and tells it how many seconds the
        /// check has been asking (MainWindow). Not set where the card does not exist (the end-to-end test mode): the
        /// balloon then goes out as it did before the card.
        /// </summary>
        public Func<SessionCheckKind, double, HintCheckAnswer> AskHintCard;

        private SessionCheckState _sessionCheck;
        private bool _firstLineShown, _wasTranslating;
        /// <summary>The player used a check's action in this run of translation: no more balloons until it starts again.</summary>
        private bool _checkBalloonsMuted;
        private DispatcherTimer _sessionCheckTicker;
        /// <summary>States already said in a balloon since Kaption started (bit per kind): once each, never a nag.</summary>
        private int _checkBalloonsShown;
        /// <summary>The check that is asking the hint card to say it; none while the player is in this window.</summary>
        private CheckWait? _checkWait;

        /// <summary>
        /// What the in-game hint card is told, once a second. Full screen as the check itself has it: nothing can be
        /// drawn over the game then, whatever the Start card shows. The other two only while the Start card says
        /// them (it hides a check under a problem it knows, such as a reader that failed), so the game and this
        /// window never say two different things.
        /// </summary>
        internal SessionCheckKind SessionCheckNow =>
            _sessionCheck.Kind == SessionCheckKind.FullScreen ? SessionCheckKind.FullScreen
            : _cardState == null ? SessionCheckKind.None : StartCardModel.SessionCheckOf(_cardState.Kind);

        private SessionCheckState NextSessionCheck(bool translating)
        {
            var evidence = GetSessionEvidence?.Invoke() ?? default;
            var context = new SessionCheckContext(translating, HasLocalGameWindow?.Invoke() ?? true, Config.Get(FirstLinePendingKey, false));
            return SessionCheckModel.Next(_sessionCheck, evidence, SessionCheckThresholds.From(GameRegionProfile.ForCurrentGame()), context);
        }

        /// <summary>The one place the shown state moves: every card refresh passes through here.</summary>
        private SessionCheckKind AdvanceSessionCheck(bool translating)
        {
            if (_wasTranslating && !translating)
            {
                _firstLineShown = false;
                _checkBalloonsMuted = false;
                string area = DescribeDialogueAreaAtStop?.Invoke(_checkRaisedThisRun) ?? "";
                _checkRaisedThisRun = false;
                if (GetSessionEvidence != null)
                    Logger.Log.Info("Translation stopped. " + DescribeSessionEvidence(GetSessionEvidence(), _sessionCheck.Kind)
                                    + (string.IsNullOrEmpty(area) ? "" : " " + area));
            }
            _wasTranslating = translating;

            SessionCheckKind before = _sessionCheck.Kind;
            _sessionCheck = NextSessionCheck(translating);
            if (_sessionCheck.Kind != before && _sessionCheck.Kind != SessionCheckKind.None)
                Logger.Log.Info($"Session check: {_sessionCheck.Kind}. " + DescribeSessionEvidence(GetSessionEvidence?.Invoke() ?? default, _sessionCheck.Kind));
            return _sessionCheck.Kind;
        }

        // Ticks only while translation runs; refreshes the card only when the state would change.
        private void UpdateSessionCheckTicker(bool translating)
        {
            if (translating && _sessionCheckTicker == null)
            {
                _sessionCheckTicker = new DispatcherTimer(DispatcherPriority.Background) { Interval = SessionCheckInterval };
                _sessionCheckTicker.Tick += (_, __) =>
                {
                    bool running = IsOcrRunning?.Invoke() ?? false;
                    if (!running || NextSessionCheck(true).Kind != _sessionCheck.Kind) UpdateDashboardStatus();
                    // The hint card looks on a clock of its own: a check it could not say a second ago it may have
                    // said by now, or given up on, and nothing else brings the balloon's question back.
                    else if (_cardState != null) NotifySessionCheckOnCard(_cardState.Kind);
                };
                _sessionCheckTicker.Start();
            }
            else if (!translating && _sessionCheckTicker != null)
            {
                _sessionCheckTicker.Stop();
                _sessionCheckTicker = null;
            }
        }

        /// <summary>Translation started again or the dialogue area changed: the evidence starts over, so does the card.</summary>
        internal void ResetSessionCheck()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ResetSessionCheck));
                return;
            }
            bool shown = _sessionCheck.Kind != SessionCheckKind.None;
            _sessionCheck = default;
            if (shown) UpdateDashboardStatus();
        }

        /// <summary>The player used the card's action: the sentence goes and stays away for this run of translation.</summary>
        private void DismissSessionCheck()
        {
            if (_sessionCheck.Kind == SessionCheckKind.None) return;
            Logger.Log.Info($"Session check: the player acted on {_sessionCheck.Kind}.");
            _sessionCheck = _sessionCheck.Dismiss();
            _checkBalloonsMuted = true;
        }

        /// <summary>"Check the dialogue area": the area's section on Game and subtitles, with its preview and both ways to set it.</summary>
        private void OpenDialogueAreaFromCheck()
        {
            DismissSessionCheck();
            SelectTabByTag("Regions");
            UpdateDashboardStatus();
        }

        /// <summary>
        /// The balloon says what the card says: called with the state the card's own priority order chose, so a check
        /// hidden by a bigger problem is not announced (and may be later, when the card does show it). A check is
        /// said in the game first, on the hint card; the balloon is for when that card cannot (it is off, has no
        /// free place, the game is in full screen) and it never repeats what the card said.
        /// </summary>
        private void NotifySessionCheckOnCard(StartCardKind shown)
        {
            SessionCheckKind kind = StartCardModel.SessionCheckOf(shown);
            long nowMs = Environment.TickCount64;
            _checkWait = NextCheckWait(_checkWait, kind, IsActive, nowMs);
            // The first-run counter: a check the card really showed, not one a bigger problem hid.
            if (kind != SessionCheckKind.None) _checkRaisedThisRun = true;
            int bit = BalloonBit(shown);
            // Acting on a check silences the checks; the first line is good news and is said regardless.
            if (bit == 0 || (kind != SessionCheckKind.None && _checkBalloonsMuted)) return;
            if ((_checkBalloonsShown & bit) != 0) return;
            // Asked only for a player who is not in this window: the wait for the hint card runs while they play.
            HintCheckAnswer inGame = _checkWait is { } wait
                ? AskHintCard?.Invoke(kind, (nowMs - wait.SinceMs) / 1000.0) ?? HintCheckAnswer.Cannot
                : HintCheckAnswer.Cannot;
            switch (BalloonDecision(IsActive, StartPageInView(), inGame))
            {
                case CheckBalloon.SeenOnCard:
                case CheckBalloon.SaidInGame:
                    _checkBalloonsShown |= bit;   // the player has read it, here or in the game: no balloon, now or later
                    break;
                case CheckBalloon.Send:
                    _checkBalloonsShown |= bit;
                    var (title, body) = shown == StartCardKind.FirstLineWorks
                        ? (L("shell_Card_FirstLine", "It works: subtitles are showing"),
                           L("shell_Card_FirstLine_Detail", "That was your first translated line. From now on Kaption puts subtitles on screen whenever someone in the game speaks."))
                        : SessionCheckText(kind, L, Services.Detection.DialogueAreaStore.Mode == Services.Detection.DialogueAreaMode.Auto);
                    notifyIcon?.ShowBalloonTip(title, body, onClick: ShowStartPage);
                    break;
                // Wait: Kaption is in front on another page (Activated, Deactivated and the rail bring us back here),
                // or the hint card says it in a moment (the session ticker asks again every second).
            }
        }

        internal enum CheckBalloon { Send, SeenOnCard, Wait, SaidInGame }

        /// <summary>A check that is asking the hint card to say it, and since when (Environment.TickCount64).</summary>
        internal readonly record struct CheckWait(SessionCheckKind Kind, long SinceMs);

        /// <summary>
        /// The wait for the hint card runs while the player is out of this window and the same check keeps asking.
        /// In this window nothing asks, so a visit ends the wait, however short, and the way back to the game starts
        /// a new one: the balloon must not go out the moment the card can speak. No check on the Start card (also
        /// translation stopped or started again), no wait.
        /// </summary>
        internal static CheckWait? NextCheckWait(CheckWait? current, SessionCheckKind kind, bool windowActive, long nowMs) =>
            windowActive || kind == SessionCheckKind.None ? null
            : current is { } wait && wait.Kind == kind ? wait
            : new CheckWait(kind, nowMs);

        /// <summary>The card states said in a balloon too, one bit each (0: no balloon): the three checks and the first line.</summary>
        internal static int BalloonBit(StartCardKind shown) =>
            shown == StartCardKind.FirstLineWorks ? 1 << 30
            : StartCardModel.SessionCheckOf(shown) is var kind && kind != SessionCheckKind.None ? 1 << (int)kind : 0;

        /// <summary>
        /// The balloon is for a player who is in the game. One reading the Start card needs none; one on another page
        /// of this window has not seen the card, so the balloon waits until they open Start or go back to the game.
        /// In the game the hint card speaks first (<paramref name="inGame"/>, its answer for a check; Cannot for the
        /// first line, which it has no word for): said there means no balloon, "in a moment" means wait, and a card
        /// that cannot say it leaves the balloon as it was before there was a card.
        /// </summary>
        internal static CheckBalloon BalloonDecision(bool windowActive, bool startPageInView, HintCheckAnswer inGame) =>
            windowActive ? (startPageInView ? CheckBalloon.SeenOnCard : CheckBalloon.Wait)
            : inGame switch
            {
                HintCheckAnswer.Shown => CheckBalloon.SaidInGame,
                HintCheckAnswer.Later => CheckBalloon.Wait,
                _ => CheckBalloon.Send,
            };

        private bool StartPageInView() =>
            IsVisible && WindowState != System.Windows.WindowState.Minimized
            && (NavTabs?.SelectedItem as System.Windows.Controls.TabItem)?.Tag as string == SettingsNavigation.Home;

        /// <summary>The moments a waiting balloon can be settled: the window gains or loses focus, the rail changes page.</summary>
        private void InitializeSessionCheck()
        {
            void Reconsider()
            {
                if (_cardState != null) NotifySessionCheckOnCard(_cardState.Kind);
            }
            Activated += (_, __) => Reconsider();
            Deactivated += (_, __) => Reconsider();
            NavTabs.SelectionChanged += (s, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, NavTabs)) Reconsider();
            };
        }

        /// <summary>The card's own title and sentence, so the balloon and the card never disagree.</summary>
        internal static (string Title, string Body) SessionCheckText(SessionCheckKind kind, Func<string, string, string> L, bool areaAutomatic = false) => kind switch
        {
            SessionCheckKind.FullScreen => (L("shell_Card_FullScreen", "Kaption can't see the game"),
                areaAutomatic
                    ? L("shell_Card_FullScreen_Detail_Auto", "The picture from the game is black. That usually means exclusive full screen: switch the game to Borderless or Windowed.")
                    : L("shell_Card_FullScreen_Detail", "The picture from the dialogue frame is black. That usually means the game runs in full screen: switch it to Borderless or Windowed. If it already is, check where the dialogue frame sits.")),
            SessionCheckKind.NoMatch => (L("shell_Card_NoMatch", "Kaption sees text but can't find the line"),
                areaAutomatic
                    ? L("shell_Card_NoMatch_Detail_Auto", "Is the game's text set to English? Kaption translates from English.")
                    : L("shell_Card_NoMatch_Detail", "Is the game's text set to English? Kaption translates from the English text. If it is, check that the dialogue frame covers the whole dialogue box.")),
            SessionCheckKind.NoDialogue => (L("shell_Card_NoDialogue", "No dialogue seen yet"),
                areaAutomatic
                    ? L("shell_Card_NoDialogue_Detail_Auto", "Talk to someone in the game and the Polish line shows up by itself.")
                    : L("shell_Card_NoDialogue_Detail", "Talk to someone in the game and the Polish line shows up. If a dialogue is on screen right now and nothing appears, check where the dialogue frame sits.")),
            _ => (string.Empty, string.Empty),
        };

        /// <summary>
        /// The first line a new install shows: remembered at once so it is said one time only, and kept on the card
        /// until translation stops. Its balloon goes out with the card, like the checks'. Returns whether this line
        /// was that one.
        /// </summary>
        private bool ConfirmFirstLine()
        {
            if (_firstLineShown || !Config.Get(FirstLinePendingKey, false)) return false;
            Config.Set(FirstLinePendingKey, false);
            _firstLineShown = true;
            Logger.Log.Info("First translated line on this install: the Start card says it works.");
            return true;
        }

        /// <summary>One line for the log and Start › Advanced; support reads it, players do not.</summary>
        internal static string DescribeSessionEvidence(SessionEvidenceSnapshot e, SessionCheckKind check) =>
            string.Format(CultureInfo.InvariantCulture,
                "Session reads: {0} dialogue-shaped, {1} matched, {2} pack hits outside dialogue, {3} black frames; {4} unmatched in a row; game in front {5:0} s{6}; check: {7}",
                e.DialogueReads, e.MatchedReads, e.PackHits, e.BlackFrames, e.UnmatchedRun, e.InFrontSeconds,
                e.SawPicture ? string.Empty : ", no picture seen", check);
    }
}
