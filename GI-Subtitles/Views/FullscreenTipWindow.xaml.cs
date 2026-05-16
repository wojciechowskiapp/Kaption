using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GI_Subtitles.Common;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Pre-OCR-start educational tip on Genshin display-mode setup. Shown
    /// once per install (gated by <c>Config["FullscreenTipAcknowledged"]</c>)
    /// — not detection-based, just a reliable nudge so every new user sets
    /// up the right Display Mode the first time they hit Start.
    ///
    /// "Got it" is intentionally disabled for a brief countdown so muscle
    /// memory hitting Enter doesn't dismiss the window before the eyes
    /// have time to scan the steps. After the countdown the button enables
    /// and Enter activates it (IsDefault=True).
    /// </summary>
    public partial class FullscreenTipWindow : Window
    {
        /// <summary>True when the user clicked "Got it" — caller persists the ack flag.</summary>
        public bool Acknowledged { get; private set; }

        // Wall-clock seconds the primary button is held disabled. Long
        // enough to force a glance, short enough not to be perceived as
        // "the app is broken". 2 seconds matches the GDPR-cookie-banner
        // pattern that nudges without irritating.
        private const int GotItDelaySeconds = 2;

        private DispatcherTimer _countdownTimer;
        private int _secondsRemaining;

        public FullscreenTipWindow()
        {
            InitializeComponent();
            Loaded += FullscreenTipWindow_Loaded;
        }

        private void FullscreenTipWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _secondsRemaining = GotItDelaySeconds;
            UpdateGotItLabel();

            _countdownTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _countdownTimer.Tick += CountdownTick;
            _countdownTimer.Start();
        }

        private void CountdownTick(object sender, EventArgs e)
        {
            _secondsRemaining--;
            if (_secondsRemaining <= 0)
            {
                _countdownTimer.Stop();
                _countdownTimer = null;
                EnableGotIt();
            }
            else
            {
                UpdateGotItLabel();
            }
        }

        private void UpdateGotItLabel()
        {
            // Example: "Got it (2)" / "Mam to (2)". The base label comes from
            // localized resources; we append the seconds inline so the user
            // sees the timer counting down without needing an extra label.
            try
            {
                string baseLabel = TryLocalizedString(
                    "FullscreenTip_GotItCounting", "Got it");
                GotItText.Text = $"{baseLabel} ({_secondsRemaining})";
            }
            catch { /* best-effort — never let the label crash the dialog */ }
        }

        private void EnableGotIt()
        {
            try
            {
                string baseLabel = TryLocalizedString("FullscreenTip_GotIt", "Got it");
                GotItText.Text = baseLabel;
                GotItButton.IsEnabled = true;
                GotItButton.Focus();
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"FullscreenTipWindow: enable button failed: {ex.Message}");
            }
        }

        private string TryLocalizedString(string key, string fallback)
        {
            try
            {
                if (Application.Current?.TryFindResource(key) is string s && !string.IsNullOrEmpty(s))
                    return s;
            }
            catch { /* fall through */ }
            return fallback;
        }

        private void GotIt_Click(object sender, RoutedEventArgs e)
        {
            if (!GotItButton.IsEnabled) return; // defence — XAML disables it but be safe
            Acknowledged = true;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Acknowledged = false;
            DialogResult = false;
            Close();
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Allow drag from anywhere on the card, like ModernDialog.
            if (e.ChangedButton == MouseButton.Left)
            {
                try { DragMove(); }
                catch { /* user lifted the mouse before WPF latched the drag — ignore */ }
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Cancel_Click(sender, e);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _countdownTimer?.Stop();
            _countdownTimer = null;
            base.OnClosed(e);
        }
    }
}
