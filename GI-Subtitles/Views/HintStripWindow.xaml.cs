using System;
using System.Windows;
using System.Windows.Interop;
using GI_Subtitles.Services.Capture;
using GI_Subtitles.Services.Hints;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>What the strip offers at this moment.</summary>
    public enum HintStripMode
    {
        /// <summary>Over a sheet: keep on screen, move, hide.</summary>
        Sheet,
        /// <summary>Over an echo or a tip: move, hide.</summary>
        Line,
        /// <summary>For a few seconds after a sheet was hidden: "Don't show again".</summary>
        Never,
    }

    /// <summary>
    /// The hint card's buttons (.plan/research/s68/HINTS-AND-MORE-TRANSLATION.md, 3.7): a second, tiny window, because
    /// the card itself never takes a click. It can be clicked and still never becomes the active window, so the
    /// game keeps the keyboard. MainWindow.Hints.cs shows it only after a visible pointer rested on the card
    /// (HintInteractivity). The constructor only loads the XAML.
    /// </summary>
    public partial class HintStripWindow : Window
    {
        private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;

        private IntPtr _hwnd;
        private long _hiddenSinceMs;
        // A plain field, not the window's own property: the capture tick asks every tick.
        private volatile bool _shown;

        public event Action PinClicked, MoveClicked, CloseClicked, NeverClicked;

        public HintStripWindow()
        {
            InitializeComponent();
            SourceInitialized += OnSourceInitialized;
        }

        public WindowsHideAnswer HideAnswer { get; private set; }

        public System.Drawing.Rectangle PhysicalBounds => HintWindowHost.Bounds(_hwnd);

        public bool CanBeInFrame(bool backendHonoursHiding) =>
            _shown && HintWindowHost.CanBeInFrame(HideAnswer, _hiddenSinceMs, backendHonoursHiding);

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                HintWindowHost.ApplyStyles(_hwnd, clickThrough: false);
                HideAnswer = HintWindowHost.AskToStayOutOfCapture(_hwnd);
                if (HideAnswer == WindowsHideAnswer.Hidden) _hiddenSinceMs = Environment.TickCount64;
                HwndSource.FromHwnd(_hwnd)?.AddHook(NeverActivate);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Hint strip init: {ex.Message}");
            }
        }

        /// <summary>A click on a button must not make this the active window: the game would lose the keyboard.</summary>
        private static IntPtr NeverActivate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_MOUSEACTIVATE) return IntPtr.Zero;
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }

        public void SetMode(HintStripMode mode, bool pinned)
        {
            NeverButton.Visibility = mode == HintStripMode.Never ? Visibility.Visible : Visibility.Collapsed;
            PinButton.Visibility = mode == HintStripMode.Sheet ? Visibility.Visible : Visibility.Collapsed;
            MoveButton.Visibility = CloseButton.Visibility = mode == HintStripMode.Never ? Visibility.Collapsed : Visibility.Visible;
            PinButton.IsChecked = pinned;
            PinButton.SetResourceReference(ToolTipProperty, (pinned ? HintCatalog.StripUnpin : HintCatalog.StripPin).Key);
        }

        /// <summary>The strip's size in DIPs as it is set up now, before it is shown.</summary>
        public Size MeasureContent()
        {
            RootBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            RootBorder.UpdateLayout(); // the mode shows and hides buttons; see HintCardWindow.MeasureContent
            return RootBorder.DesiredSize;
        }

        /// <summary>Shows the strip with its top left corner at a point in screen pixels, without activating it.</summary>
        public void Present(System.Drawing.Point topLeft)
        {
            if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
            HintWindowHost.ApplyStyles(_hwnd, clickThrough: false);
            UpdateLayout();
            HintWindowHost.MoveTo(_hwnd, topLeft.X, topLeft.Y);
            _shown = true;
            if (!IsVisible) Show();
            HintWindowHost.MoveTo(_hwnd, topLeft.X, topLeft.Y);
        }

        public void Dismiss()
        {
            if (IsVisible) Hide();
            _shown = false;
        }

        private void PinButton_Click(object sender, RoutedEventArgs e) => PinClicked?.Invoke();
        private void MoveButton_Click(object sender, RoutedEventArgs e) => MoveClicked?.Invoke();
        private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseClicked?.Invoke();
        private void NeverButton_Click(object sender, RoutedEventArgs e) => NeverClicked?.Invoke();
    }
}
