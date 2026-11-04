using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Core.Input;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Security;
using GI_Subtitles.Services.Translation;
using GI_Subtitles.Views.Shell;
using PaddleOCREngine = PaddleOCRSharp.PaddleOCREngine;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The setup window. First run: Witaj (language, licence) → Gra (the running game, its dialogue area, display
    /// mode) → Zaloguj → Przygotowuję (file key, pack download with real numbers) → Gotowe (as soon as the pack is on
    /// disk; the index builds afterwards on the Start page). Rerun ("Uruchom kreator" in Help): the same steps over the
    /// live app, through the host hooks below. Flow rules live in <see cref="OnboardingFlow"/>.
    /// </summary>
    public partial class OnboardingWindow : Window
    {
        public enum Outcome { None, Start, Characters }

        public sealed class RailItem : INotifyPropertyChanged
        {
            private string _state = "Upcoming", _label;
            public string Label
            {
                get => _label;
                set { if (_label == value) return; _label = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label))); }
            }
            public string State
            {
                get => _state;
                set { if (_state == value) return; _state = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State))); }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        private static readonly string[] StepKeys = { "Welcome", "Game", "SignIn", "Prepare", "Done" };
        private static readonly string[] RailKeys = { "onb_Rail_Welcome", "onb_Rail_Game", "onb_Rail_SignIn", "onb_Rail_Prepare", "onb_Rail_Done" };

        private readonly LicenseService _license;
        private readonly OnboardingFlow _flow;
        private readonly ObservableCollection<RailItem> _rail = new();
        private CancellationTokenSource _signInCts;
        private bool _syncingRadios, _gameEntered, _preparing, _finishing, _quitConfirmed, _regionAuto, _regionSaved, _modal;

        public Outcome Result { get; private set; }
        /// <summary>First run: what closing meant (0 start the app, 1 not signed in, 2 terms declined, 3 no file key).</summary>
        public int ExitCode { get; private set; }
        public OnboardingMode Mode => _flow.Mode;
        internal OnboardingFlow Flow => _flow;

        // ── Host hooks ──
        /// <summary>First run: wipe legacy caches and start the initial pack sync (App).</summary>
        public Action StartSync;
        /// <summary>First run: the sync StartSync started (App.InitialSyncTask).</summary>
        public Func<Task> InitialSync;
        /// <summary>Rerun: the tray's region picker with its overlap check; returns true when a region was saved.</summary>
        public Func<bool> ChooseRegion;
        /// <summary>Rerun: tell the overlay and Start about a new region.</summary>
        public Action RegionChanged;
        /// <summary>Rerun: the loaded reader, for a better automatic region.</summary>
        public Func<PaddleOCREngine> GetEngine;
        /// <summary>Rerun: switch game the way the Settings page does (restart prompt).</summary>
        public Action<string> SwitchGame;
        /// <summary>Rerun: the auto-start setting changed in the game step (MainWindow tells the controller and the page).</summary>
        public Action<bool> AutoStartChanged;
        /// <summary>Rerun: index ready, reader ready, reader on the GPU.</summary>
        public Func<(bool IndexReady, bool ReaderReady, bool OnGpu)> ReadyState;

        public OnboardingWindow(LicenseService license, OnboardingMode mode)
        {
            _license = license;
            _flow = new OnboardingFlow(mode);
            InitializeComponent();
            _gameRadios = BuildGameRadios(GameRadios, GameRegionProfile.RegisteredProfiles, Game_Checked);
            foreach (string key in RailKeys) _rail.Add(new RailItem { Label = L(key, key) });
            StepList.ItemsSource = _rail;

            _syncingRadios = true;
            (UiLanguage.Current == UiLanguage.Polish ? LangPl : LangEn).IsChecked = true;
            _syncingRadios = false;
            CrashCheck.IsChecked = Config.Get("CrashReportingEnabled", false);
            if (mode == OnboardingMode.Rerun || EulaAcceptanceWindow.IsAcceptanceCurrent())
            {
                _flow.EulaAccepted = true;
                EulaCheck.Visibility = Visibility.Collapsed;
                EulaAcceptedNote.Visibility = Visibility.Visible;
            }
            SkipAutoStartCheck.IsChecked = !Config.Get(SettingsWindow.AutoStartTranslationKey, true);
            ApplyLocalizedInlineText();
            UiLanguage.Changed += OnLanguageChanged;
            Closed += (_, __) =>
            {
                UiLanguage.Changed -= OnLanguageChanged;
                DictionarySyncService.DownloadProgress -= OnDownloadProgress;
                _signInCts?.Cancel();
            };
            Loaded += async (_, __) =>
            {
                ShowStep();
                RailArt.ImageSource = await AmbienceArt.LoadAsync(AmbienceArt.Welcome);
            };
        }

        private static string L(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        /// <summary>The first run is modal: App.OnStartup waits for it before the main window exists.</summary>
        public void ShowFirstRun()
        {
            _modal = true;
            ShowDialog();
        }

        private const string EulaFormatFallback = "I accept the {0} and the {1}.";

        private void ApplyLocalizedInlineText()
        {
            var (prefix, mid, suffix) = SplitAroundLinks(L("onb_Welcome_Eula_Format", EulaFormatFallback));
            RunEulaPrefix.Text = prefix;
            RunEulaLink1.Text = L("onb_Welcome_Eula_Link1", "licence terms");
            RunEulaMid.Text = mid;
            RunEulaLink2.Text = L("onb_Welcome_Eula_Link2", "privacy policy");
            RunEulaSuffix.Text = suffix;
        }

        /// <summary>
        /// "Akceptuję {0} i {1}." → the text around the two links. One sentence per language keeps the spaces, which
        /// XAML string resources would trim from separate pieces.
        /// </summary>
        internal static (string Prefix, string Mid, string Suffix) SplitAroundLinks(string format)
        {
            int a = format?.IndexOf("{0}", StringComparison.Ordinal) ?? -1;
            int b = a < 0 ? -1 : format.IndexOf("{1}", a + 3, StringComparison.Ordinal);
            if (b < 0) return format == EulaFormatFallback ? (string.Empty, " ", string.Empty) : SplitAroundLinks(EulaFormatFallback);
            return (format.Substring(0, a), format.Substring(a + 3, b - a - 3), format.Substring(b + 3));
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            ApplyLocalizedInlineText();
            for (int i = 0; i < _rail.Count; i++) _rail[i].Label = L(RailKeys[i], _rail[i].Label);
            RefreshStepTexts();
        }

        // ── Steps ───────────────────────────────────────────────────────────

        private void ShowStep()
        {
            var step = _flow.Step;
            WelcomePanel.Visibility = Vis(step == OnboardingStep.Welcome);
            GamePanel.Visibility = Vis(step == OnboardingStep.Game);
            SignInPanel.Visibility = Vis(step == OnboardingStep.SignIn);
            PreparePanel.Visibility = Vis(step == OnboardingStep.Prepare);
            DonePanel.Visibility = Vis(step == OnboardingStep.Done);
            RefreshStepTexts();
            for (int i = 0; i < _rail.Count; i++)
                _rail[i].State = i < _flow.Index ? "Done" : i == _flow.Index ? "Current" : "Upcoming";
            StepScroller.ScrollToTop();
            UpdateFooter();

            switch (step)
            {
                case OnboardingStep.Game: _ = EnterGameAsync(); break;
                case OnboardingStep.SignIn: EnterSignIn(); break;
                case OnboardingStep.Prepare: _ = EnterPrepareAsync(); break;
                case OnboardingStep.Done: EnterDone(); break;
            }
        }

        private void RefreshStepTexts()
        {
            Typo.SetEyebrow(StepEyebrow, string.Format(CultureInfo.CurrentUICulture, L("onb_Step_Format", "Step {0} of {1}"),
                _flow.Index + 1, OnboardingFlow.Steps.Count));
            StepHeading.SetResourceReference(TextBlock.TextProperty, $"onb_{StepKeys[_flow.Index]}_Heading");
            StepLead.SetResourceReference(TextBlock.TextProperty, $"onb_{StepKeys[_flow.Index]}_Lead");
            StepLead.Visibility = Vis(_flow.Step != OnboardingStep.Done);
        }

        private void UpdateFooter()
        {
            var step = _flow.Step;
            bool done = step == OnboardingStep.Done;
            QuitButton.Visibility = Vis(step == OnboardingStep.Welcome && Mode == OnboardingMode.FirstRun);
            BackButton.Visibility = Vis(_flow.CanGoBack);
            NextButton.Visibility = Vis(!done);
            NextButton.IsEnabled = _flow.CanAdvance;
            FinishButton.Visibility = Vis(done);
            SecondaryButton.Visibility = Vis(done);
        }

        private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

        private void Next_Click(object sender, RoutedEventArgs e) => GoNext();

        private void GoNext()
        {
            if (!_flow.CanAdvance) return;
            LeaveStep(_flow.Step);
            if (_flow.Advance()) ShowStep();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            _signInCts?.Cancel();
            if (_flow.Back()) ShowStep();
        }

        /// <summary>What a step commits when the user moves on.</summary>
        private void LeaveStep(OnboardingStep step)
        {
            switch (step)
            {
                case OnboardingStep.Welcome:
                    bool crash = CrashCheck.IsChecked == true;
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (!EulaAcceptanceWindow.IsAcceptanceCurrent())
                    {
                        // The same record the licence window writes.
                        Config.Set("EulaAcceptedVersion", EulaAcceptanceWindow.CurrentEulaVersion);
                        Config.Set("EulaAcceptedAtUnix", now);
                        Logger.Log.Info($"EULA accepted in the setup window (version={EulaAcceptanceWindow.CurrentEulaVersion}, crash_opt_in={crash}).");
                    }
                    Config.Set("CrashReportingEnabled", crash);
                    Config.Set("CrashReportingPromptShown", true);
                    Config.Set("CrashReportingPromptShownAtUnix", now);
                    try { GI_Subtitles.Services.Observability.CrashReportingService.RefreshConsent(); }
                    catch (Exception ex) { Logger.Log.Warn($"Crash consent refresh failed: {ex.Message}"); }
                    break;
                case OnboardingStep.Game:
                    // The same setting as the switch on Gra i napisy; MainWindow's auto-start reads it.
                    bool autoBefore = Config.Get(SettingsWindow.AutoStartTranslationKey, true);
                    bool auto = SkipAutoStartCheck.IsChecked != true;
                    Config.Set(SettingsWindow.AutoStartTranslationKey, auto);
                    if (auto != autoBefore) AutoStartChanged?.Invoke(auto);
                    // They read the display-mode notice here; the start gate's one-time tip is done.
                    if (GI_Subtitles.Services.Games.GameRegistry.ShowsDisplayModeTip(_flow.Game))
                        Config.Set("FullscreenTipAcknowledged", true);
                    break;
            }
        }

        // ── 1 · Witaj ───────────────────────────────────────────────────────

        private void Language_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncingRadios || !(sender is RadioButton rb) || !(rb.Tag is string tag)) return;
            UiLanguage.Apply(tag);
        }

        private void Eula_Changed(object sender, RoutedEventArgs e)
        {
            _flow.EulaAccepted = EulaCheck.IsChecked == true || EulaCheck.Visibility != Visibility.Visible;
            UpdateFooter();
        }

        private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            OpenUrl(e.Uri?.AbsoluteUri);
            e.Handled = true;
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Log.Warn($"Setup: opening {url} failed: {ex.Message}"); }
        }

        // ── 2 · Gra ─────────────────────────────────────────────────────────

        private async Task EnterGameAsync()
        {
            if (_gameEntered) { UpdateRegionUi(); return; }
            _gameEntered = true;
            _regionSaved = SavedRegionValid();
            SelectGame(Config.Get("Game", "Genshin"), persist: false);
            await DetectAsync();
        }

        private async Task DetectAsync()
        {
            DetectMark.State = MarkState.Working;
            DetectText.Text = L("onb_Game_Detecting", "Looking for the game…");
            CheckAgainButton.IsEnabled = false;
            string running = await Task.Run(GameDetection.FindRunningGame);
            CheckAgainButton.IsEnabled = true;
            if (running == null)
            {
                DetectMark.State = MarkState.Pending;
                DetectText.Text = L("onb_Game_NotRunning", "No game is running yet. Start it and click Check again, or pick the game below.");
                UpdateRegionUi();
                return;
            }
            string id = GameRegionProfile.Get(running).GameId;
            SelectGame(id, persist: true);
            // A saved area (drawn by hand, or from an earlier run) stays; "Wykryj automatycznie" replaces it on request.
            bool kept = !OnboardingFlow.ShouldAutoDetectRegion(SavedRegionValid(), userAsked: false);
            bool regionFound = !kept && await AutoDetectRegionAsync(id);
            if (kept) UpdateRegionUi();
            DetectMark.State = MarkState.Done;
            string key = kept ? "onb_Game_DetectedKept_Format" : regionFound ? "onb_Game_Detected_Format" : "onb_Game_DetectedNoRegion_Format";
            DetectText.Text = string.Format(CultureInfo.CurrentUICulture, L(key, "{0} is running."), GameRegionProfile.DisplayNameOf(id));
        }

        private void CheckAgain_Click(object sender, RoutedEventArgs e) => _ = DetectAsync();

        private IReadOnlyList<RadioButton> _gameRadios = Array.Empty<RadioButton>();

        /// <summary>The game step's radios, one per registered game. Static so the render tests build the same row.</summary>
        internal static List<RadioButton> BuildGameRadios(Panel host, IEnumerable<GameRegionProfile> games, RoutedEventHandler onChecked)
        {
            host.Children.Clear();
            var radios = new List<RadioButton>();
            foreach (GameRegionProfile game in games)
            {
                var radio = new RadioButton { GroupName = "OnbGame", Tag = game.GameId };
                GamePickerContent.Apply(radio, game);   // a beta game carries a Beta badge
                radio.SetResourceReference(StyleProperty, "SegmentRadio");
                if (onChecked != null) radio.Checked += onChecked;
                host.Children.Add(radio);
                radios.Add(radio);
            }
            return radios;
        }

        private void SelectGame(string id, bool persist)
        {
            string gameId = GameRegionProfile.Get(id ?? "Genshin").GameId;
            var radio = _gameRadios.FirstOrDefault(r => string.Equals(r.Tag as string, gameId, StringComparison.OrdinalIgnoreCase))
                        ?? _gameRadios.FirstOrDefault();
            if (radio == null) return;
            _syncingRadios = !persist;
            try
            {
                if (radio.IsChecked == true) OnGameChosen(radio.Tag as string, persist);
                else radio.IsChecked = true;
            }
            finally
            {
                _syncingRadios = false;
            }
        }

        private void Game_Checked(object sender, RoutedEventArgs e)
        {
            if (!(sender is RadioButton rb) || !(rb.Tag is string tag)) return;
            OnGameChosen(tag, persist: !_syncingRadios);
        }

        private void OnGameChosen(string id, bool persist)
        {
            _flow.Game = id;
            DisplayModeNotice.Visibility = Vis(GI_Subtitles.Services.Games.GameRegistry.ShowsDisplayModeTip(id));
            if (persist && !string.Equals(GameRegionProfile.Get(Config.Get("Game", "Genshin")).GameId, id, StringComparison.OrdinalIgnoreCase))
            {
                if (Mode == OnboardingMode.FirstRun) Config.Set("Game", id); // nothing is loaded yet: no restart needed
                else SwitchGame?.Invoke(id);
            }
            UpdateFooter();
        }

        private async Task<bool> AutoDetectRegionAsync(string gameId)
        {
            DetectRegionButton.IsEnabled = false;
            try
            {
                var engine = GetEngine?.Invoke();
                var result = await Task.Run(() => AutoRegionService.Detect(gameId, engine));
                if (result?.Success != true || string.IsNullOrEmpty(result.DialogueRegion)) return false;
                Config.Set("Region", result.DialogueRegion);
                _regionAuto = true;
                _regionSaved = false;
                RegionChanged?.Invoke();
                Logger.Log.Info($"Setup: dialogue area detected for {gameId}: {result.DialogueRegion} ({result.Method}).");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Setup: automatic region failed: {ex.Message}");
                return false;
            }
            finally
            {
                DetectRegionButton.IsEnabled = true;
                UpdateRegionUi();
            }
        }

        private async void DetectRegion_Click(object sender, RoutedEventArgs e)
        {
            if (_flow.Game != null) await AutoDetectRegionAsync(_flow.Game);
        }

        private void FixRegion_Click(object sender, RoutedEventArgs e)
        {
            var previous = WindowState;
            WindowState = WindowState.Minimized; // the picker covers the screen; this window must not
            try
            {
                if (ChooseRegion != null)
                {
                    if (!ChooseRegion()) return;
                }
                else
                {
                    string picked = RegionPicker.Pick();
                    if (picked == null) return;
                    Config.Set("Region", picked);
                }
                _regionAuto = false;
                _regionSaved = false;
                RegionChanged?.Invoke();
            }
            finally
            {
                WindowState = previous;
                Activate();
                UpdateRegionUi();
            }
        }

        private async void UpdateRegionUi()
        {
            string region = Config.Get<string>("Region", "");
            bool set = CaptureRegionValidator.TryParse(region, out _, out _, out _, out _);
            RegionStatus.Text = !set ? L("onb_Region_None", "Not set yet")
                : _regionAuto ? L("onb_Region_Auto", "Set automatically")
                : _regionSaved ? L("onb_Region_Saved", "Saved, set by hand")
                : L("onb_Region_Manual", "Drawn by hand");
            var monitor = RegionPreview.Monitor(region);
            RegionMonitor.Text = monitor == null ? string.Empty : string.Format(CultureInfo.CurrentUICulture,
                L("shell_Region_Monitor_Format", "Monitor {0} · {1}×{2}"), monitor.MonitorNumber, monitor.MonitorWidth, monitor.MonitorHeight);
            if (!set || !IsVisible) return;
            var shot = await RegionPreview.CaptureAsync(this, region, TryFindResource(AccentKey(_flow.Game)) as Brush);
            if (shot?.Image == null) return;
            RegionPreviewImage.Source = shot.Image;
            RegionPreviewEmpty.Visibility = Visibility.Collapsed;
        }

        private static bool SavedRegionValid() =>
            CaptureRegionValidator.TryParse(Config.Get<string>("Region", ""), out _, out _, out _, out _);

        private static string AccentKey(string game) => GI_Subtitles.Themes.GameLook.AccentBrushKey(game);

        private void Borderless_Click(object sender, RoutedEventArgs e) =>
            OpenUrl(L("shell_Url_Guide_Borderless", "https://kaption.one/borderless-mode/"));

        // ── 3 · Zaloguj ─────────────────────────────────────────────────────

        private void EnterSignIn()
        {
            if (_license?.IsActivated == true) ShowSignedIn();
            else if (SignInWaiting.Visibility != Visibility.Visible) ShowSignIn(SignInIdle);
        }

        private void ShowSignIn(FrameworkElement state)
        {
            foreach (var s in new FrameworkElement[] { SignInIdle, SignInWaiting, SignInError, SignInDone })
                s.Visibility = Vis(ReferenceEquals(s, state));
        }

        private void ShowSignedIn()
        {
            _flow.SignedIn = true;
            string email = _license?.CurrentActivation?.Email;
            SignInDoneText.Text = string.IsNullOrWhiteSpace(email)
                ? L("onb_SignIn_DoneNoEmail", "Signed in")
                : string.Format(CultureInfo.CurrentUICulture, L("onb_SignIn_Done_Format", "Signed in as {0}"), email);
            ShowSignIn(SignInDone);
            UpdateFooter();
        }

        private async void SignIn_Click(object sender, RoutedEventArgs e)
        {
            if (_license == null) return;
            _signInCts?.Cancel();
            _signInCts = new CancellationTokenSource();
            var ct = _signInCts.Token;
            ShowSignIn(SignInWaiting);
            ActivationResult result;
            try
            {
                result = await _license.ActivateAsync(ct).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Setup sign-in threw: {ex}");
                result = ActivationResult.Fail(ActivationFailureReason.Unknown, ex.Message);
            }
            if (ct.IsCancellationRequested && result?.Success != true) return;
            Activate(); // back in front after the browser
            if (result?.Success == true)
            {
                SessionNotices.DeviceLimitReached = false;
                ShowSignedIn();
                await Task.Delay(700).ConfigureAwait(true);
                if (_flow.Step == OnboardingStep.SignIn) GoNext();
                return;
            }
            if (result?.FailureReason == ActivationFailureReason.UserCancelled)
            {
                ShowSignIn(SignInIdle);
                return;
            }
            ShowSignInError(result);
        }

        private void ShowSignInError(ActivationResult result)
        {
            var reason = result?.FailureReason ?? ActivationFailureReason.Unknown;
            (string titleKey, string bodyKey) = reason switch
            {
                ActivationFailureReason.Timeout => ("Login_Error_Title_Timeout", "Login_Error_Timeout"),
                ActivationFailureReason.NetworkError => ("Login_Error_Title_Network", "Login_Error_Network"),
                ActivationFailureReason.MaxDevicesReached => ("Login_Error_Title_MaxDevices", "onb_SignIn_DeviceLimit"),
                ActivationFailureReason.ServerRejected => ("Login_Error_Title_ServerRejected", "Login_Error_ServerRejected"),
                ActivationFailureReason.ProviderError => ("Login_Error_Title_Provider", "Login_Error_Provider"),
                _ => ("Login_Error_Title_Generic", "Login_Error_Generic"),
            };
            SignInErrorTitle.Text = L(titleKey, "Sign-in didn't finish");
            // Unknown failures carry the link to open by hand; everything else reads better in the UI language.
            SignInErrorText.Text = reason == ActivationFailureReason.Unknown && !string.IsNullOrEmpty(result?.FailureMessage)
                ? result.FailureMessage
                : L(bodyKey, result?.FailureMessage ?? "Please try again.");
            bool deviceLimit = reason == ActivationFailureReason.MaxDevicesReached;
            SessionNotices.DeviceLimitReached = deviceLimit;
            OpenAccountButton.Visibility = Vis(deviceLimit);
            ShowSignIn(SignInError);
        }

        private void SignInCancel_Click(object sender, RoutedEventArgs e)
        {
            _signInCts?.Cancel();
            ShowSignIn(SignInIdle);
        }

        private void OpenAccount_Click(object sender, RoutedEventArgs e) =>
            OpenUrl(L("shell_Url_Account", "https://kaption.one/dashboard/"));

        // ── 4 · Przygotowuję ────────────────────────────────────────────────

        private async Task EnterPrepareAsync()
        {
            bool gpu = Config.Get("UseGpuOcr", true);
            ReaderText.Text = string.Format(CultureInfo.CurrentUICulture, L("onb_Prepare_Reader_Format", "Starting the text reader ({0})"), gpu ? "GPU" : "CPU");
            if (Mode == OnboardingMode.Rerun)
            {
                ShowRerunPrepare();
                return;
            }
            if (_preparing) return;
            _preparing = true;
            try
            {
                KeyErrorBox.Visibility = Visibility.Collapsed;
                if (!await EnsureKeyAsync()) return;
                string game = GameRegionProfile.DisplayNameOf(_flow.Game ?? Config.Get("Game", "Genshin"));
                PackMark.State = MarkState.Working;
                PackText.Text = string.Format(CultureInfo.CurrentUICulture, L("onb_Prepare_Pack_Start_Format", "Downloading Polish subtitles for {0}…"), game);
                DictionarySyncService.DownloadProgress += OnDownloadProgress;
                try
                {
                    StartSync?.Invoke();
                    Task sync = InitialSync?.Invoke();
                    if (sync != null) await sync.ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn($"Setup: initial sync failed: {ex.Message}");
                }
                finally
                {
                    DictionarySyncService.DownloadProgress -= OnDownloadProgress;
                }
                bool onDisk = PackOnDisk();
                PackBar.Visibility = Visibility.Collapsed;
                ShowPackResult(onDisk, game);
                _flow.PackSettled = true;
                UpdateFooter();
                await Task.Delay(900).ConfigureAwait(true);
                if (_flow.Step == OnboardingStep.Prepare) GoNext();
            }
            finally
            {
                _preparing = false;
            }
        }

        /// <summary>The file key, silently; only a failure shows (with Try again).</summary>
        private async Task<bool> EnsureKeyAsync()
        {
            var activation = _license?.CurrentActivation;
            if (activation?.HasDeviceFileProtectionSecret == true)
            {
                _flow.KeyReady = true;
                return true;
            }
            PackMark.State = MarkState.Working;
            PackText.Text = L("shell_Protect_Working_Title", "Getting translations ready…");
            string error = activation == null || string.IsNullOrEmpty(activation.DeviceSessionJwt)
                ? L("shell_Protect_Err_Expired", "Your sign-in expired. Restart Kaption to sign in again.")
                : await DeviceKeyBootstrap.FetchAsync(_license, activation.DeviceSessionJwt, L).ConfigureAwait(true);
            if (error == null && _license.CurrentActivation?.HasDeviceFileProtectionSecret == true)
            {
                _flow.KeyReady = true;
                return true;
            }
            _flow.KeyFailed = true;
            PackMark.State = MarkState.Pending;
            KeyErrorText.Text = error ?? L("shell_Protect_Err_Generic", "Check your internet connection and try again.");
            KeyErrorBox.Visibility = Visibility.Visible;
            return false;
        }

        private void KeyRetry_Click(object sender, RoutedEventArgs e)
        {
            _flow.KeyFailed = false;
            _ = EnterPrepareAsync();
        }

        private void OnDownloadProgress(DictionarySyncService.DownloadProgressInfo info)
        {
            if (info == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_flow.Step != OnboardingStep.Prepare) return;
                var culture = CultureInfo.CurrentUICulture;
                string game = GameRegionProfile.DisplayNameOf(info.Game);
                PackText.Text = info.TotalBytes > 0
                    ? string.Format(culture, L("onb_Prepare_Pack_Working_Format", "Downloading Polish subtitles for {0}: {1}% of {2}"),
                        game, (int)Math.Round(info.Fraction * 100), PackStatus.FormatSize(info.TotalBytes, culture))
                    : string.Format(culture, L("onb_Prepare_Pack_Start_Format", "Downloading Polish subtitles for {0}…"), game);
                PackBar.Visibility = Visibility.Visible;
                PackBar.Value = info.Fraction;
            }));
        }

        /// <summary>Ready, on the way (a beta game whose first pack is not out), or a failed download.</summary>
        private void ShowPackResult(bool onDisk, string game)
        {
            var culture = CultureInfo.CurrentUICulture;
            if (onDisk)
            {
                PackMark.State = MarkState.Done;
                PackText.Text = string.Format(culture, L("onb_Prepare_Pack_Done_Format", "Polish subtitles for {0} are ready"), game);
            }
            else if (PackComing())
            {
                PackMark.State = MarkState.Pending;
                PackText.Text = string.Format(culture, L("onb_Prepare_Pack_Coming_Format",
                    "Subtitles for {0} are on the way. When they're out, Kaption downloads them the next time you open it."), game);
            }
            else
            {
                PackMark.State = MarkState.Warning;
                PackText.Text = L("onb_Prepare_Pack_Failed", "Couldn't download the subtitles. Kaption tries again after start.");
            }
        }

        private bool PackComing() =>
            PackAvailability.IsComing(_flow.Game ?? Config.Get("Game", "Genshin"), Config.Get("Output", "PL"));

        private bool PackOnDisk()
        {
            string game = (_flow.Game ?? Config.Get("Game", "Genshin")).ToLowerInvariant();
            string lang = (Config.Get("Output", "PL") ?? "PL").ToLowerInvariant();
            return !string.IsNullOrEmpty(DictionarySyncService.GetInstalledFileVersionId(game, lang));
        }

        private void ShowRerunPrepare()
        {
            _flow.KeyReady = true;
            _flow.PackSettled = true;
            var state = ReadyState?.Invoke() ?? (false, false, false);
            bool onDisk = PackOnDisk();
            string game = GameRegionProfile.DisplayNameOf(_flow.Game ?? Config.Get("Game", "Genshin"));
            ShowPackResult(onDisk, game);
            IndexMark.State = state.IndexReady ? MarkState.Done : MarkState.Working;
            IndexText.Text = L(state.IndexReady ? "onb_Prepare_Index_Done" : "onb_Prepare_Index", "Index");
            IndexNote.Visibility = Visibility.Collapsed;
            ReaderMark.State = state.ReaderReady ? MarkState.Done : MarkState.Working;
            if (state.ReaderReady)
                ReaderText.Text = string.Format(CultureInfo.CurrentUICulture, L("onb_Prepare_Reader_Done_Format", "Text reader running ({0})"), state.OnGpu ? "GPU" : "CPU");
            ReaderNote.Visibility = Visibility.Collapsed;
            UpdateFooter();
        }

        // ── 5 · Gotowe ──────────────────────────────────────────────────────

        private void EnterDone()
        {
            var culture = CultureInfo.CurrentUICulture;
            string game = _flow.Game ?? Config.Get("Game", "Genshin");
            DoneGameText.Text = string.Format(culture, L("onb_Done_Game_Format", "Game: {0}"), GameRegionProfile.DisplayNameOf(game));
            bool regionSet = CaptureRegionValidator.TryParse(Config.Get<string>("Region", ""), out _, out _, out _, out _);
            DoneRegionMark.State = regionSet ? MarkState.Done : MarkState.Warning;
            DoneRegionText.Text = L(regionSet ? "onb_Done_Region_Set" : "onb_Done_Region_None", regionSet ? "Dialogue area: set" : "Dialogue area: not set");
            bool pack = PackOnDisk();
            bool coming = !pack && PackComing();
            DonePackMark.State = pack ? MarkState.Done : coming ? MarkState.Pending : MarkState.Warning;
            DonePackText.Text = pack ? L("onb_Done_Pack", "Subtitles: Polish")
                : coming ? L("onb_Done_PackComing", "Subtitles: on the way")
                : L("onb_Done_PackMissing", "Subtitles: not downloaded yet");
            bool auto = Config.Get(SettingsWindow.AutoStartTranslationKey, true);
            DoneStartText.Text = L(auto ? "onb_Done_AutoStart" : "onb_Done_Manual", "");
            DoneIndexText.Text = Mode == OnboardingMode.FirstRun ? L(auto ? "onb_Done_Index" : "onb_Done_IndexManual", "") : string.Empty;
            DoneIndexText.Visibility = Vis(Mode == OnboardingMode.FirstRun);
            DoneHotkeys.ItemsSource = HotkeyRows();
            if (Mode == OnboardingMode.FirstRun)
            {
                Config.Set("SetupCompleted", true);
                Config.Set("SetupDismissed", false);
                Config.Set(SettingsWindow.Intro25SeenKey, true); // the setup already said what 2.5 does
            }
            FinishButton.Focus();
        }

        /// <summary>The four most used shortcuts with the user's own bindings.</summary>
        internal static IReadOnlyList<SettingsWindow.HotkeyCardRow> HotkeyRows()
        {
            var rows = new List<SettingsWindow.HotkeyCardRow>();
            HotkeySettings settings = null;
            try { settings = HotkeySettingsManager.LoadSettings(); }
            catch (Exception ex) { Logger.Log.Warn($"Setup: hotkeys unavailable: {ex.Message}"); }
            foreach (int id in new[] { 9000, 9005, 9004, 9002 })
            {
                var h = settings?.Hotkeys?.FirstOrDefault(k => k.Id == id);
                if (h == null) continue;
                var keys = new List<string>();
                if (h.IsCtrl) keys.Add("Ctrl");
                if (h.IsShift) keys.Add("Shift");
                if (h.IsAlt) keys.Add("Alt");
                keys.Add(h.SelectedKey == '`' ? "`" : char.ToUpperInvariant(h.SelectedKey).ToString());
                rows.Add(new SettingsWindow.HotkeyCardRow
                {
                    Label = L($"shell_Hotkey_Short_{id}", h.Description),
                    Keys = keys,
                    Spoken = string.Join(" + ", keys),
                });
            }
            return rows;
        }

        private void Finish_Click(object sender, RoutedEventArgs e) => Finish(Outcome.Start);

        private void Characters_Click(object sender, RoutedEventArgs e) => Finish(Outcome.Characters);

        private void Finish(Outcome outcome)
        {
            Result = outcome;
            ExitCode = 0;
            _finishing = true;
            if (_modal) DialogResult = true;
            else Close();
        }

        // ── Leaving early ───────────────────────────────────────────────────

        private void Quit_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_finishing || Mode == OnboardingMode.Rerun)
            {
                base.OnClosing(e);
                return;
            }
            int code = _flow.ExitCodeIfClosed;
            if (code != 0 && !_quitConfirmed)
            {
                bool quit = ModernDialog.Confirm(
                    owner: this,
                    title: L("onb_QuitConfirm_Title", "Close the setup?"),
                    body: L("onb_QuitConfirm_Body", "Kaption will close. The setup starts over next time."),
                    primaryText: L("onb_QuitConfirm_Yes", "Close Kaption"),
                    secondaryText: L("onb_QuitConfirm_No", "Stay"),
                    severity: DialogSeverity.Question,
                    dangerousPrimary: true);
                if (!quit)
                {
                    e.Cancel = true;
                    return;
                }
                _quitConfirmed = true;
            }
            ExitCode = code;
            if (code == 0)
            {
                // Far enough to use the app: the key exists and the pack sync has started.
                Result = Outcome.Start;
                Config.Set("SetupCompleted", true);
                Config.Set("SetupDismissed", false);
                Config.Set(SettingsWindow.Intro25SeenKey, true);
            }
            _signInCts?.Cancel();
            base.OnClosing(e);
        }
    }
}
