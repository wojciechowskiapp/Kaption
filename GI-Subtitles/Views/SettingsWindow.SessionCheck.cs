using System;
using System.Globalization;
using System.Windows.Threading;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The first-session check on the Start card: while translation runs and no line shows, say why (the game looks
    /// like full screen, lines are read but not found, no dialogue seen yet), once as a tray balloon too; and say
    /// once that it works when a new install shows its first line. The decision is SessionCheckModel's; this file
    /// feeds it the reading loop's counters once a second and never touches the capture path.
    /// </summary>
    public partial class SettingsWindow
    {
        /// <summary>The setup window finished and no line has shown yet; cleared by the first line, for good.</summary>
        public const string FirstLinePendingKey = "Ui.FirstLinePending";

        private static readonly TimeSpan SessionCheckInterval = TimeSpan.FromSeconds(1);

        /// <summary>The reading loop's counters since translation started (MainWindow).</summary>
        public Func<SessionEvidenceSnapshot> GetSessionEvidence;
        /// <summary>False in a cloud session or with the dev switch: no local game window to call full screen (MainWindow).</summary>
        public Func<bool> HasLocalGameWindow;

        private SessionCheckState _sessionCheck;
        private bool _firstLineShown, _wasTranslating;
        /// <summary>The player used a check's action in this run of translation: no more balloons until it starts again.</summary>
        private bool _checkBalloonsMuted;
        private DispatcherTimer _sessionCheckTicker;
        /// <summary>States already said in a balloon since Kaption started (bit per kind): once each, never a nag.</summary>
        private int _checkBalloonsShown;

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
                if (GetSessionEvidence != null)
                    Logger.Log.Info("Translation stopped. " + DescribeSessionEvidence(GetSessionEvidence(), _sessionCheck.Kind));
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
        /// hidden by a bigger problem is not announced (and may be later, when the card does show it).
        /// </summary>
        private void NotifySessionCheckOnCard(StartCardKind shown)
        {
            SessionCheckKind kind = StartCardModel.SessionCheckOf(shown);
            int bit = BalloonBit(shown);
            // Acting on a check silences the checks; the first line is good news and is said regardless.
            if (bit == 0 || (kind != SessionCheckKind.None && _checkBalloonsMuted)) return;
            if ((_checkBalloonsShown & bit) != 0) return;
            switch (BalloonDecision(IsActive, StartPageInView()))
            {
                case CheckBalloon.SeenOnCard:
                    _checkBalloonsShown |= bit;   // the player is reading the card: no balloon needed, now or later
                    break;
                case CheckBalloon.Send:
                    _checkBalloonsShown |= bit;
                    var (title, body) = shown == StartCardKind.FirstLineWorks
                        ? (L("shell_Card_FirstLine", "It works: subtitles are showing"),
                           L("shell_Card_FirstLine_Detail", "That was your first translated line. From now on Kaption puts subtitles on screen whenever someone in the game speaks."))
                        : SessionCheckText(kind, L);
                    notifyIcon?.ShowBalloonTip(title, body, onClick: ShowStartPage);
                    break;
                // Wait: Kaption is in front on another page. Activated, Deactivated and the rail bring us back here.
            }
        }

        internal enum CheckBalloon { Send, SeenOnCard, Wait }

        /// <summary>The card states said in a balloon too, one bit each (0: no balloon): the three checks and the first line.</summary>
        internal static int BalloonBit(StartCardKind shown) =>
            shown == StartCardKind.FirstLineWorks ? 1 << 30
            : StartCardModel.SessionCheckOf(shown) is var kind && kind != SessionCheckKind.None ? 1 << (int)kind : 0;

        /// <summary>
        /// The balloon is for a player who is in the game. One reading the Start card needs none; one on another page
        /// of this window has not seen the card, so the balloon waits until they open Start or go back to the game.
        /// </summary>
        internal static CheckBalloon BalloonDecision(bool windowActive, bool startPageInView) =>
            !windowActive ? CheckBalloon.Send : startPageInView ? CheckBalloon.SeenOnCard : CheckBalloon.Wait;

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
        internal static (string Title, string Body) SessionCheckText(SessionCheckKind kind, Func<string, string, string> L) => kind switch
        {
            SessionCheckKind.FullScreen => (L("shell_Card_FullScreen", "Kaption can't see the game"),
                L("shell_Card_FullScreen_Detail", "The picture from the dialogue area is black. That usually means the game runs in full screen: switch it to Borderless or Windowed. If it already is, check where the dialogue area sits.")),
            SessionCheckKind.NoMatch => (L("shell_Card_NoMatch", "Kaption sees text but can't find the line"),
                L("shell_Card_NoMatch_Detail", "Is the game's text set to English? Kaption translates from the English text. If it is, check that the dialogue area covers the whole dialogue box.")),
            SessionCheckKind.NoDialogue => (L("shell_Card_NoDialogue", "No dialogue seen yet"),
                L("shell_Card_NoDialogue_Detail", "Talk to someone in the game and the Polish line shows up. If a dialogue is on screen right now and nothing appears, check where the dialogue area sits.")),
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
