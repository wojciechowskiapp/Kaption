using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace GI_Subtitles.Services.Detection
{
    /// <summary>
    /// Whether a process is the selected game. The process name decides, except for a profile with
    /// <see cref="GameRegionProfile.RequiresWindowTitle"/> (Wuthering Waves runs as Unreal's generic
    /// Client-Win64-Shipping): then one of the process's visible windows must also carry one of the game's titles.
    /// Every detection path (start gate, auto-start, foreground, window finder, running-game hint) asks here.
    /// </summary>
    public static class GameProcessMatch
    {
        /// <summary>A visible top-level window: its owning process and its title.</summary>
        public readonly record struct TopWindow(uint ProcessId, string Title);

        public static bool NameMatches(GameRegionProfile profile, string processName)
        {
            string name = Normalize(processName);
            if (profile?.ProcessNames == null || name.Length == 0) return false;
            foreach (string candidate in profile.ProcessNames)
                if (!string.IsNullOrWhiteSpace(candidate) && string.Equals(Normalize(candidate), name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>The title contains one of <paramref name="titles"/> (case-insensitive).</summary>
        public static bool TitleMatches(IReadOnlyCollection<string> titles, string windowTitle)
        {
            if (titles == null || string.IsNullOrWhiteSpace(windowTitle)) return false;
            foreach (string title in titles)
                if (!string.IsNullOrWhiteSpace(title) && windowTitle.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        /// <summary>The name matches and, when the profile requires it, so does the title of the window in question.</summary>
        public static bool Matches(GameRegionProfile profile, string processName, string windowTitle) =>
            NameMatches(profile, processName) && (!profile.RequiresWindowTitle || TitleMatches(profile.WindowTitles, windowTitle));

        /// <summary>
        /// The ids in <paramref name="pids"/> that own a visible top-level window whose title contains one of
        /// <paramref name="titles"/>. <paramref name="windows"/> is for tests; by default the desktop's windows are
        /// enumerated once, with no process handles, so an elevated game is found too.
        /// </summary>
        public static HashSet<uint> WithTitledWindow(IReadOnlyCollection<string> titles, IReadOnlyCollection<uint> pids,
                                                     IEnumerable<TopWindow> windows = null)
        {
            var found = new HashSet<uint>();
            if (pids == null || pids.Count == 0 || titles == null || titles.Count == 0) return found;
            var wanted = new HashSet<uint>(pids);
            if (windows != null)
            {
                foreach (var w in windows)
                    if (wanted.Contains(w.ProcessId) && TitleMatches(titles, w.Title)) found.Add(w.ProcessId);
                return found;
            }
            var buffer = new StringBuilder(256);
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (!wanted.Contains(pid) || found.Contains(pid)) return true;
                buffer.Clear();
                GetWindowText(hWnd, buffer, buffer.Capacity);
                if (TitleMatches(titles, buffer.ToString())) found.Add(pid);
                return found.Count < wanted.Count;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>The ids from a by-name listing that really are the game: all of them unless a title is required.</summary>
        public static HashSet<uint> GameProcesses(GameRegionProfile profile, IReadOnlyCollection<uint> byName,
                                                  IEnumerable<TopWindow> windows = null)
        {
            if (byName == null) return new HashSet<uint>();
            return profile?.RequiresWindowTitle == true
                ? WithTitledWindow(profile.WindowTitles, byName, windows)
                : new HashSet<uint>(byName);
        }

        private static string Normalize(string processName)
        {
            string value = (processName ?? string.Empty).Trim();
            return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value.Substring(0, value.Length - 4) : value;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    }
}
