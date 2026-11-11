using GI_Subtitles.Services.Detection;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// Why no line shows while Kaption translates, in rising priority: a state with direct evidence replaces a
    /// more general one, never the other way round.
    /// </summary>
    public enum SessionCheckKind
    {
        None = 0,
        /// <summary>A new install has had the game in front for a while and read nothing that looks like dialogue.</summary>
        NoDialogue = 1,
        /// <summary>Lines with a speaker are read and none of them is in the pack.</summary>
        NoMatch = 2,
        /// <summary>The picture has been black for the whole threshold and no line was ever found: exclusive full screen.</summary>
        FullScreen = 3,
    }

    /// <summary>How much evidence a state needs; per game (GameRegionProfile), 0 switches a state off.</summary>
    public readonly record struct SessionCheckThresholds(int BlackSeconds, int UnmatchedReads, int QuietSeconds)
    {
        public static SessionCheckThresholds From(GameRegionProfile profile) =>
            new(profile.CheckBlackSeconds, profile.CheckUnmatchedReads, profile.CheckQuietSeconds);
    }

    /// <param name="LocalGameWindow">False for a cloud session ("continue anyway"): a black stream is not full screen.</param>
    /// <param name="FirstLinePending">The setup finished and no line has ever shown on this install.</param>
    public readonly record struct SessionCheckContext(bool Translating, bool LocalGameWindow, bool FirstLinePending);

    /// <summary>The state on the card and what keeps it there.</summary>
    /// <param name="HitsAtRaise">Matched lines and pack hits when the state was raised; one more clears it.</param>
    /// <param name="Dismissed">States the player acted on in this run of translation (bit per kind); they do not come back.</param>
    /// <param name="ReadsAtRaise">Reads with any text when the state was raised; one more proves the capture works.</param>
    public readonly record struct SessionCheckState(SessionCheckKind Kind, int HitsAtRaise, int Dismissed, int ReadsAtRaise = 0)
    {
        public bool IsDismissed(SessionCheckKind kind) => (Dismissed & (1 << (int)kind)) != 0;

        /// <summary>The player used the card's action: the state goes and stays away until translation starts again.</summary>
        public SessionCheckState Dismiss() =>
            Kind == SessionCheckKind.None ? this : new SessionCheckState(SessionCheckKind.None, 0, Dismissed | (1 << (int)Kind));
    }

    /// <summary>
    /// Decides the session check from the reading loop's evidence. A state is raised only on its own evidence, then
    /// held: it goes when a line matches, when what it says is disproved, or when the player acts, so it cannot
    /// flap from one refresh to the next.
    /// </summary>
    public static class SessionCheckModel
    {
        public static SessionCheckState Next(SessionCheckState current, in SessionEvidenceSnapshot e,
                                             SessionCheckThresholds t, SessionCheckContext c)
        {
            if (!c.Translating) return default;

            SessionCheckKind kind = current.Kind;
            int hits = e.MatchedReads + e.PackHits;
            if (kind != SessionCheckKind.None && (hits > current.HitsAtRaise || Disproved(current, e, t, c)))
                kind = SessionCheckKind.None;

            SessionCheckKind found = Evidence(e, t, c);
            if (found > kind && !current.IsDismissed(found))
                return new SessionCheckState(found, hits, current.Dismissed, e.TextReads);
            return kind == current.Kind ? current : new SessionCheckState(kind, 0, current.Dismissed);
        }

        /// <summary>The most direct cause the evidence supports right now, or None.</summary>
        public static SessionCheckKind Evidence(in SessionEvidenceSnapshot e, SessionCheckThresholds t, SessionCheckContext c)
        {
            // An unbroken black run, before anything was ever found in this run of translation. A splash window seen
            // first does not rule it out (the game goes full screen after it); a story's fade to black comes after
            // lines were found, so it does.
            if (t.BlackSeconds > 0 && c.LocalGameWindow && e.MatchedReads + e.PackHits == 0 && e.BlackSeconds >= t.BlackSeconds)
                return SessionCheckKind.FullScreen;
            if (t.UnmatchedReads > 0 && e.UnmatchedRun >= t.UnmatchedReads)
                return SessionCheckKind.NoMatch;
            if (t.QuietSeconds > 0 && c.FirstLinePending && e.InFrontSeconds >= t.QuietSeconds && e.DialogueReads + e.PackHits == 0)
                return SessionCheckKind.NoDialogue;
            return SessionCheckKind.None;
        }

        private static bool Disproved(SessionCheckState shown, in SessionEvidenceSnapshot e, SessionCheckThresholds t, SessionCheckContext c) => shown.Kind switch
        {
            // As much unbroken picture as it took black to raise it: one frame that comes through while the display
            // mode changes proves nothing, and the card would flap with it. Text read since (a line found or not,
            // the same line again, a HUD number) does prove the capture works, whatever fades cut the picture short.
            SessionCheckKind.FullScreen => e.PictureSeconds >= t.BlackSeconds || e.TextReads > shown.ReadsAtRaise || !c.LocalGameWindow,
            SessionCheckKind.NoDialogue => e.DialogueReads + e.PackHits > 0 || !c.FirstLinePending,
            _ => false,
        };
    }
}
