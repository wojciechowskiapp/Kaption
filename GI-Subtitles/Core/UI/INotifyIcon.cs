using GI_Subtitles.Properties;
using Microsoft.Win32;
using Screenshot;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Interop;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Security;
using System.Drawing;

namespace GI_Subtitles.Core.UI
{
    /// <summary>
    /// System tray notification icon management
    /// </summary>
    public class INotifyIcon
    {
        // Screen capture exclusion (Windows 10 2004+)
        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        System.Windows.Forms.ContextMenuStrip contextMenuStrip;
        ToolStripMenuItem fontSizeSelector;
        ToolStripMenuItem languageSelector;
        ToolStripMenuItem settingItem;
        ToolStripMenuItem feedbackItem;
        ToolStripMenuItem exitItem;
        private int Size = Config.Config.Get<int>("Size");
        private bool AutoStart = Config.Config.Get("AutoStart", false);
        public string[] Region = Config.Config.Get<string>("Region", "").Split(',');
        public string[] Region2 = Config.Config.Get<string>("Region2", "").Split(',');
        public string[] AnswerRegion = Config.Config.Get<string>("AnswerRegion", "").Split(',');
        string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
        double Scale = 1;
        public bool isContextMenuOpen = false;
        private Views.SettingsWindow data;

        public NotifyIcon InitializeNotifyIcon(double scale)
        {
            Scale = scale;
            NotifyIcon notifyIcon;
            contextMenuStrip = new ContextMenuStrip();
            // Localized tray menu texts (English fallback so a missing key
            // still shows readable text rather than stale Chinese defaults).
            string trayFontSize = GetLocalizedString("Tray_FontSize", "Font Size");
            string trayLanguage = GetLocalizedString("Tray_Language", "Language");
            // Settings entry renamed to "Open Kaption" since session 32 — it
            // opens the main window (which now persists instead of closing
            // the app on X). Old Tray_Settings key kept as a fallback for
            // install bases that haven't re-loaded resources yet.
            string trayOpenApp = GetLocalizedString("Tray_OpenApp",
                                    GetLocalizedString("Tray_Settings", "Open Kaption"));
            string trayExit = GetLocalizedString("Tray_Exit", "Exit");

            fontSizeSelector = new ToolStripMenuItem(trayFontSize);
            fontSizeSelector.DropDownItems.Add(CreateSizeItem("14"));
            fontSizeSelector.DropDownItems.Add(CreateSizeItem("16"));
            fontSizeSelector.DropDownItems.Add(CreateSizeItem("18"));
            fontSizeSelector.DropDownItems.Add(CreateSizeItem("20"));
            fontSizeSelector.DropDownItems.Add(CreateSizeItem("22"));
            fontSizeSelector.DropDownItems.Add(CreateSizeItem("24"));

            // Language selector submenu in tray area
            languageSelector = new ToolStripMenuItem(trayLanguage);
            languageSelector.DropDownItems.Add(CreateLanguageItem("English", "en-US"));
            languageSelector.DropDownItems.Add(CreateLanguageItem("Polski", "pl-PL"));
            languageSelector.DropDownItems.Add(CreateLanguageItem("日本語", "ja-JP"));
            languageSelector.DropDownItems.Add(CreateLanguageItem("简体中文", "zh-CN"));

            settingItem = new ToolStripMenuItem(trayOpenApp);
            string trayFeedback = GetLocalizedString("Tray_Feedback", "Send feedback\u2026");
            feedbackItem = new ToolStripMenuItem(trayFeedback);
            exitItem = new ToolStripMenuItem(trayExit);
            ToolStripMenuItem versionItem = new ToolStripMenuItem(version)
            {
                Enabled = false
            };
            settingItem.Click += (sender, e) =>
            {
                // Idempotent open. The tray menu used to call ShowDialog(),
                // which WPF rejects with "ShowDialog can only be called on a
                // hidden window" — crashing the app if the Settings window
                // was already visible (e.g. opened via the update-balloon
                // click or the Setup Wizard "Open Settings" button). Always
                // surface non-modally and just raise if already up.
                try
                {
                    if (data == null) return;
                    if (data.IsVisible)
                    {
                        if (data.WindowState == System.Windows.WindowState.Minimized)
                            data.WindowState = System.Windows.WindowState.Normal;
                        data.Activate();
                    }
                    else
                    {
                        data.Show();
                        data.Activate();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn($"Tray → Settings open failed: {ex.Message}");
                }
            };
            // Tray-spawned feedback dialog has no natural WPF owner (the Settings
            // window is not open when the tray menu is), so we force CenterScreen
            // and catch any failure — a broken dialog must not take down the app
            // on a right-click menu path.
            feedbackItem.Click += (sender, e) =>
            {
                try
                {
                    var dlg = new Views.SendFeedbackWindow
                    {
                        WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                        Topmost = true,
                    };
                    dlg.ShowDialog();
                }
                catch (Exception ex)
                {
                    Logger.Log.Error($"Tray feedback: opening dialog failed: {ex}");
                }
            };
            exitItem.Click += (sender, e) => { System.Windows.Application.Current.Shutdown(); };

            // Tray menu trimmed to the essentials in session 32. Font Size +
            // Language submenus were removed — both duplicate controls inside
            // the Settings window and bloat the right-click menu for a quick-
            // access that people rarely used. The ToolStripMenuItem builders
            // (fontSizeSelector / languageSelector) stay assigned in case other
            // code references them, but are not added to the visible strip.
            contextMenuStrip.Items.Add(versionItem);
            contextMenuStrip.Items.Add(new ToolStripSeparator());
            contextMenuStrip.Items.Add(settingItem);
            contextMenuStrip.Items.Add(feedbackItem);
            contextMenuStrip.Items.Add(new ToolStripSeparator());
            contextMenuStrip.Items.Add(exitItem);
            contextMenuStrip.Opening += ContextMenuStrip_Opening; // The menu is opened before triggering
            contextMenuStrip.Closed += ContextMenuStrip_Closed;   // The menu is closed after triggering
            Uri iconUri = new Uri("pack://application:,,,/Resources/kaption.ico");
            Stream iconStream = System.Windows.Application.GetResourceStream(iconUri).Stream;
            notifyIcon = new NotifyIcon
            {
                Icon = new Icon(iconStream),
                Visible = true,
                ContextMenuStrip = contextMenuStrip,
                // Tooltip shows tier + email + paid_until on hover. Pulled from
                // App.LicenseService.CurrentActivation via LicenseStatusFormatter.
                // 63 chars is the Windows NotifyIcon.Text limit on legacy paths;
                // we trim if needed. Refresh: hooked to ActivationStateChanged
                // below so a mid-session purchase reflects without restart.
                Text = BuildTrayTooltip(),
            };
            try
            {
                if (App.LicenseService != null)
                {
                    App.LicenseService.ActivationStateChanged += (_, __) =>
                    {
                        try { notifyIcon.Text = BuildTrayTooltip(); }
                        catch (Exception ex) { Logger.Log.Warn($"NotifyIcon: tooltip refresh failed: {ex.Message}"); }
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"NotifyIcon: could not subscribe to ActivationStateChanged: {ex.Message}");
            }
            SetAutoStart(AutoStart);
            _trayIcon = notifyIcon;
            return notifyIcon;
        }

        /// <summary>
        /// Build the tray-icon tooltip from the current license state. Returns
        /// "Kaption" alone when there's no signed-in session — keeps the
        /// tooltip useful even before login completes. Trimmed to 63 chars
        /// because the legacy NotifyIcon path on older Windows builds
        /// truncates anything longer.
        /// </summary>
        private static string BuildTrayTooltip()
        {
            try
            {
                var data = App.LicenseService?.CurrentActivation;
                string text = data == null
                    ? "Kaption"
                    : $"Kaption — {LicenseStatusFormatter.ShortLabel(data)}";
                if (text.Length > 63) text = text.Substring(0, 60) + "...";
                return text;
            }
            catch
            {
                return "Kaption";
            }
        }

        private string GetLocalizedString(string resourceKey, string fallback)
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app != null)
                {
                    var value = app.TryFindResource(resourceKey) as string;
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }
            catch (Exception e)
            {
                // ignore and fallback
                Logger.Log.Error($"Failed {e} to find localized string for {resourceKey}. Falling back to {fallback}.");
            }
            return fallback;
        }

