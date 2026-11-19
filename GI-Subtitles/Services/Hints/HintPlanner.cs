using System;
using GI_Subtitles.Services.Notices;
using GI_Subtitles.Views.Shell;

namespace GI_Subtitles.Services.Hints
{
    /// <summary>What is on the card.</summary>
    public enum HintShowing
    {
        None,
        Echo,
        Tip,
        /// <summary>The sheet Kaption shows on its own on an install's first days.</summary>
        WelcomeSheet,
        /// <summary>The sheet the player asked for (the dialogue-frame shortcut, "Show it now").</summary>
        AskedSheet,
        /// <summary>The sheet the player pinned.</summary>
        PinnedSheet,
    }

    /// <summary>What the player has just done that the card answers at once.</summary>
    public enum HintRequest
    {
        None,
        /// <summary>The hide shortcut hid the subtitles.</summary>
        SubtitlesHidden,
        /// <summary>The hide shortcut brought them back.</summary>
        SubtitlesBack,
        /// <summary>The start/stop shortcut stopped translation.</summary>
        TranslationStopped,
        /// <summary>"Don't show again" on the card.</summary>
        HintsSwitchedOff,
        /// <summary>The player asked for the sheet.</summary>
        Sheet,
    }

    public enum HintAction
    {
        /// <summary>Leave the card as it is (also: nothing is up and nothing is due).</summary>
        Keep,
        Show,
        Hide,
    }

    /// <summary>
    /// What the card does next. <see cref="Entry"/> is the echo or the tip; a sheet has none. <see cref="Seconds"/>
    /// is 0 for a pinned sheet (it stays) and for a tip (<see cref="HintCatalog.TipSeconds"/> of its text).
    /// </summary>
    public readonly record struct HintDecision(HintAction Action, HintShowing What = HintShowing.None, HintEntry Entry = null, double Seconds = 0)
    {
        public static HintDecision Keep => default;
        public static HintDecision Hide => new(HintAction.Hide);
        public static HintDecision Show(HintShowing what, double seconds = 0) => new(HintAction.Show, what, null, seconds);
        public static HintDecision Show(HintShowing what, HintEntry entry) => new(HintAction.Show, what, entry, entry.Seconds);
    }

    /// <summary>What the card answers when the Start card's check asks to be said in the game rather than in a tray balloon.</summary>
    public enum HintCheckAnswer
    {
        /// <summary>The card will not say it: the balloon goes out as it did before there was a card.</summary>
        Cannot,
        /// <summary>
        /// The card says it at its next free moment, or it is up and nobody has had the time to read it yet: the
        /// balloon waits.
        /// </summary>
        Later,
        /// <summary>The card said it in this run, long enough to be read: no balloon.</summary>
        Shown,
    }

    /// <summary>What the app knows at one moment; read once a second, and once more when a key is pressed.</summary>
    public readonly record struct HintSignals
    {
        /// <summary><see cref="HintSettings.Enabled"/>.</summary>
        public bool Enabled { get; init; }
        /// <summary><see cref="HintSettings.Pinned"/>.</summary>
        public bool Pinned { get; init; }
        /// <summary>The end-to-end test mode without its switch: nothing shows at all.</summary>
        public bool Blocked { get; init; }
        public bool Translating { get; init; }
        /// <summary>The game is the window in front.</summary>
        public bool GameInFront { get; init; }
        /// <summary>The answers card, the Ctrl+Q popup, the area picker, the fix window, the overlap alert or a dialog of Kaption's is up.</summary>
        public bool OwnWindowUp { get; init; }
        public HintShowing Showing { get; init; }
        /// <summary>The echo or the tip that is up; null for a sheet and when nothing is.</summary>
        public HintEntry ShowingEntry { get; init; }
        /// <summary>
        /// The tip whose card is fading out: no longer <see cref="Showing"/>, but on screen for a moment more. Only
        /// the Start card's check is told: its balloon must not go out beside it.
        /// </summary>
        public HintEntry LeavingEntry { get; init; }
        public HintRequest Request { get; init; }
        /// <summary>
        /// A translated line is up right now: painted, or it would be if the subtitles were not hidden. Someone is
        /// talking. The panel's "ready" line is not one.
        /// </summary>
        public bool LineUp { get; init; }
        /// <summary>Seconds since the last such line left; null when there was none in this run.</summary>
        public double? SecondsSinceLine { get; init; }
        /// <summary>How long the subtitles have been hidden with the shortcut; null when they show, or were hidden in Kaption's own window.</summary>
        public double? HiddenByKeySeconds { get; init; }
        /// <summary>Seconds since a line was last matched in this run; null when none was.</summary>
        public double? SecondsSinceLineMatched { get; init; }
        /// <summary>Lines painted in this run.</summary>
        public int LinesShown { get; init; }
        /// <summary>
        /// Why no line shows: full screen as the session check has it (nothing can be drawn there), the other two
        /// only while the Start card says them, so the game and Kaption's window never say two different things.
        /// </summary>
        public SessionCheckKind Check { get; init; }
        /// <summary>Reads wait because the game's window cannot carry the dialogue frame (too small, off the screen).</summary>
        public Services.Detection.AreaHold AreaHold { get; init; }
        /// <summary>The window feeds the teaching tips' moments (<see cref="LinesShown"/>, <see cref="SecondsSinceLine"/>); without them none is due.</summary>
        public bool TeachingOn { get; init; }
        public HintBindings Bindings { get; init; }
    }

