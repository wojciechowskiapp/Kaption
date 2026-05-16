using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Media.Animation;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Security;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Modal sign-in window shown before <see cref="MainWindow"/> when the app
    /// has no active license or the session has hard-expired.
    ///
    /// States (driven by Visibility swaps on three sibling panels):
    ///   Initial — "Sign in with browser" call-to-action + quit.
    ///   Loading — spinner + "Waiting for sign-in..." + cancel.
    ///   Error   — descriptive error + retry + quit.
    ///
    /// DialogResult:
    ///   true  — activation succeeded, app should proceed.
    ///   false — user explicitly quit. App.xaml.cs will Shutdown(1).
    ///   null  — window was closed some other way (treat as quit).
    /// </summary>
    public partial class LoginWindow : Window
    {
        private readonly LicenseService _licenseService;
        private CancellationTokenSource _cts;
        private bool _isActivating;
        private bool _succeeded;

        public LoginWindow(LicenseService licenseService)
        {
            _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
            InitializeComponent();
            ShowInitialState();
        }

        // ───────────────────────────────────────────────────────────────────
        //  State transitions
        // ───────────────────────────────────────────────────────────────────

        private void ShowInitialState()
        {
            InitialPanel.Visibility = Visibility.Visible;
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            StopSpinner();
        }

        private void ShowLoadingState()
        {
            InitialPanel.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Visible;
            ErrorPanel.Visibility = Visibility.Collapsed;
            StartSpinner();
        }

        private void ShowErrorState(string title, string message)
        {
            InitialPanel.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorTitle.Text = title;
            ErrorMessage.Text = message ?? (Application.Current?.TryFindResource("Login_Error_Generic") as string ?? "Please try again.");
            StopSpinner();
        }

        /// <summary>
        /// Lookup helper for resource-backed strings with a sensible English
        /// fallback. Keeps the switch below terse.
        /// </summary>
        private static string L(string key, string fallback)
            => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void StartSpinner()
        {
            try
            {
                if (Resources["SpinnerStoryboard"] is Storyboard sb)
                    sb.Begin(this, true);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"LoginWindow: could not start spinner: {ex.Message}");
            }
        }

        private void StopSpinner()
        {
            try
            {
                if (Resources["SpinnerStoryboard"] is Storyboard sb)
                    sb.Stop(this);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"LoginWindow: could not stop spinner: {ex.Message}");
            }
        }

        // ───────────────────────────────────────────────────────────────────
        //  Button handlers
        // ───────────────────────────────────────────────────────────────────

        private async void SignIn_Click(object sender, RoutedEventArgs e)
        {
            if (_isActivating) return;
            _isActivating = true;

            ShowLoadingState();

            // Fresh CTS per attempt so a cancelled retry doesn't inherit a dead token.
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            ActivationResult result;
            try
            {
                result = await _licenseService.ActivateAsync(_cts.Token).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // LicenseService is supposed to translate to typed result. This catch
                // handles programmer-error paths only.
                Logger.Log.Error($"LoginWindow: unexpected activation exception: {ex}");
                _isActivating = false;
                ShowErrorState(
                    L("Login_Error_Title_Generic", "Sign-in didn't go through"),
                    L("Login_Error_Generic", "Something went wrong on our end. Please try again in a moment."));
                return;
            }

            _isActivating = false;

            if (result.Success)
            {
                _succeeded = true;
                DialogResult = true;
                Close();
                return;
            }

            switch (result.FailureReason)
            {
                case ActivationFailureReason.UserCancelled:
                    // User hit Cancel. Drop back to initial so they can retry or quit.
                    ShowInitialState();
                    break;

                case ActivationFailureReason.Timeout:
                    ShowErrorState(
                        L("Login_Error_Title_Timeout", "Sign-in timed out"),
                        L("Login_Error_Timeout", "We didn't hear back from your browser in time. Please try again."));
                    break;

                case ActivationFailureReason.NetworkError:
                    ShowErrorState(
                        L("Login_Error_Title_Network", "Can't reach Kaption"),
                        result.FailureMessage
                        ?? L("Login_Error_Network", "We couldn't reach Kaption's servers. Check your internet and try again."));
                    break;

                case ActivationFailureReason.MaxDevicesReached:
                    ShowErrorState(
                        L("Login_Error_Title_MaxDevices", "Device limit reached"),
                        result.FailureMessage
                        ?? L("Login_Error_MaxDevices", "You've reached the maximum number of devices for your account. Sign out on another device and try again."));
                    break;

                case ActivationFailureReason.ServerRejected:
                    ShowErrorState(
                        L("Login_Error_Title_ServerRejected", "Sign-in was rejected"),
                        result.FailureMessage
                        ?? L("Login_Error_ServerRejected", "Kaption's servers rejected this sign-in. Please try again."));
                    break;

                case ActivationFailureReason.ProviderError:
                    ShowErrorState(
                        L("Login_Error_Title_Provider", "Sign-in didn't complete"),
                        result.FailureMessage
                        ?? L("Login_Error_Provider", "The sign-in flow reported an error. Please try again."));
                    break;

                case ActivationFailureReason.Unknown:
                default:
                    ShowErrorState(
                        L("Login_Error_Title_Unknown", "Something went wrong"),
                        result.FailureMessage
                        ?? L("Login_Error_Generic", "Something went wrong on our end. Please try again in a moment."));
                    break;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); }
            catch (Exception ex) { Logger.Log.Warn($"LoginWindow: cancel cts failed: {ex.Message}"); }
            // The awaiting SignIn_Click will observe cancellation, hit the
            // UserCancelled branch, and restore the initial state.
        }

        private void Quit_Click(object sender, RoutedEventArgs e)
        {
            // Cancel any in-flight activation before closing so we don't leave
            // a dangling HttpListener on the port.
            try { _cts?.Cancel(); }
            catch (Exception ex) { Logger.Log.Warn($"LoginWindow: quit cancel failed: {ex.Message}"); }

            DialogResult = false;
            Close();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // Ensure we cancel outstanding work. If the user hit the X before
            // success, this is equivalent to Quit.
            try { _cts?.Cancel(); }
            catch (Exception ex) { Logger.Log.Warn($"LoginWindow: closing cancel failed: {ex.Message}"); }
            StopSpinner();

            if (DialogResult == null)
                DialogResult = _succeeded;
        }
    }
}
