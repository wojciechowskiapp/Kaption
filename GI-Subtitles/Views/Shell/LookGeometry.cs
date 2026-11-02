using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Rendering;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// Where things are on the user's screen, in the overlay's own units (DIPs), for Wygląd napisów's miniature: the
    /// frame (the game window when it is open, else the monitor holding the saved dialogue area, else the primary), the
    /// monitor the overlay centres on, and the dialogue area itself.
    /// </summary>
    public sealed record LookScene(Rect Frame, Rect Monitor, Rect Area, string Label, bool IsGameWindow)
    {
        /// <summary>Monitor 1920×1080 with a Genshin-like dialogue area: the picture before anything is saved.</summary>
        public static LookScene Fallback(string game) => FromProfile(new Rect(0, 0, 1920, 1080), game, string.Empty);

        public static LookScene FromProfile(Rect monitor, string game, string label)
        {
            var p = GameRegionProfile.Get(game);
            var area = new Rect(monitor.X + p.DialogueXPct * monitor.Width, monitor.Y + p.DialogueYPct * monitor.Height,
                p.DialogueWPct * monitor.Width, p.DialogueHPct * monitor.Height);
            return new LookScene(monitor, monitor, area, label, false);
        }
    }

    public static class LookGeometry
    {
        /// <summary>
        /// The overlay window and panel as the app places them, in screen DIPs (MainWindow.UpdateWindowPosition and
        /// UpdateWindowHeightAndTopNow): the window is the dialogue area plus 200 wide, centred on the monitor plus the
        /// horizontal offset; vertically the saved offset from the area's top, growing away from the dialogue.
        /// </summary>
        public static Rect PanelRect(LookScene scene, int padVertical, int padHorizontal, Size panel, double referenceWindowHeight)
        {
            double windowHeight = panel.Height + OverlayStyle.WindowMargin * 2;
            double windowTop = OverlayPlacement.WindowTop(scene.Area.Top, scene.Area.Height, padVertical, referenceWindowHeight, windowHeight);
            windowTop = Math.Max(scene.Monitor.Top, Math.Min(windowTop, scene.Monitor.Bottom - windowHeight));
            double centreX = scene.Monitor.Left + scene.Monitor.Width / 2 + padHorizontal;
            return new Rect(centreX - panel.Width / 2, windowTop + OverlayStyle.WindowMargin, panel.Width, panel.Height);
        }

        /// <summary>The width the app lets the subtitle text take (DefaultSubtitleLayoutEngine's rule).</summary>
        public static double TextWidth(LookScene scene, int maxOverlayWidth)
        {
            double window = scene.Area.Width + 200 - 32;
            return maxOverlayWidth > 0 ? Math.Min(maxOverlayWidth, window) : window;
        }

        /// <summary>
        /// The scene from the saved dialogue area (physical pixels, "x,y,w,h"), the app's scale and, when the game is open,
        /// its window (physical pixels). Monitor labels use the monitor's own pixels: "Monitor 2 · 2560×1440".
        /// </summary>
        public static LookScene Compute(string region, double scale, string game, Rect? gameWindowPx,
                                        Func<string, string, string> L)
        {
            if (scale <= 0) scale = 1;
            System.Windows.Forms.Screen screen;
            Rect area;
            if (CaptureRegionValidator.TryParse(region, out int x, out int y, out int w, out int h) && w > 0 && h > 0)
            {
                screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x + w / 2, y + h / 2));
                area = new Rect(x / scale, y / scale, w / scale, h / scale);
            }
            else
            {
                screen = System.Windows.Forms.Screen.PrimaryScreen;
                area = Rect.Empty;
            }
            double monitorScale = MonitorScale(screen);
            var b = screen.Bounds;
            var monitor = new Rect(b.Left / monitorScale, b.Top / monitorScale, b.Width / monitorScale, b.Height / monitorScale);
            string digits = new string((screen.DeviceName ?? string.Empty).Where(char.IsDigit).ToArray());
            string label = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                L("shell_Region_Monitor_Format", "Monitor {0} · {1}×{2}"), int.TryParse(digits, out int n) ? n : 1, b.Width, b.Height);
            var scene = area.IsEmpty ? LookScene.FromProfile(monitor, game, label) : new LookScene(monitor, monitor, area, label, false);
            if (gameWindowPx is Rect g && g.Width > 200 && g.Height > 150)
            {
                var frame = new Rect(g.X / monitorScale, g.Y / monitorScale, g.Width / monitorScale, g.Height / monitorScale);
                string gameLabel = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    L("look_Frame_Game_Format", "Game window · {0}×{1}"), (int)g.Width, (int)g.Height);
                scene = scene with { Frame = frame, Label = gameLabel, IsGameWindow = true };
            }
            return scene;
        }

        private static double MonitorScale(System.Windows.Forms.Screen screen)
        {
            try { return MainWindow.GetScaleForScreen(screen); }
            catch (Exception) { return 1.0; }
        }

        /// <summary>
        /// The open game's window in physical pixels, or null: the selected game's process with a visible, not minimised
        /// main window. A process listing, so call it off the UI thread and only when the page shows.
        /// </summary>
        public static Rect? FindGameWindow(string game)
        {
            try
            {
                foreach (string name in GameRegionProfile.Get(game)?.ProcessNames ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        using (p)
                        {
                            IntPtr hwnd;
                            try { hwnd = p.MainWindowHandle; } catch { continue; }
                            if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd)) continue;
                            if (DwmGetWindowAttribute(hwnd, DwmaExtendedFrameBounds, out RECT r, Marshal.SizeOf<RECT>()) != 0
                                && !GetWindowRect(hwnd, out r)) continue;
                            if (r.Right - r.Left > 200 && r.Bottom - r.Top > 150)
                                return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // No game window: the monitor is the frame.
            }
            return null;
        }

        private const int DwmaExtendedFrameBounds = 9;
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    }
}