    /// <summary>What the card remembers about one run of translation (from a start to the next start). A value.</summary>
    public sealed record HintRun(DateTimeOffset StartedAt)
    {
        public bool WelcomeShown { get; init; }
        public bool NoDialogueShown { get; init; }
        public bool NoMatchShown { get; init; }
        /// <summary>Held-read sentences said in this run (bit per <see cref="Services.Detection.AreaHold"/>).</summary>
        public int AreaHeldSaid { get; init; }
        public int SubtitlesHiddenTips { get; init; }
        public DateTimeOffset? SubtitlesHiddenTipAt { get; init; }
        /// <summary>Teaching tips that appeared in this run, read or not (<see cref="HintPlanner.TeachingTriesPerRun"/>).</summary>
        public int TeachingTries { get; init; }
        /// <summary>Session checks the card gave up on and left to the tray balloon (bit per kind): it does not say them after all.</summary>
        public int ChecksLeftToTray { get; init; }

        public bool LeftToTray(SessionCheckKind kind) => (ChecksLeftToTray & (1 << (int)kind)) != 0;

        public static HintRun Start(DateTimeOffset now) => new(now);
    }

    /// <summary>
    /// Decides which hint shows and when (.plan/research/s68/HINTS-AND-MORE-TRANSLATION.md, 3.4). Pure: the window
    /// asks <see cref="Next"/> once a second and on a key press, does what it says, calls <see cref="Shown"/> only
    /// when the card really appeared (a card with no free place is not shown), and <see cref="Stayed"/> once it has
    /// been up long enough to be read: only then is it counted.
    ///
    /// <para>The order when two want to show: the answer to a key, then why no line shows, then the welcome sheet,
    /// then a teaching tip, then the pinned sheet. One thing at a time; only the answer to a key replaces what is
    /// up. What Kaption shows on its own waits for a moment when nobody is talking and goes when a line arrives; a
    /// sheet the player asked for or pinned does neither, because the card is placed off the dialogue and the
    /// subtitle either way. In exclusive full
    /// screen nothing of Kaption's can be drawn over the game, so nothing unasked is shown or kept there: it would
    /// spend its budget unseen.</para>
    /// </summary>
    public static class HintPlanner
    {
        public const double WelcomeSeconds = 12, FirstWelcomeSeconds = 20, AskedSeconds = 10;
        /// <summary>The welcome sheet waits this long for the game to be in front with no line on screen; then it is dropped and the day is not counted.</summary>
        public const double WelcomeWaitSeconds = 60;
        /// <summary>
        /// The welcome sheet, a teaching tip and a "why no line" sentence count once they have been up this long
        /// (<see cref="Stayed"/>): one that was taken down sooner was read by nobody and comes again.
        /// </summary>
        public const double CountsAfterSeconds = 3;
        /// <summary>
        /// How long the tray balloon waits for the card to say why no line shows. Longer than anything the card can
        /// have up in the way (the first welcome sheet, twenty seconds); after it the balloon goes out as before.
        /// </summary>
        public const double CheckWaitSeconds = 30;
        /// <summary>Nothing shows on its own until the last line has been gone this long.</summary>
        public const double QuietSeconds = 2;
        /// <summary>"Someone is talking": a line was matched within this.</summary>
        public const double JustMatchedSeconds = 3;
        public const double HiddenBeforeTipSeconds = 60, HiddenTipGapSeconds = 600;
        public const int HiddenTipsPerRun = 2;
        public const double QuickTranslateAfterSeconds = 180, QuickTranslateNoLineSeconds = 90;
        public const int FixLineAfterLines = 30;
        public const double FixLineFromSeconds = 3, FixLineToSeconds = 10;
        public const int TeachingShowsEver = 2;
        /// <summary>
        /// Teaching tips that may appear in one run. One that a line keeps taking down before anyone read it is not
        /// counted, so without this it would flash at every pause of a long conversation.
        /// </summary>
        public const int TeachingTriesPerRun = 2;
        public static readonly TimeSpan TeachingGap = TimeSpan.FromDays(1);

