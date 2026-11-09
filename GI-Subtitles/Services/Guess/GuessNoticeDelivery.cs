using System;

namespace GI_Subtitles.Services.Guess
{
    /// <summary>What to do with a waiting ranking notice right now.</summary>
    public enum GuessDeliveryStep
    {
        /// <summary>No notice waits.</summary>
        Idle,
        /// <summary>The player has the Start strip in front of them: delivered, no balloon.</summary>
        SeenOnStrip,
        /// <summary>Show the tray balloon, then mark it delivered.</summary>
        ShowBalloon,
        /// <summary>The notice is from an earlier day: no balloon any more; the strip keeps it until the next read.</summary>
        Expired,
        /// <summary>The player turned notices off: clear everything.</summary>
        Disabled,
        /// <summary>A notice already reached the player today: no balloon; the strip keeps it.</summary>
        CappedToday,
        /// <summary>Subtitles run, a game is in front or Windows says not now: ask again later.</summary>
        Wait,
    }

    public sealed record GuessDeliveryDecision(GuessDeliveryStep Step, GuessGateResult Gate);

    /// <summary>
    /// The ranking notice between a read and the player (design section 6, R4 section 2.2). Pure: MainWindow feeds in
    /// the reads and what the screen looks like, and does what <see cref="Next"/> says (balloon, strip, logs).
    /// A notice stays undelivered until it is seen on the strip or shown as a balloon; only then do its baselines move.
    /// </summary>
    public sealed class GuessNoticeDelivery
    {
        /// <summary>The last standing read; delivery re-bases from it (every read carries every game's board).</summary>
        public GuessStanding Standing { get; private set; }

        /// <summary>What the Start strip shows; null when nothing.</summary>
        public GuessNotice Notice { get; private set; }

        /// <summary>The notice has not reached the player yet (no balloon, strip not seen).</summary>
        public bool Undelivered { get; private set; }

        /// <summary>The local day the notice was found on; its balloon is only shown that day.</summary>
        public string PendingDay { get; private set; }

        /// <summary>What the last read came to, board by board (for its log line).</summary>
        public GuessOvertakeRule.Outcome LastOutcome { get; private set; }

        /// <summary>
        /// The Start strip counts as seen only when Settings is the active window, not minimised, on Start, with the
        /// strip up. A Settings window left open behind the game is visible to WPF but not to the player.
        /// </summary>
        public static bool StripCountsAsSeen(bool settingsActive, bool minimized, bool stripVisible, bool onStartTab) =>
            settingsActive && !minimized && stripVisible && onStartTab;

        /// <summary>
        /// A standing read. Returns the baselines to keep and the candidate it found (null for none, which also clears
        /// the strip: this read replaces what it said).
        /// </summary>
        public GuessBaselines Read(GuessBaselines baselines, GuessStanding standing, string gameBoard, string today,
                                   Func<GuessOvertake, GuessNotice> build, out GuessOvertake candidate) =>
            Read(baselines, standing, gameBoard, today, null, build, out candidate);

        /// <param name="lastShownDay">The day the last notice reached the player (see <see cref="GuessOvertakeRule.Evaluate"/>).</param>
        public GuessBaselines Read(GuessBaselines baselines, GuessStanding standing, string gameBoard, string today, string lastShownDay,
                                   Func<GuessOvertake, GuessNotice> build, out GuessOvertake candidate)
        {
            candidate = null;
            if (standing == null) return baselines ?? GuessBaselines.Empty;
            Standing = standing;
            var outcome = GuessOvertakeRule.Evaluate(baselines, standing, gameBoard, lastShownDay);
            LastOutcome = outcome;
            candidate = outcome.Candidate;
            GuessNotice notice = candidate == null ? null : build?.Invoke(candidate);
            if (notice == null)
            {
                Clear();
                return outcome.Baselines;
            }
            Notice = notice;
            Undelivered = true;
            PendingDay = today;
            return outcome.Baselines;
        }

        /// <summary>
        /// The next step for the waiting notice. Seeing the strip comes first and counts as today's notice even when
        /// the notice is from yesterday, so a notice the player has read never comes back as a balloon.
        /// </summary>
        public GuessDeliveryDecision Next(string today, bool stripSeen, bool enabled, string lastShownDay, bool translating,
                                          bool gameInFront, int quns)
        {
            if (Notice == null || !Undelivered) return new GuessDeliveryDecision(GuessDeliveryStep.Idle, GuessGateResult.Show);
            if (!enabled) return new GuessDeliveryDecision(GuessDeliveryStep.Disabled, GuessGateResult.Disabled);
            if (stripSeen) return new GuessDeliveryDecision(GuessDeliveryStep.SeenOnStrip, GuessGateResult.Show);
            if (!string.Equals(today, PendingDay, StringComparison.Ordinal))
                return new GuessDeliveryDecision(GuessDeliveryStep.Expired, GuessGateResult.Show);
            var gate = GuessNoticeGate.Check(true, lastShownDay, today, translating, gameInFront, quns);
            return gate switch
            {
                GuessGateResult.Show => new GuessDeliveryDecision(GuessDeliveryStep.ShowBalloon, gate),
                GuessGateResult.ShownToday => new GuessDeliveryDecision(GuessDeliveryStep.CappedToday, gate),
                _ => new GuessDeliveryDecision(GuessDeliveryStep.Wait, gate),
            };
        }

        /// <summary>
        /// The notice reached the player: the selected game's board, the global board and the notice's own board start
        /// again from the last read. The strip keeps its text until the next read.
        /// </summary>
        public GuessBaselines Delivered(GuessBaselines baselines, string gameBoard)
        {
            Undelivered = false;
            return GuessOvertakeRule.Delivered(baselines, Standing, gameBoard, Notice?.Board);
        }

        /// <summary>The player opened the ranking (balloon or strip): re-base as for a delivery, then clear the strip.</summary>
        public GuessBaselines LobbyOpened(GuessBaselines baselines, string gameBoard)
        {
            var rebased = GuessOvertakeRule.Delivered(baselines, Standing, gameBoard, Notice?.Board);
            Clear();
            return rebased;
        }

        public void Clear()
        {
            Notice = null;
            Undelivered = false;
            PendingDay = null;
        }
    }
}
