using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.OCR;
using GI_Subtitles.Services.Translation;
using GI_Subtitles.Themes;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The app shell of the Settings window: rail navigation, the Start page (state card, packs, reader, shortcuts,
    /// what's new), the status bar chips, theme and advanced mode. Everything here reads state the window already
    /// has; nothing runs on the capture path.
    /// </summary>
    public partial class SettingsWindow
    {
        public const string AdvancedModeKey = "Ui.AdvancedMode";
        /// <summary>Start translating by itself when the game is on screen (default on).</summary>
        public const string AutoStartTranslationKey = "AutoStartTranslation";
        /// <summary>Auto-start waits for the game window to be in front (default) or only for the game to run.</summary>
        public const string AutoStartFocusKey = "AutoStartRequireFocus";

        /// <summary>Set once the user has seen what 2.5 changes (the setup sets it for new installs).</summary>
        public const string Intro25SeenKey = "Ui.Intro25Seen";

        /// <summary>The auto-start setting changed on Gra i napisy (MainWindow tells the controller).</summary>
        public Action<bool> AutoStartSettingChanged;
        /// <summary>The gate that keeps auto-start from starting, game aside (MainWindow's silent check); None when clear.</summary>
        public Func<Services.Automation.StartGate> AutoStartBlockedGate;
        /// <summary>Another game is selected than the one this process loaded; the card offers the restart (MainWindow).</summary>
        public Func<bool> GameSwitchAwaitsRestart;
        /// <summary>The game this process loaded at launch, named on the card while a switch waits for the restart (MainWindow).</summary>
        public Func<string> LoadedGame;
        /// <summary>What happened to this session's automatic dialogue area, for the card's note (MainWindow).</summary>
        public Func<RegionAutoFillNote> GetRegionAutoFillNote;
        /// <summary>A setting behind a Start gate changed here (MainWindow re-checks auto-start).</summary>
        public Action GatesChanged;
        /// <summary>Open what resolves a gate: the Start button's own dialog or page for it (MainWindow).</summary>
        public Action<Services.Automation.StartGate> OnResolveGate;
        private bool _syncingAutoStart;
        private const string AdvancedSectionKeyPrefix = "Ui.Advanced.";
        private const string WhatsNewSeenKey = "Ui.WhatsNewSeen";

        private StartCardState _cardState;
        private DateTime? _indexBuildStartedUtc;
        private DispatcherTimer _cardTicker;
        private bool _bulkExpanding;
        private string _artAsset;
        private ReleaseNote _whatsNew;
        private int _linesThisSession;
        private bool _packOnDisk;
        private IReadOnlyList<LatestPack> _latestPacks;
        private bool _regionPreviewBusy;

        /// <summary>True while the auto-start controller is armed and the game is not on screen (set by MainWindow).</summary>
        public Func<bool> IsWaitingForGame;

        /// <summary>Starts the initial pack sync again after a network failure (set by MainWindow).</summary>
        public Action OnRetryPackSync;

        private static readonly string AppVersion =
            (typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
                .Split('+')[0];

        private void InitializeShell()
        {
            ApplyVersionToShell();
            SyncThemeRadios();
            SyncAutoStartControls();
            ShowIntro25IfNew();
            RestoreAdvancedState();
            RefreshBuildCardMemory();
            InitializeLookPage();
            CharactersPage?.SyncPaletteWithApp();
            ThemeService.ThemeChanged += OnThemeChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            DictionarySyncService.DownloadProgress += OnPackDownloadProgress;
            PackAvailability.Changed += OnPackAvailabilityChanged;

            if (LogListView != null)
                ((INotifyCollectionChanged)LogListView.Items).CollectionChanged += (_, __) => UpdateLogsEmptyState();
            if (hotkeyListView != null)
                ((INotifyCollectionChanged)hotkeyListView.Items).CollectionChanged += (_, __) => UpdateHotkeysSkeleton();
            UpdateLogsEmptyState();
            UpdateHotkeysSkeleton();
            RefreshHotkeysCard();

            NavTabs.SelectionChanged += (s, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, NavTabs)) return;
                if ((NavTabs.SelectedItem as TabItem)?.Tag as string == SettingsNavigation.Game)
                {
                    _ = UpdateRegionPreviewAsync();
                    UpdateRunningGameHint();
                }
            };
            Loaded += (_, __) =>
            {
                _ = RefreshPacksCardAsync();
                _ = LoadWhatsNewAsync();
                UpdateEngineCard();
            };
            Closed += (_, __) =>
            {
                ThemeService.ThemeChanged -= OnThemeChanged;
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
                DictionarySyncService.DownloadProgress -= OnPackDownloadProgress;
                PackAvailability.Changed -= OnPackAvailabilityChanged;
            };
        }

        private void ApplyVersionToShell()
        {
            string v = string.IsNullOrEmpty(AppVersion) ? _version : AppVersion;
            if (WindowTitleBar != null) WindowTitleBar.Subtitle = string.IsNullOrEmpty(v) ? null : "v" + v;
            if (VersionChip != null)
            {
                VersionChip.Content = "v" + v;
                VersionChip.ToolTip = string.Format(CultureInfo.CurrentCulture, L("shell_Chip_Version_Tip", "Kaption {0}. Click to see what changed."), v);
                System.Windows.Automation.AutomationProperties.SetName(VersionChip, (string)VersionChip.ToolTip);
            }
        }

        // ── Navigation ─────────────────────────────────────────────────────

        /// <summary>Opens a page by id or by an old tab id (Translations, Logs, …), scrolled to the section.</summary>
        internal bool SelectTabByTag(string tag)
        {
            var (page, section) = SettingsNavigation.Resolve(tag);
            if (page == null || NavTabs == null) return false;
            var item = NavTabs.Items.OfType<TabItem>()
                .FirstOrDefault(t => string.Equals(t.Tag as string, page, StringComparison.OrdinalIgnoreCase));
            if (item == null) return false;
            item.IsSelected = true;
            if (section != null)
                Dispatcher.BeginInvoke(new Action(() => RevealSection(section)), DispatcherPriority.Loaded);
            return true;
        }

        private void RevealSection(string section)
        {
            FrameworkElement target = section switch
            {
                SettingsNavigation.SectionPacks => TranslationsList?.Parent as FrameworkElement,
                SettingsNavigation.SectionRegion => RegionCard,
                SettingsNavigation.SectionAdvanced => GameAdvancedExpander,
                SettingsNavigation.SectionCommunity => CommunityReferralsButton,
                SettingsNavigation.SectionLog => LogCard,
                _ => null,
            };
            if (target is Expander ex) ex.IsExpanded = true;
            target?.BringIntoView();
        }

        private void GoToHotkeys_Click(object sender, RoutedEventArgs e) => SelectTabByTag(SettingsNavigation.Hotkeys);
        private void GoToPacks_Click(object sender, RoutedEventArgs e) => SelectTabByTag("Translations");

        /// <summary>Shows the Characters page (the setup window's last step links here).</summary>
        internal void ShowCharactersPage()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            SelectTabByTag(SettingsNavigation.Characters);
        }

        /// <summary>Switch game the way the game buttons do (writes Config, offers the restart).</summary>
        internal void SwitchGame(string gameId)
        {
            Button pill = GamePillFor(GameRegionProfile.Get(gameId).GameId);
            if (pill != null) TranslationsGamePill_Click(pill, new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, pill));
        }

        // ── Game pickers: one entry per registered game (games/<id>.json) ───

        private IReadOnlyList<Button> _gamePills = Array.Empty<Button>();

        private void InitGamePickers()
        {
            _gamePills = BuildGamePills(GamePills, GameRegionProfile.RegisteredProfiles, TranslationsGamePill_Click);
            FillGameSelector(GameSelector, GameRegionProfile.RegisteredProfiles);
        }

        private Button GamePillFor(string gameId) =>
            _gamePills.FirstOrDefault(b => string.Equals(b.Tag as string, gameId, StringComparison.OrdinalIgnoreCase));

        /// <summary>The game pills on Gra i napisy, one per registered game. Static so the render tests build the same row.</summary>
        internal static List<Button> BuildGamePills(Panel host, IEnumerable<GameRegionProfile> games, RoutedEventHandler onClick)
        {
            host.Children.Clear();
            var pills = new List<Button>();
            foreach (GameRegionProfile game in games)
            {
                var pill = new Button
                {
                    Tag = game.GameId,
                    MinWidth = 140,
                    Padding = new Thickness(14, 0, 14, 0),
                    Margin = new Thickness(0, 0, 8, 8),
                };
                GamePickerContent.Apply(pill, game);   // a beta game carries a Beta badge
                pill.SetResourceReference(StyleProperty, "SecondaryButton");
                pill.SetResourceReference(ToolTipProperty, "Tip_Translations_Game");
                if (onClick != null) pill.Click += onClick;
                host.Children.Add(pill);
                pills.Add(pill);
            }
            return pills;
        }

        /// <summary>The hidden compatibility game combo, one item per registered game, the first selected.</summary>
        internal static void FillGameSelector(ComboBox selector, IEnumerable<GameRegionProfile> games)
        {
            selector.Items.Clear();
            foreach (GameRegionProfile game in games)
                selector.Items.Add(new ComboBoxItem { Content = game.DisplayName, Tag = game.GameId });
            if (selector.Items.Count > 0) selector.SelectedIndex = 0;
        }

        /// <summary>The one-time Genshin display-mode tip, inline on Start (the start gate calls this).</summary>
        internal void ShowDisplayModeNotice()
        {
            ShowStartPage();
            if (DisplayModeCard != null) DisplayModeCard.Visibility = Visibility.Visible;
        }

        private void DisplayModeOk_Click(object sender, RoutedEventArgs e)
        {
            Config.Set("FullscreenTipAcknowledged", true);
            DisplayModeCard.Visibility = Visibility.Collapsed;
            GatesChanged?.Invoke();
            UpdateDashboardStatus();
        }

        internal void ShowStartPage()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            SelectTabByTag(SettingsNavigation.Home);
        }

        // ── Auto-start setting (Gra i napisy) ───────────────────────────────

        // The controls show the saved settings (default on); InitializeShell calls this once the window is built.
        private void SyncAutoStartControls()
        {
            _syncingAutoStart = true;
            try
            {
                bool on = Config.Get(AutoStartTranslationKey, true);
                if (AutoTranslateToggle != null) AutoTranslateToggle.IsChecked = on;
                if (Intro25AutoToggle != null) Intro25AutoToggle.IsChecked = on;
                if (AutoTranslateFocusCheck != null) AutoTranslateFocusCheck.IsChecked = Config.Get(AutoStartFocusKey, true);
            }
            finally
            {
                _syncingAutoStart = false;
            }
        }

        /// <summary>The setup guide changed the setting: show it on Gra i napisy.</summary>
        internal void RefreshAutoStartControls() => SyncAutoStartControls();

        // XAML's IsChecked="True" fires these while InitializeComponent builds the window: only a user's change may
        // write, or every launch would turn a saved "off" back on (and the sync would meet controls not built yet).
        private bool IsUserChange => IsInitialized && !_syncingAutoStart;

        private void AutoTranslateToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsUserChange) return;
            SetAutoStart(AutoTranslateToggle.IsChecked == true, "Gra i napisy");
        }

        private void Intro25AutoToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsUserChange) return;
            SetAutoStart(Intro25AutoToggle.IsChecked == true, "the 2.5 card");
        }

        private void SetAutoStart(bool on, string where)
        {
            Config.Set(AutoStartTranslationKey, on);
            Logger.Log.Info($"Auto-start turned {(on ? "on" : "off")} on {where}.");
            AutoStartSettingChanged?.Invoke(on);
            SyncAutoStartControls();
            UpdateDashboardStatus();
        }

        /// <summary>Returning users see the 2.5 card until "Rozumiem"; new installs had the setup window.</summary>
        internal static bool ShouldShowIntro25(bool seen) => !seen;

        private void ShowIntro25IfNew()
        {
            if (Intro25Card == null) return;
            Intro25Card.Visibility = ShouldShowIntro25(Config.Get(Intro25SeenKey, false)) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Intro25Ok_Click(object sender, RoutedEventArgs e)
        {
            Config.Set(Intro25SeenKey, true);
            Intro25Card.Visibility = Visibility.Collapsed;
        }

        private void AutoTranslateFocus_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsUserChange) return;
            Config.Set(AutoStartFocusKey, AutoTranslateFocusCheck.IsChecked == true);
        }

        // ── State card and status bar ──────────────────────────────────────

        /// <summary>Called by MainWindow for every new translated line shown in the overlay.</summary>
        public void CountTranslatedLine()
        {
            _linesThisSession++;
            if (_cardState?.Kind == StartCardKind.Translating) UpdateDashboardStatus();
        }

        /// <summary>Refreshes the state card and the status chips. Safe from any thread.</summary>
        public void UpdateDashboardStatus()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateDashboardStatus));
                return;
            }
            try
            {
                UpdateDashboardActiveTranslation();
                StartCardInputs inputs = GatherCardInputs();
                _cardState = StartCardModel.Compute(inputs);
                ApplyStartCard(_cardState, inputs);
                ApplyStatusChips(_cardState, inputs);
                notifyIcon?.SetTrayState(TrayStateFor(_cardState.Kind), DashOcrDetailStatus?.Text);
                UpdateCardTicker(inputs);
                if (DashToggleSubtitleButton != null)
                {
                    bool subtitleVis = IsSubtitleVisible?.Invoke() ?? true;
                    DashToggleSubtitleButton.Content = subtitleVis ? L("Dash_HideSubtitles", "Hide Subtitles") : L("Dash_ShowSubtitles", "Show Subtitles");
                }
                RefreshHomeAdvancedDetails();
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"UpdateDashboardStatus error: {ex}");
            }
        }

        private StartCardInputs GatherCardInputs()
        {
            var dl = DictionarySyncService.CurrentDownload;
            bool dictLoaded = IsMatcherReady;
            if (dictLoaded) _indexBuildStartedUtc = null;
            int indexSeconds = _indexBuildStartedUtc.HasValue
                ? (int)Math.Max(0, (DateTime.UtcNow - _indexBuildStartedUtc.Value).TotalSeconds) : 0;
            bool downloadingTab;
            lock (_activeDownloads) downloadingTab = _activeDownloads.Count > 0;
            var state = Engine;
            bool switchPending = GameSwitchAwaitsRestart?.Invoke() ?? false;
            return new StartCardInputs
            {
                Translating = IsOcrRunning?.Invoke() ?? false,
                LinesThisSession = _linesThisSession,
                Downloading = App.StartupStatus == App.InitialStartupStatus.DownloadingTranslations || dl != null || downloadingTab,
                DownloadFraction = dl?.Fraction,
                DownloadBytesTotal = dl?.TotalBytes,
                IndexBuilding = _indexBuildStartedUtc.HasValue && !dictLoaded,
                IndexSeconds = indexSeconds,
                Reader = state == EngineStatus.Ready ? ReaderState.Ready
                    : state != EngineStatus.Failed ? ReaderState.Loading
                    : LastEngineError is GI_Subtitles.Services.OCR.OcrRuntimeUnavailableException ? ReaderState.Unavailable
                    : ReaderState.Failed,
                ReaderErrorCode = (LastEngineError as GI_Subtitles.Services.OCR.OcrRuntimeUnavailableException)?.ErrorCode,
                ReaderOnCpu = state == EngineStatus.Ready && engine != null && !engine.IsUsingGpu,
                Offline = !_online,
                PackOnDisk = dictLoaded || _packOnDisk,
                PackMissing = Matcher?.Loaded == true && !dictLoaded,
                PackComing = PackAvailability.IsComing(Config.Get("Game", "Genshin"), Config.Get("Output", "PL")),
                DeviceLimit = SessionNotices.DeviceLimitReached,
                WaitingForGame = IsWaitingForGame?.Invoke() ?? false,
                GameName = GameRegionProfile.DisplayNameOf(Config.Get("Game", "Genshin")),
                AutoStartBlockedBy = AutoStartBlockedGate?.Invoke() ?? Services.Automation.StartGate.None,
                GameSwitchPending = switchPending,
                LoadedGameName = switchPending && LoadedGame?.Invoke() is string loaded ? GameRegionProfile.DisplayNameOf(loaded) : null,
                RegionAutoFill = GetRegionAutoFillNote?.Invoke() ?? RegionAutoFillNote.None,
            };
        }

        private void ApplyStartCard(StartCardState s, StartCardInputs i)
        {
            string failure = IsAdvancedMode && LastEngineError != null ? $"{LastEngineError.GetType().Name}: {LastEngineError.Message}" : null;
            ApplyStartCardTo(FindName, s, i, L, CultureInfo.CurrentUICulture, failure);
        }

        /// <summary>
        /// Fills the state card's named elements. Static so the render tests and the guide screenshots run the same
        /// code on the window's XAML that the app runs.
        /// </summary>
        internal static void ApplyStartCardTo(Func<string, object> find, StartCardState s, StartCardInputs i,
                                              Func<string, string, string> L, CultureInfo culture, string failureDetail = null)
        {
            if (!(find("StateCardTitle") is TextBlock title)) return;
            string titleText, detail = null;
            switch (s.Kind)
            {
                case StartCardKind.Translating:
                    titleText = i.LinesThisSession > 0
                        ? string.Format(culture, L("shell_Card_Translating_Lines", "Translating · {0}"),
                            Plural.Format(L("shell_Plural_Lines", "{0:N0} line|{0:N0} lines|{0:N0} lines"), i.LinesThisSession, culture))
                        : L("shell_Card_Translating", "Translating");
                    break;
                case StartCardKind.WaitingForGame:
                    titleText = L("shell_Card_WaitingForGame", "Waiting for the game");
                    detail = string.Format(culture, L("shell_Card_WaitingForGame_Detail_Format", "Start {0} and Kaption starts by itself."),
                        i.GameName ?? "Genshin Impact");
                    break;
                case StartCardKind.Downloading:
                    titleText = i.DownloadFraction.HasValue && i.DownloadBytesTotal > 0
                        ? string.Format(culture, L("shell_Card_Downloading_Format", "Downloading subtitles {0}% of {1}"),
                            (int)Math.Round(i.DownloadFraction.Value * 100), PackStatus.FormatSize(i.DownloadBytesTotal.Value, culture))
                        : L("shell_Card_Downloading", "Downloading subtitles");
                    break;
                case StartCardKind.BuildingIndex:
                    titleText = string.Format(culture, L("shell_Card_BuildingIndex_Format", "Building the index {0} s"), i.IndexSeconds);
                    detail = L("shell_Card_BuildingIndex_Detail", "First time takes about 20 s. Translation starts by itself afterwards.");
                    break;
                case StartCardKind.StartingReader:
                    titleText = L("shell_Card_StartingReader", "Starting the text reader");
                    break;
                case StartCardKind.ReaderFailed:
                    titleText = L("shell_Card_ReaderFailed", "The text reader did not start");
                    detail = L("shell_Card_ReaderFailed_Detail", "Try again, or run it on the processor if the graphics card keeps failing.");
                    if (!string.IsNullOrEmpty(failureDetail)) detail += $"  ({failureDetail})";
                    break;
                case StartCardKind.ReaderUnavailable:
                    titleText = L("Dash_EngineFailed_Runtime_Title", "Text recognition couldn't start");
                    detail = string.Format(culture,
                        L("Dash_EngineFailed_Runtime_Body", "Windows couldn't load a component Kaption needs to read on-screen text (error {0}). Try restarting Kaption first. If it keeps happening, check that your antivirus hasn't quarantined any Kaption files, then reinstall from kaption.one."),
                        i.ReaderErrorCode ?? "?");
                    if (!string.IsNullOrEmpty(failureDetail)) detail += $"  ({failureDetail})";
                    break;
                case StartCardKind.Offline:
                    titleText = L("shell_Card_Offline", "No internet");
                    detail = L("shell_Card_Offline_NoPack", "I need the internet to download the subtitles.");
                    break;
                case StartCardKind.DeviceLimit:
                    titleText = L("shell_Card_DeviceLimit", "Device limit reached");
                    detail = L("shell_Card_DeviceLimit_Detail", "Your plan is signed in on too many PCs. Sign one out on your account page.");
                    break;
                case StartCardKind.PackMissing:
                    titleText = L("shell_Card_PackMissing", "No subtitles for this game yet");
                    detail = L("shell_Card_PackMissing_Detail", "Download the pack on Game and subtitles, then start again.");
                    break;
                case StartCardKind.PackComing:
                    titleText = L("shell_Card_PackComing", "Subtitles for this game are on the way");
                    detail = string.Format(culture, L("shell_Card_PackComing_Detail_Format",
                        "{0} is new in Kaption. When its subtitles are out, Kaption downloads them the next time you open it."),
                        i.GameName ?? string.Empty);
                    break;
                case StartCardKind.AutoStartBlocked:
                    titleText = L("shell_Card_AutoStartBlocked", "Auto-start on hold");
                    detail = s.RegionAutoFailed
                        ? L("shell_Card_RegionAutoFailed", "Kaption couldn't find the dialogue area by itself. Mark it once and translation starts.")
                        : GateReason(s.Gate, L);
                    break;
                case StartCardKind.GameSwitchPending:
                    titleText = L("Dialog_RestartGame_Title", "Restart to switch games");
                    // Without this line "Later" looked like nothing happened: say why auto-start waits.
                    if (!string.IsNullOrEmpty(i.LoadedGameName))
                        detail = string.Format(culture, L("shell_Card_GameSwitchPending_Detail_Format",
                            "Subtitles for {0} are still loaded, so auto-start waits for the restart."), i.LoadedGameName);
                    break;
                default:
                    titleText = L("shell_Card_Stopped", "Stopped · ready to start");
                    break;
            }
            if (s.ShowRegionAutoSetNote)
            {
                string note = L("shell_Card_RegionAutoSet", "Kaption set the dialogue area by itself. If subtitles miss the text, adjust the frame on Game and subtitles.");
                detail = string.IsNullOrEmpty(detail) ? note : detail + "\n" + note;
            }
            // A new state (not a new line count) fades the hero's text in; the first fill and a repeat stay still.
            bool newState = title.Tag is StartCardKind shown && shown != s.Kind;
            title.Tag = s.Kind;
            title.Text = titleText;
            if (newState && find("StateCardText") is UIElement text) Motion.Enter(text, rise: 4);
            if (find("StateCardDetail") is TextBlock detailBlock)
            {
                detailBlock.Text = detail ?? string.Empty;
                detailBlock.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            }

            string action = s.Action switch
            {
                StartCardAction.Start => L("shell_Action_Start", "_Start translating"),
                StartCardAction.Stop => L("shell_Action_Stop", "_Stop"),
                StartCardAction.StartAnyway => L("shell_Action_StartAnyway", "Start _anyway"),
                StartCardAction.Retry or StartCardAction.RetryReader => L("shell_Action_Retry", "_Try again"),
                StartCardAction.RestartApp => L("Dash_EngineFailed_Restart", "Restart Kaption"),
                StartCardAction.OpenAccount => L("shell_Action_OpenAccount", "Open my _account"),
                StartCardAction.OpenPacks => L("shell_Action_OpenPacks", "Go to the _packs"),
                StartCardAction.ResolveGate => s.RegionAutoFailed
                    ? L("shell_Card_RegionAutoFailed_Action", "_Mark the dialogue area")
                    : GateAction(s.Gate, L),
                _ => null,
            };
            if (find("DashToggleOcrButton") is Button primary)
            {
                primary.SetResourceReference(FrameworkElement.StyleProperty,
                    StartCardModel.IsGradientAction(s.Action) ? "PrimaryHeroButton" : "BandOutlineButton");
                primary.Content = action;
                primary.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
                primary.IsEnabled = action != null;
                System.Windows.Automation.AutomationProperties.SetName(primary, AccessKeyTextConverter.Strip(action ?? string.Empty));
            }
            if (find("DashChangeTranslationButton") is Button secondary)
            {
                // "Zmień grę" sits under the action in every state.
                secondary.Content = s.Kind == StartCardKind.ReaderFailed
                    ? L("shell_Action_RunOnCpu", "Run on the _processor")
                    : L("shell_Action_ChangeGame", "Change _game");
            }

            if (find("StateCardOfflineNote") is UIElement offline) offline.Visibility = s.ShowOfflineNote ? Visibility.Visible : Visibility.Collapsed;
            if (s.ShowOfflineNote && find("StateCardOfflineText") is TextBlock offlineText)
                offlineText.Text = L("shell_Card_Offline", "No internet") + " · " + L("shell_Card_Offline_Cached", "Translating from the downloaded pack.");
            if (find("StateCardCpuNote") is UIElement cpu) cpu.Visibility = s.ShowCpuNote ? Visibility.Visible : Visibility.Collapsed;
            if (find("StateCardProgress") is ProgressBar bar)
            {
                bar.Visibility = s.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
                bar.Value = s.Progress;
            }
        }

        /// <summary>Why auto-start waits, in the words of the Start button's own dialog for that gate.</summary>
        internal static string GateReason(Services.Automation.StartGate gate, Func<string, string, string> L) => gate switch
        {
            Services.Automation.StartGate.Session => L("shell_Blocked_Session", "Your sign-in has expired."),
            Services.Automation.StartGate.InitialSync => L("Dialog_StillDownloading_Title", "Translations are being updated"),
            Services.Automation.StartGate.Engine => L("Dialog_EngineLoading_Title", "Translator still loading"),
            Services.Automation.StartGate.Matcher => L("Dash_Status_TranslationDataMissing", "Translation data missing"),
            Services.Automation.StartGate.Region => L("Dialog_RegionMissing_Title", "Capture region missing"),
            Services.Automation.StartGate.DisplayModeTip => L("shell_DisplayMode_Title", "Set the game to Borderless or Windowed"),
            Services.Automation.StartGate.OverlayOverlap => L("Dialog_OverlayInRegion_Title", "Subtitles cover the game's dialogue").TrimStart('\u26a0', ' '),
            _ => string.Empty,
        };

        /// <summary>The one action that opens what resolves the gate.</summary>
        internal static string GateAction(Services.Automation.StartGate gate, Func<string, string, string> L) => gate switch
        {
            Services.Automation.StartGate.Session => L("shell_Blocked_Action_Session", "_Sign in"),
            Services.Automation.StartGate.Region => L("shell_Blocked_Action_Region", "_Pick the dialogue area"),
            Services.Automation.StartGate.DisplayModeTip => L("shell_Blocked_Action_DisplayMode", "_Show the tip"),
            Services.Automation.StartGate.OverlayOverlap => L("shell_Blocked_Action_Overlay", "_Fix the overlap"),
            _ => L("shell_Blocked_Action_Details", "Show _details"),
        };

        /// <summary>The status bar's state chip: one word and a coloured dot (grey idle, green translating, amber busy, red problem).</summary>
        internal static void ApplyStateChipTo(Func<string, object> find, StartCardState s, Func<string, string, string> L)
        {
            (string text, string dot) = s.Kind switch
            {
                StartCardKind.Translating => (L("shell_Status_Translating", "Translating"), "SuccessBrush"),
                StartCardKind.WaitingForGame => (L("shell_Status_WaitingForGame", "Waiting for the game"), "WarnBrush"),
                StartCardKind.AutoStartBlocked => (L("shell_Status_AutoStartBlocked", "Auto-start on hold"), "WarnBrush"),
                StartCardKind.GameSwitchPending => (L("shell_Status_Problem", "Needs attention"), "WarnBrush"),
                StartCardKind.Downloading or StartCardKind.BuildingIndex or StartCardKind.StartingReader
                    => (L("shell_Status_Busy", "Getting ready"), "WarnBrush"),
                StartCardKind.ReaderFailed or StartCardKind.ReaderUnavailable or StartCardKind.Offline or StartCardKind.DeviceLimit or StartCardKind.PackMissing
                    => (L("shell_Status_Problem", "Needs attention"), "ErrorBrush"),
                StartCardKind.PackComing => (L("shell_Status_Coming", "Coming soon"), "TextTertiaryBrush"),
                _ => (L("shell_Status_Stopped", "Stopped"), "TextTertiaryBrush"),
            };
            if (find("DashOcrDetailStatus") is TextBlock label) label.Text = text;
            if (find("StateChipDot") is Shape dotShape) dotShape.SetResourceReference(Shape.FillProperty, dot);
            if (find("StateChip") is FrameworkElement chip && find("StateCardTitle") is TextBlock t) chip.ToolTip = t.Text;
        }

        private void ApplyStatusChips(StartCardState s, StartCardInputs i)
        {
            if (DashOcrDetailStatus == null) return;
            ApplyStateChipTo(FindName, s, L);

            var culture = CultureInfo.CurrentUICulture;
            if (i.Reader == ReaderState.Ready)
            {
                // "GPU" and the card's product name; the model, driver ids and memory stay in the log and Pomoc › Zaawansowane.
                DashEngineStatus.Text = EngineLabels.Chip(!i.ReaderOnCpu, L);
                EngineChip.ToolTip = i.ReaderOnCpu
                    ? string.Format(culture, L("shell_Chip_Cpu_Tip", "The text reader runs on the processor. It works, it is just slower. {0}"), GpuNoteText() ?? string.Empty).Trim()
                    : (EngineLabels.ProductName(AdapterName()) is string adapter
                        ? string.Format(culture, L("shell_Chip_Gpu_Tip", "The text reader runs on your graphics card: {0}."), adapter)
                        : L("shell_Chip_Gpu_TipNoName", "The text reader runs on your graphics card."));
            }
            else
            {
                bool readerDown = i.Reader is ReaderState.Failed or ReaderState.Unavailable;
                DashEngineStatus.Text = readerDown ? L("shell_Engine_Failed", "Did not start") : L("shell_Chip_ReaderStarting", "Reader starting");
                EngineChip.ToolTip = i.Reader == ReaderState.Unavailable ? L("Dash_EngineFailed_Runtime_Title", "Text recognition couldn't start")
                    : readerDown ? L("shell_Card_ReaderFailed_Detail", "Try again.") : null;
            }
            DashEngineStatus.SetResourceReference(TextBlock.ForegroundProperty,
                i.Reader is ReaderState.Failed or ReaderState.Unavailable ? "ErrorBrush" : "TextSecondaryBrush");

            bool loaded = IsMatcherReady;
            DashDictStatus.Text = EngineLabels.SubtitlesChip(loaded, i.PackMissing, i.PackComing, L);
            DictChip.ToolTip = loaded
                ? EngineLabels.SubtitlesTip(GameRegionProfile.DisplayNameOf(Config.Get("Game", "Genshin")), HumanLang(Config.Get("Output", "PL")),
                    IsAdvancedMode, contentDict.Count, Matcher?.EntryCount ?? 0, culture, L)
                : null;
            DashDictStatus.SetResourceReference(TextBlock.ForegroundProperty, i.PackMissing && !i.PackComing ? "ErrorBrush" : "TextSecondaryBrush");
        }

        /// <summary>The tray icon's state for the Start card's state: the same groups (and colours) as the status chip.</summary>
        internal static Core.UI.TrayState TrayStateFor(StartCardKind kind) => kind switch
        {
            StartCardKind.Translating => Core.UI.TrayState.Translating,
            StartCardKind.Downloading or StartCardKind.BuildingIndex or StartCardKind.StartingReader => Core.UI.TrayState.Busy,
            StartCardKind.WaitingForGame or StartCardKind.AutoStartBlocked or StartCardKind.GameSwitchPending => Core.UI.TrayState.Waiting,
            StartCardKind.ReaderFailed or StartCardKind.ReaderUnavailable or StartCardKind.Offline or StartCardKind.DeviceLimit
                or StartCardKind.PackMissing => Core.UI.TrayState.Problem,
            _ => Core.UI.TrayState.Idle,
        };

        // Seconds tick while the index builds; nothing ticks otherwise.
        private void UpdateCardTicker(StartCardInputs i)
        {
            bool needed = i.IndexBuilding;
            if (needed && _cardTicker == null)
            {
                _cardTicker = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
                _cardTicker.Tick += (_, __) => UpdateDashboardStatus();
                _cardTicker.Start();
            }
            else if (!needed && _cardTicker != null)
            {
                _cardTicker.Stop();
                _cardTicker = null;
            }
        }

        /// <summary>CheckDataAsync calls this when it starts building or loading the translation index.</summary>
        internal void MarkIndexBuildStarted()
        {
            _indexBuildStartedUtc ??= DateTime.UtcNow;
            UpdateDashboardStatus();
        }

        private void OnStateCardPrimary()
        {
            switch (_cardState?.Action)
            {
                case StartCardAction.Start:
                case StartCardAction.Stop:
                case StartCardAction.StartAnyway:
                    OnToggleOCR?.Invoke();
                    break;
                case StartCardAction.Retry:
                    OnRetryPackSync?.Invoke();
                    _ = RefreshTranslationsAsync();
                    break;
                case StartCardAction.RetryReader:
                    OnRetryEngineLoad?.Invoke(false);
                    break;
                case StartCardAction.RestartApp:
                    // Only a new process can retry the native load or load another game; the click is the decision, so no second confirm.
                    Logger.Log.Info(_cardState.Kind == StartCardKind.GameSwitchPending
                        ? "Restarting from the state card to load the selected game."
                        : "Restarting from the state card to retry loading ONNX Runtime.");
                    AppRestartPrompt.Restart();
                    break;
                case StartCardAction.OpenAccount:
                    OpenUrlInBrowser(L("shell_Url_Account", "https://kaption.one/dashboard/"));
                    break;
                case StartCardAction.OpenPacks:
                    SelectTabByTag("Translations");
                    break;
                case StartCardAction.ResolveGate:
                    OnResolveGate?.Invoke(_cardState.Gate);
                    break;
            }
            UpdateDashboardStatus();
        }

        // Read once and then kept by the change notice: the card is rebuilt on every translated line, and asking the
        // network stack each time cost a system call per line.
        private volatile bool _online = NetworkInterface.GetIsNetworkAvailable();

        private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            _online = e.IsAvailable;
            Dispatcher.BeginInvoke(new Action(UpdateDashboardStatus));
        }

        private void OnPackDownloadProgress(DictionarySyncService.DownloadProgressInfo info) =>
            Dispatcher.BeginInvoke(new Action(UpdateDashboardStatus), DispatcherPriority.Background);

        // A listing came back: a beta game's empty list turns "download the pack" into "on the way".
        private void OnPackAvailabilityChanged() =>
            Dispatcher.BeginInvoke(new Action(UpdateDashboardStatus), DispatcherPriority.Background);

        private void OnThemeChanged(object sender, EventArgs e)
        {
            SyncThemeRadios();
            CharactersPage?.SyncPaletteWithApp();
            _ = RefreshPacksCardAsync();
            _ = UpdateRegionPreviewAsync();
            UpdateEngineCard();
        }

        // ── Game identity on the card ──────────────────────────────────────

        /// <summary>The speaker line (game), its accent and art, and the language pair under the state.</summary>
        private void UpdateDashboardActiveTranslation()
        {
            if (DashActiveTranslationText == null) return;
            try
            {
                string game = Config.Get("Game", "Genshin") ?? "Genshin";
                string input = Config.Get("Input", "EN") ?? "EN";
                string output = Config.Get("Output", "PL") ?? "PL";
                string pending = _translationRestartPending
                    ? "  ·  " + L("Dash_Status_RestartRequired", "Selected, restart required")
                    : string.Empty;
                StateCardSpeaker.Text = GameRegionProfile.DisplayNameOf(game);
                DashActiveTranslationText.Text = string.Format(CultureInfo.CurrentUICulture, L("shell_Card_Pair_Format", "{0} → {1}"),
                    HumanLang(input), HumanLang(output)) + pending;
                var accent = TryFindResource(AccentKeyFor(game)) as Brush;
                StateCardSpeaker.Foreground = accent;
                StateCardAccent.Background = accent;
                StateCardProgress.Foreground = accent;
                NavRail.UseGameAccent(NavTabs, game);
                ApplyAtmosphereTo(FindName, game, key => TryFindResource(key));
                _ = ApplyCardArtAsync(game);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"UpdateDashboardActiveTranslation failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The Start hero's atmosphere for a game: the art framed on the scene's landmark and its glow (Genshin warm gold,
        /// Star Rail blue, ZZZ orange) rising from the lower right, behind the action and away from the text. Static so the
        /// render tests draw every game.
        /// </summary>
        internal static void ApplyAtmosphereTo(Func<string, object> find, string game, Func<string, object> resource)
        {
            string asset = AmbienceArt.ForGame(game);
            if (find("StateCardArt") is ImageBrush art) art.Viewbox = ArtFrame(asset);
            if (!(find("StateCardGlow") is Border glowLayer)) return;
            glowLayer.Background = resource(GI_Subtitles.Themes.GameLook.GlowColorKey(game)) is Color c ? GlowBrush(c) : null;
        }

        /// <summary>
        /// The part of a game's art the hero band shows (relative to the image): the band is far wider than the art, so
        /// only a horizontal strip fits, centred on this frame. Genshin's strip sits higher, on the sunset, the peaks and
        /// the floating ruins, instead of the dark valley floor; Star Rail and ZZZ keep the middle (the asteroid ring,
        /// the neon street).
        /// </summary>
        internal static Rect ArtFrame(string asset) => asset switch
        {
            "genshin" => new Rect(0, 0, 1, 0.8),
            _ => new Rect(0, 0, 1, 1),
        };

        internal static Brush GlowBrush(Color c)
        {
            var glow = new RadialGradientBrush
            {
                Center = new Point(0.9, 1.05),
                GradientOrigin = new Point(0.9, 1.05),
                RadiusX = 0.55,
                RadiusY = 1.25,
            };
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x80, c.R, c.G, c.B), 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x2E, c.R, c.G, c.B), 0.5));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1));
            glow.Freeze();
            return glow;
        }

        private static string AccentKeyFor(string game) => GI_Subtitles.Themes.GameLook.AccentBrushKey(game);

        private async Task ApplyCardArtAsync(string game)
        {
            string asset = AmbienceArt.ForGame(game);
            if (asset == _artAsset) return;
            _artAsset = asset;
            ImageSource art = await AmbienceArt.LoadAsync(asset).ConfigureAwait(true);
            if (_artAsset == asset && StateCardArt != null) StateCardArt.ImageSource = art;
        }

        // ── Reader card ─────────────────────────────────────────────────────

        private string GpuNoteText()
        {
            bool quarantined = OcrGpuQuarantine.IsActiveForCurrentVersion();
            OcrGpuStatus status = OcrGpuFitness.Status(Config.Get("UseGpuOcr", true), quarantined, Engine == EngineStatus.Ready,
                engine?.IsUsingGpu == true, OcrGpuQuarantine.StrikeThisSession);
            return status switch
            {
                OcrGpuStatus.Ok => null,
                OcrGpuStatus.Unavailable => L("Dash_GpuUnavailable", "Running on the processor: the graphics card could not be started for translation. Updating the graphics driver usually fixes this; restart Kaption afterwards."),
                OcrGpuStatus.RetryNextLaunch => L("Dash_GpuRetryNextLaunch", "Running on the processor this time: the graphics card was busy or slow while Kaption started. It gets another try at the next launch."),
                _ => L("Dash_GpuQuarantined", "Running on the processor: the graphics card was too slow at startup. Tick “Use GPU acceleration” under Help › Advanced to try it again."),
            };
        }

        private static string _adapterName;
        /// <summary>The graphics card's full description (with its driver ids and memory); logged once when first read.</summary>
        private static string AdapterName()
        {
            if (_adapterName != null) return _adapterName.Length == 0 ? null : _adapterName;
            try
            {
                _adapterName = Services.Capture.DxgiScreenCapture.TryGetAdapterName(0, out string name) ? name ?? "" : "";
                if (_adapterName.Length > 0) Logger.Log.Info($"Graphics adapter: {_adapterName}");
            }
            catch
            {
                _adapterName = "";
            }
            return _adapterName.Length == 0 ? null : _adapterName;
        }

        /// <summary>
        /// Pomoc › Zaawansowane: the graphics card with its driver details, for support. Start and the status-bar chip
        /// show the product name only (the Silnik tile is gone; the chip says "GPU · PP-OCRv6").
        /// </summary>
        private void UpdateEngineCard()
        {
            if (OcrGpuAdapterText == null) return;
            string adapter = AdapterName();
            OcrGpuAdapterText.Text = adapter == null
                ? string.Empty
                : string.Format(CultureInfo.CurrentUICulture, L("start_Diag_Adapter_Format", "Graphics card: {0}"), adapter);
            OcrGpuAdapterText.Visibility = adapter == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // ── Packs card ──────────────────────────────────────────────────────

        public sealed class PackCardRow
        {
            public string Name { get; init; }
            public string Version { get; init; }
            public string Status { get; init; }
            public Brush StatusBrush { get; init; }
            public Brush Accent { get; init; }
            public string Tooltip { get; init; }
            /// <summary>The status is a "Pobierz 4.6.0-r5" link.</summary>
            public bool IsDownload { get; init; }
            public string GameId { get; init; }
            public string Language { get; init; }
            public bool IsActive { get; init; }
        }

        /// <summary>
        /// One pack row, one line: game \u00b7 installed version \u00b7 state. Amber only when the game Kaption reads has no pack;
        /// a newer pack is a quiet "Pobierz 4.6.0-r5" link; another game's missing pack quietly says it arrives when you
        /// switch. <c>SubLine</c> is the installed version, "" when there is none.
        /// </summary>
        internal static (string Text, string BrushKey, bool IsDownload, string SubLine) PackRowStatus(PackFreshness fresh,
            bool downloading, bool active, string installed, string latest, Func<string, string, string> L, CultureInfo culture)
        {
            string version = installed ?? string.Empty;
            if (downloading) return (L("shell_Packs_Downloading", "downloading\u2026"), "InfoBrush", false, version);
            return fresh switch
            {
                PackFreshness.UpToDate => ("\u2713 " + L("shell_Packs_UpToDate", "up to date"), "SuccessBrush", false, version),
                PackFreshness.UpdateAvailable => (string.Format(culture, L("shell_Packs_Download_Format", "Download {0}"), latest), "AccentTextBrush", true, version),
                PackFreshness.NotInstalled when active => (L("shell_Packs_Missing", "not downloaded"), "WarnBrush", false, version),
                PackFreshness.NotInstalled => (L("shell_Packs_OnSwitch", "downloads when you switch"), "TextTertiaryBrush", false, version),
                _ => (string.Empty, "TextTertiaryBrush", false, version),
            };
        }

        private async Task RefreshPacksCardAsync()
        {
            if (PacksCardList == null) return;
            try
            {
                string output = (Config.Get("Output", "PL") ?? "PL").ToLowerInvariant();
                string game = (Config.Get("Game", "Genshin") ?? "Genshin").ToLowerInvariant();
                var manifest = await Task.Run(DictionarySyncService.GetManifestSnapshot).ConfigureAwait(true);
                _packOnDisk = manifest.TryGetValue($"{game}/{output}", out var active) && !string.IsNullOrEmpty(active?.FileVersionId);
                _latestPacks ??= await DashboardFeeds.LatestPacksAsync().ConfigureAwait(true);
                var downloading = DictionarySyncService.CurrentDownload;
                var rows = new List<PackCardRow>();
                foreach (var profile in GameRegionProfile.RegisteredProfiles)
                {
                    string wire = profile.GameId.ToLowerInvariant();
                    string installed = manifest.TryGetValue($"{wire}/{output}", out var e) ? e?.Version : null;
                    string latest = _latestPacks?.FirstOrDefault(p => string.Equals(p.Game, wire, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(p.Language, output, StringComparison.OrdinalIgnoreCase))?.PackVersion;
                    bool isDownloading = downloading != null && string.Equals(downloading.Game, wire, StringComparison.OrdinalIgnoreCase);
                    bool isCurrent = string.Equals(wire, game, StringComparison.OrdinalIgnoreCase);
                    var (status, brush, isDownload, subLine) = PackRowStatus(PackStatus.Compare(installed, latest), isDownloading,
                        isCurrent, installed, latest, L, CultureInfo.CurrentUICulture);
                    string tip = isDownload ? L("shell_Packs_Download_Tip", "A newer pack is ready.") : null;
                    if (IsAdvancedMode && e != null) tip = (tip == null ? "" : tip + "\n") + $"{e.FileVersionId} · {e.FileSizeBytes:N0} B";
                    rows.Add(new PackCardRow
                    {
                        Name = profile.DisplayName,
                        Version = subLine,
                        Status = status,
                        StatusBrush = TryFindResource(brush) as Brush,
                        Accent = TryFindResource(AccentKeyFor(profile.GameId)) as Brush,
                        Tooltip = tip,
                        IsDownload = isDownload,
                        GameId = profile.GameId,
                        Language = output.ToUpperInvariant(),
                        IsActive = isCurrent,
                    });
                }
                PacksCardList.ItemsSource = rows;
                PacksCardList.Visibility = Visibility.Visible;
                PacksCardSkeleton.Visibility = Visibility.Collapsed;
                UpdateDashboardStatus();
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Packs card refresh failed: {ex.Message}");
            }
        }

        /// <summary>"Pobierz 4.6.0-r5" on the Start page: the same download as the pack list on Game and subtitles.</summary>
        private async void PackRowDownload_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is PackCardRow row) || row.GameId == null) return;
            if (sender is UIElement el) el.IsEnabled = false;
            try
            {
                var pack = new Models.TranslationPackInfo
                {
                    Game = row.GameId,
                    Language = row.Language,
                    GameDisplayName = row.Name,
                    LanguageDisplayName = HumanLang(row.Language),
                };
                bool installed = await DownloadPackCoreAsync(pack);
                if (installed && _lastPackSyncChangedContent && row.IsActive) PromptRestartForTranslationChange();
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Packs card download failed for {row.GameId}: {ex.Message}");
            }
            finally
            {
                _latestPacks = null;
                await RefreshPacksCardAsync();
            }
        }

        // ── Shortcuts card ──────────────────────────────────────────────────

        public sealed class HotkeyCardRow
        {
            public string Label { get; init; }
            public IReadOnlyList<string> Keys { get; init; }
            public string Spoken { get; init; }
        }

        private static readonly int[] CardHotkeyIds = { 9000, 9005, 9004, 9002 };

        private void RefreshHotkeysCard()
        {
            if (HotkeysCardList == null || _hotkeys == null) return;
            var rows = new List<HotkeyCardRow>();
            foreach (int id in CardHotkeyIds)
            {
                var vm = _hotkeys.FirstOrDefault(h => h.Id == id);
                if (vm == null) continue;
                string text = vm.DisplayText ?? string.Empty;
                rows.Add(new HotkeyCardRow
                {
                    Label = L($"shell_Hotkey_Short_{id}", vm.Description),
                    Keys = text.Split(new[] { " + " }, StringSplitOptions.RemoveEmptyEntries),
                    Spoken = text,
                });
            }
            HotkeysCardList.ItemsSource = rows;
        }

        /// <summary>Shortcuts Windows refused (another app holds them): a notice on Skróty and a strip on Start.</summary>
        public void SetHotkeyConflicts(IReadOnlyCollection<string> taken)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetHotkeyConflicts(taken)));
                return;
            }
            bool any = taken != null && taken.Count > 0;
            string list = any ? string.Join(", ", taken) : string.Empty;
            HotkeyConflictNotice.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            HotkeyConflictStrip.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (!any) return;
            var culture = CultureInfo.CurrentUICulture;
            HotkeyConflictNoticeText.Text = string.Format(culture, L("shell_Hotkey_Conflict_Notice",
                "Some shortcuts are taken by another app and do not work: {0}. Click a shortcut to set a free one."), list);
            HotkeyConflictStripText.Text = taken.Count == 1
                ? string.Format(culture, L("shell_Hotkey_Conflict", "{0} is taken by another app"), list)
                : HotkeyConflictNoticeText.Text;
        }

        private void UpdateHotkeysSkeleton()
        {
            if (HotkeysSkeleton == null || hotkeyListView == null) return;
            HotkeysSkeleton.Visibility = hotkeyListView.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            RefreshHotkeysCard();
        }

        // ── What's new ──────────────────────────────────────────────────────

        private async Task LoadWhatsNewAsync()
        {
            try
            {
                ReleaseNote note = await DashboardFeeds.LatestReleaseNoteAsync("pl").ConfigureAwait(true);
                if (note == null || WhatsNewCard == null) return;
                if (ApplyWhatsNewTo(FindName, note, Config.Get<string>(WhatsNewSeenKey, null), DateTimeOffset.UtcNow, L,
                        CultureInfo.CurrentUICulture, key => TryFindResource(key) as Brush))
                    _whatsNew = note;
            }
            catch (Exception ex)
            {
                Logger.Log.Info($"What's new card skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows the "Co nowego" card for a release note that is newer than two weeks and not dismissed. Static so the
        /// guide screenshots run the app's code; returns whether the card shows.
        /// </summary>
        internal static bool ApplyWhatsNewTo(Func<string, object> find, ReleaseNote note, string seenReleaseId, DateTimeOffset now,
                                             Func<string, string, string> L, CultureInfo culture, Func<string, Brush> brush)
        {
            if (note == null || !(find("WhatsNewCard") is UIElement card)) return false;
            bool fresh = note.PublishedAt > now.AddDays(-14);
            if (string.Equals(seenReleaseId, note.ReleaseId, StringComparison.Ordinal) || !fresh)
            {
                card.Visibility = Visibility.Collapsed;
                return false;
            }
            string gameName = GameRegionProfile.DisplayNameOf(note.Game ?? "Genshin");
            if (find("WhatsNewTitle") is TextBlock title)
                title.Text = ReleaseNoteTitle.Build(gameName, note.GameVersion, note.Kind, note.Tagline,
                    L("shell_WhatsNew_NewVersion", "{0} {1} in Polish: {2}"),
                    L("shell_WhatsNew_NewVersionNoTagline", "{0} {1} in Polish: what's new"),
                    L("shell_WhatsNew_Revision", "{0} {1} in Polish: subtitle fixes"));
            string date = note.PublishedAt.ToLocalTime().ToString("d MMMM yyyy", culture);
            if (find("WhatsNewMeta") is TextBlock meta)
                meta.Text = note.NewLines > 0
                    ? date + " · " + Plural.Format(L("shell_Plural_NewLines", "{0:N0} new line|{0:N0} new lines|{0:N0} new lines"), note.NewLines, culture)
                    : date;
            if (find("WhatsNewAccent") is Border accent) accent.Background = brush(AccentKeyFor(note.Game));
            card.Visibility = Visibility.Visible;
            return true;
        }

        private void WhatsNewOpen_Click(object sender, RoutedEventArgs e)
        {
            if (_whatsNew?.Slug == null) return;
            OpenUrlInBrowser(string.Format(CultureInfo.InvariantCulture,
                L("shell_Url_ReleaseNote_Format", "https://kaption.one/pl/aktualizacje/{0}/"), _whatsNew.Slug));
        }

        private void WhatsNewDismiss_Click(object sender, RoutedEventArgs e)
        {
            if (_whatsNew != null) Config.Set(WhatsNewSeenKey, _whatsNew.ReleaseId);
            WhatsNewCard.Visibility = Visibility.Collapsed;
        }

        // ── Dialogue area preview ───────────────────────────────────────────

        /// <summary>The dialogue-area thumbnail and its monitor; on page show and after region changes only.</summary>
        private async Task UpdateRegionPreviewAsync()
        {
            if (RegionPreviewImage == null || _regionPreviewBusy || !IsVisible) return;
            if ((NavTabs?.SelectedItem as TabItem)?.Tag as string != SettingsNavigation.Game) return;
            string region = Config.Get<string>("Region", "");
            var monitor = RegionPreview.Monitor(region);
            if (monitor == null)
            {
                RegionPreviewImage.Source = null;
                RegionPreviewEmpty.Visibility = Visibility.Visible;
                RegionMonitorText.Text = string.Empty;
                return;
            }
            RegionMonitorText.Text = string.Format(CultureInfo.CurrentUICulture, L("shell_Region_Monitor_Format", "Monitor {0} · {1}×{2}"),
                monitor.MonitorNumber, monitor.MonitorWidth, monitor.MonitorHeight);
            _regionPreviewBusy = true;
            try
            {
                var shot = await RegionPreview.CaptureAsync(this, region, TryFindResource(AccentKeyFor(Config.Get("Game", "Genshin"))) as Brush);
                if (shot?.Image != null)
                {
                    RegionPreviewImage.Source = shot.Image;
                    RegionPreviewEmpty.Visibility = Visibility.Collapsed;
                }
            }
            finally
            {
                _regionPreviewBusy = false;
            }
        }

        /// <summary>
        /// The screen rectangle to show around a dialogue area: 16:9 like the thumbnail, a little wider than the
        /// area, slid back inside the monitor at its edges rather than cut.
        /// </summary>
        internal static System.Drawing.Rectangle PreviewArea(System.Drawing.Rectangle region, System.Drawing.Rectangle screen)
        {
            double cw = Math.Max(region.Width * 1.12, region.Height * 3.2), ch = cw * 9 / 16;
            cw = Math.Min(cw, screen.Width);
            ch = Math.Min(ch, screen.Height);
            double ax = region.X + region.Width / 2.0 - cw / 2, ay = region.Y + region.Height / 2.0 - ch / 2;
            ax = Math.Max(screen.Left, Math.Min(ax, screen.Right - cw));
            ay = Math.Max(screen.Top, Math.Min(ay, screen.Bottom - ch));
            return new System.Drawing.Rectangle((int)Math.Round(ax), (int)Math.Round(ay), (int)Math.Round(cw), (int)Math.Round(ch));
        }

        /// <summary>The capture dimmed outside the area, the area outlined; rendered once at 2x the thumbnail.</summary>
        internal static BitmapSource ComposeRegionPreview(BitmapSource shot, Rect region, Brush accent)
        {
            const int W = 544, H = 306;
            double s = Math.Min(W / (double)shot.PixelWidth, H / (double)shot.PixelHeight);
            double dw = shot.PixelWidth * s, dh = shot.PixelHeight * s, ox = (W - dw) / 2, oy = (H - dh) / 2;
            var r = new Rect(ox + region.X * s, oy + region.Y * s, region.Width * s, region.Height * s);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x17)), null, new Rect(0, 0, W, H));
                dc.DrawImage(shot, new Rect(ox, oy, dw, dh));
                var dim = new SolidColorBrush(Color.FromArgb(0x99, 0x0B, 0x0D, 0x17));
                var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(ox, oy, dw, dh)), new RectangleGeometry(r));
                dc.DrawGeometry(dim, null, outside);
                dc.DrawRoundedRectangle(null, new Pen(accent, 3), r, 4, 4);
            }
            var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        // ── Running game hint ───────────────────────────────────────────────

        private string _detectedGame;
        private int _gameHintVersion;

        // The process list (tens of milliseconds) is read off the UI thread; a newer visit to the page wins.
        private async void UpdateRunningGameHint()
        {
            if (GameDetectedHint == null) return;
            int version = ++_gameHintVersion;
            string running;
            try
            {
                running = await Task.Run(GameDetection.FindRunningGame).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Logger.Log.Info($"Running-game hint skipped: {ex.Message}");
                return;
            }
            if (version != _gameHintVersion) return;
            string current = Config.Get("Game", "Genshin");
            if (running == null || string.Equals(GameRegionProfile.Get(running).GameId, GameRegionProfile.Get(current).GameId, StringComparison.OrdinalIgnoreCase))
            {
                GameDetectedHint.Visibility = Visibility.Collapsed;
                _detectedGame = null;
                return;
            }
            _detectedGame = running;
            string name = GameRegionProfile.DisplayNameOf(running);
            GameDetectedText.Text = string.Format(CultureInfo.CurrentUICulture, L("shell_Game_Detected_Format", "{0} is on screen right now."), name);
            GameDetectedSwitch.Content = string.Format(CultureInfo.CurrentUICulture, L("shell_Game_Detected_Switch", "Switch to {0}"), name);
            GameDetectedHint.Visibility = Visibility.Visible;
        }

        private void GameDetectedSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (_detectedGame == null) return;
            Button pill = GamePillFor(GameRegionProfile.Get(_detectedGame).GameId);
            if (pill != null) TranslationsGamePill_Click(pill, new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, pill));
            GameDetectedHint.Visibility = Visibility.Collapsed;
        }

        // ── Loading and empty states ────────────────────────────────────────

        private void SetTranslationsLoading(bool loading)
        {
            if (TranslationsLoading == null) return;
            TranslationsLoading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            if (loading && TranslationsEmptyState != null) TranslationsEmptyState.Visibility = Visibility.Collapsed;
        }

        private void UpdateLogsEmptyState()
        {
            if (LogsEmptyState == null || LogListView == null) return;
            LogsEmptyState.Visibility = LogListView.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Advanced sections and advanced mode ─────────────────────────────

        public bool IsAdvancedMode => Config.Get(AdvancedModeKey, false);

        private IEnumerable<Expander> AdvancedExpanders() => new[]
        {
            HomeAdvancedExpander, GameAdvancedExpander, LookAdvancedExpander, HotkeysAdvancedExpander, HelpAdvancedExpander,
        }.Where(x => x != null);

        private void RestoreAdvancedState()
        {
            bool all = IsAdvancedMode;
            _bulkExpanding = true;
            try
            {
                if (AdvancedModeToggle != null) AdvancedModeToggle.IsChecked = all;
                foreach (var ex in AdvancedExpanders())
                    ex.IsExpanded = all || Config.Get(AdvancedSectionKeyPrefix + ex.Name, false);
            }
            finally
            {
                _bulkExpanding = false;
            }
        }

        private void AdvancedExpander_Changed(object sender, RoutedEventArgs e)
        {
            if (_bulkExpanding || !(sender is Expander ex) || !ReferenceEquals(e.OriginalSource, ex)) return;
            Config.Set(AdvancedSectionKeyPrefix + ex.Name, ex.IsExpanded);
            if (ex != HomeAdvancedExpander) return;
            RefreshHomeAdvancedDetails();
            RefreshBuildCardMemory();
        }

        private void AdvancedModeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_bulkExpanding) return;
            Config.Set(AdvancedModeKey, AdvancedModeToggle.IsChecked == true);
            RestoreAdvancedState();
            UpdateDashboardStatus();
            _ = RefreshPacksCardAsync();
        }

        private void RefreshHomeAdvancedDetails()
        {
            if (HomeAdvancedDetails == null || HomeAdvancedExpander?.IsExpanded != true) return;
            var lines = new List<string>();
            if (engine != null)
                lines.Add($"Reader: {engine.ExecutionProvider} · {engine.ModelName} · GPU {(engine.IsUsingGpu ? "on" : "off")}" +
                          (AdapterName() is string a ? $" · {a}" : ""));
            else
                lines.Add($"Reader: {Engine}");
            if (Matcher != null)
                lines.Add($"Index: {Matcher.EntryCount:N0} entries · dictionary {contentDict.Count:N0}");
            var manifest = DictionarySyncService.GetManifestSnapshot();
            foreach (var kv in manifest.OrderBy(k => k.Key))
                lines.Add($"Pack {kv.Key}: {kv.Value?.Version} ({kv.Value?.FileVersionId})");
            try { lines.Add(GameOcrTuning.Describe()); } catch { /* best effort */ }
            HomeAdvancedDetails.Text = string.Join("\n", lines);
        }

        // ── Theme ───────────────────────────────────────────────────────────

        private bool _syncingTheme;

        private void SyncThemeRadios()
        {
            if (ThemeAutoRadio == null) return;
            _syncingTheme = true;
            try
            {
                ThemeAutoRadio.IsChecked = ThemeService.Preference == ThemePreference.Auto;
                ThemeDarkRadio.IsChecked = ThemeService.Preference == ThemePreference.Dark;
                ThemeLightRadio.IsChecked = ThemeService.Preference == ThemePreference.Light;
            }
            finally
            {
                _syncingTheme = false;
            }
        }

        private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncingTheme || !(sender is RadioButton rb) || !(rb.Tag is string tag)) return;
            ThemeService.SetPreference(ThemeService.Parse(tag));
        }

        // ── Links ───────────────────────────────────────────────────────────

        private void GuideSetup_Click(object sender, RoutedEventArgs e) => OpenUrlInBrowser(L("shell_Url_Guide_Setup", "https://kaption.one/setup-guide/"));
        private void GuideRegion_Click(object sender, RoutedEventArgs e) => OpenUrlInBrowser(L("shell_Url_Guide_Region", "https://kaption.one/how-to/region"));
        private void GuideBorderless_Click(object sender, RoutedEventArgs e) => OpenUrlInBrowser(L("shell_Url_Guide_Borderless", "https://kaption.one/borderless-mode/"));
        private void GuideSpeed_Click(object sender, RoutedEventArgs e) => OpenUrlInBrowser(L("shell_Url_Guide_Speed", "https://kaption.one/subtitle-speed/"));
        private void VersionChip_Click(object sender, RoutedEventArgs e) => OpenUrlInBrowser(L("shell_Url_Changelog", "https://kaption.one/changelog/"));

        private void BarAbout_Click(object sender, RoutedEventArgs e)
        {
            string v = string.IsNullOrEmpty(AppVersion) ? _version : AppVersion;
            // Version, credits and licences: the dialog says what Kaption is; its button opens the licences page.
            bool openLicences = ModernDialog.Confirm(this,
                string.Format(CultureInfo.CurrentUICulture, L("shell_About_Title", "Kaption {0}"), v),
                L("shell_About_Body", "Polish subtitles for Genshin Impact, Honkai: Star Rail and Zenless Zone Zero, drawn over the game as you play."),
                primaryText: L("shell_About_Licences", "Licences and credits"),
                secondaryText: L("Common_Close", "Close"),
                severity: DialogSeverity.Info,
                details: L("shell_About_Details", "Licence: kaption.one/legal/eula · Privacy: kaption.one/legal/privacy"));
            if (openLicences) OpenUrlInBrowser(L("shell_Url_Licenses", "https://kaption.one/licenses/"));
        }

        // ── "Więcej" in the status bar ─────────────────────────────────────

        /// <summary>Opens the overflow menu above the button, right edges aligned, with the interface language ticked.</summary>
        private void BarMore_Click(object sender, RoutedEventArgs e)
        {
            SyncBarLanguageChecks();
            BarMoreMenu.PlacementTarget = BarMoreButton;
            BarMoreMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Custom;
            BarMoreMenu.CustomPopupPlacementCallback = AboveRightAligned;
            BarMoreMenu.IsOpen = true;
        }

        internal static System.Windows.Controls.Primitives.CustomPopupPlacement[] AboveRightAligned(Size popup, Size target, Point offset) =>
            new[]
            {
                new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(target.Width - popup.Width, -popup.Height),
                    System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
            };

        private void SyncBarLanguageChecks()
        {
            string current = (UILangCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
            foreach (var item in new[] { BarLangPl, BarLangEn })
                item.IsChecked = string.Equals(item.Tag as string, current, StringComparison.OrdinalIgnoreCase);
        }

        // The menu's language rows drive the same combo as Wygląd napisów › Aplikacja, so one code path applies a language.
        private void BarLanguage_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as MenuItem)?.Tag is string tag) || UILangCombo == null) return;
            var item = UILangCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
            if (item != null && !ReferenceEquals(UILangCombo.SelectedItem, item)) UILangCombo.SelectedItem = item;
            SyncBarLanguageChecks();
        }
    }
}
