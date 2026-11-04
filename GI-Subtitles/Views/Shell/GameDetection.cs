using System;
using System.Collections.Generic;
using System.Diagnostics;
using GI_Subtitles.Services.Detection;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// Which supported game is running right now: one process-list pass matched against every profile's process
    /// names, in profile order. A profile with RequiresWindowTitle (a generic executable) also needs one of its titles
    /// on a window of that process. For UI moments only (setup, opening the Game page); never on a timer or the
    /// capture path.
    /// </summary>
    public static class GameDetection
    {
        /// <summary>A running process: its name (no ".exe") and id.</summary>
        public readonly record struct RunningProcess(string Name, uint Id);

        public static string FindRunningGame()
        {
            Process[] all;
            try
            {
                all = Process.GetProcesses();
            }
            catch (Exception ex)
            {
                Logger.Log.Info($"GameDetection: process list unavailable: {ex.Message}");
                return null;
            }
            var running = new List<RunningProcess>(all.Length);
            foreach (var p in all)
            {
                try { running.Add(new RunningProcess(p.ProcessName, (uint)p.Id)); }
                catch { /* exited while listing */ }
                finally { p.Dispose(); }
            }
            return Match(running);
        }

        /// <summary>
        /// The first profile (in UI order) with a process in <paramref name="running"/>. <paramref name="windows"/> is
        /// for tests; by default a title-checked profile reads the desktop's windows.
        /// </summary>
        public static string Match(IReadOnlyCollection<RunningProcess> running, IEnumerable<GameProcessMatch.TopWindow> windows = null)
        {
            if (running == null || running.Count == 0) return null;
            foreach (var profile in GameRegionProfile.RegisteredProfiles)
            {
                var pids = new List<uint>();
                foreach (var p in running)
                    if (GameProcessMatch.NameMatches(profile, p.Name)) pids.Add(p.Id);
                if (pids.Count > 0 && GameProcessMatch.GameProcesses(profile, pids, windows).Count > 0)
                    return profile.GameId;
            }
            return null;
        }
    }

    /// <summary>Session facts the sign-in paths learn and the Start page shows.</summary>
    public static class SessionNotices
    {
        /// <summary>The last sign-in was refused because the plan is active on too many PCs.</summary>
        public static bool DeviceLimitReached { get; set; }
    }
}