        public void SetData(Views.SettingsWindow data)
        {
            this.data = data;
        }

        /// <summary>
        /// Shell_NotifyIcon balloon tip. Surfaces a Windows notification from
        /// the tray icon — used for updates and other out-of-band events the
        /// user needs to notice even with the Settings window closed.
        ///
        /// Click behaviour: <paramref name="onClick"/> fires when the user
        /// clicks the balloon itself (not a hidden click on the icon). We
        /// self-unsubscribe after a single fire so multiple calls don't stack
        /// handlers. Safe to call from any thread — we marshal back to the
        /// icon's synchronisation context.
        /// </summary>
        public void ShowBalloonTip(string title, string body, Action onClick = null)
        {
            try
            {
                if (_trayIcon == null) return;
                // Microsoft recommends 0 to use the system default (~5-10s on
                // Windows 10+). Explicit timeout would be ignored on modern
                // Windows anyway because toast notifications aren't timed.
                if (onClick != null)
                {
                    EventHandler handler = null;
                    handler = (s, e) =>
                    {
                        _trayIcon.BalloonTipClicked -= handler;
                        try { onClick(); }
                        catch (Exception ex) { Logger.Log.Warn($"Balloon click handler threw: {ex.Message}"); }
                    };
                    _trayIcon.BalloonTipClicked += handler;
                    // If the balloon times out without a click, drop the
                    // handler so we don't leak it across subsequent balloons.
                    EventHandler closed = null;
                    closed = (s, e) =>
                    {
                        _trayIcon.BalloonTipClicked -= handler;
                        _trayIcon.BalloonTipClosed -= closed;
                    };
                    _trayIcon.BalloonTipClosed += closed;
                }
                _trayIcon.ShowBalloonTip(0, title ?? string.Empty, body ?? string.Empty, ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"ShowBalloonTip failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Captured NotifyIcon reference for out-of-band callers (e.g. update
        /// service needs to fire a balloon). Set inside InitializeNotifyIcon.
        /// </summary>
        private NotifyIcon _trayIcon;

        /// <summary>
        /// Refresh tray menu texts based on current language resources
        /// </summary>
        public void RefreshMenuTexts()
        {
            if (contextMenuStrip == null || fontSizeSelector == null || settingItem == null || exitItem == null)
                return;

            try
            {
                // Update font size selector text
                string trayFontSize = GetLocalizedString("Tray_FontSize", "Font Size");
                fontSizeSelector.Text = trayFontSize;

                // Update language selector text
                if (languageSelector != null)
                {
                    string trayLanguage = GetLocalizedString("Tray_Language", "Language");
                    languageSelector.Text = trayLanguage;
                }

                // Update settings menu item text
                string traySettings = GetLocalizedString("Tray_Settings", "Settings");
                settingItem.Text = traySettings;

                // Update exit menu item text
                string trayExit = GetLocalizedString("Tray_Exit", "Exit");
                exitItem.Text = trayExit;
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Error refreshing tray menu texts: {ex.Message}");
            }
        }

        private void DateUpdate()
        {
            data.ShowDialog();
        }

        public void ChooseRegion()
        {
            try
            {
                // Close any stale diagnostic overlay up front — otherwise the
                // Ctrl+Shift+D rectangle from a prior viewing stays on screen
                // showing the OLD coordinates while the user draws the new
                // one. Same reason ShowRegionOverlay itself closes + reopens
                // on re-invoke.
                CloseRegionOverlayIfOpen();

                // Live overlap feedback while the user is still dragging:
                // the predicate projects where the subtitle overlay WILL land
                // for the proposed region, then asks the validator whether
                // that projection would overlap the proposed region. Returns
                // false silently if MainWindow isn't around (pre-startup or
                // test host) — colour stays the default safe green.
                var rect = Screenshot.Screenshot.GetRegion(BuildLiveOverlapPredicate());
                // User-cancel path (Esc / closed picker) returns Rect.Empty,
                // whose Width/Height are double.NegativeInfinity. Convert.ToInt32
                // on those throws OverflowException BEFORE the > 0 short-circuit
                // can save us — guard explicitly so cancel is a silent no-op.
                if (!IsCommittableRect(rect)) return;
                if (Convert.ToInt32(rect.Width) > 0 && Convert.ToInt32(rect.Height) > 0)
                {
                    Config.Config.Set("Region", $"{Convert.ToInt32(rect.TopLeft.X)},{Convert.ToInt32(rect.TopLeft.Y)},{Convert.ToInt32(rect.Width)},{Convert.ToInt32(rect.Height)}");
                    Region = Config.Config.Get<string>("Region").ToString().Split(',');
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"ChooseRegion failed: {ex}");
            }
        }

        /// <summary>
        /// True when <paramref name="r"/> represents a committable region —
        /// finite numbers in Int32 range and a positive area. Guards the
        /// three Choose*Region commit paths against <c>Rect.Empty</c>
        /// (returned by <see cref="Screenshot.Screenshot.GetRegion(System.Func{System.Windows.Rect, bool})"/>
        /// when the user cancels) and against any NaN / Infinity slipping in
        /// from a misbehaving DPI computation upstream. Centralised so all
        /// three overloads stay in sync.
        /// </summary>
        private static bool IsCommittableRect(System.Windows.Rect r)
        {
            if (r.IsEmpty) return false;
            if (double.IsNaN(r.X) || double.IsNaN(r.Y) ||
                double.IsNaN(r.Width) || double.IsNaN(r.Height)) return false;
            if (double.IsInfinity(r.X) || double.IsInfinity(r.Y) ||
                double.IsInfinity(r.Width) || double.IsInfinity(r.Height)) return false;
            if (r.Width <= 0 || r.Height <= 0) return false;
            // Int32 ceiling check — coordinates outside this range would
            // also throw OverflowException at Convert.ToInt32 time.
            const double max = int.MaxValue;
            const double min = int.MinValue;
            if (r.X < min || r.X > max || r.Y < min || r.Y > max) return false;
            if (r.Width > max || r.Height > max) return false;
            return true;
        }

        /// <summary>
        /// Build the Func&lt;Rect, bool&gt; passed into Screenshot.GetRegion
        /// for live overlap detection. Separated so the three ChooseRegion
        /// overloads (Region, Region2, AnswerRegion) share identical
        /// projection logic. Returns null when MainWindow isn't reachable —
        /// callers hand null to GetRegion which then skips feedback
        /// entirely, preserving the old code path.
        /// </summary>
        private Func<System.Windows.Rect, bool> BuildLiveOverlapPredicate()
        {
            var main = System.Windows.Application.Current?.MainWindow
                       as GI_Subtitles.Views.MainWindow;
            if (main == null) return null;
            double scale = Scale > 0 ? Scale : 1.0;

            return proposed =>
            {
                try
                {
                    // proposed is already in screen physical pixels
                    // (Screenshot.GetRegion converted before invoking us).
                    var overlayProjected = GI_Subtitles.Services.Validation.OverlayRegionValidator
                        .ProjectOverlayRectUsingConfig(
                            proposed,
                            GetScreenBoundsForPoint(proposed),
                            scale);
                    if (overlayProjected.IsEmpty) return false;
                    var inter = overlayProjected;
                    inter.Intersect(proposed);
                    return !inter.IsEmpty && inter.Width > 0 && inter.Height > 0;
                }
                catch
                {
                    return false;
                }
            };
        }

        /// <summary>
        /// Find the screen that contains the top-left of <paramref name="probe"/>
        /// and return its physical-pixel bounds. Used by the projection path
        /// so overlays land on the correct monitor's bounds for multi-monitor
        /// setups.
        /// </summary>
        private static System.Windows.Rect GetScreenBoundsForPoint(System.Windows.Rect probe)
        {
            try
            {
                var pt = new System.Drawing.Point((int)probe.X, (int)probe.Y);
                foreach (var s in System.Windows.Forms.Screen.AllScreens)
                {
                    if (s.Bounds.Contains(pt))
                        return new System.Windows.Rect(s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height);
                }
                var primary = System.Windows.Forms.Screen.PrimaryScreen;
                return new System.Windows.Rect(primary.Bounds.Left, primary.Bounds.Top, primary.Bounds.Width, primary.Bounds.Height);
            }
            catch
            {
                return new System.Windows.Rect(0, 0, 1920, 1080);
            }
        }

        public void ChooseRegion2()
        {
            try
            {
                CloseRegionOverlayIfOpen();
                var rect = Screenshot.Screenshot.GetRegion(BuildLiveOverlapPredicate());
                if (!IsCommittableRect(rect)) return;
                if (Convert.ToInt32(rect.Width) > 0 && Convert.ToInt32(rect.Height) > 0)
                {
                    Config.Config.Set("Region2", $"{Convert.ToInt32(rect.TopLeft.X)},{Convert.ToInt32(rect.TopLeft.Y)},{Convert.ToInt32(rect.Width)},{Convert.ToInt32(rect.Height)}");
                    Region2 = Config.Config.Get<string>("Region2").ToString().Split(',');
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"ChooseRegion2 failed: {ex}");
            }
        }

        public void ChooseAnswerRegion()
        {
            try
            {
                CloseRegionOverlayIfOpen();
                var rect = Screenshot.Screenshot.GetRegion(BuildLiveOverlapPredicate());
                if (!IsCommittableRect(rect)) return;
                if (Convert.ToInt32(rect.Width) > 0 && Convert.ToInt32(rect.Height) > 0)
                {
                    Config.Config.Set("AnswerRegion", $"{Convert.ToInt32(rect.TopLeft.X)},{Convert.ToInt32(rect.TopLeft.Y)},{Convert.ToInt32(rect.Width)},{Convert.ToInt32(rect.Height)}");
                    AnswerRegion = Config.Config.Get<string>("AnswerRegion").ToString().Split(',');
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"ChooseAnswerRegion failed: {ex}");
            }
        }

        private ToolStripMenuItem CreateSizeItem(string code)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(code)
            {
                Tag = code,
                CheckOnClick = true
            };
            item.CheckedChanged += SizeItem_CheckedChanged;
            if (Size == Convert.ToInt32(code))
            {
                item.Checked = true;
            }
            return item;
        }

        /// <summary>
        /// Create a language menu item for the tray language selector.
        /// </summary>
        /// <param name="displayName">Display text of the language.</param>
        /// <param name="cultureTag">Culture tag such as zh-CN / en-US / ja-JP.</param>
        /// <returns>Configured ToolStripMenuItem.</returns>
        private ToolStripMenuItem CreateLanguageItem(string displayName, string cultureTag)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(displayName)
            {
                Tag = cultureTag,
                CheckOnClick = true
            };
            // Initialize checked state from config BEFORE subscribing,
            // so setting Checked=true doesn't trigger the handler and overwrite config.
            string currentLang = Config.Config.Get("UILang", "en-US");
            if (string.Equals(currentLang, cultureTag, StringComparison.OrdinalIgnoreCase))
            {
                item.Checked = true;
            }

            item.CheckedChanged += LanguageItem_CheckedChanged;

            return item;
        }

        /// <summary>
        /// Handle language selection from the tray submenu.
        /// Ensures only one language is selected and propagates change to SettingsWindow.
        /// </summary>
        private void LanguageItem_CheckedChanged(object sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem selectedItem && selectedItem.Checked)
            {
                string cultureTag = selectedItem.Tag as string;
                if (string.IsNullOrEmpty(cultureTag))
                {
                    return;
                }

                // Uncheck other language items in the same submenu
                foreach (ToolStripMenuItem langItem in languageSelector.DropDownItems)
                {
                    if (!ReferenceEquals(langItem, selectedItem))
                    {
                        langItem.Checked = false;
                    }
                }

                // Persist to config
                Config.Config.Set("UILang", cultureTag);

                // Apply to settings window (which will update resources, tray texts, hotkeys, etc.)
                if (data != null)
                {
                    data.SetUILanguage(cultureTag);
                }
            }
        }

