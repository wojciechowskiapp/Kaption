using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Network;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Small modal that ships a single "drop the developer a note" message to
    /// <c>POST /api/feedback</c>. Non-spammy by design — there is no survey,
    /// no rating prompt, no follow-up nagging. The user types a line, clicks
    /// Send, sees a checkmark, closes.
    ///
    /// Auth: uses the current activation's device session JWT from
    /// <see cref="App.LicenseService"/>. The dialog refuses to open (and the
    /// entry point that launched it is hidden) when the user is not signed
    /// in — that's a consequence of the whole app gating on activation.
    /// </summary>
    public partial class SendFeedbackWindow : Window
    {
        private const int MaxChars = 2000;

        // Counter turns amber at this ratio of MaxChars and red at 100%.
        // Cheap, readable signal that you're near the limit without punishing
        // short messages with a progress bar.
        private const double CounterAmberRatio = 0.80;

        private static readonly System.Windows.Media.SolidColorBrush CounterAmber =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB4, 0x53, 0x09));
        private static readonly System.Windows.Media.SolidColorBrush CounterRed =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB9, 0x1C, 0x1C));

        private readonly KaptionApiClient _api;
        private bool _sending;

        /// <summary>Currently-selected category chip tag (Bug/Idea/Love/Other), or null.</summary>
        private string _category;
        private System.Windows.Media.Brush _counterDefaultBrush;

        public SendFeedbackWindow()
        {
            InitializeComponent();
            _api = new KaptionApiClient();

            _counterDefaultBrush = CharCount.Foreground;

            Loaded += (_, __) =>
            {
                // Pull focus to the textbox so the user can just start typing.
                Keyboard.Focus(MessageBox);
            };
        }

        /// <summary>Click handler shared by all four category chips. Toggles
        /// the visual selection and stores the Tag — the tag is later
        /// prepended to the message body as "[Bug] ...".</summary>
        private void CategoryChip_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button clicked)) return;
            string tag = clicked.Tag as string;

            if (_category == tag)
            {
                // Click again on the selected chip to deselect.
                _category = null;
            }
            else
            {
                _category = tag;
            }

            RefreshCategorySelection();
        }

        private void RefreshCategorySelection()
        {
            foreach (var chip in new[] { CatBug, CatIdea, CatLove, CatOther })
            {
                bool selected = _category != null && (chip.Tag as string) == _category;
                // Visual "selected" state: swap background + border to the
                // primary accent. We use inline colours rather than a second
                // style so this doesn't depend on theme resources.
                chip.Background = selected
                    ? (System.Windows.Media.Brush)Application.Current.TryFindResource("AccentBrush")
                        ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x5B, 0xDB))
                    : System.Windows.Media.Brushes.Transparent;
                chip.Foreground = selected
                    ? System.Windows.Media.Brushes.White
                    : (System.Windows.Media.Brush)Application.Current.TryFindResource("TextBrush")
                        ?? System.Windows.Media.Brushes.Black;
            }
        }

        /// <summary>
        /// Resource-lookup helper with a guaranteed English fallback so a
        /// missing key never surfaces as a WPF error glyph in the UI.
        /// </summary>
        private static string L(string key, string fallback)
            => Application.Current?.TryFindResource(key) as string ?? fallback;

        // ══════════════════════════════════════════════════════════════════
        //  TEXT INPUT
        // ══════════════════════════════════════════════════════════════════

        private void MessageBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string raw = MessageBox.Text ?? string.Empty;
            int length = raw.Length;
            CharCount.Text = $"{length} / {MaxChars}";

            // Counter turns amber near the limit and red when full — soft
            // signal so users don't hit the hard MaxLength ceiling confused.
            if (length >= MaxChars)
                CharCount.Foreground = CounterRed;
            else if (length >= MaxChars * CounterAmberRatio)
                CharCount.Foreground = CounterAmber;
            else
                CharCount.Foreground = _counterDefaultBrush;

            // Trimmed length is the real gate — a textarea full of whitespace
            // should not enable Send.
            bool canSend = !_sending && raw.Trim().Length > 0;
            BtnSend.IsEnabled = canSend;

            // Clear any previous error as soon as the user starts typing again.
            if (ErrorText.Visibility == Visibility.Visible)
            {
                ErrorText.Visibility = Visibility.Collapsed;
                ErrorText.Text = string.Empty;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  ACTIONS
        // ══════════════════════════════════════════════════════════════════

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            if (_sending) return;

            string message = (MessageBox.Text ?? string.Empty).Trim();
            if (message.Length == 0)
                return;
            if (message.Length > MaxChars)
            {
                string tooLongTemplate = L("Feedback_Error_TooLong", "Please keep it under {0} characters.");
                ShowError(string.Format(tooLongTemplate, MaxChars));
                return;
            }

            // Prepend the selected category tag so triage on the backend can
            // sort without a schema change. If the user picked nothing, send
            // the message as-is.
            if (!string.IsNullOrEmpty(_category))
            {
                message = $"[{_category}] {message}";
            }

            string sessionJwt = App.LicenseService?.CurrentActivation?.DeviceSessionJwt;
            if (string.IsNullOrEmpty(sessionJwt))
            {
                // Shouldn't happen — the entry point is gated behind sign-in —
                // but if it does we surface a clear message instead of a
                // spinner that never resolves.
                ShowError(L("Feedback_Error_NotSignedIn",
                    "You need to be signed in to send feedback. Open Kaption and sign in, then try again."));
                return;
            }

            SetSendingState(true);

            string clientVersion;
            try
            {
                clientVersion = Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "unknown";
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"SendFeedback: failed to read assembly version: {ex.Message}");
                clientVersion = "unknown";
            }

            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    var resp = await _api.SendFeedbackAsync(sessionJwt, message, clientVersion, cts.Token)
                        .ConfigureAwait(true);

                    if (resp == null || !resp.Ok)
                    {
                        ShowError(L("Feedback_Error_NotStored",
                            "The server accepted the request but didn't confirm storage. Please try again."));
                        SetSendingState(false);
                        return;
                    }
                }

                ShowThanks();
            }
            catch (ApiValidationException ex)
            {
                int code = (int)ex.StatusCode;
                string msg;
                if (code == 429)
                {
                    msg = L("Feedback_Error_RateLimited",
                        "You've already sent a few notes today — thanks! Try again tomorrow.");
                }
                else if (code == 401)
                {
                    msg = L("Feedback_Error_Expired",
                        "Your session has expired. Close this window, sign in again, and retry.");
                }
                else if (code >= 400 && code < 500)
                {
                    msg = ex.Error?.Describe() ?? L("Feedback_Error_Rejected", "The server rejected the request.");
                }
                else
                {
                    msg = L("Feedback_Error_ServerGeneric",
                        "The server couldn't record your note. Please try again in a moment.");
                }
                ShowError(msg);
                SetSendingState(false);
            }
            catch (UnauthorizedException)
            {
                ShowError(L("Feedback_Error_Expired",
                    "Your session has expired. Close this window, sign in again, and retry."));
                SetSendingState(false);
            }
            catch (ForbiddenException ex)
            {
                ShowError(ex.Message);
                SetSendingState(false);
            }
            catch (ApiUnavailableException)
            {
                ShowError(L("Feedback_Error_Unreachable",
                    "We couldn't reach Kaption's servers. Check your connection and try again."));
                SetSendingState(false);
            }
            catch (OperationCanceledException)
            {
                ShowError(L("Feedback_Error_Timeout",
                    "The request took too long. Please try again."));
                SetSendingState(false);
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"SendFeedback failed: {ex}");
                ShowError(L("Feedback_Error_Generic", "Something went wrong. Please try again."));
                SetSendingState(false);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // ══════════════════════════════════════════════════════════════════
        //  UI STATE
        // ══════════════════════════════════════════════════════════════════

        private void SetSendingState(bool sending)
        {
            _sending = sending;
            BtnSend.IsEnabled = !sending && (MessageBox.Text ?? string.Empty).Trim().Length > 0;
            BtnSend.Content = sending
                ? L("Feedback_Sending", "Sending…")
                : L("Feedback_Send", "Send");
            MessageBox.IsReadOnly = sending;
            BtnCancel.IsEnabled = !sending;
            Mouse.OverrideCursor = sending ? Cursors.Wait : null;
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void ShowThanks()
        {
            Mouse.OverrideCursor = null;
            _sending = false;
            ComposePanel.Visibility = Visibility.Collapsed;
            CategoryPanel.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
            CharCount.Visibility = Visibility.Collapsed;
            BtnSend.Visibility = Visibility.Collapsed;
            BtnCancel.Visibility = Visibility.Collapsed;
            ThanksPanel.Visibility = Visibility.Visible;

            // Auto-close after 4 s so the user doesn't have to click Close —
            // they can still click it manually earlier.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                if (this.IsLoaded)
                    this.Close();
            };
            timer.Start();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_sending)
            {
                // Don't let a half-sent request hang the dialog open.
                // Cancellation is soft — the HTTP call has its own 20 s
                // budget and will resolve shortly.
                Mouse.OverrideCursor = null;
            }
            base.OnClosing(e);
        }
    }
}
