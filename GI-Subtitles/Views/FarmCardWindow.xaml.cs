using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GI_Subtitles.Services.QuickTranslate;
using GI_Subtitles.Services.Today;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Hosts <see cref="FarmCardView"/> over the game like the Ctrl+Q popup: no activation (ShowActivated=false and
    /// WS_EX_NOACTIVATE), excluded from captures, placed with SetWindowPos in physical pixels on the game's monitor,
    /// closed after the reading time (hover pauses, a click pins).
    /// </summary>
    public partial class FarmCardWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private readonly System.Drawing.Rectangle _workAreaPx;
        private readonly IReadOnlyList<Rect> _avoidPx;
        private readonly QuickTranslateDismissal _dismissal;
        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _clock = new();
        private IntPtr _hwnd;
        private bool _closing;

        public FarmCardView View => Card;

        /// <param name="workAreaPx">The game monitor's work area in physical pixels.</param>
        /// <param name="avoidPx">Boxes the card must not cover (the dialogue region), physical pixels.</param>
        public FarmCardWindow(FarmCardViewModel vm, System.Drawing.Rectangle workAreaPx, IReadOnlyList<Rect> avoidPx, bool dark)
            : this(new TodayCardViewModel(vm), workAreaPx, avoidPx, dark)
        {
        }

        /// <summary>Any game's Today card, in that game's accent.</summary>
        public FarmCardWindow(TodayCardViewModel vm, System.Drawing.Rectangle workAreaPx, IReadOnlyList<Rect> avoidPx, bool dark)
        {
            InitializeComponent();
            _workAreaPx = workAreaPx;
            _avoidPx = avoidPx ?? Array.Empty<Rect>();
            var screen = System.Windows.Forms.Screen.FromRectangle(workAreaPx);
            double scale = MainWindow.GetScaleForScreen(screen);
            if (!(scale > 0)) scale = 1;
            MaxHeight = Math.Max(320, workAreaPx.Height / scale - 2 * FarmCardPlacement.Margin);
            Card.ApplyAccent(string.IsNullOrEmpty(vm.Model.GameId) ? "Genshin" : vm.Model.GameId);
            Card.UsePalette(dark);
            Card.Show(vm);

            // The card is a notification: Auto reading time whatever the Ctrl+Q setting is.
            var auto = QuickTranslateSettings.FromValues("Auto", QuickTranslateSettings.DefaultSeconds, QuickTranslateSettings.DefaultFontSize);
            _dismissal = new QuickTranslateDismissal(ReadingTime.Compute(vm.Model.ReadingChars, auto));

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += Timer_Tick;
            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            SizeChanged += (s, e) => Place(e.NewSize);
            MouseEnter += (s, e) => _dismissal.Pause();
            MouseLeave += (s, e) => _dismissal.Resume();
            Card.Interacted += (s, e) => Pin();
            Card.CloseRequested += (s, e) => BeginFadeClose();
            Closed += (s, e) => _timer.Stop();
        }

        /// <summary>Screen bounds in physical pixels.</summary>
        public System.Drawing.Rectangle PhysicalBounds =>
            _hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out RECT r) ? System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : System.Drawing.Rectangle.Empty;

        public void Pin()
        {
            _dismissal.Pin();
            Card.SetCountdown(null);
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
                SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                MainWindow.TryExcludeFromCapture(_hwnd);
                // Land on the game's monitor first, so WPF lays out at that monitor's DPI.
                SetWindowPos(_hwnd, IntPtr.Zero, _workAreaPx.Right - 1, _workAreaPx.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Farm card init: {ex.Message}");
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Place();
            bool motion = SystemParameters.ClientAreaAnimation;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(motion ? 200 : 1)));
            if (motion)
            {
                var slide = new TranslateTransform(28, 0);
                Card.RenderTransform = slide;
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
                Card.PlayEntrance();
            }
            _clock.Start();
            _timer.Start();
        }

        private void Place(Size? newSize = null)
        {
            if (_hwnd == IntPtr.Zero || _closing) return;
            try
            {
                if (!GetWindowRect(_hwnd, out RECT r)) return;
                var size = new Size(r.Right - r.Left, r.Bottom - r.Top);
                if (newSize.HasValue && PresentationSource.FromVisual(this)?.CompositionTarget is CompositionTarget target)
                    size = new Size(Math.Round(newSize.Value.Width * target.TransformToDevice.M11), Math.Round(newSize.Value.Height * target.TransformToDevice.M22));
                var at = FarmCardPlacement.Compute(new Rect(_workAreaPx.X, _workAreaPx.Y, _workAreaPx.Width, _workAreaPx.Height), size, _avoidPx);
                SetWindowPos(_hwnd, HWND_TOPMOST, (int)Math.Round(at.X), (int)Math.Round(at.Y), 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Farm card placement: {ex.Message}");
            }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            try
            {
                TimeSpan elapsed = _clock.Elapsed;
                _clock.Restart();
                if (_dismissal.Tick(elapsed))
                {
                    BeginFadeClose();
                    return;
                }
                if (!_dismissal.IsPinned) Card.SetCountdown(_dismissal.RemainingFraction);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Farm card tick: {ex.Message}");
                CloseNow();
            }
        }

        public void BeginFadeClose()
        {
            if (_closing) return;
            _closing = true;
            _timer.Stop();
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 220 : 1));
            fade.Completed += (s, e) => SafeClose();
            BeginAnimation(OpacityProperty, fade);
        }

        public void CloseNow()
        {
            _closing = true;
            _timer.Stop();
            SafeClose();
        }

        private void SafeClose()
        {
            try { Close(); }
            catch (InvalidOperationException) { /* already closing */ }
        }
    }
}