        private void SizeItem_CheckedChanged(object sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem selectedSize && selectedSize.Checked)
            {
                int newSize = Convert.ToInt32(selectedSize.Tag.ToString());
                if (Size != newSize)
                {
                    Size = newSize;

                    foreach (ToolStripMenuItem langItem in fontSizeSelector.DropDownItems)
                    {
                        if (langItem != selectedSize)
                        {
                            langItem.Checked = false;
                        }
                    }
                    Config.Config.Set("Size", Size);
                }
            }
        }

        private void SetAutoStart(bool autoStart)
        {
            try
            {
                string appPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);

                if (key == null)
                {
                    Logger.Log.Error("Failed to open registry key for auto-start");
                    return;
                }

                using (key)
                {
                    string existingValue = (string)key.GetValue(Process.GetCurrentProcess().ProcessName, null);
                    if (autoStart)
                    {
                        if (existingValue != appPath)
                        {
                            key.SetValue(Process.GetCurrentProcess().ProcessName, appPath);
                            Logger.Log.Info("Startup item added successfully!");
                        }
                    }
                    else
                    {
                        if (existingValue != null)
                        {
                            key.DeleteValue(Process.GetCurrentProcess().ProcessName, false);
                            Logger.Log.Info("Startup item removed!");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"SetAutoStart failed: {ex}");
            }
        }
        private void ContextMenuStrip_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            isContextMenuOpen = true;
        }

