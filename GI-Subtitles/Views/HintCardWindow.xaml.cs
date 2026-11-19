using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GI_Subtitles.Services.Capture;
using GI_Subtitles.Services.Rendering;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// What the hint card's two windows share. Neither ever becomes the active window, both sit above the game, and
    /// both ask Windows to keep them out of screen capture whatever "Show subtitles in recordings" says: they are
    /// helpers, like the area picker and the Ctrl+Q popup. Windows 10 refuses that for a see-through WPF window, so
    /// each window keeps Windows' answer and Kaption blacks it out of its own reads when it can be in a frame.
    /// The dialogue frame (INotifyIcon.ShowRegionOverlay) takes its styles from here too: nothing on it takes a click.
    /// </summary>
    internal static class HintWindowHost
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new(-1);

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>Never active, not in Alt+Tab; with <paramref name="clickThrough"/> every click on it goes to the window underneath.</summary>
        public static void ApplyStyles(IntPtr hwnd, bool clickThrough)
        {
            if (hwnd == IntPtr.Zero) return;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLong(hwnd, GWL_EXSTYLE, clickThrough ? ex | WS_EX_TRANSPARENT : ex);
        }

        public static WindowsHideAnswer AskToStayOutOfCapture(IntPtr hwnd) =>
            MainWindow.TryExcludeFromCapture(hwnd) ? WindowsHideAnswer.Hidden : WindowsHideAnswer.Refused;

        /// <summary>Moves the window's top left corner to a point in screen pixels, above the game, without activating it.</summary>
        public static void MoveTo(IntPtr hwnd, int x, int y)
        {
            if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public static System.Drawing.Rectangle Bounds(IntPtr hwnd) =>
            hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT r)
                ? System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
                : System.Drawing.Rectangle.Empty;

        /// <summary>
        /// Whether a read of the screen can show a window with this answer: Windows refused to hide it, the backend
        /// does not honour the hiding, or Windows agreed less than a few frames ago.
        /// </summary>
        public static bool CanBeInFrame(WindowsHideAnswer answer, long hiddenSinceMs, bool backendHonoursHiding) =>
            CaptureVisibility.MasksOverlay(false, answer, backendHonoursHiding)
            || Environment.TickCount64 < hiddenSinceMs + CaptureVisibility.HideSettleMs;
    }

    /// <summary>
    /// The in-game hint card (.plan/research/s68/HINTS-AND-MORE-TRANSLATION.md, 3.6 and 3.7): the sheet of
    /// shortcuts, or one sentence. It never takes a click or the focus from the game: the window is click-through
    /// for good and never activates. What it shows, when and where is decided elsewhere (HintPlanner, HintPlacement,
    /// MainWindow.Hints.cs); this window only draws. The constructor does nothing but load the XAML, so a test can
    /// render its content without showing a window.
    /// </summary>
    public partial class HintCardWindow : Window
    {
        public const int FadeInMs = 120, FadeOutMs = 160;
        /// <summary>A pinned sheet that nobody is looking at goes to this strength, so the game shows through it.</summary>
        public const double RestOpacity = 0.45;
        public const int RestInMs = 300, RestOutMs = 100;

        private IntPtr _hwnd;
        private long _hiddenSinceMs;
        private bool _resting, _leaving;
        // A plain field, not the window's own property: the capture tick asks every tick.
        private volatile bool _shown;

        public HintCardWindow()
        {
            InitializeComponent();
            SourceInitialized += OnSourceInitialized;
        }

        /// <summary>What Windows said when the card asked to stay out of screen capture.</summary>
        public WindowsHideAnswer HideAnswer { get; private set; }

        /// <summary>The card in screen pixels; empty before it has a window.</summary>
        public System.Drawing.Rectangle PhysicalBounds => HintWindowHost.Bounds(_hwnd);

        /// <summary>On screen, a fade out included.</summary>
        public bool IsUp => _shown;

        public bool CanBeInFrame(bool backendHonoursHiding) =>
            _shown && HintWindowHost.CanBeInFrame(HideAnswer, _hiddenSinceMs, backendHonoursHiding);

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                HintWindowHost.ApplyStyles(_hwnd, clickThrough: true);
                HideAnswer = HintWindowHost.AskToStayOutOfCapture(_hwnd);
                if (HideAnswer == WindowsHideAnswer.Hidden) _hiddenSinceMs = Environment.TickCount64;
                Logger.Log.Info($"Hint card: Windows {(HideAnswer == WindowsHideAnswer.Hidden ? "keeps it out of screen capture" : "refused to hide it from screen capture; Kaption masks it in its own reads")}.");
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Hint card init: {ex.Message}");
            }
        }

        /// <summary>Fills the card. <paramref name="compact"/>: the sheet without its header and footer.</summary>
        public void SetContent(HintCardViewModel model, Color accent, bool compact = false)
        {
            ArgumentNullException.ThrowIfNull(model);
            var brush = new SolidColorBrush(accent);
            brush.Freeze();
            Resources["Char_Accent"] = brush;

            HeaderText.Text = model.Header;
            RowList.ItemsSource = model.Rows;
            RowList.Visibility = model.IsSheet ? Visibility.Visible : Visibility.Collapsed;
            Fill(FooterText, model.Footer, 10.5);
            Fill(MessageText, model.Message, 12);
            MessageText.Visibility = model.IsSheet ? Visibility.Collapsed : Visibility.Visible;
            ClockTrack.Visibility = model.HasClock ? Visibility.Visible : Visibility.Collapsed;
            ClockBar.Width = double.NaN;
            ClockBar.HorizontalAlignment = HorizontalAlignment.Stretch;
            SetCompact(model.IsSheet && compact, model);
        }

        private void SetCompact(bool compact, HintCardViewModel model)
        {
            bool full = model.IsSheet && !compact;
            HeaderRow.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
            FooterText.Visibility = full && !model.Footer.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The card's size in DIPs for this content, full or compact, before it is shown.</summary>
        public Size MeasureContent(HintCardViewModel model, bool compact)
        {
            // The model's own parts, whatever was on the card before: the clock bar has a height too.
            ClockTrack.Visibility = model.HasClock ? Visibility.Visible : Visibility.Collapsed;
            SetCompact(model.IsSheet && compact, model);
            // Measure alone would hand back the size it remembers: showing or hiding a part marks only that part's
            // parent, and UpdateLayout is what carries the change up to the panel.
            RootBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            RootBorder.UpdateLayout();
            return RootBorder.DesiredSize;
        }

        /// <summary>A sentence with the shortcut drawn as a keycap in the line.</summary>
        private void Fill(TextBlock target, HintLine line, double keySize)
        {
            target.Inlines.Clear();
            if (!string.IsNullOrEmpty(line.Before)) target.Inlines.Add(new Run(line.Before));
            if (line.Keys != null)
            {
                var cap = new Border
                {
                    Style = (Style)FindResource("HintKeycap"),
                    Margin = new Thickness(1, 0, 1, 0),
                    Child = new TextBlock { Style = (Style)FindResource("HintKeycapText"), Text = line.Keys, FontSize = keySize },
                };
                target.Inlines.Add(new InlineUIContainer(cap) { BaselineAlignment = BaselineAlignment.Center });
            }
            if (!string.IsNullOrEmpty(line.After)) target.Inlines.Add(new Run(line.After));
        }

        /// <summary>
        /// Puts the card at <paramref name="topLeft"/> (screen pixels) and shows it, without activating it.
        /// <paramref name="fade"/> false: it replaces a card that was already up, so it does not blink.
        /// </summary>
        public void Present(System.Drawing.Point topLeft, bool fade)
        {
            _leaving = false;
            _resting = false;
            if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
            HintWindowHost.ApplyStyles(_hwnd, clickThrough: true);
            UpdateLayout();
            HintWindowHost.MoveTo(_hwnd, topLeft.X, topLeft.Y);
            bool wasUp = IsVisible;
            _shown = true;
            if (!wasUp) Show();
            HintWindowHost.MoveTo(_hwnd, topLeft.X, topLeft.Y);
            if (fade && !wasUp) RootBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, SubtitleCard.Fade(FadeInMs)));
            else
            {
                RootBorder.BeginAnimation(OpacityProperty, null);
                RootBorder.Opacity = 1;
            }
        }

        public void MoveTo(System.Drawing.Point topLeft) => HintWindowHost.MoveTo(_hwnd, topLeft.X, topLeft.Y);

        /// <summary>Takes the card off the screen: a short fade, or at once.</summary>
        public void Dismiss(bool fade)
        {
            if (!IsVisible || _leaving) return;
            TimeSpan length = fade ? SubtitleCard.Fade(FadeOutMs) : TimeSpan.Zero;
            if (length == TimeSpan.Zero)
            {
                RootBorder.BeginAnimation(OpacityProperty, null);
                Hide();
                _shown = false;
                return;
            }
            _leaving = true;
            var out_ = new DoubleAnimation(RootBorder.Opacity, 0, length);
            out_.Completed += (_, _) =>
            {
                if (!_leaving) return; // shown again meanwhile
                _leaving = false;
                Hide();
                _shown = false;
                RootBorder.BeginAnimation(OpacityProperty, null);
            };
            RootBorder.BeginAnimation(OpacityProperty, out_);
        }

        /// <summary>The share of its time the card has left, 1 to 0.</summary>
        public void SetClock(double fraction)
        {
            if (ClockTrack.Visibility != Visibility.Visible) return;
            ClockBar.HorizontalAlignment = HorizontalAlignment.Left;
            ClockBar.Width = Math.Max(0, ClockTrack.ActualWidth * Math.Clamp(fraction, 0, 1));
        }

        /// <summary>A pinned sheet at rest lets the game show through; a pointer resting on it brings it back.</summary>
        public void SetResting(bool resting)
        {
            if (resting == _resting || _leaving) return;
            _resting = resting;
            RootBorder.BeginAnimation(OpacityProperty,
                new DoubleAnimation(resting ? RestOpacity : 1, SubtitleCard.Fade(resting ? RestInMs : RestOutMs)));
        }
    }
}