        /// <param name="run">This run of translation; null when translation has not started since the app did.</param>
        public static HintDecision Next(in HintSignals s, HintState state, HintRun run, DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(state);
            bool up = s.Showing != HintShowing.None;
            if (s.Blocked) return up ? HintDecision.Hide : HintDecision.Keep;

            if (Reply(s) is HintDecision reply) return reply;

            // The session check's word for exclusive full screen: the tray keeps that one.
            bool unseen = s.Check == SessionCheckKind.FullScreen;
            switch (s.Showing)
            {
                case HintShowing.Echo:
                    return HintDecision.Keep; // four seconds at most, never cut short
                case HintShowing.AskedSheet:
                    return s.OwnWindowUp ? HintDecision.Hide : HintDecision.Keep;
                case HintShowing.WelcomeSheet:
                case HintShowing.Tip:
                    // A new line takes down what Kaption put up unasked: the subtitle would push the card to its other
                    // place while the player reads. The tip about hidden subtitles is about that very line and stays.
                    bool aboutThisLine = ReferenceEquals(s.ShowingEntry, HintCatalog.TipSubtitlesHidden);
                    return s.Enabled && s.GameInFront && !s.OwnWindowUp && !unseen && (!s.LineUp || aboutThisLine)
                        ? HintDecision.Keep
                        : HintDecision.Hide;
            }

            // Nothing is up, or the pinned sheet, which gives way to a tip and comes back after it.
            if (!s.Enabled || !s.GameInFront || s.OwnWindowUp || !s.Translating || run == null || unseen)
                return up ? HintDecision.Hide : HintDecision.Keep;

            if (OnItsOwn(s, state, run, now) is HintDecision own) return own;
            if (s.Pinned && HintCatalog.HasRows(s.Bindings))
                return up ? HintDecision.Keep : HintDecision.Show(HintShowing.PinnedSheet);
            return up ? HintDecision.Hide : HintDecision.Keep;
        }

        /// <summary>The answer to what the player has just done, when it can be given.</summary>
        private static HintDecision? Reply(in HintSignals s)
        {
            switch (s.Request)
            {
                case HintRequest.HintsSwitchedOff:
                    // The answer to a button on the card itself: it shows where the card was, although hints are now off.
                    return HintDecision.Show(HintShowing.Echo, HintCatalog.EchoHintsOff);
                case HintRequest.Sheet:
                    // Asked for: whatever is in front, and also with hints switched off.
                    return !s.OwnWindowUp && HintCatalog.HasRows(s.Bindings) ? HintDecision.Show(HintShowing.AskedSheet, AskedSeconds) : null;
                case HintRequest.SubtitlesHidden: return Echo(s, HintCatalog.EchoSubtitlesHidden);
                case HintRequest.SubtitlesBack: return Echo(s, HintCatalog.EchoSubtitlesBack);
                case HintRequest.TranslationStopped: return Echo(s, HintCatalog.EchoStopped);
                default: return null;
            }
        }