        private void ContextMenuStrip_Closed(object sender, ToolStripDropDownClosedEventArgs e)
        {
            isContextMenuOpen = false;
        }

        /// <summary>
        /// Reference to the currently-open Show-Region overlay (Ctrl+Shift+D
        /// result). Held so a new region pick can close the stale one before
        /// opening a fresh overlay — otherwise the old 10-second timer keeps
        /// the outdated rectangle on screen after the user redraws.
        /// </summary>
        private System.Windows.Window _activeRegionOverlayWindow;
        private System.Windows.Threading.DispatcherTimer _activeRegionOverlayTimer;

        public void ShowRegionOverlay()
        {
            ShowRegionOverlay(TimeSpan.FromSeconds(10));
        }

        /// <summary>
        /// Close any currently-displayed Show-Region overlay. Safe to call
        /// when nothing is open. Wired from region-change callers so the
        /// outdated rectangle doesn't linger after the user redraws.
        /// </summary>
        public void CloseRegionOverlayIfOpen()
        {
            try
            {
                _activeRegionOverlayTimer?.Stop();
                _activeRegionOverlayTimer = null;
                if (_activeRegionOverlayWindow != null)
                {
                    try { _activeRegionOverlayWindow.Close(); }
                    catch { /* best-effort */ }
                    _activeRegionOverlayWindow = null;
                }
            }
            catch { /* never crash on cleanup */ }
        }

