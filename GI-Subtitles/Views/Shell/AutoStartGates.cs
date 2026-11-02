using GI_Subtitles.Services.Automation;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>What the eight start gates look at, read without opening any window.</summary>
    public readonly record struct GateSnapshot(
        bool SessionActive,
        bool InitialSyncRunning,
        bool EngineReady,
        bool MatcherReady,
        bool RegionSet,
        bool GameRunning,
        bool DisplayModeTipPending,
        bool OverlayOverlapsRegion);

    /// <summary>The silent side of the Start button's gates, for auto-start. Same order as MainWindow's TryGate* chain.</summary>
    public static class AutoStartGates
    {
        public static StartGate FirstClosed(in GateSnapshot s) =>
            !s.SessionActive ? StartGate.Session
            : s.InitialSyncRunning ? StartGate.InitialSync
            : !s.EngineReady ? StartGate.Engine
            : !s.MatcherReady ? StartGate.Matcher
            : !s.RegionSet ? StartGate.Region
            : !s.GameRunning ? StartGate.GameRunning
            : s.DisplayModeTipPending ? StartGate.DisplayModeTip
            : s.OverlayOverlapsRegion ? StartGate.OverlayOverlap
            : StartGate.None;

        /// <summary>
        /// "Czekam na grę" on Start, the chip and the tray: auto-start is armed and only the game in front is missing.
        /// Any other closed gate shows its own state (reader starting, pack missing, ...).
        /// </summary>
        public static bool IsWaitingForGame(AutoStartState state, StartGate gate) =>
            state == AutoStartState.WaitingForGame || (state == AutoStartState.Blocked && gate == StartGate.GameRunning);
    }
}