        private static HintDecision? Echo(in HintSignals s, HintEntry echo) =>
            s.Enabled && s.GameInFront && s.Bindings.Allows(echo) ? HintDecision.Show(HintShowing.Echo, echo) : null;

        /// <summary>What Kaption says unasked, while translating in the game with none of its windows up.</summary>
        private static HintDecision? OnItsOwn(in HintSignals s, HintState state, HintRun run, DateTimeOffset now)
        {
            // The one thing said because someone is talking; everything else waits until nobody is.
            if (s.HiddenByKeySeconds >= HiddenBeforeTipSeconds && s.SecondsSinceLineMatched <= JustMatchedSeconds
                && s.Bindings.Allows(HintCatalog.TipSubtitlesHidden) && HiddenTipAllowed(s, run, now))
                return HintDecision.Show(HintShowing.Tip, HintCatalog.TipSubtitlesHidden);
            if (s.LineUp || s.SecondsSinceLine < QuietSeconds) return null;
            double intoRun = (now - run.StartedAt).TotalSeconds;
            string today = NoticeGate.DayOf(now);

            // Nothing is read while the game's window cannot carry the frame: that is why, before any other "why no line".
            if (HeldTip(s, run) is HintEntry heldTip) return HintDecision.Show(HintShowing.Tip, heldTip);
            // While they wait, "talk to someone" or "is the text English" would be wrong advice.
            if (s.AreaHold == Services.Detection.AreaHold.None && SessionTip(s, run) is HintEntry sessionTip) return HintDecision.Show(HintShowing.Tip, sessionTip);

            // A pinned sheet is the welcome already.
            if (!s.Pinned && !run.WelcomeShown && intoRun <= WelcomeWaitSeconds && state.WelcomeDays.Count < state.WelcomeLimit
                && !state.WelcomeShownOn(today) && HintCatalog.HasRows(s.Bindings))
                return HintDecision.Show(HintShowing.WelcomeSheet, state.WelcomeDays.Count == 0 ? FirstWelcomeSeconds : WelcomeSeconds);

            // While lines are read and not found the pack does not know this game's text (it is not in English):
            // the taught shortcuts would not work either, so neither is taught then.
            if (s.TeachingOn && s.Check != SessionCheckKind.NoMatch && !run.WelcomeShown && run.TeachingTries < TeachingTriesPerRun
                && !TeachingTipShownOn(state, today))
                foreach (HintEntry tip in HintCatalog.TeachingTips)
                    if (TeachingTipDue(tip, s, state, intoRun, today, now)) return HintDecision.Show(HintShowing.Tip, tip);
            return null;
        }

        /// <summary>The sentence for a window that cannot carry the frame; once per run each, and again when nobody had the time to read it.</summary>
        public static HintEntry HeldTip(in HintSignals s, HintRun run)
        {
            if (s.AreaHold == Services.Detection.AreaHold.None || (run.AreaHeldSaid & (1 << (int)s.AreaHold)) != 0) return null;
            HintEntry tip = s.AreaHold == Services.Detection.AreaHold.TooSmall ? HintCatalog.TipAreaHeldSmall : HintCatalog.TipAreaHeldOff;
            return s.Bindings.Allows(tip) ? tip : null;
        }

        /// <summary>Why no line shows, in the Start card's own words; once per run each, and again when nobody had the time to read it.</summary>
        private static HintEntry SessionTip(in HintSignals s, HintRun run) =>
            run.LeftToTray(s.Check) || CheckSaid(run, s.Check) ? null : TipFor(s.Check);

        /// <summary>The card's line for a session check; none for full screen, where nothing can be drawn over the game.</summary>
        public static HintEntry TipFor(SessionCheckKind kind) => kind switch
        {
            SessionCheckKind.NoMatch => HintCatalog.TipNoMatch,
            SessionCheckKind.NoDialogue => HintCatalog.TipNoDialogue,
            _ => null,
        };

        private static bool CheckSaid(HintRun run, SessionCheckKind kind) => kind switch
        {
            SessionCheckKind.NoMatch => run.NoMatchShown,
            SessionCheckKind.NoDialogue => run.NoDialogueShown,
            _ => false,
        };

