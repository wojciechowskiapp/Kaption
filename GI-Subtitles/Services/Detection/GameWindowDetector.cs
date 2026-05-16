using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GI_Subtitles.Common;

namespace GI_Subtitles.Services.Detection
{
    /// <summary>
    /// Finds a running game window by process name or title using Win32 APIs,
    /// and provides ratio-based fallback region calculation with aspect-ratio correction.
    /// </summary>
    public static class GameWindowDetector
    {
        // ── Win32 P/Invoke ──

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        /// <summary>
        /// Information about a detected game window: its handle, screen-space client
        /// area coordinates, and window title.
        /// </summary>
        public struct WindowInfo
        {
            /// <summary>Win32 window handle.</summary>
            public IntPtr Handle;
            /// <summary>Client area left edge in screen coordinates.</summary>
            public int GameX;
            /// <summary>Client area top edge in screen coordinates.</summary>
            public int GameY;
            /// <summary>Client area width in pixels.</summary>
            public int GameW;
            /// <summary>Client area height in pixels.</summary>
            public int GameH;
            /// <summary>Window title text at the time of detection.</summary>
            public string Title;
        }

        /// <summary>
        /// Attempt to find a running game window that matches the given profile.
        /// Tries process name matching first (more reliable), then falls back to
        /// window title substring matching. Returns null if no matching window is found.
        /// </summary>
        public static WindowInfo? FindGameWindow(GameRegionProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            // Build a set of target process IDs from profile's process names
            var targetPids = new HashSet<uint>();
            if (profile.ProcessNames != null && profile.ProcessNames.Length > 0)
            {
                try
                {
                    foreach (var proc in Process.GetProcesses())
                    {
                        try
                        {
                            foreach (var name in profile.ProcessNames)
                            {
                                if (string.Equals(proc.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                                {
                                    targetPids.Add((uint)proc.Id);
                                    break;
                                }
                            }
                        }
                        finally
                        {
                            proc.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn($"Process enumeration failed: {ex.Message}");
                }
            }

            // Strategy 1: match by process ID (reliable)
            if (targetPids.Count > 0)
            {
                var result = FindWindowByPids(targetPids);
                if (result != null)
                {
                    Logger.Log.Debug($"Found game window by process: \"{result.Value.Title}\" " +
                                     $"at ({result.Value.GameX},{result.Value.GameY}) " +
                                     $"{result.Value.GameW}x{result.Value.GameH}");
                    return result;
                }
            }

            // Strategy 2: match by window title substring (fallback)
            if (profile.WindowTitles != null && profile.WindowTitles.Length > 0)
            {
                var result = FindWindowByTitles(profile.WindowTitles);
                if (result != null)
                {
                    Logger.Log.Debug($"Found game window by title: \"{result.Value.Title}\" " +
                                     $"at ({result.Value.GameX},{result.Value.GameY}) " +
                                     $"{result.Value.GameW}x{result.Value.GameH}");
                    return result;
                }
            }

            Logger.Log.Warn($"Game window not found for profile \"{profile.GameId}\"");
            return null;
        }

        /// <summary>
        /// Calculate dialogue and answer regions from the profile's ratio data,
        /// applying aspect-ratio correction for non-16:9 displays (ultrawide, 16:10).
        /// Returns formatted "x,y,w,h" strings in screen-absolute coordinates.
        /// </summary>
        public static (string dialogueRegion, string answerRegion) CalculateFromRatios(
            GameRegionProfile profile, int gameX, int gameY, int gameW, int gameH)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            // Calculate 16:9 reference area within the actual game window.
            // This handles ultrawide (21:9, 32:9) and taller (16:10, 4:3) aspect ratios
            // by finding the largest 16:9 rect centered in the window.
            double aspect = (double)gameW / gameH;
            const double target = 16.0 / 9.0;

            int refX = 0, refY = 0, refW = gameW, refH = gameH;

            if (aspect > target + 0.01)
            {
                // Ultrawide: pillarbox — game renders 16:9 in center, black bars on sides
                refW = (int)(gameH * target);
                refX = (gameW - refW) / 2;
            }
            else if (aspect < target - 0.01)
            {
                // Taller (16:10, 4:3): letterbox — black bars top/bottom
                refH = (int)(gameW / target);
                refY = (gameH - refH) / 2;
            }

            // Apply ratios to the 16:9 reference area, then offset to screen coords
            int dX = gameX + refX + (int)(profile.DialogueXPct * refW);
            int dY = gameY + refY + (int)(profile.DialogueYPct * refH);
            int dW = (int)(profile.DialogueWPct * refW);
            int dH = (int)(profile.DialogueHPct * refH);

            int aX = gameX + refX + (int)(profile.AnswerXPct * refW);
            int aY = gameY + refY + (int)(profile.AnswerYPct * refH);
            int aW = (int)(profile.AnswerWPct * refW);
            int aH = (int)(profile.AnswerHPct * refH);

            string dialogueRegion = $"{dX},{dY},{dW},{dH}";
            string answerRegion = $"{aX},{aY},{aW},{aH}";

            Logger.Log.Debug($"Ratio-based regions (ref {refW}x{refH} at +{refX},+{refY}): " +
                             $"dialogue={dialogueRegion}, answer={answerRegion}");

            return (dialogueRegion, answerRegion);
        }

        // ── Private helpers ──

        /// <summary>
        /// Enumerate all visible top-level windows and return the first one whose
        /// owning process ID is in the target set.
        /// </summary>
        private static WindowInfo? FindWindowByPids(HashSet<uint> targetPids)
        {
            WindowInfo? found = null;

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd))
                    return true; // continue

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (!targetPids.Contains(pid))
                    return true; // continue

                var info = GetWindowClientInfo(hWnd);
                if (info != null && info.Value.GameW > 100 && info.Value.GameH > 100)
                {
                    found = info;
                    return false; // stop enumeration
                }
                return true;
            }, IntPtr.Zero);

            return found;
        }

        /// <summary>
        /// Enumerate all visible top-level windows and return the first one whose
        /// title contains any of the target substrings (case-insensitive).
        /// </summary>
        private static WindowInfo? FindWindowByTitles(string[] titles)
        {
            WindowInfo? found = null;

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd))
                    return true;

                var sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, sb.Capacity);
                string windowTitle = sb.ToString();

                if (string.IsNullOrEmpty(windowTitle))
                    return true;

                foreach (var title in titles)
                {
                    if (windowTitle.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var info = GetWindowClientInfo(hWnd);
                        if (info != null && info.Value.GameW > 100 && info.Value.GameH > 100)
                        {
                            found = info;
                            return false; // stop
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);

            return found;
        }

        /// <summary>
        /// Get the client area of a window in screen coordinates.
        /// Returns null if the client area is degenerate (0 width/height).
        /// </summary>
        private static WindowInfo? GetWindowClientInfo(IntPtr hWnd)
        {
            if (!GetClientRect(hWnd, out RECT clientRect))
                return null;

            int w = clientRect.Right - clientRect.Left;
            int h = clientRect.Bottom - clientRect.Top;
            if (w <= 0 || h <= 0)
                return null;

            // Convert client (0,0) to screen coordinates
            var pt = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hWnd, ref pt))
                return null;

            // Get window title for logging
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);

            return new WindowInfo
            {
                Handle = hWnd,
                GameX = pt.X,
                GameY = pt.Y,
                GameW = w,
                GameH = h,
                Title = sb.ToString(),
            };
        }
    }
}