        /// <summary>
        /// Shows detected regions on screen: dialogue (green) and answer (blue).
        /// When the main-window overlay bounds intersect any region, that region
        /// is drawn in RED with a filled intersection rectangle and a "⚠ Overlap"
        /// label — a quick visual diagnostic that explains the "OCR isn't working"
        /// failure mode without the user needing to start OCR first.
        /// </summary>
        public void ShowRegionOverlay(TimeSpan duration)
        {
            // Close any previously-open region overlay so a re-trigger with
            // new coords (e.g. after the user redrew the region via
            // Ctrl+Shift+R) replaces the old rectangle instead of stacking
            // two of them on screen with the old one still visible.
            CloseRegionOverlayIfOpen();

            if (Region == null || Region.Length < 4 || Region[1] == "0") return;
            int x = Convert.ToInt32(int.Parse(Region[0]) / Scale);
            int y = Convert.ToInt32(int.Parse(Region[1]) / Scale);
            int w = Convert.ToInt32(int.Parse(Region[2]) / Scale);
            int h = Convert.ToInt32(int.Parse(Region[3]) / Scale);
            Logger.Log.Debug($"x {x} y {y} w {w} h {h}");

            // Compute overlap ONCE up front so the visual treatment of every
            // region and the placement of the "⚠ Overlap" label stay consistent.
            // Screen-pixel rects are converted back to logical pixels inside
            // the drawing code (matching the /Scale divisions above) because
            // the host overlay Window is in WPF DIPs.
            var overlapCheck = ComputeOverlapForVisualOverlay();

            // Virtual screen coords (DIPs) — the overlay window spans every
            // monitor. When the primary monitor is NOT the top-left corner
            // of the virtual desktop (e.g. secondary extends left of
            // primary → VirtualScreenLeft is negative), the old Left=0/
            // Top=0 clipped the overlay to the primary and drew nothing on
            // the other monitor. Anchoring at VirtualScreenLeft/Top makes
            // the Canvas origin coincide with the top-left of the virtual
            // desktop, and canvas offsets subtract that origin so region/
            // answer rects land on the correct monitor.
            double virtualLeftDip = SystemParameters.VirtualScreenLeft;
            double virtualTopDip = SystemParameters.VirtualScreenTop;

            var overlay = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                Width = SystemParameters.VirtualScreenWidth,
                Height = SystemParameters.VirtualScreenHeight,
                Left = virtualLeftDip,
                Top = virtualTopDip,
            };