        /// <summary>
        /// The Start card's check asks before its tray balloon goes out: does the card say it in the game? The card
        /// replaces the balloon, it never doubles it. <paramref name="noPlace"/>: the card tried this line and found
        /// no free place. <paramref name="waitedSeconds"/>: how long this check has been asking in this run.
        /// </summary>
        public static HintCheckAnswer CheckAnswer(SessionCheckKind kind, in HintSignals s, HintRun run, bool noPlace, double waitedSeconds)
        {
            HintEntry tip = TipFor(kind);
            if (tip == null) return HintCheckAnswer.Cannot;
            // Reads wait for the game's window: the balloon says nothing about the game's text until they run again.
            if (!s.Blocked && s.AreaHold != Services.Detection.AreaHold.None) return HintCheckAnswer.Later;
            // Said is said, whatever happened to the card since (switched off, failed): asked first, so the balloon
            // never repeats it.
            if (run != null && CheckSaid(run, kind)) return HintCheckAnswer.Shown;
            // Up, and not read yet. Not said: the Start card would keep the balloon silent until Kaption starts
            // again. Not the balloon's either, however long the check has waited: it would go out beside the card.
            // It is counted or taken down within CountsAfterSeconds, and the next question gets that answer. Taken
            // down, it fades for a moment and is still on screen: the same answer until it is gone. A card that
            // failed counts nothing any more, so what it may have left up does not hold the balloon back.
            bool onScreen = (s.Showing == HintShowing.Tip && ReferenceEquals(s.ShowingEntry, tip)) || ReferenceEquals(s.LeavingEntry, tip);
            if (!s.Blocked && onScreen) return HintCheckAnswer.Later;
            if (s.Blocked || !s.Enabled || !s.Translating || run == null || run.LeftToTray(kind)) return HintCheckAnswer.Cannot;
            // Something else is up, a window of Kaption's is open or the game is behind another window: soon, but
            // not for ever. A player who stays away gets the balloon after all.
            return noPlace || waitedSeconds >= CheckWaitSeconds ? HintCheckAnswer.Cannot : HintCheckAnswer.Later;
        }

        /// <summary>The balloon has this check now: the card keeps quiet about it for the rest of the run.</summary>
        public static HintRun CheckLeftToTray(HintRun run, SessionCheckKind kind) =>
            run == null || TipFor(kind) == null ? run : run with { ChecksLeftToTray = run.ChecksLeftToTray | (1 << (int)kind) };

        /// <summary>
        /// Once, and a second time only for a player who answered the first: the subtitles were hidden again after
        /// that tip (so the key was pressed in between), at least ten minutes later.
        /// </summary>
        private static bool HiddenTipAllowed(in HintSignals s, HintRun run, DateTimeOffset now)
        {
            if (run.SubtitlesHiddenTips == 0) return true;
            if (run.SubtitlesHiddenTips >= HiddenTipsPerRun || run.SubtitlesHiddenTipAt is not DateTimeOffset at) return false;
            double sinceTip = (now - at).TotalSeconds;
            return sinceTip >= HiddenTipGapSeconds && s.HiddenByKeySeconds < sinceTip;
        }

        private static bool TeachingTipShownOn(HintState state, string day)
        {
            foreach (HintEntry tip in HintCatalog.TeachingTips)
                if (state.Tip(tip.Id).Last is DateTimeOffset last && NoticeGate.DayOf(last) == day) return true;
            return false;
        }

        private static bool TeachingTipDue(HintEntry tip, in HintSignals s, HintState state, double intoRun, string today, DateTimeOffset now)
        {
            HintTipRecord shown = state.Tip(tip.Id);
            if (shown.Count >= TeachingShowsEver || state.KeyUsed(tip.KeyId) || !s.Bindings.Allows(tip)) return false;
            if (shown.Last is DateTimeOffset last && now - last < TeachingGap) return false;
            if (ReferenceEquals(tip, HintCatalog.TipQuickTranslate))
                return intoRun >= QuickTranslateAfterSeconds && (s.SecondsSinceLine ?? intoRun) >= QuickTranslateNoLineSeconds;
            // Fixing a line is for a player who has read a few: not on the first day, and right after a line has gone.
            if (ReferenceEquals(tip, HintCatalog.TipFixLine))
                return string.CompareOrdinal(today, state.Since) > 0 && s.LinesShown >= FixLineAfterLines
                    && s.SecondsSinceLine is >= FixLineFromSeconds and <= FixLineToSeconds;
            return false; // a tip without a moment of its own is never due
        }

