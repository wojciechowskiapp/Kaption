using System;
using System.Collections.Generic;
using System.Diagnostics;
using GI_Subtitles.Services.Detection;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// Which supported game is running right now: one process-list pass matched against every profile's process
    /// names, in profile order. For UI moments only (setup, opening the Game page); never on a timer or the
    /// capture path.
    /// </summary>
    public static class GameDetection
    {
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
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in all)
            {
                try { names.Add(p.ProcessName); }
                catch { /* exited while listing */ }
                finally { p.Dispose(); }
            }
            return Match(names);
        }

        /// <summary>The first profile (in UI order) with a process name in <paramref name="running"/>.</summary>
        public static string Match(ICollection<string> running)
        {
            if (running == null || running.Count == 0) return null;
            foreach (var profile in GameRegionProfile.RegisteredProfiles)
                foreach (string name in profile.ProcessNames ?? Array.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(name) && running.Contains(name))
                        return profile.GameId;
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
