using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Core.UI;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Services.Security;
using PaddleOCRSharp;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// A 3-step setup wizard that guides the user through initial region selection.
    /// Communicates with the host window via <see cref="OnStartOCR"/> and
    /// <see cref="OnOpenSettings"/> action delegates only — no direct MainWindow reference.
    /// </summary>
    public partial class SetupWizardWindow : Window
    {
        private readonly INotifyIcon _notify;
        private int _currentStep;

        // Track whether regions were set during this wizard session
        private bool _dialogueRegionSet;
        private bool _answerRegionSet;

        /// <summary>Invoked when the user clicks "Start Translating!" on the final step.</summary>
        public Action OnStartOCR;

        /// <summary>Invoked when the user clicks "Open Settings" on the final step.</summary>
        public Action OnOpenSettings;

        /// <summary>Provides the OCR engine for auto-detection.</summary>
        public Func<PaddleOCREngine> GetEngine;

        /// <summary>Provides the current game ID for auto-detection.</summary>
        public Func<string> GetGameId;

        /// <summary>Invoked after the user confirms a region (manual pick or
        /// auto-detect). MainWindow wires this to the overlap validator so
        /// a bad first-run layout surfaces the warning BEFORE the user hits
        /// Start. Nullable because the wizard can run standalone for tests.</summary>
        public Action OnCaptureRegionUserChanged;

        // ── Colour constants ──────────────────────────────────────────────
        private static readonly SolidColorBrush AccentFill =
            new SolidColorBrush(Color.FromRgb(0x5B, 0x7F, 0xFF));

        private static readonly SolidColorBrush SuccessFill =
            new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));

        private static readonly SolidColorBrush InactiveFill =
            new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));

        private static readonly SolidColorBrush InactiveText =
            new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

        private static readonly SolidColorBrush WhiteBrush = Brushes.White;

        private static readonly SolidColorBrush BlueBrush =
            new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3));

        private static readonly SolidColorBrush GrayStatus =
            new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

        /// <summary>
        /// Creates the setup wizard.
        /// </summary>
        /// <param name="notify">
        /// The system-tray helper that exposes <c>ChooseRegion()</c> and
        /// <c>ChooseAnswerRegion()</c> for interactive region selection.
        /// </param>
        public SetupWizardWindow(INotifyIcon notify)
        {
            _notify = notify ?? throw new ArgumentNullException(nameof(notify));
            InitializeComponent();
            ApplyLocalizedInlineText();

            // Check if regions are already configured from a previous session
            _dialogueRegionSet = IsRegionValid(Config.Get<string>("Region", ""));
            _answerRegionSet = IsRegionValid(Config.Get<string>("AnswerRegion", ""));

            if (_dialogueRegionSet)
            {
                UpdateDialogueStatus(Config.Get<string>("Region", ""));
            }

            if (_answerRegionSet)
            {
                UpdateAnswerStatus(Config.Get<string>("AnswerRegion", ""));
            }

            RefreshAccountCard();
            RefreshReferralCardVisibility();
            CollapseStep2IndicatorIfDisabled();
            UpdateStepDisplay();
        }

        /// <summary>
        /// When the answer-translation feature is off, Step 2 is silently
        /// skipped (the Next/Back handlers jump over index 1). Leaving the
        /// "2" circle in the header makes users think the wizard ate a step,
        /// so we collapse it + its connector line and renumber the remaining
        /// circles 1·2·3 instead of 1·3·4. Safe to call once at construction
        /// because <c>FeatureAnswerTranslationEnabled</c> is a static flag.
        /// </summary>
        private void CollapseStep2IndicatorIfDisabled()
        {
#pragma warning disable CS0162 // Unreachable code — FeatureAnswerTranslationEnabled is const false; remove pragma when flag flips.
            if (MainWindow.FeatureAnswerTranslationEnabled) return;
#pragma warning restore CS0162

            // Hide the now-phantom step: circle + its preceding connector line.
            Step2Circle.Visibility = Visibility.Collapsed;
            Step2Number.Visibility = Visibility.Collapsed;
            Line2.Visibility = Visibility.Collapsed;

            // Parent Grids of the circles are fixed-width (32px) + their
            // containers have explicit margins — collapse them too so the
            // header reflows cleanly.
            if (Step2Circle.Parent is UIElement step2Host) step2Host.Visibility = Visibility.Collapsed;

            // Renumber the visible circles so the user sees 1-2-3 not 1-3-4.
            Step3Number.Text = "2";
            Step4Number.Text = "3";
        }

        /// <summary>
        /// Resource-lookup helper with English fallback — used throughout the
        /// wizard code-behind to keep dialog copy / status text in sync with
        /// the active UI language.
        /// </summary>
        private static string L(string key, string fallback)
            => Application.Current?.TryFindResource(key) as string ?? fallback;

        /// <summary>
        /// The Tips &amp; Settings step mixes bold + regular Runs inside
        /// TextBlocks; DynamicResource bindings on Run.Text behave
        /// inconsistently on .NET Framework 4.8 WPF, so we set each Run
        /// from code at construction time. Step-1 tip copy is handled the
        /// same way for consistency. Fallbacks preserve English when a
        /// key is missing.
        /// </summary>
        private void ApplyLocalizedInlineText()
        {
            try
            {
                if (RunStep1TipPrefix != null) RunStep1TipPrefix.Text = L("Wizard_Step1_TipPrefix", "Tip:");
                if (RunStep1TipBody != null) RunStep1TipBody.Text = " " + L("Wizard_Step1_TipBody",
                    "Start a quest with an NPC who speaks in 3+ lines, then draw the region a little bigger than that longest line. Short dialogues still fit; long ones won't get clipped.");
                if (RunStep1BorderlessPrefix != null) RunStep1BorderlessPrefix.Text = L("Wizard_Step1_BorderlessPrefix", "Display mode:");
                if (RunStep1BorderlessBody != null) RunStep1BorderlessBody.Text = " " + L("Wizard_Step1_BorderlessBody",
                    "Run the game in Borderless or Windowed mode (Alt+Enter inside the game). Exclusive fullscreen makes the subtitle flicker — same reason Discord and Steam overlays struggle there.");
                if (RunStep1GuidePrefix != null) RunStep1GuidePrefix.Text = L("Wizard_Step1_GuidePrefix", "Prefer a walkthrough with screenshots? ");
                if (RunStep1GuideLink != null) RunStep1GuideLink.Text = L("Wizard_Step1_GuideLink", "Read the full guide");

                if (RunTipDragTitle != null) RunTipDragTitle.Text = L("Wizard_Tip_DragTitle", "Drag to reposition");
                if (RunTipDragBody != null) RunTipDragBody.Text = L("Wizard_Tip_DragBody",
                    " — Click and drag the subtitle box to move it. Your position is saved automatically.");
                if (RunTipClickThroughTitle != null) RunTipClickThroughTitle.Text = L("Wizard_Tip_ClickThroughTitle", "Click-through");
                if (RunTipClickThroughBody != null) RunTipClickThroughBody.Text = L("Wizard_Tip_ClickThroughBody",
                    " — Press Ctrl+Shift+D to let clicks pass through the subtitle box (useful during combat).");
                if (RunTipFontTitle != null) RunTipFontTitle.Text = L("Wizard_Tip_FontTitle", "Font & size");
                if (RunTipFontBody != null) RunTipFontBody.Text = L("Wizard_Tip_FontBody",
                    " — Customize in the Appearance tab. You can also adjust max width and height for the subtitle box.");

                if (RunTipAnswerTitle != null) RunTipAnswerTitle.Text = L("Wizard_Tip_AnswerTrTitle", "Answer translation");
                if (RunTipAnswerBody != null) RunTipAnswerBody.Text = L("Wizard_Tip_AnswerTrBody",
                    " — Enable in Settings > Options to translate player dialogue choices. Requires an answer region set up in the Regions tab.");
                if (RunTipPlayerNameTitle != null) RunTipPlayerNameTitle.Text = L("Wizard_Tip_PlayerNameTitle", "Player name");
                if (RunTipPlayerNameBody != null) RunTipPlayerNameBody.Text = L("Wizard_Tip_PlayerNameBody",
                    " — Set your character name in Settings > Options so every dialogue line uses it. If you skip this, Kaption falls back to \"Traveler\" everywhere.");
                if (RunTipPredictionTitle != null) RunTipPredictionTitle.Text = L("Wizard_Tip_PredictionTitle", "Dialogue prediction");
                if (RunTipPredictionBody != null) RunTipPredictionBody.Text = L("Wizard_Tip_PredictionBody",
                    " — The app predicts the next dialogue line from game data and shows translations before OCR finishes.");

                if (RunTipShowRegionsTitle != null) RunTipShowRegionsTitle.Text = L("Wizard_Tip_ShowRegionsTitle", "Show regions");
                if (RunTipShowRegionsBody != null) RunTipShowRegionsBody.Text = L("Wizard_Tip_ShowRegionsBody",
                    " — Use the \"Show Region\" button in the Regions tab to verify your capture areas are positioned correctly.");
                if (RunTipOcrTuningTitle != null) RunTipOcrTuningTitle.Text = L("Wizard_Tip_OcrTuningTitle", "OCR tuning");
                if (RunTipOcrTuningBody != null) RunTipOcrTuningBody.Text = L("Wizard_Tip_OcrTuningBody",
                    " — Adjust OCR speed and stability window in the Settings tab. Lower values = faster but uses more CPU. Higher = more stable results.");
                if (RunTipManualTitle != null) RunTipManualTitle.Text = L("Wizard_Tip_ManualTitle", "Manual region");
                if (RunTipManualBody != null) RunTipManualBody.Text = L("Wizard_Tip_ManualBody",
                    " — If auto-detect doesn't work well, use \"Select Region\" to draw the capture area manually. Fine-tune with the X/Y/W/H fields.");
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"SetupWizardWindow: inline text localization failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  REFERRAL ATTRIBUTION (session 26)
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Loose client-side format match. The backend does the strict
        /// validation; we just gate the Submit button so users don't ship
        /// obviously-malformed codes.
        /// Net8: [GeneratedRegex] source generator (was RegexOptions.Compiled).
        /// </summary>
        [GeneratedRegex(@"^[A-Z0-9]{3,12}(-[A-Z0-9]{3,12})?$", RegexOptions.CultureInvariant)]
        private static partial Regex ReferralCodeRegex();

        private bool _submittingReferral;

        /// <summary>
        /// Hide the "Invited by a friend?" card entirely once the user has
        /// either submitted a valid code or opted out via Skip. Also hides it
        /// when there's no session (shouldn't happen, but be resilient).
        /// </summary>
        private void RefreshReferralCardVisibility()
        {
            try
            {
                if (ReferralCard == null) return;
                bool alreadyHandled = Config.Get("ReferralCodeAttributedOrSkipped", false);
                bool hasSession = !string.IsNullOrEmpty(App.LicenseService?.CurrentActivation?.DeviceSessionJwt);
                ReferralCard.Visibility = (!alreadyHandled && hasSession)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"RefreshReferralCardVisibility failed: {ex.Message}");
            }
        }

        private void ReferralCode_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (BtnReferralSubmit == null) return;
            string code = (ReferralCodeBox.Text ?? "").Trim().ToUpperInvariant();
            BtnReferralSubmit.IsEnabled = !_submittingReferral && ReferralCodeRegex().IsMatch(code);
            if (ReferralStatusText.Visibility == Visibility.Visible)
                ReferralStatusText.Visibility = Visibility.Collapsed;
        }

        private void ReferralSkip_Click(object sender, RoutedEventArgs e)
        {
            try { Config.Set("ReferralCodeAttributedOrSkipped", true); }
            catch (Exception ex) { Logger.Log.Warn($"Could not persist referral skip: {ex.Message}"); }
            ReferralCard.Visibility = Visibility.Collapsed;
        }

        private async void ReferralSubmit_Click(object sender, RoutedEventArgs e)
        {
            if (_submittingReferral) return;

            string code = (ReferralCodeBox.Text ?? "").Trim().ToUpperInvariant();
            if (!ReferralCodeRegex().IsMatch(code))
            {
                ShowReferralStatus(L("Wizard_Referral_Invalid",
                    "That doesn't look like a valid code. Ask your friend to send it again."), isError: true);
                return;
            }

            string jwt = App.LicenseService?.CurrentActivation?.DeviceSessionJwt;
            if (string.IsNullOrEmpty(jwt))
            {
                ShowReferralStatus(L("Wizard_Referral_SignInFirst", "Please sign in first."), isError: true);
                return;
            }

            _submittingReferral = true;
            BtnReferralSubmit.IsEnabled = false;
            BtnReferralSkip.IsEnabled = false;
            BtnReferralSubmit.Content = L("Wizard_Referral_Submitting", "Submitting\u2026");

            try
            {
                var api = new KaptionApiClient();
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    await api.AttributeReferralAsync(jwt, code, cts.Token).ConfigureAwait(true);
                }

                try { Config.Set("ReferralCodeAttributedOrSkipped", true); }
                catch (Exception ex) { Logger.Log.Warn($"Could not persist referral submit: {ex.Message}"); }

                ShowReferralStatus(L("Wizard_Referral_Thanks",
                    "Thanks! Your friend will get credit when you start using Kaption."), isError: false);
                ReferralCodeBox.IsEnabled = false;
                // Short pause so the user reads the status before we collapse the card.
                await Task.Delay(TimeSpan.FromSeconds(2.5)).ConfigureAwait(true);
                ReferralCard.Visibility = Visibility.Collapsed;
            }
            catch (UnauthorizedException)
            {
                ShowReferralStatus(L("Wizard_Referral_Expired",
                    "Your session has expired. Please sign in again."), isError: true);
            }
            catch (ForbiddenException ex)
            {
                ShowReferralStatus(
                    ex.Message ?? L("Wizard_Referral_SelfReferral",
                        "That code can't be linked to your account (self-referral?)."),
                    isError: true);
            }
            catch (ApiValidationException ex)
            {
                int code2 = (int)ex.StatusCode;
                string serverMsg = ex.Error?.Describe();
                string msg = code2 == 409
                    ? L("Wizard_Referral_Conflict",
                        "Looks like you're already linked to a referrer, or this code has already been used.")
                    : (serverMsg ?? L("Wizard_Referral_PastWindow",
                        "That code isn't valid, or your account is past the 7-day window."));
                ShowReferralStatus(msg, isError: true);
            }
            catch (ApiUnavailableException)
            {
                ShowReferralStatus(L("Wizard_Referral_Unreachable",
                    "We couldn't reach Kaption's servers. Check your connection and try again."), isError: true);
            }
            catch (OperationCanceledException)
            {
                ShowReferralStatus(L("Wizard_Referral_Timeout",
                    "The request took too long. Please try again."), isError: true);
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Referral submit failed: {ex.GetType().Name}");
                ShowReferralStatus(L("Wizard_Referral_Generic", "Something went wrong. Please try again."), isError: true);
            }
            finally
            {
                _submittingReferral = false;
                BtnReferralSkip.IsEnabled = true;
                BtnReferralSubmit.Content = L("Wizard_Referral_Submit", "Submit");
                if (ReferralCodeBox.IsEnabled)
                {
                    string c = (ReferralCodeBox.Text ?? "").Trim().ToUpperInvariant();
                    BtnReferralSubmit.IsEnabled = ReferralCodeRegex().IsMatch(c);
                }
            }
        }

        private void ShowReferralStatus(string message, bool isError)
        {
            ReferralStatusText.Text = message;
            ReferralStatusText.Foreground = isError
                ? new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C))
                : SuccessFill;
            ReferralStatusText.Visibility = Visibility.Visible;
        }

        // ══════════════════════════════════════════════════════════════════
        //  ACCOUNT / SIGN-IN INFO (Step 4 account card)
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Refresh the "Signed in" card with the current activation state from
        /// <see cref="App.LicenseService"/>. Called on open and after sign-out.
        ///
        /// Normal case: <see cref="App.LicenseService"/> is non-null and activated
        /// (App.xaml.cs gates startup on activation). If somehow not, we still
        /// render a coherent fallback rather than crash.
        /// </summary>
        private void RefreshAccountCard()
        {
            try
            {
                var svc = App.LicenseService;
                var current = svc?.CurrentActivation;

                if (current != null && svc.IsActivated)
                {
                    AccountEmailText.Text = string.IsNullOrWhiteSpace(current.Email)
                        ? L("Wizard_Account_SignedInPlaceholder", "(signed in)")
                        : current.Email;
                    AccountTierText.Text = BuildTierDescription(current);
                    BtnSignOut.IsEnabled = true;
                    BtnSignOut.Content = L("Wizard_Account_SignOut", "Sign out");
                }
                else
                {
                    // Shouldn't happen given the App.xaml.cs gate, but render a
                    // safe fallback so we don't surface nulls.
                    AccountEmailText.Text = L("Wizard_Account_NotSignedIn", "(not signed in)");
                    AccountTierText.Text = L("Wizard_Account_RestartToSignIn", "Restart Kaption to sign in.");
                    BtnSignOut.IsEnabled = false;
                    BtnSignOut.Content = L("Wizard_Account_SignInFirst", "Sign in first");
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"RefreshAccountCard failed: {ex.Message}");
            }
        }

        private static string BuildTierDescription(ActivationData data)
        {
            string tier = string.IsNullOrWhiteSpace(data.Tier) ? "free" : data.Tier;
            string expires;
            try
            {
                var remaining = data.ExpiresAtUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    expires = L("Wizard_Account_Expired", "expired");
                else if (remaining < TimeSpan.FromHours(24))
                    expires = string.Format(
                        L("Wizard_Account_ExpiresInHours", "expires in {0}h"),
                        (int)Math.Max(1, remaining.TotalHours));
                else if (remaining < TimeSpan.FromDays(60))
                    expires = string.Format(
                        L("Wizard_Account_ExpiresInDays", "expires in {0}d"),
                        (int)remaining.TotalDays);
                else
                    expires = string.Format(
                        L("Wizard_Account_ExpiresOn", "expires {0}"),
                        data.ExpiresAtUtc.ToLocalTime().ToString("yyyy-MM-dd"));
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"BuildTierDescription failed: {ex.Message}");
                expires = "";
            }
            return string.IsNullOrEmpty(expires) ? tier : $"{tier} \u00B7 {expires}";
        }

        private void SignOut_Click(object sender, RoutedEventArgs e)
        {
            var svc = App.LicenseService;
            if (svc == null) return;

            bool confirmed = ModernDialog.Confirm(
                owner: this,
                title: L("Wizard_SignOut_Confirm_Title", "Sign out"),
                body: L("Wizard_SignOut_Confirm_Body",
                    "Sign out of Kaption? You'll need to sign in again before you can use the app."),
                primaryText: L("Wizard_Account_SignOut", "Sign out"),
                secondaryText: L("Common_Cancel", "Cancel"));

            if (!confirmed)
                return;

            try
            {
                svc.SignOut();
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"SignOut failed: {ex}");
                ModernDialog.Warn(
                    owner: this,
                    title: "Kaption",
                    body: L("Wizard_SignOut_FailedBody",
                        "Could not sign out cleanly. You can restart Kaption to complete sign-out."));
                return;
            }

            RefreshAccountCard();

            // The App.xaml.cs state-change handler will re-prompt for sign-in
            // via the modal LoginWindow once this message loop returns.
            this.Close();
        }

        // ══════════════════════════════════════════════════════════════════
        //  STEP NAVIGATION
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Updates all visual elements to reflect <see cref="_currentStep"/>.
        /// Controls panel visibility, step indicator colours, and navigation buttons.
        /// </summary>
        private void UpdateStepDisplay()
        {
            // ── Panel visibility ──────────────────────────────────────────
            Step1Panel.Visibility = _currentStep == 0 ? Visibility.Visible : Visibility.Collapsed;
            Step2Panel.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
            Step3Panel.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
            Step4Panel.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;

            // ── Step indicator circles ────────────────────────────────────
            UpdateStepCircle(Step1Circle, Step1Number, stepIndex: 0);
            UpdateStepCircle(Step2Circle, Step2Number, stepIndex: 1);
            UpdateStepCircle(Step3Circle, Step3Number, stepIndex: 2);
            UpdateStepCircle(Step4Circle, Step4Number, stepIndex: 3);

            // ── Connecting lines ──────────────────────────────────────────
            Line1.Fill = _currentStep > 0 ? AccentFill : InactiveFill;
            Line2.Fill = _currentStep > 1 ? AccentFill : InactiveFill;
            Line3.Fill = _currentStep > 2 ? AccentFill : InactiveFill;

            // ── Navigation buttons ────────────────────────────────────────
            BtnBack.Visibility = _currentStep > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_currentStep >= 3)
            {
                // Final step — hide Skip and Next, the "Start Translating!" button takes over
                BtnSkip.Visibility = Visibility.Collapsed;
                BtnNext.Visibility = Visibility.Collapsed;
            }
            else
            {
                BtnSkip.Visibility = Visibility.Visible;
                BtnNext.Visibility = Visibility.Visible;
            }

            // ── Step 3 summary ────────────────────────────────────────────
            if (_currentStep == 2)
            {
                RefreshStep3Summary();
            }
        }

        /// <summary>
        /// Sets the fill and text of a step indicator circle based on
        /// whether the step is completed, active, or inactive.
        /// </summary>
        private void UpdateStepCircle(
            System.Windows.Shapes.Ellipse circle,
            System.Windows.Controls.TextBlock text,
            int stepIndex)
        {
            if (stepIndex < _currentStep)
            {
                // Completed — green with checkmark
                circle.Fill = SuccessFill;
                text.Text = "\u2713";    // checkmark
                text.Foreground = WhiteBrush;
            }
            else if (stepIndex == _currentStep)
            {
                // Active — accent blue with number
                circle.Fill = AccentFill;
                text.Text = (stepIndex + 1).ToString();
                text.Foreground = WhiteBrush;
            }
            else
            {
                // Inactive — gray with number
                circle.Fill = InactiveFill;
                text.Text = (stepIndex + 1).ToString();
                text.Foreground = InactiveText;
            }
        }

        /// <summary>
        /// Populates the step-3 summary card with the latest region info.
        /// </summary>
        private void RefreshStep3Summary()
        {
            // Dialogue region
            var dialogueRegion = Config.Get<string>("Region", "");
            if (IsRegionValid(dialogueRegion))
            {
                var parts = dialogueRegion.Split(',');
                Step3DialogueStatus.Text = $"{parts[0]},{parts[1]}  {parts[2]}x{parts[3]}";
                Step3DialogueStatus.Foreground = SuccessFill;
                Step3DialogueIcon.Foreground = SuccessFill;
            }
            else
            {
                Step3DialogueStatus.Text = L("Wizard_Summary_NotSet", "Not set");
                Step3DialogueStatus.Foreground = GrayStatus;
                Step3DialogueIcon.Foreground = GrayStatus;
            }

            // Answer region
            var answerRegion = Config.Get<string>("AnswerRegion", "");
            if (IsRegionValid(answerRegion))
            {
                var parts = answerRegion.Split(',');
                Step3AnswerStatus.Text = $"{parts[0]},{parts[1]}  {parts[2]}x{parts[3]}";
                Step3AnswerStatus.Foreground = BlueBrush;
                Step3AnswerIcon.Foreground = BlueBrush;
            }
            else
            {
                Step3AnswerStatus.Text = L("Wizard_Summary_Skipped", "Skipped");
                Step3AnswerStatus.Foreground = GrayStatus;
                Step3AnswerIcon.Foreground = GrayStatus;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  REGION SELECTION
        // ══════════════════════════════════════════════════════════════════

        private void SelectDialogueRegion_Click(object sender, RoutedEventArgs e)
        {
            PerformRegionSelection(() =>
            {
                _notify.ChooseRegion();
                var region = Config.Get<string>("Region", "");
                if (IsRegionValid(region))
                {
                    _dialogueRegionSet = true;
                    UpdateDialogueStatus(region);
                    OnCaptureRegionUserChanged?.Invoke();
                }
            });
        }

        // CS0162 wraps the whole body: when the feature flag is a compile-time const
        // false, every statement after the early-return is provably unreachable. The
        // suppression documents this is intentional for the disable window — remove
        // the pragma when FeatureAnswerTranslationEnabled flips true.
#pragma warning disable CS0162
        private void SelectAnswerRegion_Click(object sender, RoutedEventArgs e)
        {
            // Defense-in-depth: the wizard's Step 2 UI is hidden while the
            // answer-translation feature is disabled, so this handler is normally
            // unreachable. If triggered anyway (e.g. via automation), no-op silently.
            if (!MainWindow.FeatureAnswerTranslationEnabled)
            {
                Logger.Log.Info("SelectAnswerRegion ignored — answer translation is temporarily disabled");
                return;
            }

            PerformRegionSelection(() =>
            {
                _notify.ChooseAnswerRegion();
                var region = Config.Get<string>("AnswerRegion", "");
                if (IsRegionValid(region))
                {
                    _answerRegionSet = true;
                    UpdateAnswerStatus(region);
                }
            });
        }
#pragma warning restore CS0162

        /// <summary>
        /// Minimizes the wizard, runs the region selection action,
        /// then restores and activates the wizard.
        /// </summary>
        private void PerformRegionSelection(Action selectionAction)
        {
            try
            {
                this.WindowState = WindowState.Minimized;
                selectionAction();
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Region selection failed: {ex}");
            }
            finally
            {
                this.WindowState = WindowState.Normal;
                this.Activate();
            }
        }

        /// <summary>
        /// Updates the step-1 status label after a successful dialogue region selection.
        /// </summary>
        private void UpdateDialogueStatus(string region)
        {
            var parts = region.Split(',');
            if (parts.Length == 4)
            {
                Step1Status.Text = string.Format(
                    L("Wizard_Region_Set_Format", "\u2713 Region set: {0},{1}  {2}\u00D7{3}"),
                    parts[0], parts[1], parts[2], parts[3]);
                Step1Status.Foreground = SuccessFill;
            }
        }

        /// <summary>
        /// Updates the step-2 status label after a successful answer region selection.
        /// </summary>
        private void UpdateAnswerStatus(string region)
        {
            var parts = region.Split(',');
            if (parts.Length == 4)
            {
                Step2Status.Text = string.Format(
                    L("Wizard_Region_Set_Format", "\u2713 Region set: {0},{1}  {2}\u00D7{3}"),
                    parts[0], parts[1], parts[2], parts[3]);
                Step2Status.Foreground = BlueBrush;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  NAVIGATION EVENTS
        // ══════════════════════════════════════════════════════════════════

        private void NavigateNext(object sender, RoutedEventArgs e)
        {
            if (_currentStep < 3)
            {
                _currentStep++;
                // TEMPORARY: skip over Step 2 (answer-region selection) while the
                // answer-translation feature is disabled — see
                // MainWindow.FeatureAnswerTranslationEnabled. Remove this branch
                // when the feature returns.
                if (_currentStep == 1 && !MainWindow.FeatureAnswerTranslationEnabled)
                    _currentStep = 2;
                UpdateStepDisplay();
            }
        }

        private void NavigateBack(object sender, RoutedEventArgs e)
        {
            if (_currentStep > 0)
            {
                _currentStep--;
                // Same skip on the reverse direction.
                if (_currentStep == 1 && !MainWindow.FeatureAnswerTranslationEnabled)
                    _currentStep = 0;
                UpdateStepDisplay();
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  FINAL STEP ACTIONS
        // ══════════════════════════════════════════════════════════════════

        private void StartTranslating_Click(object sender, RoutedEventArgs e)
        {
            Config.Set("SetupCompleted", true);
            OnStartOCR?.Invoke();
            this.Close();
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            Config.Set("SetupCompleted", true);
            OnOpenSettings?.Invoke();
            this.Close();
        }

        /// <summary>
        /// Opens the standalone "Send feedback" dialog. Kept modal to this
        /// window so closing the wizard while a note is open sends the focus
        /// back somewhere reasonable (the wizard) rather than to the OCR
        /// overlay mid-compose.
        /// </summary>
        private void SendFeedback_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SendFeedbackWindow { Owner = this };
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Opening feedback dialog failed: {ex}");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  AUTO-DETECT
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Runs <see cref="AutoRegionService.Detect"/> on a background thread,
        /// then applies the detected regions to config and notifies the OCR loop.
        /// </summary>
        private async void AutoDetect_Click(object sender, RoutedEventArgs e)
        {
            var engine = GetEngine?.Invoke();
            var gameId = GetGameId?.Invoke() ?? "Genshin";

            BtnAutoDetect.IsEnabled = false;
            AutoDetectStatus.Text = L("Wizard_AutoDetect_Scanning", "Scanning game screen...");
            AutoDetectStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x40, 0xAF));

            try
            {
                var result = await Task.Run(() => AutoRegionService.Detect(gameId, engine));

                if (result.Success)
                {
                    // Save dialogue region
                    Config.Set("Region", result.DialogueRegion);
                    _notify.Region = result.DialogueRegion.Split(',');
                    _dialogueRegionSet = true;
                    UpdateDialogueStatus(result.DialogueRegion);

                    // Save answer region if detected
                    if (!string.IsNullOrEmpty(result.AnswerRegion))
                    {
                        Config.Set("AnswerRegion", result.AnswerRegion);
                        _notify.AnswerRegion = result.AnswerRegion.Split(',');
                        _answerRegionSet = true;
                        UpdateAnswerStatus(result.AnswerRegion);
                    }

                    AutoDetectStatus.Text = string.Format(
                        L("Wizard_AutoDetect_Detected_Format", "\u2713 Detected at {0} ({1})"),
                        result.Resolution, result.Method);
                    AutoDetectStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));

                    // Show detected regions on screen for 5 seconds
                    _notify.ShowRegionOverlay(TimeSpan.FromSeconds(5));

                    // Notify MainWindow so the overlap validator can warn the
                    // user if the auto-detected region overlaps where the
                    // subtitle overlay will land. Fires before we auto-advance
                    // to the summary step, so the user sees the warning while
                    // still on the region step and can re-run detection.
                    OnCaptureRegionUserChanged?.Invoke();

                    // Auto-advance to summary step after a brief delay.
                    // When answer-translation is disabled we always skip straight to
                    // Step 2 (Summary) regardless of whether auto-detect found an
                    // answer region — the AnswerRegion value is kept in config but
                    // won't be used until the feature is re-enabled.
                    await Task.Delay(1500);
                    if (_currentStep == 0)
                    {
                        // Compile-time const short-circuits the branch below when the
                        // feature is off. The CS0162 suppression acknowledges the
                        // intentional unreachable code for the duration of the disable —
                        // deleting the pragma will re-enable the warning as a reminder
                        // to clean up once FeatureAnswerTranslationEnabled flips true.
#pragma warning disable CS0162 // Unreachable code detected
                        if (!MainWindow.FeatureAnswerTranslationEnabled)
                            _currentStep = 2;
                        else
                            _currentStep = result.AnswerRegion != null ? 2 : 1;
#pragma warning restore CS0162
                        UpdateStepDisplay();
                    }
                }
                else
                {
                    AutoDetectStatus.Text = $"\u2717 {result.Error}";
                    AutoDetectStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
                }
            }
            catch (Exception ex)
            {
                AutoDetectStatus.Text = string.Format(
                    L("Wizard_AutoDetect_Failed_Format", "\u2717 Detection failed: {0}"),
                    ex.Message);
                AutoDetectStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
                Logger.Log.Error($"Auto-detect failed: {ex}");
            }
            finally
            {
                BtnAutoDetect.IsEnabled = true;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  HELPERS
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Opens the Hyperlink's target URL (e.g. the /how-to/region guide
        /// on kaption.one) in the user's default browser. Wired to every
        /// Hyperlink in this wizard's XAML via RequestNavigate.
        ///
        /// Mirrors <see cref="EulaAcceptanceWindow.Hyperlink_RequestNavigate"/>
        /// — UseShellExecute=true is required on .NET Framework for the
        /// shell to resolve the http(s) scheme handler. Failures are
        /// swallowed with a log line; the user can still manually type the
        /// URL if their default-browser registration is broken.
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
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Could not open setup-guide URL '{e.Uri}': {ex.Message}");
            }
        }

        /// <summary>
        /// Returns <c>true</c> when the region string looks like a valid
        /// comma-separated "x,y,w,h" with non-zero dimensions.
        /// </summary>
        private static bool IsRegionValid(string region)
        {
            if (string.IsNullOrWhiteSpace(region))
                return false;

            var parts = region.Split(',');
            if (parts.Length != 4)
                return false;

            // At minimum, width and height must be non-zero
            if (int.TryParse(parts[2], out int w) &&
                int.TryParse(parts[3], out int h))
            {
                return w > 0 && h > 0;
            }

            return false;
        }
    }
}