        /// <summary>
        /// The player hid a sheet, or let go of a pinned one: no welcome sheet follows it in this run. The day is not
        /// counted, because Kaption did not show that sheet on its own.
        /// </summary>
        public static HintRun SheetDismissed(HintRun run) => run == null ? null : run with { WelcomeShown = true };

        /// <summary>
        /// The card appeared: what appearing alone costs. Nothing lasting: a teaching tip uses one of the run's tries
        /// (<see cref="TeachingTriesPerRun"/>), and the tip about hidden subtitles is counted here, because no line
        /// takes it down and a player may answer it with its key within a second. Everything else is counted by
        /// <see cref="Stayed"/>: a new line can take it down before anyone read it.
        /// </summary>
        public static HintRun Shown(HintDecision decision, HintRun run, DateTimeOffset now)
        {
            if (run == null || decision.Action != HintAction.Show || decision.What != HintShowing.Tip || decision.Entry is not HintEntry tip)
                return run;
            if (tip.Kind == HintEntryKind.TeachingTip) return run with { TeachingTries = run.TeachingTries + 1 };
            if (ReferenceEquals(tip, HintCatalog.TipSubtitlesHidden))
                return run with { SubtitlesHiddenTips = run.SubtitlesHiddenTips + 1, SubtitlesHiddenTipAt = now };
            return run;
        }

        /// <summary>
        /// Whether a sheet or tip that is up and not yet counted counts at this look: the look keeps it, so it is
        /// still where the player can read it, and it has been up for <see cref="CountsAfterSeconds"/>. A look that
        /// takes it down or replaces it does not count it, however late that look comes: the player left the game
        /// or locked Windows some time before it, and nobody knows what they saw.
        /// </summary>
        public static bool CountsNow(in HintDecision decision, double upSeconds) =>
            decision.Action == HintAction.Keep && upSeconds >= CountsAfterSeconds;

        /// <summary>
        /// What is up has been there for <see cref="CountsAfterSeconds"/>, or the player hid it themselves: it was
        /// read, and now it counts. The welcome sheet takes its day and no second one comes in this run; a teaching
        /// tip takes one of its showings; a "why no line" sentence is said for this run. The state comes back as the
        /// same object when nothing lasting changed, so the caller saves only when it differs.
        /// </summary>
        public static (HintState State, HintRun Run) Stayed(HintShowing what, HintEntry entry, HintState state, HintRun run, DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(state);
            switch (what)
            {
                case HintShowing.WelcomeSheet:
                    return (state.WithWelcomeOn(NoticeGate.DayOf(now)), run == null ? null : run with { WelcomeShown = true });
                case HintShowing.Tip when entry is HintEntry tip:
                    if (tip.Kind == HintEntryKind.TeachingTip) return (state.WithTipShown(tip.Id, now), run);
                    if (run == null) return (state, null);
                    if (ReferenceEquals(tip, HintCatalog.TipNoMatch)) return (state, run with { NoMatchShown = true });
                    if (ReferenceEquals(tip, HintCatalog.TipNoDialogue)) return (state, run with { NoDialogueShown = true });
                    if (ReferenceEquals(tip, HintCatalog.TipAreaHeldSmall))
                        return (state, run with { AreaHeldSaid = run.AreaHeldSaid | (1 << (int)Services.Detection.AreaHold.TooSmall) });
                    if (ReferenceEquals(tip, HintCatalog.TipAreaHeldOff))
                        return (state, run with { AreaHeldSaid = run.AreaHeldSaid | (1 << (int)Services.Detection.AreaHold.OffScreen) });
                    return (state, run);
                default:
                    return (state, run);
            }
        }
    }
}
