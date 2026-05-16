using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using GI_Subtitles.Common;
using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// First-run legal acceptance window.
    ///
    /// Why a dialog instead of prompting inside the installer: Velopack's
    /// <c>Setup.exe</c> is intentionally silent because it's the same binary
    /// that runs on delta updates — prompting there would make every auto-
    /// update re-ask for EULA, which is worse UX than showing the dialog
    /// once in-app when legal text genuinely changes.
    ///
    /// Gate semantics:
    ///   - Stored version (<c>EulaAcceptedVersion</c>) ≥ <see cref="CurrentEulaVersion"/>
    ///     → acceptance recorded on this or newer copy of the legal text,
    ///       skip the dialog.
    ///   - Anything else (missing, zero, older) → show the dialog.
    ///
    /// Bump <see cref="CurrentEulaVersion"/> whenever the EULA or Privacy
    /// Policy is updated in a way that affects users' rights. Cosmetic
    /// edits (typos, rewordings) do NOT bump the version — re-prompting
    /// for that annoys users and trains them to click through dialogs.
    /// </summary>
    public partial class EulaAcceptanceWindow : Window
    {
        /// <summary>
        /// Bump this integer when the EULA or Privacy Policy change in a
        /// way that affects users' rights. Kept deliberately trivial — an
        /// int, not a semver or date — so comparisons are unambiguous and
        /// future-Claude can't accidentally regress a user's acceptance.
        /// </summary>
        public const int CurrentEulaVersion = 1;

        private const string ConfigKeyVersion = "EulaAcceptedVersion";
        private const string ConfigKeyAcceptedAt = "EulaAcceptedAtUnix";
        private const string ConfigKeyCrashOptIn = "CrashReportingEnabled";
        private const string ConfigKeyCrashPrompted = "CrashReportingPromptShown";
        private const string ConfigKeyCrashPromptedAt = "CrashReportingPromptShownAtUnix";

        /// <summary>
        /// True when the app may proceed past the acceptance gate (either
        /// the user accepted, or a prior launch already did). False means
        /// App.xaml.cs should terminate — the user declined.
        /// </summary>
        public bool Accepted { get; private set; }

        private bool _explicitQuit;

        public EulaAcceptanceWindow()
        {
            InitializeComponent();
            ApplyLocalizedInlineText();
        }

        /// <summary>
        /// Hyperlink / mixed-inline Runs can't bind DynamicResource cleanly in
        /// the XAML parser for WPF on .NET Framework 4.8, so we set their
        /// Text from the resource dictionary at construction. Fallbacks keep
        /// the dialog functional if a key is missing.
        /// </summary>
        private void ApplyLocalizedInlineText()
        {
            try
            {
                string L(string key, string fallback)
                    => Application.Current?.TryFindResource(key) as string ?? fallback;

                if (RunLinkEula != null)
                    RunLinkEula.Text = L("Eula_LinkEula", "EULA");
                if (RunLinkPrivacy != null)
                    RunLinkPrivacy.Text = L("Eula_LinkPrivacy", "Privacy Policy");
                if (RunLegalPrefix != null)
                    RunLegalPrefix.Text = L("Eula_LegalMicrocopyPrefix", "By clicking ");
                if (RunLegalAction != null)
                    RunLegalAction.Text = L("Eula_LegalMicrocopyAction", "Accept & continue");
                if (RunLegalSuffix != null)
                    RunLegalSuffix.Text = L("Eula_LegalMicrocopySuffix", ", you agree to the End-User License Agreement and Privacy Policy linked above.");
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"EulaAcceptanceWindow: localized inline text setup failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns true when the current stored acceptance covers
        /// <see cref="CurrentEulaVersion"/>. Called from App.xaml.cs to
        /// decide whether to even show the dialog.
        /// </summary>
        public static bool IsAcceptanceCurrent()
        {
            try
            {
                int accepted = Config.Get<int>(ConfigKeyVersion, 0);
                return accepted >= CurrentEulaVersion;
            }
            catch
            {
                // If Config is broken, refuse to assume consent.
                return false;
            }
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            // Clicking "Accept & continue" IS the acceptance — no separate
            // required checkbox anymore. The legal microcopy under the button
            // makes that explicit ("By clicking Accept & continue, you agree
            // to the EULA and Privacy Policy"). Simpler flow, same legal weight.
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Config.Set(ConfigKeyVersion, CurrentEulaVersion);
                Config.Set(ConfigKeyAcceptedAt, now);

                // Record the crash-reporting answer NOW, plus mark the
                // crash prompt as shown so PromptCrashReportingIfFirstRun()
                // in App.xaml.cs is a no-op. Rolling the two prompts into
                // one first-run dialog is the whole point of this window —
                // if we left the other prompt alive we'd ask twice.
                bool crashOptIn = CbCrashReports.IsChecked == true;
                Config.Set(ConfigKeyCrashOptIn, crashOptIn);
                Config.Set(ConfigKeyCrashPrompted, true);
                Config.Set(ConfigKeyCrashPromptedAt, now);

                Logger.Log.Info(
                    $"EULA accepted (version={CurrentEulaVersion}, crash_opt_in={crashOptIn}).");
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"EULA acceptance persist failed: {ex.Message}");
                // Fall through: we still mark Accepted so the session can
                // proceed. Next launch will re-prompt because Config didn't
                // update — annoying but safe.
            }

            Accepted = true;
            DialogResult = true;
            Close();
        }

        private void Quit_Click(object sender, RoutedEventArgs e)
        {
            _explicitQuit = true;
            Accepted = false;
            DialogResult = false;
            Close();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // Closing via the title-bar X without an accept → treat as declined.
            if (!Accepted && !_explicitQuit)
            {
                Accepted = false;
                DialogResult = false;
            }
        }

        /// <summary>
        /// Open EULA / Privacy Policy in the user's default browser. Using
        /// a shell execute with a quoted URI rather than Process.Start(string)
        /// — on .NET Framework 4.8 that still works, but the explicit form
        /// guarantees the shell handles the http(s) scheme.
        /// </summary>
        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Could not open legal URL '{e.Uri}': {ex.Message}");
                try
                {
                    ModernDialog.Error(
                        owner: this,
                        title: Application.Current?.TryFindResource("Eula_LinkError_Title") as string
                               ?? "Couldn't open the link",
                        body: Application.Current?.TryFindResource("Eula_LinkError_Body") as string
                               ?? "We couldn't launch your browser. You can read the documents at:\nhttps://kaption.one/legal/eula\nhttps://kaption.one/legal/privacy",
                        technicalDetails: ex.ToString());
                }
                catch { /* last-ditch */ }
            }
            e.Handled = true;
        }
    }
}