            var canvas = new Canvas();

            // Dialogue region — green when safe, red when the overlay overlaps it.
            bool dialogueOverlaps = overlapCheck != null
                                    && overlapCheck.HasOverlap
                                    && overlapCheck.Kind == GI_Subtitles.Services.Validation.OverlapRegionKind.Dialogue;

            var dialogueStrokeBrush = dialogueOverlaps
                ? System.Windows.Media.Brushes.Red
                : System.Windows.Media.Brushes.LimeGreen;
            var dialogueFillBrush = dialogueOverlaps
                ? new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(35, 255, 0, 0))
                : new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(25, 0, 255, 0));

            var dialogueRect = new System.Windows.Shapes.Rectangle
            {
                Stroke = dialogueStrokeBrush,
                StrokeThickness = 3,
                Width = w,
                Height = h,
                Fill = dialogueFillBrush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(dialogueRect, x - virtualLeftDip);
            Canvas.SetTop(dialogueRect, y - virtualTopDip);
            canvas.Children.Add(dialogueRect);

            // Dialogue label — colour matches the rectangle, content adds the
            // ⚠ Overlap badge so the user sees it even if they miss the red stroke.
            string dialogueLabelText = GetLocalizedString("Overlay_DialogueRegion_Label", "Dialogue Region");
            if (dialogueOverlaps)
            {
                dialogueLabelText += "  " + GetLocalizedString(
                    "Overlap_Badge_Warning", "⚠ Overlay covers this region — OCR will fail");
            }
            var dialogueLabel = new System.Windows.Controls.TextBlock
            {
                Text = dialogueLabelText,
                Foreground = dialogueStrokeBrush,
                FontSize = 14,
                FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(dialogueLabel, x + 6 - virtualLeftDip);
            Canvas.SetTop(dialogueLabel, y - 22 - virtualTopDip);
            canvas.Children.Add(dialogueLabel);

            // Visualise the intersection rectangle: a filled red rectangle
            // over the exact pixels OCR will see as "masked garbage". Drawn
            // after the stroke so it stacks on top. Only emit when the
            // dialogue region itself is the overlap victim — drawing it on
            // secondary/answer hits is covered below.
            if (dialogueOverlaps)
            {
                DrawOverlapIntersection(canvas, overlapCheck.IntersectionRect);
            }

            // Answer region — blue when safe, red when overlay overlaps.
            if (AnswerRegion != null && AnswerRegion.Length == 4 &&
                int.TryParse(AnswerRegion[2], out int aw) && aw > 0 &&
                int.TryParse(AnswerRegion[3], out int ah) && ah > 0)
            {
                int ax = Convert.ToInt32(int.Parse(AnswerRegion[0]) / Scale);
                int ay = Convert.ToInt32(int.Parse(AnswerRegion[1]) / Scale);
                aw = Convert.ToInt32(aw / Scale);
                ah = Convert.ToInt32(ah / Scale);

                bool answerOverlaps = overlapCheck != null
                                      && overlapCheck.HasOverlap
                                      && overlapCheck.Kind == GI_Subtitles.Services.Validation.OverlapRegionKind.Answer;

                var answerStrokeBrush = answerOverlaps
                    ? System.Windows.Media.Brushes.Red
                    : (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x21, 0x96, 0xF3));
                var answerFillBrush = answerOverlaps
                    ? new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(35, 255, 0, 0))
                    : new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(20, 33, 150, 243));

                var answerRect = new System.Windows.Shapes.Rectangle
                {
                    Stroke = answerStrokeBrush,
                    StrokeThickness = 3,
                    Width = aw,
                    Height = ah,
                    Fill = answerFillBrush,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(answerRect, ax - virtualLeftDip);
                Canvas.SetTop(answerRect, ay - virtualTopDip);
                canvas.Children.Add(answerRect);

                string answerLabelText = GetLocalizedString("Overlay_AnswerRegion_Label", "Answer Region");
                if (answerOverlaps)
                {
                    answerLabelText += "  " + GetLocalizedString(
                        "Overlap_Badge_Warning", "⚠ Overlay covers this region — OCR will fail");
                }
                var answerLabel = new System.Windows.Controls.TextBlock
                {
                    Text = answerLabelText,
                    Foreground = answerStrokeBrush,
                    FontSize = 14,
                    FontWeight = System.Windows.FontWeights.Bold
                };
                Canvas.SetLeft(answerLabel, ax + 6 - virtualLeftDip);
                Canvas.SetTop(answerLabel, ay - 22 - virtualTopDip);
                canvas.Children.Add(answerLabel);

                if (answerOverlaps)
                {
                    DrawOverlapIntersection(canvas, overlapCheck.IntersectionRect);
                }
            }

            overlay.Content = canvas;
            overlay.Show();

            // Exclude from screen capture to avoid OCR interference
            try
            {
                var overlayHandle = new WindowInteropHelper(overlay).Handle;
                SetWindowDisplayAffinity(overlayHandle, WDA_EXCLUDEFROMCAPTURE);
            }
            catch { }

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = duration
            };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                try { overlay.Close(); } catch { }
                if (ReferenceEquals(_activeRegionOverlayWindow, overlay))
                {
                    _activeRegionOverlayWindow = null;
                    _activeRegionOverlayTimer = null;
                }
            };
            // Track so region-change callers (and user actions mid-timer)
            // can close this instance when the data it displays becomes
            // stale — see CloseRegionOverlayIfOpen.
            _activeRegionOverlayWindow = overlay;
            _activeRegionOverlayTimer = timer;
            timer.Start();
        }

        /// <summary>
        /// Ask MainWindow (via Application.Current.MainWindow) to evaluate
        /// overlap against the live overlay bounds. Returns null when the
        /// MainWindow is not available (pre-startup or standalone test host)
        /// so the caller can fall back to the "safe" rendering. Silent
        /// catch-all — this is a visual-only enhancement and must never
        /// take down the Show-Region overlay.
        /// </summary>
        private GI_Subtitles.Services.Validation.OverlapCheckResult ComputeOverlapForVisualOverlay()
        {
            try
            {
                var main = System.Windows.Application.Current?.MainWindow
                           as GI_Subtitles.Views.MainWindow;
                return main?.EvaluateOverlayRegionOverlap();
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"ComputeOverlapForVisualOverlay: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fill the intersection rectangle with a solid semi-transparent red
        /// block — the exact pixels MaskOverlayAreas will paint BLACK at OCR
        /// time. Makes the failure mode visible: "this is the area OCR won't
        /// be able to read." Input is in screen physical pixels (from the
        /// validator); we divide by <see cref="Scale"/> to match the host
        /// overlay's WPF logical-pixel coordinate system.
        /// </summary>
        private void DrawOverlapIntersection(Canvas canvas, System.Windows.Rect intersectionScreenPx)
        {
            if (intersectionScreenPx.IsEmpty || intersectionScreenPx.Width <= 0 || intersectionScreenPx.Height <= 0)
                return;

            var fill = new System.Windows.Shapes.Rectangle
            {
                Stroke = System.Windows.Media.Brushes.Red,
                StrokeThickness = 2,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 2 },
                Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(90, 255, 0, 0)),
                Width = intersectionScreenPx.Width / Scale,
                Height = intersectionScreenPx.Height / Scale,
                IsHitTestVisible = false,
            };
            // Subtract the host overlay's window origin so the intersection
            // rectangle lands on the correct monitor when the virtual screen
            // doesn't start at (0,0).
            double virtualLeftDip = SystemParameters.VirtualScreenLeft;
            double virtualTopDip = SystemParameters.VirtualScreenTop;
            Canvas.SetLeft(fill, intersectionScreenPx.X / Scale - virtualLeftDip);
            Canvas.SetTop(fill, intersectionScreenPx.Y / Scale - virtualTopDip);
            canvas.Children.Add(fill);
        }
    }
}

