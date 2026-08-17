using System;
using System.Linq;

namespace GI_Subtitles.Services.Detection
{
    /// <summary>
    /// Separates the decision to capture from the decision to keep Kaption's
    /// overlay visible. Window titles alone are not trustworthy: desktop and
    /// several shell windows expose an empty title, while a browser tab can
    /// contain a game's name without being the game.
    /// </summary>
    internal enum ForegroundTarget
    {
        Game,
        Kaption,
        Other,
    }

    internal static class GameForegroundClassifier
    {
        internal static ForegroundTarget Classify(
            uint foregroundPid,
            uint kaptionPid,
            string processName,
            bool isKnownGameProcessId,
            string windowTitle,
            GameRegionProfile profile,
            bool cloudSessionBypass,
            bool developmentBypass)
        {
            if (foregroundPid != 0 && foregroundPid == kaptionPid)
                return ForegroundTarget.Kaption;

            if (developmentBypass)
                return ForegroundTarget.Game;

            string normalizedProcess = NormalizeProcessName(processName);
            // Process-name lookup may be denied when the game is elevated.
            // Use the PID captured by the explicit start gate only as that
            // lookup fallback. If a different process now owns a reused PID,
            // its available non-game name must win.
            if (isKnownGameProcessId && string.IsNullOrEmpty(normalizedProcess))
                return ForegroundTarget.Game;
            if (profile?.ProcessNames?.Any(name =>
                    string.Equals(NormalizeProcessName(name), normalizedProcess,
                        StringComparison.OrdinalIgnoreCase)) == true)
            {
                return ForegroundTarget.Game;
            }

            // Cloud gaming is an explicit, session-only user choice. Only in
            // that mode may a known cloud host or the expected game title stand
            // in for a local game process.
            if (cloudSessionBypass &&
                (IsKnownCloudHost(normalizedProcess, windowTitle) ||
                 profile?.WindowTitles?.Any(title => Contains(windowTitle, title)) == true))
            {
                return ForegroundTarget.Game;
            }

            // Unknown/custom games have no registered process. Preserve their
            // title-based compatibility without weakening registered profiles.
            bool hasRegisteredProcesses = profile?.ProcessNames?.Any(name => !string.IsNullOrWhiteSpace(name)) == true;
            if (!hasRegisteredProcesses &&
                profile?.WindowTitles?.Any(title => Contains(windowTitle, title)) == true)
            {
                return ForegroundTarget.Game;
            }

            return ForegroundTarget.Other;
        }

        private static string NormalizeProcessName(string processName)
        {
            string value = (processName ?? string.Empty).Trim();
            return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? value.Substring(0, value.Length - 4)
                : value;
        }

        private static bool IsKnownCloudHost(string processName, string windowTitle)
            => Contains(processName, "geforcenow") || Contains(windowTitle, "geforce now") ||
               Contains(processName, "boosteroid") || Contains(windowTitle, "boosteroid") ||
               Contains(windowTitle, "xbox cloud gaming") || Contains(windowTitle, "xcloud");

        private static bool Contains(string value, string expected)
            => !string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(expected) &&
               value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
