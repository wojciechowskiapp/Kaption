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

            // 3 attempts total: immediate + 1s + 3s back-off. Each attempt
            // is capped at 10 s to keep the dialog responsive — the shared
            // HttpClient's default 30 s timeout would let one slow request
            // freeze the UI for half a minute.
            int[] backoffMs = { 1000, 3000 };
            int maxAttempts = backoffMs.Length + 1;
            const int perAttemptTimeoutMs = 10_000;
            string lastError = null;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    var api = new KaptionApiClient();
                    using var cts = new CancellationTokenSource(perAttemptTimeoutMs);
                    var resp = await api
                        .FetchFileProtectionKeyAsync(_deviceSessionJwt, cts.Token)
                        .ConfigureAwait(true);

                    if (resp == null || string.IsNullOrEmpty(resp.DeviceSecretB64))
                    {
                        lastError = "Empty server response.";
                    }
                    else if (!Persist(resp))
                    {
                        // Persist returns false only when LicenseService has
                        // no current activation in memory — should not happen
                        // here because EnsureActivated ran before us. Bail out
                        // with a clear error rather than proceeding into
                        // FileProtectionFactory.Create() which would throw on
                        // the first encrypt/decrypt instead.
                        lastError = "Couldn't store the secret locally. Restart Kaption and sign in again.";
                        Logger.Log.Error(
                            "FileProtectionBootstrap (foreground): Persist returned false — activation went missing during bootstrap.");
                        break;
                    }
                    else
                    {
                        _allowClose = true;
                        DialogResult = true;
                        return;
                    }
                }
                catch (UnauthorizedException ex)
                {
                    // Session expired between activation and now. Re-login
                    // is the only recovery — bail out and let the next
                    // launch surface the LoginWindow.
                    lastError = "Sign-in expired. Restart Kaption to sign in again.";
                    Logger.Log.Warn($"FileProtectionBootstrap (foreground): unauthorised: {ex.Message}");
                    break;
                }
                catch (FileProtectionKeyUnavailableException ex)
                {
                    lastError = "This device isn't registered. Restart Kaption to re-activate.";
                    Logger.Log.Warn($"FileProtectionBootstrap (foreground): device not registered: {ex.Message}");
                    break;
                }
                catch (RateLimitedException ex)
                {
                    lastError = $"Server is busy. Try again in {Math.Max(1, (int)ex.RetryAfter.TotalSeconds)} seconds.";
                    Logger.Log.Warn($"FileProtectionBootstrap (foreground): rate limited (retry-after {ex.RetryAfter.TotalSeconds:N0}s)");
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Log.Warn(
                        $"FileProtectionBootstrap (foreground): attempt {attempt + 1}/{maxAttempts} " +
                        $"failed ({ex.GetType().Name}: {ex.Message})");
                }

                if (attempt + 1 < maxAttempts)
                {
                    await Task.Delay(backoffMs[attempt]).ConfigureAwait(true);
                }
            }

            ShowError(lastError ?? "Unknown error.");
        }

        /// <summary>
        /// Decode + persist the wire response. Returns false when the
        /// LicenseService has no current activation (a programmer error
        /// in our caller — EnsureActivated ran before this should have
        /// guaranteed one). Returns true otherwise; an in-memory persist
        /// is enough even if the disk save failed (LicenseService logs
        /// the disk-save failure separately and the next launch will
        /// re-fetch).
        /// </summary>
        private bool Persist(FileProtectionKeyResponse resp)
        {
            byte[] secret = KaptionApiClient.DecodeBase64Url(resp.DeviceSecretB64);
            long? issuedAtMs = ParseIso(resp.IssuedAt);
            long? expiresAtMs = ParseIso(resp.ExpiresAt);

            bool ok = _licenseService.SetFileProtectionSecret(
                secret, issuedAtMs, expiresAtMs, resp.Version, resp.PbkdfIterations);

            Logger.Log.Info(
                $"FileProtectionBootstrap (foreground): secret stored " +
                $"(deviceSecret=<redacted, len={secret.Length}>, " +
                $"version={resp.Version}, iters={resp.PbkdfIterations}, " +
                $"persisted={ok}).");
            return ok;
        }

        private static long? ParseIso(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return null;
            if (!DateTime.TryParse(iso,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTime utc))
                return null;
            return new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();
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
                ? "Check your internet connection and try again."
                : message;
        }

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
