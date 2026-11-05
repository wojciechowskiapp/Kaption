using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using GI_Subtitles.Services.Detection;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// "Is the game running, is it in front?" for auto-start, cheap enough to ask every second off the UI thread.
    /// Listing every process costs tens of milliseconds (85 ms with 850 processes), so the common path asks Windows
    /// about the foreground window's process and the one game process already seen; a full listing runs only every few
    /// seconds while the game has not been seen yet. Not thread-safe: one probe at a time.
    /// </summary>
    public sealed class GameProbe
    {
        public static readonly TimeSpan FullScanInterval = TimeSpan.FromSeconds(5);

        private uint _gamePid;
        private DateTime _lastFullScanUtc = DateTime.MinValue;
        private string _namesKey;

        public readonly record struct Result(bool Running, bool Foreground);

        /// <summary>The game process the last probe saw (0 when none).</summary>
        public uint GamePid => _gamePid;

        /// <param name="processNames">The selected game's process names (GameRegionProfile.ProcessNames).</param>
        /// <param name="requiredTitles">For a generic executable (GameRegionProfile.RequiresWindowTitle): a process is the
        /// game only while one of its windows has one of these titles. Null when the name alone decides.</param>
        public Result Probe(IReadOnlyCollection<string> processNames, DateTime nowUtc, IReadOnlyCollection<string> requiredTitles = null)
        {
            if (processNames == null || processNames.Count == 0) return default;
            string key = string.Join("|", processNames) + "#" + (requiredTitles == null ? "" : string.Join("|", requiredTitles));
            if (!string.Equals(key, _namesKey, StringComparison.OrdinalIgnoreCase))
            {
                // Another game was selected: forget the old one's process and look again at once.
                _namesKey = key;
                _gamePid = 0;
                _lastFullScanUtc = DateTime.MinValue;
            }
            IntPtr fg = GetForegroundWindow();
            uint fgPid = ProcessIdOf(fg);
            // The window in front is a game window: the game's executable and, when required, the game's title.
            bool GameInFront() => fgPid != 0 && Matches(ImageNameOf(fgPid), processNames)
                                  && (requiredTitles == null || GameProcessMatch.TitleMatches(requiredTitles, TitleOf(fg)));

            // The game seen before: still alive?
            if (_gamePid != 0)
            {
                bool? alive = IsAlive(_gamePid);
                // A live process under that id must still be the game: Windows reuses the ids of exited processes.
                if (alive == true && ImageNameOf(_gamePid) is string image && !Matches(image, processNames)) alive = false;
                if (alive == true) return new Result(true, fgPid == _gamePid || GameInFront());
                if (alive == false) _gamePid = 0; // exited, or the id now belongs to something else
                // null: Windows will not open it (a protected process). Trust the last listing until the next one is due.
                else if (nowUtc - _lastFullScanUtc < FullScanInterval) return new Result(true, fgPid == _gamePid);
            }

            if (GameInFront())
            {
                _gamePid = fgPid;
                return new Result(true, true);
            }

            // Not seen yet, or the game protects its process: list processes now and then.
            if (nowUtc - _lastFullScanUtc < FullScanInterval) return default;
            _lastFullScanUtc = nowUtc;
            var pids = ListGameProcesses(processNames);
            if (requiredTitles != null && pids.Count > 0) pids = GameProcessMatch.WithTitledWindow(requiredTitles, pids);
            if (pids.Count == 0)
            {
                _gamePid = 0;
                return default;
            }
            if (_gamePid == 0 || !pids.Contains(_gamePid))
                foreach (uint pid in pids) { _gamePid = pid; break; }
            return new Result(true, pids.Contains(fgPid));
        }

        public static bool Matches(string imageName, IReadOnlyCollection<string> processNames)
        {
            if (string.IsNullOrEmpty(imageName)) return false;
            foreach (string n in processNames)
                if (!string.IsNullOrWhiteSpace(n) && string.Equals(n, imageName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The executable name (no ".exe") of the window in front, or null when there is none or Windows will not say.</summary>
        public static string ForegroundImageName()
        {
            uint pid = ProcessIdOf(GetForegroundWindow());
            return pid == 0 ? null : ImageNameOf(pid);
        }

        private static HashSet<uint> ListGameProcesses(IReadOnlyCollection<string> processNames)
        {
            var pids = new HashSet<uint>();
            foreach (string name in processNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                Process[] processes = Array.Empty<Process>();
                try { processes = Process.GetProcessesByName(name); }
                catch { /* listing failed: treat as not running this round */ }
                foreach (var p in processes)
                {
                    try { pids.Add((uint)p.Id); }
                    catch { /* exited while listing */ }
                    finally { p.Dispose(); }
                }
            }
            return pids;
        }

        private static uint ProcessIdOf(IntPtr window)
        {
            if (window == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(window, out uint pid);
            return pid;
        }

        private static string TitleOf(IntPtr window)
        {
            var sb = new StringBuilder(256);
            GetWindowText(window, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>The process's file name without ".exe", or null when Windows will not say.</summary>
        private static string ImageNameOf(uint pid)
        {
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? Path.GetFileNameWithoutExtension(sb.ToString()) : null;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>True running, false exited, null when Windows will not open the process.</summary>
        private static bool? IsAlive(uint pid)
        {
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == ErrorInvalidParameter ? false : null;
            try
            {
                return GetExitCodeProcess(h, out uint code) ? code == StillActive : null;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint StillActive = 259;
        private const int ErrorInvalidParameter = 87; // no such process

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    }
}
