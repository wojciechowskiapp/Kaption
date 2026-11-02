using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GI_Subtitles.Services.QuickTranslate;
using Logger = GI_Subtitles.Common.Logger;
using WinForms = System.Windows.Forms;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Ctrl+Q result window. Never takes focus from the game, positions itself in physical
    /// pixels next to the selection, and closes per <see cref="QuickTranslateDismissal"/>.
    /// </summary>
    public partial class QuickTranslatePopup : Window
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
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        private readonly IReadOnlyList<QuickTranslateSegment> _segments;
        private readonly QuickTranslateSettings _settings;
        private readonly System.Drawing.Rectangle _selectionPx;
        private readonly System.Drawing.Rectangle _workAreaPx;
        private readonly QuickTranslateDismissal _dismissal;
        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _clock = new Stopwatch();
        private IntPtr _hwnd;
        private bool _userMoved;
        private bool _closing;
        private Func<string> _overviewText;
        private QuickTranslateContext _context;
        private CharacterOverviewView _overview;
        private IReadOnlyList<LineMark> _marks = Array.Empty<LineMark>();
        // The window that had focus (the game) before the user clicked into the popup; it gets focus back on close.
        private IntPtr _returnFocusTo;

        public QuickTranslatePopup(IReadOnlyList<QuickTranslateSegment> segments,
                                   QuickTranslateSettings settings,
                                   System.Drawing.Rectangle selectionPx)
        {
            InitializeComponent();
            ApplyGameAccent(GI_Subtitles.Core.Config.Config.Get("Game", "Genshin"));
            _segments = segments ?? Array.Empty<QuickTranslateSegment>();
            _settings = settings ?? QuickTranslateSettings.FromValues(null, QuickTranslateSettings.DefaultSeconds, QuickTranslateSettings.DefaultFontSize);
            _selectionPx = selectionPx;

            var screen = WinForms.Screen.FromRectangle(selectionPx);
            _workAreaPx = screen.WorkingArea;
            double scale = SafeScale(screen);
            MaxHeight = Math.Max(160, _workAreaPx.Height * 0.7 / scale);
            double widest = Math.Max(300, Math.Min(760, _workAreaPx.Width / scale - 16));
            MaxWidth = Math.Min(widest, Math.Max(380, selectionPx.Width / scale + 40));

            _dismissal = new QuickTranslateDismissal(ReadingTime.Compute(VisibleCharCount(), _settings));
            if (_dismissal.IsPinned)
            {
                CountdownTrack.Visibility = Visibility.Collapsed;
                PinButton.Visibility = Visibility.Collapsed; // Manual mode: nothing to pin
            }

            RenderTranslation();

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += Timer_Tick;

            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            SizeChanged += (s, e) => { if (!_userMoved) Place(e.NewSize); };
            MouseEnter += (s, e) => _dismissal.Pause();
            MouseLeave += (s, e) => _dismissal.Resume();
            Closed += (s, e) =>
            {
                _timer.Stop();
                if (_returnFocusTo != IntPtr.Zero) SetForegroundWindow(_returnFocusTo);
            };
        }

        /// <summary>Screen bounds in physical pixels, for masking the popup out of captures.</summary>
        public System.Drawing.Rectangle PhysicalBounds
        {
            get
            {
                if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out RECT r)) return System.Drawing.Rectangle.Empty;
                return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            }
        }

        /// <summary>The game's accent (kaption.one) on the band edge, titles, tabs and countdown, here and in the overview.</summary>
        internal void ApplyGameAccent(string game)
        {
            var accent = new SolidColorBrush(GameAccents.For(game));
            accent.Freeze();
            Resources["Char_Accent"] = accent;
        }

        /// <summary>
        /// Shows the recognised character: the strip under the translation, the skill lines marked, and the page
        /// ready to open inline. Safe before or after the window is shown, and again when images arrive.
        /// </summary>
        public void SetCharacter(CharacterOverviewView overview, QuickTranslateContext context, Func<string> plainText)
        {
            if (overview == null || context == null) return;
            bool redraw = _context?.IsExpanded == true; // a redraw keeps the page open, at the same place
            double offset = _overview?.VerticalOffset ?? 0;
            if (redraw) context.Expand();
            _context = context;
            _overview = overview;
            _overviewText = plainText;
            overview.ShowHero = false; // the strip heads the page
            OverviewHost.Content = overview;
            ContextStrip.DataContext = context;
            ContextStrip.Visibility = Visibility.Visible;
            _marks = context.MarksFor(_segments);
            RenderTranslation();
            ApplyMode();
            if (redraw && offset > 0)
                Dispatcher.BeginInvoke(new Action(() => { if (overview.VerticalOffset < 1) overview.ScrollTo(offset); }),
                    DispatcherPriority.Loaded);
        }

        /// <summary>Translation with the strip under it, or the character's page with the strip as its header.</summary>
        private void ApplyMode()
        {
            bool page = _context?.IsExpanded == true;
            BodyScroll.Visibility = page ? Visibility.Collapsed : Visibility.Visible;
            OverviewHost.Visibility = page ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetRow(ContextStrip, page ? 1 : 3);
            ContextStrip.Margin = page ? new Thickness(12, 2, 12, 8) : new Thickness(12, 0, 12, 10);
            OpenCardButton.Visibility = page ? Visibility.Collapsed : Visibility.Visible;
            BackButton.Visibility = page ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OpenCardButton_Click(object sender, RoutedEventArgs e)
        {
            if (_context == null) return;
            PinFromInteraction();
            _context.Expand();
            ApplyMode();
            // Open where the recognised skill is; the page lays out first.
            string section = _context.SectionToOpen;
            if (section != null && _overview != null)
                Dispatcher.BeginInvoke(new Action(() => _overview.ScrollToSection(section)), DispatcherPriority.Loaded);
            if (IsActive) BackButton.Focus();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            _context?.Collapse();
            ApplyMode();
            if (IsActive) OpenCardButton.Focus();
        }

        // Esc: an open page closes first, then the popup. Tab and Enter work as in any window once it has focus.
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (_context != null && _context.Escape() == PopupEscape.Collapse)
            {
                ApplyMode();
                OpenCardButton.Focus();
                return;
            }
            CloseNow();
        }

        // The popup never takes focus by itself. Once the user clicks into it (anything but closing), it can,
        // so Tab / Enter / Esc reach it; the game gets focus back when it closes.
        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_hwnd == IntPtr.Zero || _returnFocusTo != IntPtr.Zero || IsWithin(e.OriginalSource, CloseButton)) return;
            try
            {
                _returnFocusTo = GetForegroundWindow();
                int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
                SetWindowLong(_hwnd, GWL_EXSTYLE, ex & ~WS_EX_NOACTIVATE);
                Activate();
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"QuickTranslatePopup activate: {ex.Message}");
            }
        }

        private static bool IsWithin(object source, DependencyObject ancestor)
        {
            for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
                if (ReferenceEquals(d, ancestor)) return true;
            return false;
        }

        /// <summary>Closes immediately, without the fade. Used when a new Ctrl+Q replaces this popup.</summary>
        public void CloseNow()
        {
            _closing = true;
            _timer.Stop();
            SafeClose();
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
                SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                MainWindow.TryExcludeFromCapture(_hwnd);
                // Land on the selection's monitor first, so WPF lays out at that monitor's DPI.
                SetWindowPos(_hwnd, IntPtr.Zero, _selectionPx.Left, _selectionPx.Top, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"QuickTranslatePopup init: {ex.Message}");
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Place();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
            _clock.Start();
            _timer.Start();
        }

        /// <param name="newSize">The size being applied (DIP). The window rectangle can still hold the old one
        /// here, which put a popup that shrank back from the card beside the selection instead of below it.</param>
        private void Place(Size? newSize = null)
        {
            if (_hwnd == IntPtr.Zero || _closing) return;
            try
            {
                if (!GetWindowRect(_hwnd, out RECT r)) return;
                var size = new Size(r.Right - r.Left, r.Bottom - r.Top);
                if (newSize.HasValue && PresentationSource.FromVisual(this)?.CompositionTarget is CompositionTarget target0)
                {
                    var toDevice = target0.TransformToDevice;
                    size = new Size(Math.Round(newSize.Value.Width * toDevice.M11), Math.Round(newSize.Value.Height * toDevice.M22));
                }
                var target = PopupPlacement.Compute(
                    new Rect(_selectionPx.X, _selectionPx.Y, _selectionPx.Width, _selectionPx.Height),
                    size,
                    new Rect(_workAreaPx.X, _workAreaPx.Y, _workAreaPx.Width, _workAreaPx.Height));
                SetWindowPos(_hwnd, HWND_TOPMOST, (int)Math.Round(target.X), (int)Math.Round(target.Y), 0, 0,
                    SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"QuickTranslatePopup placement: {ex.Message}");
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
                if (CountdownTrack.Visibility == Visibility.Visible)
                    CountdownBar.Width = Math.Max(0, CountdownTrack.ActualWidth * _dismissal.RemainingFraction);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"QuickTranslatePopup tick: {ex.Message}");
                CloseNow();
            }
        }

        private void BeginFadeClose()
        {
            if (_closing) return;
            _closing = true;
            _timer.Stop();
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (s, e) => SafeClose();
            BeginAnimation(OpacityProperty, fade);
        }

        private void SafeClose()
        {
            try { Close(); }
            catch (InvalidOperationException) { /* already closing */ }
        }

        private void PinFromInteraction()
        {
            _dismissal.Pin();
            PinButton.IsChecked = true;
            CountdownTrack.Visibility = Visibility.Collapsed;
        }

        private void RenderTranslation()
        {
            TranslationPanel.Children.Clear();
            bool showOriginal = OriginalToggle.IsChecked == true;

            if (_segments.Count == 0)
            {
                TranslationPanel.Children.Add(Notice(L("QuickTranslate_NoText", "No text found in the selected area.")));
                return;
            }
            if (!_segments.Any(s => s.IsMatched))
                TranslationPanel.Children.Add(Notice(L("QuickTranslate_NoMatch", "No translation found. Showing the original text.")));

            for (int i = 0; i < _segments.Count; i++)
            {
                var seg = _segments[i];
                if (!seg.IsMatched)
                {
                    TranslationPanel.Children.Add(Paragraph(seg.Source, "Char_FgMuted", italic: true));
                    continue;
                }
                var mark = i < _marks.Count ? _marks[i] : default;
                bool title = mark.FromCard || (i < _segments.Count - 1 && seg.Translation.Length <= 60 && seg.Translation.IndexOf('\n') < 0);
                var p = Paragraph(seg.Translation, title ? "Char_Accent" : "Char_Fg", italic: false);
                if (title) p.FontWeight = FontWeights.SemiBold;
                TranslationPanel.Children.Add(mark.FromCard ? FromCardLine(p, mark.Badge) : (UIElement)p);
                if (showOriginal)
                {
                    var src = Paragraph(seg.Source, "Char_FgMuted", italic: false);
                    src.FontSize = Math.Max(11, _settings.FontSize - 3);
                    src.Margin = new Thickness(0, -4, 0, 10);
                    TranslationPanel.Children.Add(src);
                }
            }
        }

        private TextBlock Paragraph(string text, string brushKey, bool italic)
        {
            var block = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = _settings.FontSize,
                LineHeight = _settings.FontSize * 1.4,
                FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
                Margin = new Thickness(0, 0, 0, 8),
            };
            // A palette key, resolved where the text lives, so it follows the palette (and works in any host).
            block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return block;
        }

        // A skill title the character's data confirms: its type badge and a quiet "z karty postaci".
        private UIElement FromCardLine(TextBlock title, string badge)
        {
            title.Margin = new Thickness(0, 0, 8, 0);
            var line = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            line.Children.Add(title);
            if (badge.Length > 0) line.Children.Add(TagChip(badge, null, "Char_Fg", semiBold: true));
            line.Children.Add(TagChip(null, "QuickTranslate_FromCard", "Char_FgSubtle", semiBold: false));
            return line;
        }

        // A small tag from Themes/Chips.xaml: literal text, or a string resource resolved where the tag lives.
        private static Border TagChip(string text, string textKey, string brushKey, bool semiBold)
        {
            var tag = new Border { Margin = new Thickness(0, 2, 6, 2) };
            tag.SetResourceReference(StyleProperty, "KTag");
            var label = new TextBlock { FontWeight = semiBold ? FontWeights.SemiBold : FontWeights.Normal };
            label.SetResourceReference(StyleProperty, "KTagText");
            label.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            if (textKey != null) label.SetResourceReference(TextBlock.TextProperty, textKey);
            else label.Text = text;
            tag.Child = label;
            return tag;
        }

        private TextBlock Notice(string text)
        {
            var t = Paragraph(text, "Char_Accent", italic: false);
            t.FontSize = Math.Max(11, _settings.FontSize - 2);
            return t;
        }

        private int VisibleCharCount() =>
            _segments.Sum(s => s.IsMatched ? s.Translation.Length : s.Source.Length);

        private string TranslationPlainText()
        {
            var sb = new StringBuilder();
            foreach (var s in _segments)
                sb.AppendLine(s.IsMatched ? s.Translation : s.Source);
            return sb.ToString().TrimEnd();
        }

        private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try
            {
                PinFromInteraction();
                _userMoved = true; // before DragMove: a DPI change mid-drag fires SizeChanged
                DragMove();
            }
            catch (InvalidOperationException) { /* button released before the drag began */ }
        }

        private void OriginalToggle_Click(object sender, RoutedEventArgs e)
        {
            PinFromInteraction();
            RenderTranslation();
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            PinFromInteraction();
            string text = _context?.IsExpanded == true ? _overviewText?.Invoke() : TranslationPlainText();
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                Clipboard.SetText(text);
                FlashCopied();
            }
            catch (Exception ex)
            {
                // The clipboard can be locked by another process; not worth an error dialog.
                Logger.Log.Warn($"QuickTranslatePopup copy: {ex.Message}");
            }
        }

        private void FlashCopied()
        {
            CopyButton.Content = "";
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            reset.Tick += (s, e) => { reset.Stop(); CopyButton.Content = ""; };
            reset.Start();
        }

        private void PinButton_Click(object sender, RoutedEventArgs e) => PinFromInteraction();

        private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseNow();

        private void TranslationPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => CloseNow();

        private static double SafeScale(WinForms.Screen screen)
        {
            try
            {
                double s = MainWindow.GetScaleForScreen(screen);
                return s > 0 ? s : 1.0;
            }
            catch
            {
                return 1.0;
            }
        }

        private static string L(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}
