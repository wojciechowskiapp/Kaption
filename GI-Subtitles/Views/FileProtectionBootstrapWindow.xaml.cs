using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Services.Security;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Modal pre-MainWindow dialog that fetches the per-device file-protection
    /// secret from <c>POST /api/app/file-protection-key</c>. Blocks application
    /// startup until either the secret is provisioned (success) or the user
    /// chooses to quit (failure).
    ///
    /// Lifecycle:
    ///   1. App.OnStartup constructs and ShowDialog()-s this window after
    ///      activation succeeds and BEFORE any consumer of
    ///      <see cref="FileProtectionFactory"/> is built.
    ///   2. Window.Loaded fires the fetch on a background task.
    ///   3. Success → DialogResult = true, window closes, OnStartup proceeds.
    ///   4. Failure (after 3 retries) → switch to error panel with Retry/Quit.
    ///   5. Quit → DialogResult = false; OnStartup exits the app.
    ///
    /// Non-goals: localisation. The text is intentionally English-only because
    /// (a) this dialog is rare (first launch + secret-expiry only) and
    /// (b) the rest of OnStartup runs before localisation resources are
    /// bound, so DynamicResource lookups would no-op anyway.
    /// </summary>
    public partial class FileProtectionBootstrapWindow : Window
    {
        private readonly LicenseService _licenseService;
        private readonly string _deviceSessionJwt;
        private bool _allowClose;

        public FileProtectionBootstrapWindow(LicenseService licenseService, string deviceSessionJwt)
        {
            InitializeComponent();
            _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
            _deviceSessionJwt = deviceSessionJwt ?? throw new ArgumentNullException(nameof(deviceSessionJwt));

            Loaded += async (s, e) =>
            {
                StartSpinner();
                await RunFetchAsync().ConfigureAwait(true);
            };
        }

        private void StartSpinner()
        {
            try
            {
                var sb = (Storyboard)Resources["SpinnerStoryboard"];
                sb.Begin();
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"FileProtectionBootstrapWindow: spinner start failed: {ex.Message}");
            }
        }

        private async Task RunFetchAsync()
        {
            ShowWorking();
            string error = await Shell.DeviceKeyBootstrap.FetchAsync(_licenseService, _deviceSessionJwt, L).ConfigureAwait(true);
            if (error == null)
            {
                _allowClose = true;
                DialogResult = true;
                return;
            }
            ShowError(error);
        }

        private void ShowWorking()
        {
            WorkingPanel.Visibility = Visibility.Visible;
            ErrorPanel.Visibility = Visibility.Collapsed;
        }

        private void ShowError(string message)
        {
            WorkingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorDetail.Text = string.IsNullOrEmpty(message)
                ? L("shell_Protect_Err_Generic", "Check your internet connection and try again.")
                : message;
        }

        private static string L(string key, string fallback)
            => Application.Current?.TryFindResource(key) as string ?? fallback;

        private async void Retry_Click(object sender, RoutedEventArgs e)
        {
            await RunFetchAsync().ConfigureAwait(true);
        }

        private void Quit_Click(object sender, RoutedEventArgs e)
        {
            _allowClose = true;
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// Block window close while the fetch is in flight. The only ways out
        /// are success (DialogResult=true) or Quit (DialogResult=false). This
        /// stops the user from clicking the X button mid-fetch and leaving
        /// the app in an indeterminate state — App.OnStartup couldn't tell
        /// "user gave up" from "fetch still running" otherwise.
        /// </summary>
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!_allowClose && DialogResult == null)
            {
                e.Cancel = true;
            }
        }
    }
}
