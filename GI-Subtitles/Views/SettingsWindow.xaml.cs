using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Window;
using System.Net.Http;
using Newtonsoft.Json;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.StartPanel;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;
using System.Reflection;
using PaddleOCRSharp;
using System.Drawing;
using System.Threading;
using System.Windows.Markup;
using System.Collections;
using System.Globalization;
using System.Xml;
using System.ServiceModel.Syndication;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Core.Input;
using GI_Subtitles.Core.UI;
using GI_Subtitles.Models;
using GI_Subtitles.Services.Data;
using GI_Subtitles.Services.Rendering;
using GI_Subtitles.Services.Security;
using GI_Subtitles.Services.Translation;
using GI_Subtitles.Services.Video;
using GI_Subtitles.Common;
using GI_Subtitles.Core.Logging;
using GI_Subtitles.Services.Detection;
using static GI_Subtitles.Core.Config.Config;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// SettingsWindow.xaml interaction logic
    /// </summary>
    public partial class SettingsWindow : Window
    {
        public string repoUrl = "https://github.com/DimbreathBot/AnimeGameData/commits/master.atom";
        string Game = Config.Get<string>("Game") ?? "Genshin";
        // Read the OCR source language from config (persisted by the Dashboard's
        // InputLanguageCombo). Default "EN" — the English PaddleOCR model is the
        // one every build ships by default.
        string InputLanguage = Config.Get("Input", "EN") ?? "EN";
        // Read the translation-target language from config (persisted by the
        // Dashboard's OutputLanguageCombo). Default "PL" matches the Polish
        // localisation that's the headline target for v2.0.
        string OutputLanguage = Config.Get("Output", "PL") ?? "PL";
        string OutputLanguage2 = null;
        private const int MaxRetries = 1; // Maximum number of retries
        // Net8 migration: SocketsHttpHandler with PooledConnectionLifetime +
        // HTTP/2 multiplexing + Brotli. Same rationale as KaptionApiClient.
        private static readonly HttpClient client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
        }) { Timeout = TimeSpan.FromSeconds(30) };
        public Dictionary<string, string> contentDict = new Dictionary<string, string>();

        // File protection: machine-bound AES-256 encryption for proprietary data.
        // The factory chooses between the legacy embedded-AppSecret service and
        // the server-issued-key service per Config["UseServerFileProtectionKey"]
        // — see FileProtectionFactory for the selection rules.
        private readonly IFileProtectionService _protectionService = FileProtectionFactory.Create();
        private readonly FileProtectionHelper _protectionHelper;

        readonly Dictionary<string, string> OutputLanguages = new Dictionary<string, string>() { { "简体中文", "CHS" }, { "English", "EN" }, { "日本語", "JP" }, { "繁體中文", "CHT" }, { "Deutsch", "DE" }, { "Español", "ES" }, { "Français", "FR" }, { "Bahasa Indonesia", "ID" }, { "한국어", "KR" }, { "Português", "PT" }, { "Русский", "RU" }, { "ไทย", "TH" }, { "Tiếng Việt", "VI" }, { "Polski", "PL" } };
        readonly Dictionary<string, string> InputLanguages = new Dictionary<string, string>()
            {
                { "简体中文", "CHS"},
                { "English", "EN"},
                { "日本語", "JP"}
            };
        readonly Dictionary<string, string> GameDict = new Dictionary<string, string>
        {
            ["Genshin Impact"] = "Genshin",
            ["Honkai: Star Rail"] = "StarRail",
        };
        readonly Stopwatch sw = new Stopwatch();
        readonly static string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kaption");
        readonly string outpath = Path.Combine(dataDir, "out");
        public PaddleOCREngine engine;
        private Bitmap bitmap;
        double Scale = 1;
        INotifyIcon notifyIcon;
        private readonly string _version;
        // Windows API functions for registering and unregistering hotkeys
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // Hotkey constants
        private const int HOTKEY_ID_1 = 9000;
        private const int HOTKEY_ID_2 = 9001;
        private const int HOTKEY_ID_3 = 9002;
        private const int HOTKEY_ID_4 = 9003;

        private const uint MOD_NONE = 0x0000;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CTRL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;

        private IntPtr _windowHandle;
        private ObservableCollection<HotkeyViewModel> _hotkeys;
        private bool REAL_CLOSE = false;
        public OptimizedMatcher Matcher;
        public Services.Translation.IGameDialogueContext ContextEngine;
        // Used to suppress initial UILangSelector SelectionChanged events triggered by XAML default selection
        private bool _uiLangInitialized = false;

        // Dashboard action delegates — MainWindow subscribes to these
        public Action OnToggleOCR;
        public Action OnSelectRegion;
        public Action OnShowRegion;
        public Action OnToggleSubtitles;
        public Action OnOpenSetupWizard;
        /// <summary>Invoked after code paths that change the capture region
        /// via Config.Set (auto-detect, wizard completion). MainWindow wires
        /// this to clear the overlap session-override and surface a warning
        /// dialog if the new region overlaps the overlay position. Nullable
        /// because callers in pre-MainWindow startup paths may fire before
        /// the delegate is wired.</summary>
        public Action OnCaptureRegionUserChanged;

        /// <summary>Invoked after the user commits a value that changes the
        /// subtitle overlay's size or vertical position — MaxOverlayHeight,
        /// MaxOverlayWidth, Pad (vertical/horizontal). MainWindow wires this
        /// to the overlap validator so editing the overlay into a region
        /// surfaces the same warning as editing the region itself. Fires on
        /// LostFocus (not on slider-drag ticks) so sliders don't spam the
        /// dialog during drag.</summary>
        public Action OnOverlaySizeUserChanged;
        // Dashboard state queries — MainWindow sets these
        public Func<bool> IsOcrRunning;
        public Func<bool> IsSubtitleVisible;
        public Func<bool> IsEngineReady;

        /// <summary>Invoked by the Dashboard when the user clicks "Retry" on the
        /// engine-load failure banner. The handler (wired in MainWindow) reruns
        /// the background engine init with the existing Config settings. If
        /// <c>forceCpu</c> is <c>true</c> it first flips <c>UseGpuOcr=false</c>
        /// so a broken DirectML install doesn't re-fail the same way.</summary>
        public Action<bool> OnRetryEngineLoad;

        // ── Engine status (L vs R design, session 33) ─────────────────────
        // Engine init happens on a background Task.Run kicked off from
        // MainWindow_Loaded. The Dashboard + overlay both need to know where
        // it's at: amber "Loading" while the ONNX sessions spin up, green
        // "Ready" once inference is live, red "Failed" on a DirectML/model
        // error. The flag is backed by a volatile int (enum-int round-trip
        // is lossless; C# rejects enum-typed volatile) so cross-thread reads
        // see the current value without a lock.
        public enum EngineStatus
        {
            /// <summary>Background init still running. The Start button stays
            /// disabled and the Dashboard shows an amber "Loading…" pill.</summary>
            Loading,
            /// <summary>Engine's InferenceSessions are live and OCR can run.</summary>
            Ready,
            /// <summary>Init threw — DirectML missing, model file corrupt, etc.
            /// Dashboard shows a retry/CPU-fallback banner and Start stays
            /// blocked by <see cref="MainWindow.TryGateEngineReady"/>.</summary>
            Failed,
        }

        private volatile int _engineStatus = (int)EngineStatus.Loading;

        /// <summary>Current engine-init progress state. Safe to read from any
        /// thread.</summary>
        public EngineStatus Engine => (EngineStatus)_engineStatus;

        /// <summary>Last exception thrown by engine init. Populated only when
        /// <see cref="Engine"/> is <see cref="EngineStatus.Failed"/>; read by
        /// the Dashboard banner to surface a short technical-details line.
        /// Volatile via the enclosing property lock isn't needed here — the
        /// event fires AFTER the field is assigned, and subscribers run on
        /// the UI thread (<see cref="SetEngineStatus"/> marshals).</summary>
        public Exception LastEngineError { get; private set; }

        /// <summary>Fires on the UI thread whenever <see cref="Engine"/>
        /// changes. MainWindow subscribes to flip the overlay loading hint;
        /// SettingsWindow handles it internally to refresh the Dashboard.</summary>
        public event EventHandler EngineStatusChanged;

        /// <summary>Transition the engine status and marshal the event to
        /// the UI thread. No-op when the new value matches the current one
        /// so subscribers don't redraw on idempotent writes. The exception
        /// argument is retained for the Failed state and cleared otherwise
        /// — subscribers never see a stale error on a successful retry.</summary>
        public void SetEngineStatus(EngineStatus next, Exception error = null)
        {
            int prev = System.Threading.Interlocked.Exchange(ref _engineStatus, (int)next);
            LastEngineError = next == EngineStatus.Failed ? error : null;
            if (prev == (int)next) return;

            void Fire() { try { EngineStatusChanged?.Invoke(this, EventArgs.Empty); } catch { /* subscriber bug — not ours */ } }
            if (Dispatcher.CheckAccess()) Fire();
            else try { Dispatcher.BeginInvoke(new Action(Fire)); } catch (Exception ex) { Logger.Log.Warn($"EngineStatusChanged marshal failed: {ex.Message}"); }
        }

        public SettingsWindow(string version, INotifyIcon notify, double scale = 1)
        {
            _protectionHelper = new FileProtectionHelper(_protectionService);
            _version = version;
            InitializeComponent();
            // Keep the Dashboard reactive to the initial dictionary sync kicked
            // off from App.OnStartup. While it's running the Start button shows
            // a "Downloading translations…" status and stays disabled; the moment
            // the task finishes the button flips back to "Start". We also run
            // UpdateDashboardStatus once at Loaded: the initial Idle→Downloading
            // status change was fired by App.OnStartup BEFORE this window existed,
            // so the subscription alone would miss the very first render. The
            // explicit Loaded call closes that gap. Loaded is a non-reentrant
            // routed event on a single Window instance, so no unsubscribe dance
            // is needed.
            App.StartupStatusChanged += OnAppStartupStatusChanged;
            // Local engine-status event — SettingsWindow is both publisher
            // and subscriber so the Dashboard pill flips the moment the
            // background Task.Run in MainWindow completes/fails.
            EngineStatusChanged += OnEngineStatusChanged;
            Loaded += (_, __) =>
            {
                try { UpdateDashboardStatus(); }
                catch (Exception ex) { Logger.Log.Warn($"Initial Dashboard refresh failed: {ex.Message}"); }
            };
            Scale = scale;
            // Load UI language from config, default to zh-CN
            string uiLang = Config.Get("UILang", "en-US");
            ApplyLanguage(uiLang);

            // Sync UI language selector without triggering extra logic
            try
            {
                UILangSelector.SelectionChanged -= UILangSelector_SelectionChanged;
                var uiItem = UILangSelector.Items.Cast<ComboBoxItem>()
                    .FirstOrDefault(i => i.Tag is string tag && tag == uiLang);
                if (uiItem != null)
                {
                    // This may raise SelectionChanged again, but we will suppress it via _uiLangInitialized flag
                    UILangSelector.SelectedItem = uiItem;
                }
                UILangSelector.SelectionChanged += UILangSelector_SelectionChanged;
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Failed to sync UI language selector: {ex.Message}");
            }

            // Seed the visible Appearance-tab dropdown with the saved culture. Handler
            // still early-outs on !_uiLangInitialized, but setting SelectedItem before
            // the flag flips keeps the UI visibly in-sync from first paint and matches
            // the hidden UILangSelector's initialization path.
            try
            {
                if (UILangCombo != null)
                {
                    UILangCombo.SelectionChanged -= UILangCombo_SelectionChanged;
                    var comboItem = UILangCombo.Items.Cast<ComboBoxItem>()
                        .FirstOrDefault(i => i.Tag is string tag && tag == uiLang);
                    if (comboItem != null)
                    {
                        UILangCombo.SelectedItem = comboItem;
                    }
                    UILangCombo.SelectionChanged += UILangCombo_SelectionChanged;
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Failed to sync UI language combo: {ex.Message}");
            }
            // From this point on, UILangSelector_SelectionChanged should start updating config
            _uiLangInitialized = true;

            // Initialize window title with current language and version
            UpdateWindowTitle();
            // Refresh prediction stats on Dashboard every 3 seconds
            var statsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            statsTimer.Tick += (s, args) =>
            {
                if (DashPredictionStatus != null && ContextEngine?.IsLoaded == true)
                {
                    DashPredictionStatus.Text = ContextEngine.GetStats();
                    DashPredictionStatus.Foreground = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x05, 0x96, 0x69));
                }
            };
            statsTimer.Start();

            GameSelector.SelectionChanged += OnGameSelectorChanged;
            InputSelector.SelectionChanged += OnInputSelectorChanged;
            // OutputSelector uses multi-select with explicit confirm, handled by OutputSelector_SelectionChanged + OutputConfirmButton_Click
            OutputSelector.SelectionChanged += OutputSelector_SelectionChanged;
            Dictionary<string, string> InputNames = InputLanguages.ToDictionary(x => x.Value, x => x.Key);
            Dictionary<string, string> OutputNames = OutputLanguages.ToDictionary(x => x.Value, x => x.Key);
            var item = GameSelector.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == Game);
            if (item != null)
            {
                GameSelector.SelectedItem = item;
            }
            // Seed the visible input/output language combos from saved config.
            // Detach handlers first so the programmatic SelectedItem assignments
            // don't look like user-initiated changes (which would flip the
            // "Restart to apply" hint on first paint).
            try
            {
                if (InputLanguageCombo != null)
                {
                    InputLanguageCombo.SelectionChanged -= InputLanguageCombo_SelectionChanged;
                    var inputItem = InputLanguageCombo.Items.Cast<ComboBoxItem>()
                        .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), InputLanguage, StringComparison.OrdinalIgnoreCase));
                    if (inputItem != null)
                        InputLanguageCombo.SelectedItem = inputItem;
                    InputLanguageCombo.SelectionChanged += InputLanguageCombo_SelectionChanged;
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Failed to init InputLanguageCombo: {ex.Message}");
            }
            try
            {
                if (OutputLanguageCombo != null)
                {
                    OutputLanguageCombo.SelectionChanged -= OutputLanguageCombo_SelectionChanged;
                    var outputItem = OutputLanguageCombo.Items.Cast<ComboBoxItem>()
                        .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), OutputLanguage, StringComparison.OrdinalIgnoreCase));
                    if (outputItem != null)
                        OutputLanguageCombo.SelectedItem = outputItem;
                    OutputLanguageCombo.SelectionChanged += OutputLanguageCombo_SelectionChanged;
                    UpdateTranslationDirectionText(OutputLanguage);
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Failed to init OutputLanguageCombo: {ex.Message}");
            }
            item = InputSelector.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Content.ToString() == InputNames[InputLanguage]);
            if (item != null)
            {
                InputSelector.SelectedItem = item;
            }
            // Initialize output language selection (support up to 2 outputs)
            var outputItems = OutputSelector.Items.Cast<ListBoxItem>().ToList();
            if (OutputNames.TryGetValue(OutputLanguage, out var primaryName))
            {
                var primaryItem = outputItems.FirstOrDefault(i => i.Content.ToString() == primaryName);
                if (primaryItem != null)
                {
                    primaryItem.IsSelected = true;
                }
            }
            if (!string.IsNullOrEmpty(OutputLanguage2) && OutputNames.TryGetValue(OutputLanguage2, out var secondName))
            {
                var secondItem = outputItems.FirstOrDefault(i => i.Content.ToString() == secondName);
                if (secondItem != null)
                {
                    secondItem.IsSelected = true;
                }
            }
            DisplayLocalFileDates();
            // Net10 (SYSLIB0014): ServicePointManager.SecurityProtocol setting
            // no longer affects HttpClient on net5+. Removed. TLS 1.2+ is the
            // default negotiation on net8/net10 SocketsHttpHandler + Windows
            // Schannel. Keeping the old line was a no-op at best, and net10
            // started flagging it as obsolete.
            //
            // C-6 (session 21 review): previously `ServerCertificateValidationCallback += (...) => true;`
            // which disabled TLS validation process-wide. That MITM'd every subsequent
            // KaptionApiClient/UpdateService/R2 call once SettingsWindow was constructed.
            // Removed. If any legacy feature needs a self-signed mirror, scope the
            // exception to a single HttpClientHandler bound to that hostname.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3");
            if (contentDict.Count > 100)
            {
                Status.Content = $"Loaded {contentDict.Count} key-values";
            }
            if (!Directory.Exists(Path.Combine(dataDir, Game)))
            {
                Directory.CreateDirectory(Path.Combine(dataDir, Game));
            }
            notifyIcon = notify;
            DataContext = this;

            // Initialize hotkey list
            InitializeHotkeys();
            Logger.Log.Debug("InitializeHotkeys");

            // Fire-and-forget: fetch admin announcements and render banners
            // at the top of the Dashboard. Non-critical — failures are
            // swallowed silently; the user still has a working Dashboard.
            _ = LoadAnnouncementsAsync();
            // Bind button events. saveButton is kept in the tree (x:Name
            // reference for legacy code paths) but Visibility=Collapsed --
            // auto-save fires inline from HotkeyChip_PreviewKeyDown now, so
            // an explicit Save click is no longer required.
            saveButton.Click += SaveButton_Click;
            resetButton.Click += ResetButton_Click;
            // Pad - set text and slider values
            int pad = Config.GetPad(-140);
            int padHorizontal = Config.GetPadHorizontal(0);
            _suppressSliderUpdate = true;
            PadTextBox.Text = pad.ToString();
            PadHorizontalTextBox.Text = padHorizontal.ToString();
            PadVerticalSlider.Value = Math.Max(PadVerticalSlider.Minimum, Math.Min(PadVerticalSlider.Maximum, pad));
            PadHorizontalSlider.Value = Math.Max(PadHorizontalSlider.Minimum, Math.Min(PadHorizontalSlider.Maximum, padHorizontal));
            _suppressSliderUpdate = false;

            // OCR Speed
            OcrIntervalTextBox.Text = Config.Get<int>("OcrInterval", 100).ToString();
            UiRefreshTextBox.Text = Config.Get<int>("UiRefreshInterval", 200).ToString();
            StabilityWindowTextBox.Text = Config.Get<int>("StabilityWindow", 4).ToString();

            // Region: parse the string "x,y,w,h"
            var regionStr = Config.Get("Region", "763,1797,2226,110");
            var parts = regionStr.Split(',');
            if (parts.Length == 4)
            {
                RegionX.Text = parts[0];
                RegionY.Text = parts[1];
                RegionWidth.Text = parts[2];
                RegionHeight.Text = parts[3];
            }

            // Display Zone settings
            int maxHeight = Config.Get<int>("MaxOverlayHeight", 0);
            int maxWidth = Config.Get<int>("MaxOverlayWidth", 900);
            _suppressSliderUpdate = true;
            if (maxHeight > 0)
            {
                MaxHeightSlider.Value = Math.Max(MaxHeightSlider.Minimum, Math.Min(MaxHeightSlider.Maximum, maxHeight));
                MaxHeightTextBox.Text = maxHeight.ToString();
            }
            else
            {
                MaxHeightSlider.Value = MaxHeightSlider.Minimum;
                MaxHeightTextBox.Text = "Auto";
            }
            if (maxWidth > 0)
            {
                MaxWidthSlider.Value = Math.Max(MaxWidthSlider.Minimum, Math.Min(MaxWidthSlider.Maximum, maxWidth));
                MaxWidthTextBox.Text = maxWidth.ToString();
            }
            else
            {
                MaxWidthSlider.Value = MaxWidthSlider.Minimum;
                MaxWidthTextBox.Text = "Auto";
            }
            AutoShrinkCheckBox.IsChecked = Config.Get<bool>("AutoShrinkText", true);

            // Font settings
            int fontSize = Config.Get<int>("Size", 22);
            FontSizeSlider.Value = Math.Max(FontSizeSlider.Minimum, Math.Min(FontSizeSlider.Maximum, fontSize));
            FontSizeTextBox.Text = fontSize.ToString();
            string fontFamily = Config.Get("FontFamily", "Segoe UI");
            foreach (ComboBoxItem fontItem in FontFamilyComboBox.Items)
            {
                if ((string)fontItem.Tag == fontFamily) { FontFamilyComboBox.SelectedItem = fontItem; break; }
            }
            _suppressSliderUpdate = false;

            // Boolean flags
            AutoStartCheckBox.IsChecked = Config.Get("AutoStart", false);
            EnableAnswerTranslationCheckBox.IsChecked = Config.Get("EnableAnswerTranslation", false);
            CrashReportingCheckBox.IsChecked = Config.Get("CrashReportingEnabled", false);
            PlayerNameTextBox.Text = Config.Get<string>("PlayerName", "");

            // Initialize logs tab
            InitializeLogTab();

            // Initialize the Translations tab. Scan is deferred to when the
            // tab is first shown so startup stays fast; we only wire up the
            // placeholder summary here.
            InitializeTranslationsTab();
        }

        private void ResetLocation_Click(object sender, RoutedEventArgs e)
        {
            Config.Set("Pad", new int[] { -175, 0 });
            _suppressSliderUpdate = true;
            PadTextBox.Text = "-175";
            PadHorizontalTextBox.Text = "0";
            PadVerticalSlider.Value = -175;
            PadHorizontalSlider.Value = 0;
            _suppressSliderUpdate = false;
            UpdateMainWindowPosition();
        }

        private void SecondRegion_Click(object sender, RoutedEventArgs e)
        {
            notifyIcon.ChooseRegion2();
        }

        private void UILangSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Ignore initial SelectionChanged events fired during window construction
            if (!_uiLangInitialized)
            {
                return;
            }

            if (UILangSelector.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                // Delegate to unified language setter so that tray and config stay in sync
                SetUILanguage(tag);
            }
        }

        /// <summary>
        /// Visible UI-language dropdown in the Appearance tab. Mirrors the hidden
        /// <c>UILangSelector</c> combo — both funnel through <see cref="SetUILanguage"/>
        /// so the tray menu, hotkey labels and the window title stay in lockstep.
        /// After the user picks a new culture we offer an immediate restart, because
        /// a handful of cached strings (DynamicResource bindings that resolved during
        /// window construction, plus any RenderTransform/size values baked into the
        /// layout) only refresh cleanly on a fresh process.
        /// </summary>
        private void UILangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Suppress the initial selection event that fires while we're still wiring
            // up the window in the constructor — SetUILanguage would clobber the saved
            // Config value with the default-selected combo entry otherwise.
            if (!_uiLangInitialized)
            {
                return;
            }

            if (!(UILangCombo?.SelectedItem is ComboBoxItem item) || !(item.Tag is string tag))
            {
                return;
            }

            string currentLang = Config.Get("UILang", "en-US");
            if (string.Equals(tag, currentLang, StringComparison.OrdinalIgnoreCase))
            {
                // No-op — user reselected the culture already in effect.
                return;
            }

            // Apply live (best-effort: resource dictionaries swap immediately, so most
            // DynamicResource bindings repaint without a restart).
            try
            {
                SetUILanguage(tag);
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"UILangCombo: failed to apply '{tag}': {ex.Message}");
            }

            // Offer a restart so any strings that were resolved once at window-build
            // time (tab headers read as StaticResource in some styles, cached tooltips,
            // etc.) get refreshed. If the user says no, the change persists across
            // restarts anyway because SetUILanguage already wrote UILang to Config.
            try
            {
                GI_Subtitles.Views.AppRestartPrompt.PromptAndRestart(
                    owner: this,
                    title: L("Dialog_LangRestart_Title", "Restart to apply"),
                    body: L("Dialog_LangRestart_Body", "Kaption needs to restart to apply the new language. Restart now?"),
                    restartButtonText: L("Dialog_LangRestart_Restart", "Restart"),
                    laterButtonText: L("Dialog_LangRestart_Later", "Later"),
                    severity: GI_Subtitles.Views.DialogSeverity.Question);
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"UILangCombo: restart-prompt failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Set UI language from any caller (tray menu or settings window).
        /// Keeps Config, resources, tray text, hotkeys and selector in sync.
        /// </summary>
        /// <param name="cultureTag">Culture tag such as zh-CN / en-US / ja-JP.</param>
        public void SetUILanguage(string cultureTag)
        {
            try
            {
                // Temporarily suppress SelectionChanged side effects
                _uiLangInitialized = false;

                // Apply language resources and persist configuration
                ApplyLanguage(cultureTag);
                Config.Set("UILang", cultureTag);

                // Sync combo box selection if it exists (even if hidden)
                if (UILangSelector != null)
                {
                    UILangSelector.SelectionChanged -= UILangSelector_SelectionChanged;
                    var uiItem = UILangSelector.Items.Cast<ComboBoxItem>()
                        .FirstOrDefault(i => i.Tag is string tag && tag == cultureTag);
                    if (uiItem != null)
                    {
                        UILangSelector.SelectedItem = uiItem;
                    }
                    UILangSelector.SelectionChanged += UILangSelector_SelectionChanged;
                }

                // Sync the visible Appearance-tab dropdown (introduced with pl-PL).
                // Unsubscribe/resubscribe mirrors the hidden selector so the programmatic
                // update doesn't round-trip back through the restart-prompt handler.
                if (UILangCombo != null)
                {
                    UILangCombo.SelectionChanged -= UILangCombo_SelectionChanged;
                    var comboItem = UILangCombo.Items.Cast<ComboBoxItem>()
                        .FirstOrDefault(i => i.Tag is string tag && tag == cultureTag);
                    if (comboItem != null)
                    {
                        UILangCombo.SelectedItem = comboItem;
                    }
                    UILangCombo.SelectionChanged += UILangCombo_SelectionChanged;
                }

                // Refresh tray menu texts
                if (notifyIcon != null)
                {
                    notifyIcon.RefreshMenuTexts();
                }

                // Re-initialize hotkeys to update descriptions with new language
                InitializeHotkeys();

                // Update window title so that it reflects the new language
                UpdateWindowTitle();
            }
            finally
            {
                // Re-enable SelectionChanged handling
                _uiLangInitialized = true;
            }
        }

        /// <summary>
        /// Update the Settings window title based on current language resources and version.
        /// </summary>
        private void UpdateWindowTitle()
        {
            try
            {
                string baseTitle = System.Windows.Application.Current?
                    .TryFindResource("App_Settings") as string;

                if (string.IsNullOrWhiteSpace(baseTitle))
                {
                    // Fallback to existing title if resource is missing
                    baseTitle = this.Title;
                }

                if (!string.IsNullOrEmpty(_version))
                {
                    this.Title = $"{baseTitle} ({_version})";
                }
                else
                {
                    this.Title = baseTitle;
                }
            }
            catch
            {
                // In case of any unexpected error, keep the current title
            }
        }

        private void ApplyLanguage(string cultureTag)
        {
            // Optional: set the thread culture (if you need it elsewhere)
            try
            {
                var culture = new CultureInfo(cultureTag);
                CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
            }
            catch (Exception ex) { Logger.Log.Error($"Invalid culture '{cultureTag}': {ex.Message}"); }

            // First remove the old language resources
            var oldLangs = System.Windows.Application.Current.Resources.MergedDictionaries
                .Where(d => d.Source != null && d.Source.OriginalString.Contains("Resources/Strings"))
                .ToList();
            foreach (var d in oldLangs)
                System.Windows.Application.Current.Resources.MergedDictionaries.Remove(d);

            // Merge the new language resources. Load en-US first as the BASE dictionary so
            // that any keys missing from the selected locale fall back to English instead
            // of rendering raw resource keys (WPF merged-dict lookup walks in reverse order,
            // so the overlay culture wins when present and en-US fills every gap).
            var enBase = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Resources/Strings.en-US.xaml", UriKind.Absolute)
            };
            System.Windows.Application.Current.Resources.MergedDictionaries.Add(enBase);

            // Only layer a second dictionary on top when the requested culture is something
            // other than en-US — loading en-US twice would be wasteful.
            if (!string.Equals(cultureTag, "en-US", StringComparison.OrdinalIgnoreCase))
            {
                var rd = new ResourceDictionary();
                switch (cultureTag)
                {
                    case "pl-PL":
                        rd.Source = new Uri("pack://application:,,,/Resources/Strings.pl-PL.xaml", UriKind.Absolute);
                        break;
                    case "ja-JP":
                        rd.Source = new Uri("pack://application:,,,/Resources/Strings.ja-JP.xaml", UriKind.Absolute);
                        break;
                    case "zh-CN":
                        rd.Source = new Uri("pack://application:,,,/Resources/Strings.zh-CN.xaml", UriKind.Absolute);
                        break;
                    default:
                        // Unknown culture — en-US base is already loaded, nothing to overlay.
                        rd = null;
                        break;
                }
                if (rd != null)
                {
                    System.Windows.Application.Current.Resources.MergedDictionaries.Add(rd);
                }
            }

            // Force refresh the bindings on the window
            this.InvalidateVisual();
        }

        public async Task Load()
        {
            // v2.0.0+: run the bootstrap orchestrator first so missing files
            // (TextMap{Input}.json from GitHub, TextMap{Output}.gisub from R2)
            // are fetched BEFORE CheckDataAsync tries to build a matcher.
            // The previous flow bailed out silently on fresh installs
            // (FileExists()==false → no matcher → infinite "Matcher not
            // loaded yet" warnings).
            await EnsureGameDataReadyAsync();
            await CheckDataAsync();
        }

        /// <summary>
        /// First-run bootstrap hook. Idempotent: a cached install sees this
        /// resolve instantly (everything already on disk). Never throws —
        /// surfaces failures via the status bar + log. Runs before the
        /// matcher build path in <see cref="CheckDataAsync"/>.
        /// </summary>
        private async Task EnsureGameDataReadyAsync()
        {
            try
            {
                var license = App.LicenseService;
                if (license == null)
                {
                    // Pre-activation — activation gate hasn't run yet. The
                    // License service is initialised in App.OnStartup before
                    // MainWindow loads, so this should be impossible; guard
                    // defensively for safety.
                    Logger.Log.Warn("EnsureGameDataReady: LicenseService not initialised yet — skipping bootstrap.");
                    return;
                }

                var progress = new Progress<(int percent, string message)>(p =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (Status != null) Status.Content = p.message;
                        if (DownloadProgressBar != null)
                        {
                            DownloadProgressBar.Visibility = Visibility.Visible;
                            DownloadProgressBar.Value = p.percent;
                        }
                    }));
                });

                var bootstrap = new GI_Subtitles.Services.Data.GameDataBootstrapService(
                    license,
                    GI_Subtitles.Services.Security.FileProtectionFactory.Create());

                using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10)))
                {
                    var result = await bootstrap.EnsureReadyAsync(
                        Game, InputLanguage, OutputLanguage, progress, cts.Token).ConfigureAwait(false);

                    if (!result.Ready)
                    {
                        Logger.Log.Warn($"Bootstrap: not ready — {result.FailureReason ?? "(no reason given)"}");
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (Status != null) Status.Content = result.FailureReason ?? "Language data not ready.";
                            if (DownloadProgressBar != null) DownloadProgressBar.Visibility = Visibility.Collapsed;
                        });
                    }
                    else
                    {
                        Logger.Log.Info(
                            $"Bootstrap: ready — inputDownloaded={result.InputDownloaded} outputDownloaded={result.OutputDownloaded}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"EnsureGameDataReady failed: {ex}");
            }
        }

        public void RefreshUrl()
        {
            InputLangDownloadUrl.Text = $"https://raw.githubusercontent.com/DimbreathBot/AnimeGameData/refs/heads/master/TextMap/TextMap{InputLanguage}.json";
            OutputLangDownloadUrl.Text = $"https://raw.githubusercontent.com/DimbreathBot/AnimeGameData/refs/heads/master/TextMap/TextMap{OutputLanguage}.json";
            // Second output language download url (if configured)
            if (!string.IsNullOrEmpty(OutputLanguage2))
            {
                OutputLangDownloadUrl2.Text = $"https://raw.githubusercontent.com/DimbreathBot/AnimeGameData/refs/heads/master/TextMap/TextMap{OutputLanguage2}.json";
            }
            else
            {
                OutputLangDownloadUrl2.Text = string.Empty;
            }
            if (Game == "StarRail")
            {
                repoUrl = "https://gitlab.com/Dimbreath/turnbasedgamedata/-/refs/main/logs_tree/?format=json&offset=0&ref_type=HEADS";
                InputLangDownloadUrl.Text = $"https://gitlab.com/Dimbreath/turnbasedgamedata/-/raw/main/TextMap/TextMap{InputLanguage}.json?inline=false";
                OutputLangDownloadUrl.Text = $"https://gitlab.com/Dimbreath/turnbasedgamedata/-/raw/main/TextMap/TextMap{OutputLanguage}.json?inline=false";
                if (!string.IsNullOrEmpty(OutputLanguage2))
                {
                    OutputLangDownloadUrl2.Text = $"https://gitlab.com/Dimbreath/turnbasedgamedata/-/raw/main/TextMap/TextMap{OutputLanguage2}.json?inline=false";
                }
                else
                {
                    OutputLangDownloadUrl2.Text = string.Empty;
                }
            }
            else if (Game == "Zenless")
            {
                repoUrl = "https://git.mero.moe/dimbreath/ZenlessData";
                InputLangDownloadUrl.Text = ZenlessUrl(InputLanguage);
                OutputLangDownloadUrl.Text = ZenlessUrl(OutputLanguage);
                if (!string.IsNullOrEmpty(OutputLanguage2))
                {
                    OutputLangDownloadUrl2.Text = ZenlessUrl(OutputLanguage2);
                }
                else
                {
                    OutputLangDownloadUrl2.Text = string.Empty;
                }
            }
            else if (Game == "Wuthering")
            {
                repoUrl = "https://github.com/Dimbreath/WutheringData/commits/master.atom";
                InputLangDownloadUrl.Text = WutheringUrl(InputLanguage);
                OutputLangDownloadUrl.Text = WutheringUrl(OutputLanguage);
                if (!string.IsNullOrEmpty(OutputLanguage2))
                {
                    OutputLangDownloadUrl2.Text = WutheringUrl(OutputLanguage2);
                }
                else
                {
                    OutputLangDownloadUrl2.Text = string.Empty;
                }
            }
            else if (Game == "Endfield")
            {
                repoUrl = "https://github.com/XiaBei-cy/EndfieldData/commits/master.atom";
                InputLangDownloadUrl.Text = EndfieldUrl(InputLanguage);
                OutputLangDownloadUrl.Text = EndfieldUrl(OutputLanguage);
                if (!string.IsNullOrEmpty(OutputLanguage2))
                {
                    OutputLangDownloadUrl2.Text = EndfieldUrl(OutputLanguage2);
                }
                else
                {
                    OutputLangDownloadUrl2.Text = string.Empty;
                }
            }
        }

        private string ZenlessUrl(string language)
        {
            string url = "https://git.mero.moe/dimbreath/ZenlessData/raw/branch/master/TextMap/TextMapTemplateTb.json";
            if (language != "CHS")
            {
                if (language == "JP")
                {
                    language = "JA";
                }
                url = $"https://git.mero.moe/dimbreath/ZenlessData/raw/branch/master/TextMap/TextMap_{language}TemplateTb.json";
            }
            return url;
        }

        private string WutheringUrl(string language)
        {
            string url = "https://raw.githubusercontent.com/Dimbreath/WutheringData/refs/heads/master/TextMap/zh-Hans/MultiText.json";
            if (language != "CHS")
            {
                if (language == "JP")
                {
                    language = "JA";
                }
                url = $"https://raw.githubusercontent.com/Dimbreath/WutheringData/refs/heads/master/TextMap/{language.ToLower()}/MultiText.json";
            }
            return url;
        }

        private string EndfieldUrl(string language)
        {
            string url = "https://raw.githubusercontent.com/XiaBei-cy/EndfieldData/refs/heads/master/i18n/I18nTextTable_CN.json";
            if (language != "CHS")
            {
                url = $"https://raw.githubusercontent.com/XiaBei-cy/EndfieldData/refs/heads/master/i18n/I18nTextTable_{language.ToUpper()}.json";
            }
            return url;
        }

        private async void OnGameSelectorChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(sender is System.Windows.Controls.ComboBox comboBox))
            {
                return;
            }

            if (comboBox.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is string tag)
            {
                string newValue = tag;
                if (Game != newValue)
                {
                    string previousGame = Game;
                    Game = newValue;
                    if (!Directory.Exists(Path.Combine(dataDir, Game)))
                    {
                        Directory.CreateDirectory(Path.Combine(dataDir, Game));
                    }
                    DisplayLocalFileDates();
                    Config.Set("Game", newValue);

                    // Prompt before the heavy CheckDataAsync rebuild — if the
                    // user picks Restart we skip the work that a fresh launch
                    // will redo anyway.
                    PromptRestartForGameChange(previousGame, newValue);

                    await CheckDataAsync(true);

                    // Retarget the Translations tab at the newly-selected
                    // game. Two steps: refresh the existing filtered view
                    // immediately (so Genshin rows vanish the instant the
                    // user picks Star Rail) and kick off a fresh scan so
                    // the remote catalog / pack list is actually current
                    // for this game.
                    _translationsView?.Refresh();
                    _ = RefreshTranslationsAsync();
                }
            }
        }

        private async void OnInputSelectorChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(sender is System.Windows.Controls.ComboBox comboBox))
            {
                return;
            }

            if (comboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                string newValue = InputLanguages[selectedItem.Content.ToString()];
                if (InputLanguage != newValue)
                {
                    InputLanguage = newValue;
                    DisplayLocalFileDates();
                    Config.Set("Input", InputLanguage);
                    await CheckDataAsync(true);
                }
            }
        }

        /// <summary>
        /// OCR source language selector. Persists the chosen Tag to
        /// <c>Config["Input"]</c>. MainWindow reads <c>InputLanguage</c> once
        /// at construction and the PaddleOCR recognizer model is loaded for
        /// it, so a live swap needs a restart — we surface a shared hint.
        /// </summary>
        private void InputLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(InputLanguageCombo?.SelectedItem is ComboBoxItem selected)) return;
            string tag = selected.Tag?.ToString();
            if (string.IsNullOrEmpty(tag)) return;

            string previous = Config.Get("Input", "EN") ?? "EN";
            // Keep direction badge in sync regardless of whether the value
            // actually changed — covers edge cases like programmatic reseeds.
            UpdateTranslationDirectionText(OutputLanguage);

            if (string.Equals(previous, tag, StringComparison.OrdinalIgnoreCase))
                return;

            Config.Set("Input", tag);
            Logger.Log.Info($"Input language changed: {previous} -> {tag}.");

            if (OutputLangRestartHint != null)
                OutputLangRestartHint.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Translation-target selector. Persists the chosen Tag to
        /// <c>Config["Output"]</c>. Because MainWindow reads
        /// <c>OutputLanguage</c> once at construction and the matcher/dict
        /// caches are rebuilt around it, a live swap requires an app restart
        /// — we surface a shared "Restart to apply" hint rather than silently
        /// lying about the current state.
        /// </summary>
        private void OutputLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(OutputLanguageCombo?.SelectedItem is ComboBoxItem selected)) return;
            string tag = selected.Tag?.ToString();
            if (string.IsNullOrEmpty(tag)) return;

            string previous = Config.Get("Output", "PL") ?? "PL";
            UpdateTranslationDirectionText(tag);

            if (string.Equals(previous, tag, StringComparison.OrdinalIgnoreCase))
                return;

            Config.Set("Output", tag);
            Logger.Log.Info($"Output language changed: {previous} -> {tag}.");

            if (OutputLangRestartHint != null)
                OutputLangRestartHint.Visibility = Visibility.Visible;
        }

        /// <summary>Refresh the hidden compatibility direction badge. Kept
        /// for any external code that still reads it; the visible UX is the
        /// two combos themselves with an arrow between them.</summary>
        private void UpdateTranslationDirectionText(string outputTag)
        {
            if (TranslationDirectionText == null) return;
            string safeOut = string.IsNullOrEmpty(outputTag) ? "PL" : outputTag;
            TranslationDirectionText.Text = $"{InputLanguage} \u2192 {safeOut}";
        }

        private void OutputSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Enable confirm button when selection changes
            if (OutputConfirmButton != null)
            {
                OutputConfirmButton.IsEnabled = true;
            }

            // Enforce max 2 selected languages
            var listBox = sender as System.Windows.Controls.ListBox;
            if (listBox == null)
            {
                return;
            }

            if (listBox.SelectedItems.Count > 2)
            {
                // Deselect the last added item to keep max 2
                if (e.AddedItems != null && e.AddedItems.Count > 0)
                {
                    foreach (var added in e.AddedItems)
                    {
                        listBox.SelectedItems.Remove(added);
                        break;
                    }
                }
            }
        }
        public bool FileExists()
        {
            // v2.0.0+: paths come from GameDataPaths. Input is always a
            // plaintext JSON from GitHub; output can be either plaintext
            // (mirrored langs: DE/ES/FR/...) OR an encrypted .gisub from
            // R2 (Kaption-exclusive langs: PL). GameDataPaths.HasAnyTextMap
            // accepts both.
            if (!GameDataPaths.HasAnyTextMap(Game, InputLanguage)) return false;
            if (!GameDataPaths.HasAnyTextMap(Game, OutputLanguage)) return false;

            if (!string.IsNullOrEmpty(OutputLanguage2) &&
                !GameDataPaths.HasAnyTextMap(Game, OutputLanguage2))
            {
                return false;
            }

            return true;
        }

        public async Task CheckDataAsync(bool renew = false)
        {
            // Create progress handler that updates UI.
            // Must use Dispatcher explicitly because CheckDataAsync may be called from a background
            // thread (e.g. Task.Run(() => Load())), so Progress<T> would capture a ThreadPool
            // SynchronizationContext and fire callbacks off the UI thread.
            var progress = new Progress<(int percent, string message)>(p =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Status.Content = p.message;
                    if (DownloadProgressBar != null)
                    {
                        DownloadProgressBar.Visibility = Visibility.Visible;
                        DownloadProgressBar.Value = p.percent;
                    }
                }));
            });

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Status.Content = "Data loading...";
                Logger.Log.Debug(Status.Content);
                contentDict.Clear();
                DownloadProgressBar.Visibility = Visibility.Visible;
                DownloadProgressBar.Value = 0;
            });

            string userName = (OutputLanguage == "CHS") ? "旅行者" : "Traveler";

            if (FileExists())
            {
                string inputFilePath = $"{Path.Combine(dataDir, Game)}\\TextMap{InputLanguage}.json";
                string outputFilePath1 = $"{Path.Combine(dataDir, Game)}\\TextMap{OutputLanguage}.json";

                // Migrate existing plaintext files to encrypted format (runs once, parallel)
                string gameDataDir2 = Path.Combine(dataDir, Game);
                await Task.Run(() => _protectionHelper.MigrateExistingFiles(gameDataDir2, OutputLanguage));

                string effectiveOutputPath = outputFilePath1;
                // When two outputs are selected, build a merged json so each key maps to two-language content
                if (!string.IsNullOrEmpty(OutputLanguage2))
                {
                    string outputFilePath2 = $"{Path.Combine(dataDir, Game)}\\TextMap{OutputLanguage2}.json";
                    effectiveOutputPath = VoiceContentHelper.BuildMultiOutputJson(
                        inputFilePath, outputFilePath1, outputFilePath2,
                        _protectionHelper, OutputLanguage);
                }

                var jsonFilePath = Path.Combine(Path.GetDirectoryName(inputFilePath),
                    $"{Path.GetFileNameWithoutExtension(inputFilePath)}_{Path.GetFileNameWithoutExtension(effectiveOutputPath)}.json");
                // Compute the serialized matcher index path (GSMX format, encrypted)
                string indexPath = Path.Combine(Path.GetDirectoryName(jsonFilePath),
                    Path.GetFileNameWithoutExtension(jsonFilePath) + ".gsmx.gisub");

                if (renew)
                {
                    _protectionHelper.DeleteBothVariants(jsonFilePath);
                    // Also delete the serialized matcher index so it gets rebuilt
                    try { if (File.Exists(indexPath)) File.Delete(indexPath); } catch { }
                }
                try
                {
                    contentDict = await Task.Run(() =>
                        VoiceContentHelper.CreateVoiceContentDictionary(
                            inputFilePath, effectiveOutputPath, userName, progress,
                            _protectionHelper, OutputLanguage));
                }
                catch (Exception ex)
                {
                    Logger.Log.Error($"Failed to load dictionary: {ex.Message}", ex);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        Status.Content = $"Error loading data: {ex.Message}";
                        DownloadProgressBar.Visibility = Visibility.Collapsed;
                    });
                    return;
                }
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (string.IsNullOrEmpty(OutputLanguage2))
                {
                    Status.Content = $"Loaded {contentDict.Count} key-values, {InputLanguage} -> {OutputLanguage}";
                }
                else
                {
                    Status.Content = $"Loaded {contentDict.Count} key-values, {InputLanguage} -> {OutputLanguage}+{OutputLanguage2}";
                }
                Logger.Log.Debug(Status.Content);
            });

            // One-shot sanity log — helps diagnose "did I upload the wrong
            // translation pack?" mix-ups. Dumps three sample keys so the
            // user can eyeball whether the values look like their game's
            // content. If the user swapped Honkai ↔ Genshin files, the
            // samples here will be obviously wrong.
            try
            {
                if (contentDict != null && contentDict.Count > 0)
                {
                    var samples = new List<string>(3);
                    foreach (var kv in contentDict)
                    {
                        if (samples.Count >= 3) break;
                        // Skip trivial entries so samples are informative.
                        if (string.IsNullOrWhiteSpace(kv.Key) || kv.Key.Length < 8) continue;
                        if (string.IsNullOrWhiteSpace(kv.Value) || kv.Value.Length < 4) continue;
                        string k = kv.Key.Length > 60 ? kv.Key.Substring(0, 60) + "…" : kv.Key;
                        string v = kv.Value.Length > 60 ? kv.Value.Substring(0, 60) + "…" : kv.Value;
                        samples.Add($"\"{k}\" → \"{v}\"");
                    }
                    Logger.Log.Info($"Matcher corpus: {contentDict.Count:N0} entries for {Game}/{InputLanguage}→{OutputLanguage}.");
                    GI_Subtitles.Core.Runtime.RamDiag.LogCheckpoint("after TextMap parse");
                    for (int i = 0; i < samples.Count; i++)
                        Logger.Log.Info($"  sample[{i}]: {samples[i]}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Debug($"Corpus sample log failed: {ex.Message}");
            }

            // Build or load the matcher with progress reporting (50-98%).
            // Try loading a pre-serialized GSMX index first (much faster than rebuilding).
            // Same Dispatcher marshaling needed as above -- CheckDataAsync may run off UI thread.
            var matcherProgress = new Progress<(int percent, string message)>(p =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Status.Content = p.message;
                    DownloadProgressBar.Value = p.percent;
                }));
            });

            // Resolve the serialized index path. It was computed inside the FileExists() block,
            // but we need it here too for the case when contentDict was loaded successfully.
            string matcherIndexPath = null;
            string matcherBlobPath = null;
            if (FileExists())
            {
                string inPath = $"{Path.Combine(dataDir, Game)}\\TextMap{InputLanguage}.json";
                string outPath = $"{Path.Combine(dataDir, Game)}\\TextMap{OutputLanguage}.json";
                string effectiveOut = outPath;
                if (!string.IsNullOrEmpty(OutputLanguage2))
                {
                    string outPath2 = $"{Path.Combine(dataDir, Game)}\\TextMap{OutputLanguage2}.json";
                    effectiveOut = Path.Combine(Path.GetDirectoryName(inPath),
                        $"{Path.GetFileNameWithoutExtension(outPath)}_{Path.GetFileNameWithoutExtension(outPath2)}.json");
                }
                string cacheJsonPath = Path.Combine(Path.GetDirectoryName(inPath),
                    $"{Path.GetFileNameWithoutExtension(inPath)}_{Path.GetFileNameWithoutExtension(effectiveOut)}.json");
                matcherIndexPath = Path.Combine(Path.GetDirectoryName(cacheJsonPath),
                    Path.GetFileNameWithoutExtension(cacheJsonPath) + ".gsmx.gisub");
                matcherBlobPath = Path.Combine(Path.GetDirectoryName(cacheJsonPath),
                    Path.GetFileNameWithoutExtension(cacheJsonPath) + ".kmx.gisub");
            }

            // UseMatcherBlob opt-in (default false): prefer the FST+ZSTD .kmx blob
            // over the legacy GSMX cache. On fresh install or flag-flip with no
            // blob yet, falls through to GSMX then to full rebuild. Rebuild saves
            // both formats when flag is on so the next launch hits the blob path.
            // See .plan/research/FST-LIBRARY-EVAL.md and ZEROCOPY-LIBRARY-EVAL.md.
            // Note: until AES-CTR v3 + hot-path mmap integration land, the blob is
            // just a compact serialisation format — expect smaller disk + faster
            // cold load, same steady-state RAM. Full mmap-backed RAM win is a
            // follow-up (OptimizedMatcher hot path needs to read via FstKeyIndex
            // + ZstdValueDecoder directly, not hydrate Entry[]).
            bool useMatcherBlob = Config.Get<bool>("UseMatcherBlob", false);

            await Task.Run(() =>
            {
                bool loadedFromCache = false;

                // Phase-2 blob fast path (gated by UseMatcherBlob flag).
                //
                // Preference order (when UseMatcherBlob=true):
                //   1. v3 .kmx.gisub → LoadFromMmap (RAM-preserving; this
                //      is the real Phase 2 win, ~200-400 MB vs the legacy
                //      GSMX path on a 488k-entry corpus).
                //   2. v2 .kmx.gisub → LoadFromBlob (materialises). Then
                //      (best effort) rewrite to v3 so the next launch
                //      takes path #1.
                //   3. No .kmx.gisub on disk → fall through to GSMX / rebuild.
                //
                // Any failure (e.g. mmap view creation fails on the OS,
                // HMAC mismatch, corrupt container) falls through silently
                // to the GSMX materialise path so users never see a hard
                // block on the cache.
                if (useMatcherBlob && matcherBlobPath != null && File.Exists(matcherBlobPath))
                {
                    bool isV3;
                    try { isV3 = ProtectedFileFormatV3.HasV3Header(matcherBlobPath); }
                    catch { isV3 = false; }

                    if (isV3)
                    {
                        try
                        {
                            ((IProgress<(int, string)>)matcherProgress).Report((45, "Mapping FST matcher blob..."));
                            var sw = Stopwatch.StartNew();

                            // The decryptor is adopted by the matcher — we
                            // must NOT wrap it in a `using`. The matcher's
                            // Dispose tears it down when the matcher is
                            // replaced (see below + Matcher setter).
                            var decryptor = _protectionService.OpenMmapDecryptor(matcherBlobPath);
                            OptimizedMatcher blobMatcher;
                            try
                            {
                                blobMatcher = OptimizedMatcher.LoadFromMmap(
                                    decryptor, InputLanguage, matcherProgress);
                            }
                            catch
                            {
                                // LoadFromMmap's own error path disposes the
                                // decryptor; defensive Dispose here in case
                                // a future refactor misses that contract.
                                try { decryptor.Dispose(); } catch { /* best-effort */ }
                                throw;
                            }
                            sw.Stop();

                            int blobCount = blobMatcher.EntryCount;
                            int dictCount = contentDict?.Count ?? 0;
                            bool blobSane = dictCount == 0 || blobCount >= dictCount / 2;
                            if (!blobSane)
                            {
                                Logger.Log.Warn(
                                    $"Matcher blob (mmap/v3) rejected: {blobCount:N0} entries vs live dict " +
                                    $"{dictCount:N0}. Deleting and falling back to GSMX/rebuild.");
                                try { blobMatcher.Dispose(); } catch { /* best-effort */ }
                                try { File.Delete(matcherBlobPath); } catch { /* best-effort */ }
                            }
                            else
                            {
                                Logger.Log.Info(
                                    $"Mapped matcher from v3 FST blob in {sw.ElapsedMilliseconds}ms " +
                                    $"({blobCount:N0} entries, mmap-backed).");
                                Dispatcher.Invoke(() =>
                                {
                                    // Dispose the old matcher before swapping —
                                    // releases any previously-owned mmap view.
                                    if (Matcher is IDisposable oldDisposable) { try { oldDisposable.Dispose(); } catch { /* best-effort */ } }
                                    Matcher = blobMatcher;
                                });
                                loadedFromCache = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log.Warn(
                                $"v3 mmap blob load failed, falling back to GSMX/rebuild: {ex.Message}");
                            // Fall through to GSMX path
                        }
                    }
                    else
                    {
                        // Legacy v2 blob on disk — load the old way (materialises
                        // Entry[]) and queue a migrate-to-v3 rewrite so the
                        // NEXT launch can mmap. The materialising path still
                        // benefits from reader disposal (see LoadFromBlob).
                        try
                        {
                            ((IProgress<(int, string)>)matcherProgress).Report((45, "Loading FST matcher blob (v2)..."));
                            var sw = Stopwatch.StartNew();
                            OptimizedMatcher blobMatcher;
                            using (var decryptedBlob = _protectionService.OpenDecryptStream(matcherBlobPath))
                            {
                                blobMatcher = OptimizedMatcher.LoadFromBlob(decryptedBlob, InputLanguage, matcherProgress);
                            }
                            sw.Stop();

                            int blobCount = blobMatcher.EntryCount;
                            int dictCount = contentDict?.Count ?? 0;
                            bool blobSane = dictCount == 0 || blobCount >= dictCount / 2;
                            if (!blobSane)
                            {
                                Logger.Log.Warn(
                                    $"Matcher blob (v2) rejected: {blobCount:N0} entries vs live dict {dictCount:N0}. " +
                                    $"Deleting and falling back to GSMX/rebuild.");
                                try { blobMatcher.Dispose(); } catch { /* best-effort */ }
                                try { File.Delete(matcherBlobPath); } catch { /* best-effort */ }
                            }
                            else
                            {
                                Logger.Log.Info(
                                    $"Loaded matcher from v2 FST blob in {sw.ElapsedMilliseconds}ms " +
                                    $"({blobCount:N0} entries). Will migrate to v3 on save.");
                                Dispatcher.Invoke(() =>
                                {
                                    if (Matcher is IDisposable oldDisposable) { try { oldDisposable.Dispose(); } catch { /* best-effort */ } }
                                    Matcher = blobMatcher;
                                });
                                loadedFromCache = true;

                                // Fire-and-forget migration: rewrite the v2
                                // blob as v3 so next launch can mmap. Uses
                                // the live in-memory corpus (which we just
                                // loaded from JSON); that's cheaper than
                                // re-reading and re-decrypting the v2 blob.
                                TryMigrateMatcherBlobToV3(matcherBlobPath, blobMatcher, Game, OutputLanguage);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log.Warn($"v2 FST blob load failed, falling back to GSMX/rebuild: {ex.Message}");
                            // Fall through to GSMX path
                        }
                    }
                }

                // Legacy GSMX fast path (~1-2s vs 30-60s rebuild)
                if (!loadedFromCache && matcherIndexPath != null && File.Exists(matcherIndexPath))
                {
                    try
                    {
                        ((IProgress<(int, string)>)matcherProgress).Report((50, "Loading pre-built search index..."));
                        var sw = Stopwatch.StartNew();

                        OptimizedMatcher newMatcher;
                        // Streaming decrypt (Phase-1 peak-memory flatten): CryptoStream over
                        // the underlying FileStream, no intermediate byte[]. Peak RAM during
                        // load drops from ~3x payload to ~1x the final object graph. HMAC is
                        // pre-verified by the service so any bytes we see here are authentic.
                        using (var decryptedStream = _protectionService.OpenDecryptStream(matcherIndexPath))
                        {
                            newMatcher = OptimizedMatcher.DeserializeFromStream(
                                decryptedStream, matcherProgress);
                        }

                        sw.Stop();

                        // Defence in depth (Session 26): a cached `.gsmx.gisub`
                        // built against an earlier, possibly-empty dict will
                        // deserialise cleanly but carry the old entry count.
                        // If it's dramatically smaller than the dict we just
                        // loaded, it's stale — throw it away and rebuild.
                        // 50% floor is generous: rebuilding on a legitimate
                        // shrink (e.g. publisher pruned ~10k dup keys) is
                        // fine; loading an empty cache against a 488k dict
                        // means every FindClosestMatch returns "" and users
                        // see no translations.
                        int cachedCount = newMatcher.EntryCount;
                        int dictCount = contentDict?.Count ?? 0;
                        bool cacheLooksSane =
                            dictCount == 0 ||                // nothing to compare against
                            cachedCount >= dictCount / 2;    // at least half the live dict

                        if (!cacheLooksSane)
                        {
                            Logger.Log.Warn(
                                $"Matcher cache rejected: {cachedCount:N0} entries deserialised " +
                                $"vs {dictCount:N0} in live dict (threshold: {dictCount / 2:N0}). " +
                                $"Cache is stale; deleting and rebuilding from scratch.");
                            try { File.Delete(matcherIndexPath); } catch { /* best-effort */ }
                        }
                        else
                        {
                            Logger.Log.Info(
                                $"Loaded serialized matcher index in {sw.ElapsedMilliseconds}ms " +
                                $"({cachedCount:N0} entries — matches live dict {dictCount:N0}).");
                            Dispatcher.Invoke(() =>
                            {
                                if (Matcher is IDisposable oldDisposable) { try { oldDisposable.Dispose(); } catch { /* best-effort */ } }
                                Matcher = newMatcher;
                            });
                            loadedFromCache = true;
                            GI_Subtitles.Core.Runtime.RamDiag.AggressiveReclaim("GSMX matcher loaded");

                            // Opportunistic GSMX → v3 .kmx.gisub upgrade. If the user
                            // has UseMatcherBlob=true but only a GSMX cache on disk
                            // (not a .kmx.gisub), we never trigger the mmap hot path
                            // because the blob-first branch skips out on File.Exists.
                            // Serialize the just-loaded matcher to the v3 blob format
                            // in the background so the next launch hits the mmap path
                            // automatically. No UI impact — runs on the same load
                            // Task.Run pool and finishes ~1-2 s after GSMX load on a
                            // 488k corpus. If it fails, we simply don't get mmap on
                            // next launch; harmless fallback.
                            if (useMatcherBlob && matcherBlobPath != null && !File.Exists(matcherBlobPath))
                            {
                                try
                                {
                                    Logger.Log.Info("UseMatcherBlob=true and no .kmx.gisub found — writing v3 blob from the GSMX cache for next-launch mmap path.");
                                    TryWriteMatcherBlobV3(matcherBlobPath, newMatcher, Game, OutputLanguage);
                                }
                                catch (Exception ex)
                                {
                                    Logger.Log.Warn($"Opportunistic GSMX → v3 blob upgrade failed (non-fatal): {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log.Warn($"Failed to load serialized matcher index, rebuilding: {ex.Message}");
                        // Fall through to rebuild from scratch
                    }
                }

                if (!loadedFromCache)
                {
                    var sw = Stopwatch.StartNew();
                    var newMatcher = new OptimizedMatcher(contentDict, InputLanguage, matcherProgress);
                    sw.Stop();
                    Logger.Log.Info($"Built matcher from scratch in {sw.ElapsedMilliseconds}ms");
                    GI_Subtitles.Core.Runtime.RamDiag.LogCheckpoint("after matcher build");

                    // Serialize and encrypt for next launch
                    if (matcherIndexPath != null)
                    {
                        try
                        {
                            using (var ms = new MemoryStream())
                            {
                                newMatcher.SerializeToStream(ms);
                                _protectionService.EncryptBytes(ms.ToArray(), matcherIndexPath);
                            }
                            long sizeKb = new FileInfo(matcherIndexPath).Length / 1024;
                            Logger.Log.Info($"Saved serialized matcher index ({sizeKb:N0} KB)");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log.Error($"Failed to save serialized matcher index: {ex.Message}");
                        }
                    }

                    // Also save the FST+ZSTD .kmx blob when the opt-in flag is set.
                    // Writes the v3 AES-CTR container (the OS-page-cache-friendly
                    // format that the mmap hot path requires). Written alongside
                    // the GSMX cache so flag-flip and flag-unflip both have a
                    // valid load target without a rebuild. No trained ZSTD
                    // dictionary yet — publish-tool follow-up will emit one
                    // per (game, lang) family and ship it inside .gisub-dist.
                    if (useMatcherBlob && matcherBlobPath != null)
                    {
                        TryWriteMatcherBlobV3(matcherBlobPath, newMatcher, Game, OutputLanguage);
                    }

                    // Update Matcher on UI thread
                    Dispatcher.Invoke(() =>
                    {
                        if (Matcher is IDisposable oldDisposable) { try { oldDisposable.Dispose(); } catch { /* best-effort */ } }
                        Matcher = newMatcher;
                    });
                }
            });

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Logger.Log.Debug("Loaded OptimizedMatcher...");
                Status.Content = $"Ready - {contentDict.Count:N0} entries loaded";
                UpdateDashboardStatus();
                UpdateDashboardRegionInfo();
            });

            // Load DialogueContextEngine on background thread (non-blocking)
            // This downloads/builds the dialogue graph on first run, then loads it.
            // Always use TextMapEN — dialog graph hashes reference EN text regardless of InputLanguage.
            // Reset previous engine when switching games/languages.
            ContextEngine = null;
            string gameDataDir = Path.Combine(dataDir, Game);
            string textMapEnPath = $"{gameDataDir}\\TextMapEN.json";

            _ = Task.Run(() =>
            {
                try
                {
                    var engineProgress = new Progress<(int percent, string message)>(p =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            Status.Content = p.message;
                            if (DownloadProgressBar != null && DownloadProgressBar.Visibility == Visibility.Collapsed)
                            {
                                DownloadProgressBar.Visibility = Visibility.Visible;
                            }
                            if (DownloadProgressBar != null)
                                DownloadProgressBar.Value = p.percent;
                        }));
                    });

                    var engine = GameDialogueContextFactory.Create(Game);
                    // Session 24+: graph files now ship as machine-bound `.gisub`
                    // via GamedataSyncService (R2-delivered bundle). Passing the
                    // real protectionHelper lets DialogueContextEngine resolve
                    // both the new `.gisub` files and any legacy plaintext
                    // `.json` files side-by-side. Prior to this fix the engine
                    // was called with null, which meant `GraphExists` only
                    // checked for `.json` — it never saw GamedataSync's output
                    // and fell through to DialogGraphDownloader's GitHub
                    // fallback on every launch, wasting 127 MB of bandwidth
                    // and overwriting the R2 bundle with unprotected plaintext.
                    //
                    // The original "no encrypt/decrypt CPU on 25 MB" rationale
                    // is obsolete: (a) these files ARE proprietary-ish now —
                    // user wants them protected the same way lang packs are,
                    // (b) AES-CBC over 25 MB is ~50 ms on modern hardware, not
                    // a meaningful cost for a one-shot engine load.
                    engine.Load(gameDataDir, textMapEnPath, engineProgress, _protectionHelper);

                    Dispatcher.Invoke(() =>
                    {
                        ContextEngine = engine;
                        DownloadProgressBar.Visibility = Visibility.Collapsed;
                        if (engine.IsLoaded)
                        {
                            Status.Content = $"Ready - {contentDict.Count:N0} entries, prediction enabled";
                            // Post-lazy-init refactor: "IsLoaded == true" at
                            // this point means the engine is primed for
                            // on-demand materialisation — the ~200k node
                            // graph doesn't hit RAM until the first OCR
                            // match against dialogue. See
                            // DialogueContextBase.EnsureLoadedCore.
                            Logger.Log.Info("DialogueContextEngine prepared — HOT CACHE will materialise on first OCR match (deferred load).");
                            GI_Subtitles.Core.Runtime.RamDiag.AggressiveReclaim("startup complete");
                        }
                        else
                        {
                            Status.Content = $"Ready - {contentDict.Count:N0} entries (prediction offline)";
                            Logger.Log.Warn("DialogueContextEngine failed to prepare — HOT CACHE / prefix-suppression disabled. Check earlier log lines for bundle-meta / parse errors.");
                        }
                        UpdateDashboardStatus();
                    });
                }
                catch (Exception ex)
                {
                    Logger.Log.Error($"DialogueContextEngine load failed: {ex.Message}");
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DownloadProgressBar.Visibility = Visibility.Collapsed;
                    }));
                }
            });

            // Must run on UI thread since it accesses WPF controls
            Dispatcher.Invoke(() => DisplayLocalFileDates());
        }

        private void DisplayLocalFileDates()
        {
            RefreshUrl();
            string inputFilePath = $"{Path.Combine(dataDir, Game)}\\TextMap{InputLanguage}.json";
            string outputFilePath = $"{Path.Combine(dataDir, Game)}\\TextMap{OutputLanguage}.json";

            // Resolve actual file path (may be .gisub for encrypted files)
            string resolvedInput = ResolveDisplayPath(inputFilePath);
            string resolvedOutput = ResolveDisplayPath(outputFilePath);

            if (resolvedInput != null)
            {
                DateTime modDate1 = File.GetLastWriteTime(resolvedInput);
                string encTag = resolvedInput.EndsWith(".gisub") ? " [encrypted]" : "";
                inputFilePathDate.Text = $"{inputFilePath} file date {modDate1}{encTag}";
            }
            else
            {
                inputFilePathDate.Text = $"{inputFilePath} not found";
            }

            if (resolvedOutput != null)
            {
                DateTime modDate2 = File.GetLastWriteTime(resolvedOutput);
                string encTag = resolvedOutput.EndsWith(".gisub") ? " [encrypted]" : "";
                outputFilePathDate.Text = $"{outputFilePath} file date {modDate2}{encTag}";
            }
            else
            {
                outputFilePathDate.Text = $"{outputFilePath} not found";
            }

            // Second output language (if configured)
            if (!string.IsNullOrEmpty(OutputLanguage2))
            {
                string outputFilePath2 = $"{Path.Combine(dataDir, Game)}\\TextMap{OutputLanguage2}.json";
                string resolved2 = ResolveDisplayPath(outputFilePath2);
                if (resolved2 != null)
                {
                    DateTime modDate3 = File.GetLastWriteTime(resolved2);
                    string encTag = resolved2.EndsWith(".gisub") ? " [encrypted]" : "";
                    outputFilePathDate2.Text = $"{outputFilePath2} file date {modDate3}{encTag}";
                }
                else
                {
                    outputFilePathDate2.Text = $"{outputFilePath2} not found";
                }
            }
            else
            {
                outputFilePathDate2.Text = "Second output language not selected";
            }
        }

        public DateTime GetLocalFileDates(string input, string output, string game)
        {
            string inputFilePath = $"{Path.Combine(dataDir, Game)}\\TextMap{input}.json";
            string outputFilePath = $"{Path.Combine(dataDir, Game)}\\TextMap{output}.json";
            if (File.Exists(inputFilePath))
            {
                return File.GetLastWriteTime(inputFilePath);
            }
            else if (File.Exists(outputFilePath))
            {
                return File.GetLastWriteTime(outputFilePath);
            }
            else
            {
                return DateTime.Now.AddYears(-1);
            }
        }


        public async Task GetRepositoryModificationDateAsync()
        {
            try
            {
                Logger.Log.Info($"Load start.");
                HttpResponseMessage response = await client.GetAsync(repoUrl);
                response.EnsureSuccessStatusCode();
                string responseText = await response.Content.ReadAsStringAsync();
                if (Game == "Zenless")
                {
                    string pattern = @"datetime=""([^""]*)""";
                    Match match = Regex.Match(responseText, pattern);

                    if (match.Success)
                    {
                        string dateTimeString = match.Groups[1].Value;
                        try
                        {
                            DateTimeOffset dateTimeOffset = DateTimeOffset.Parse(dateTimeString);
                            DateTime localTime = dateTimeOffset.LocalDateTime; // 自动转换为本地时区
                            RepoModifiedDate.Text = localTime.ToString();
                        }
                        catch (Exception ex)
                        {
                            Logger.Log.Error("Error parsing datetime: " + ex.Message);
                        }
                    }
                    else
                    {
                        Logger.Log.Error("No datetime attribute found in the input string.");
                    }

                }
                else if (Game == "Wuthering" || Game == "Genshin")
                {
                    var reader = XmlReader.Create(new System.IO.StringReader(responseText));
                    var feed = SyndicationFeed.Load(reader);
                    var item = feed?.Items?.FirstOrDefault();
                    var dateTime = item?.LastUpdatedTime ?? item?.PublishDate;
                    RepoModifiedDate.Text = dateTime.ToString();
                }
                else
                {
                    JArray jsonArray = JArray.Parse(responseText);
                    if (jsonArray.Count > 0)
                    {
                        List<string> dateList = new List<string>();
                        foreach (var date in jsonArray)
                        {
                            dateList.Add(date["commit"]?["committed_date"]?.ToString());
                        }
                        dateList.Sort();
                        dateList.Reverse();
                        RepoModifiedDate.Text = dateList.Count > 0 ? dateList[0] : "Unable to get committed_date";
                    }
                    else
                    {
                        RepoModifiedDate.Text = "No response";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error(ex);
                RepoModifiedDate.Text = "Error: " + ex.Message;
            }
        }

        public async Task<string> GetRepositoryModificationDate(string url, string game)
        {
            try
            {
                HttpResponseMessage response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();
                string responseText = await response.Content.ReadAsStringAsync();
                if (Game == "Zenless")
                {
                    string pattern = @"datetime=""([^""]*)""";
                    Match match = Regex.Match(responseText, pattern);

                    if (match.Success)
                    {
                        string dateTimeString = match.Groups[1].Value;
                        try
                        {
                            DateTimeOffset dateTimeOffset = DateTimeOffset.Parse(dateTimeString);
                            DateTime localTime = dateTimeOffset.LocalDateTime;
                            return localTime.ToString();
                        }
                        catch (Exception ex)
                        {
                            Logger.Log.Error("Error parsing datetime: " + ex.Message);
                        }
                    }
                    else
                    {
                        Logger.Log.Error("No datetime attribute found in the input string.");
                    }

                }
                else if (Game == "Wuthering" || Game == "Genshin")
                {
                    var reader = XmlReader.Create(new System.IO.StringReader(responseText));
                    var feed = SyndicationFeed.Load(reader);
                    var item = feed?.Items?.FirstOrDefault();
                    var dateTime = item?.LastUpdatedTime ?? item?.PublishDate;
                    return dateTime?.ToString() ?? "";
                }
                else
                {
                    JArray jsonArray = JArray.Parse(responseText);
                    if (jsonArray.Count > 0)
                    {
                        List<string> dateList = new List<string>();
                        foreach (var date in jsonArray)
                        {
                            dateList.Add(date["commit"]?["committed_date"]?.ToString());
                        }
                        dateList.Sort();
                        dateList.Reverse();
                        return dateList[0];
                    }
                }
            }
            catch (Exception ex)
            {
                if (!url.Contains("gitlab"))
                {
                    Logger.Log.Error(ex);
                }
            }
            return "";
        }

        private async void SyncButton_Click(object sender, RoutedEventArgs e)
        {
            await GetRepositoryModificationDateAsync();
        }

        private async void DownloadAllButton_Click(object sender, RoutedEventArgs e)
        {
            DownloadAllButton.IsEnabled = false;
            DownloadProgressBar.Visibility = Visibility.Visible;
            try
            {
                // v2.0.0+: delegate to the bootstrap orchestrator. It handles
                // the right source for each file:
                //   input lang (always mirrored)  → GitHub
                //   output lang (mirrored)        → GitHub
                //   output lang (PL / exclusive)  → R2 via DictionarySync
                // Previously this button hard-coded a GitHub fetch for the
                // output language, which 404'd for PL (not on the mirror).
                await EnsureGameDataReadyAsync();

                // Then rebuild the matcher — renew=true invalidates the
                // merged cache so the refresh actually takes effect.
                await CheckDataAsync(true);
            }
            catch (Exception ex)
            {
                Logger.Log.Error("Download failed: " + ex.Message, ex);
            }
            finally
            {
                DownloadAllButton.IsEnabled = true;
                DownloadProgressBar.Visibility = Visibility.Collapsed;
                DownloadProgressBar.Value = 0;
                DownloadSpeedText.Text = "";
            }
        }

        private async void DownloadButton1_Click(object sender, RoutedEventArgs e)
        {
            string inputFilePath = $"{Path.Combine(dataDir, Game)}\\TextMap{InputLanguage}.json";
            await DownloadFileAsync(InputLangDownloadUrl.Text, inputFilePath);
        }

        private async void DownloadButton2_Click(object sender, RoutedEventArgs e)
        {
            string outputFilePath = $"{Path.Combine(dataDir, Game)}\\TextMap{OutputLanguage}.json";
            await DownloadFileAsync(OutputLangDownloadUrl.Text, outputFilePath);
            await CheckDataAsync(true);
        }

        private async void OutputConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            // Commit selected output languages (max 2) and reload data
            var selectedItems = OutputSelector.SelectedItems.Cast<ListBoxItem>().ToList();
            if (selectedItems.Count == 0)
            {
                System.Windows.MessageBox.Show("Please select at least one output language.");
                return;
            }

            // Map UI text back to language codes
            var uiToCode = OutputLanguages;

            // Primary output
            string primaryName = selectedItems[0].Content.ToString();
            if (!uiToCode.TryGetValue(primaryName, out var primaryCode))
            {
                System.Windows.MessageBox.Show("Invalid primary output language.");
                return;
            }

            string secondCode = null;
            if (selectedItems.Count > 1)
            {
                string secondName = selectedItems[1].Content.ToString();
                if (!uiToCode.TryGetValue(secondName, out secondCode))
                {
                    System.Windows.MessageBox.Show("Invalid secondary output language.");
                    return;
                }
            }

            OutputLanguage = primaryCode;
            OutputLanguage2 = secondCode;

            // Persist config
            Config.Set("Output", OutputLanguage);
            Config.Set("Output2", OutputLanguage2 ?? "");

            DisplayLocalFileDates();

            // Reload data with new output configuration
            await CheckDataAsync(true);

            // Disable button until next selection change
            OutputConfirmButton.IsEnabled = false;
        }

        private async Task DownloadFileAsync(string url, string fileName)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                System.Windows.MessageBox.Show($"Invalid URL: {url}");
                return;
            }
            fileName = Path.Combine(dataDir, fileName);
            int attempt = 0;
            bool success = false;
            long existingLength = 0;
            string tmpFileName = fileName.Replace("json", "jsontmp");


            while (attempt < MaxRetries && !success)
            {
                try
                {
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri))
                    {

                        sw.Start();
                        using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                        {
                            response.EnsureSuccessStatusCode();

                            // Get the total size
                            long totalBytes = response.Content.Headers.ContentLength.Value;

                            using (Stream contentStream = await response.Content.ReadAsStreamAsync(),
                                          fileStream = new FileStream(tmpFileName, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                            {
                                byte[] buffer = new byte[8192];
                                int bytesRead;
                                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                                    existingLength += bytesRead;

                                    // Update the progress
                                    double progressPercentage = (double)existingLength / totalBytes * 100;
                                    DownloadProgressBar.Value = progressPercentage;

                                    // Calculate the download speed
                                    double speed = existingLength / 1024d / sw.Elapsed.TotalSeconds;
                                    DownloadSpeedText.Text = $"{speed:0.00} KB/s";
                                }
                            }
                        }
                    }

                    sw.Reset();
                    if (File.Exists(tmpFileName))
                    {
                        if (File.Exists(fileName))
                        {
                            File.Delete(fileName);
                        }
                        File.Move(tmpFileName, fileName);
                        string directoryPath = Path.GetDirectoryName(fileName);

                        // Delete old derived caches (both .json and .gisub)
                        string baseFileName = Path.GetFileNameWithoutExtension(fileName);
                        string[] matchingFiles = Directory.GetFiles(directoryPath);
                        foreach (string file in matchingFiles)
                        {
                            try
                            {
                                string baseName = Path.GetFileNameWithoutExtension(file);
                                if (baseName.Contains(baseFileName) && baseName.Contains("_"))
                                {
                                    File.Delete(file);
                                    Logger.Log.Info($"Deleted: {file}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log.Error($"Failed to delete {file}: {ex.Message}");
                            }
                        }

                        // If this is a custom language file, encrypt it immediately
                        if (LanguageClassification.ShouldProtectFile(Path.GetFileName(fileName), OutputLanguage))
                        {
                            try
                            {
                                string gisubPath = _protectionService.GetProtectedPath(fileName);
                                _protectionService.EncryptFile(fileName, gisubPath);
                                File.Delete(fileName); // Remove plaintext
                                Logger.Log.Info($"Encrypted downloaded file: {Path.GetFileName(gisubPath)}");
                            }
                            catch (Exception ex)
                            {
                                Logger.Log.Error($"Failed to encrypt download: {ex.Message}");
                                // File remains as plaintext — will be migrated on next load
                            }
                        }
                    }

                    DisplayLocalFileDates(); // Update the local file date
                    success = true;
                }
                catch (Exception ex)
                {
                    sw.Reset();
                    attempt++;
                    if (attempt >= MaxRetries)
                    {
                        System.Windows.MessageBox.Show($"Error: {ex.Message}");
                    }
                    else
                    {
                        await Task.Delay(2000);
                    }
                }
                finally
                {
                    DownloadProgressBar.Value = 0;
                    DownloadSpeedText.Text = "";
                }
            }
        }

        private string ResolveDisplayPath(string jsonPath)
        {
            string gisubPath = _protectionService.GetProtectedPath(jsonPath);
            if (File.Exists(gisubPath)) return gisubPath;
            if (File.Exists(jsonPath)) return jsonPath;
            return null;
        }

        private void RestartButton_Click(object sender, RoutedEventArgs e)
        {
            string executablePath = Assembly.GetEntryAssembly().Location;
            Process.Start(executablePath, "Restart");
            Environment.Exit(0);
        }

        public void LoadEngine()
        {
            if (engine != null)
            {
                engine.Dispose();
            }
            engine = LoadEngine(InputLanguage);
        }

        public static PaddleOCREngine LoadEngine(string input)
        {
            OCRModelConfig config = null;
            bool useGpu = Config.Get("UseGpuOcr", true);
            OCRParameter oCRParameter = new OCRParameter
            {
                cpu_math_library_num_threads = 3,//Prediction concurrent thread count
                enable_mkldnn = true,//If you deploy on the web, it is recommended to set this value to 0, otherwise it will error. If the memory is used very large, it is recommended to set this value to 0.
                use_angle_cls = false,//Whether to enable direction detection, used to detect 180 degree rotation
                det_db_score_mode = false,//Whether to use multiple segments, that is, whether the text area is used with multiple segments or with rectangles,
                max_side_len = 960,
                use_gpu = useGpu,
                gpu_id = 0
            };
            Logger.Log.Info($"Loading OCR engine (GPU acceleration: {(useGpu ? "requested" : "disabled")})");

            if (input == "JP")
            {
                config = new OCRModelConfig();
                string root = System.IO.Path.GetDirectoryName(typeof(OCRModelConfig).Assembly.Location);
                string modelPathroot = root + @"\inference";
                config.det_infer = modelPathroot + @"\Det\V4\PP-OCRv4_mobile_det_infer\slim.onnx";
                config.rec_infer = modelPathroot + @"\Rec\V4\jp_PP-OCRv4_mobile_rec_infer\slim.onnx";
                config.keys = modelPathroot + @"\Rec\V4\jp_PP-OCRv4_mobile_rec_infer\dict.txt";
            }
            else
            {
                config = new OCRModelConfig();
                string root = System.IO.Path.GetDirectoryName(typeof(OCRModelConfig).Assembly.Location);
                string modelPathroot = root + @"\inference";
                config.det_infer = modelPathroot + @"\Det\V4\PP-OCRv4_mobile_det_infer\slim.onnx";
                config.rec_infer = modelPathroot + @"\Rec\V4\PP-OCRv4_mobile_rec_infer\slim.onnx";
                config.keys = modelPathroot + @"\Rec\V4\PP-OCRv4_mobile_rec_infer\dict.txt";
            }
            try
            {
                var engine = new PaddleOCREngine(config, oCRParameter);
                Logger.Log.Info($"OCR engine loaded successfully — provider: {engine.ExecutionProvider}, GPU active: {engine.IsUsingGpu}");
                return engine;
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Error loading engine: {ex.Message}");
                throw new Exception("Failed to load engine.");
            }
        }
        private void TestButton_Click(object sender, RoutedEventArgs e)
        {
            LoadEngine();
            string testFile = InputLanguage + ".jpg";
            if (Game == "Wuthering")
            {
                testFile = "Wuthering.png";
            }
            string report = "";
            try
            {
                while (contentDict.Count < 10)
                {
                    Thread.Sleep(1000);
                    Logger.Log.Debug("Sleeping ...");
                }
                DateTime dateTime = DateTime.Now;
                Bitmap target;
                if (bitmap == null)
                {
                    target = (Bitmap)Bitmap.FromFile(testFile);
                }
                else
                {
                    target = bitmap;
                }
                OCRResult ocrResult = engine.DetectText(target);
                string ocrText = ocrResult.Text;
                dateTime = DateTime.Now;
                string res = Matcher.FindClosestMatch(ocrText, out string key);
                report = $"OCR: {ocrText}\nMatch: {key}\nTranslate: {res}";
            }
            catch (Exception ex)
            {
                report = ex.Message;
            }
            System.Windows.MessageBox.Show(report);
        }

        public void SetImage(Bitmap bitmap)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                // Return the previous frame to the pool before replacing the reference —
                // the MainWindow capture path rents from BitmapPool.Default, so every
                // SetImage call would otherwise leak a pool slot's worth of bitmap per tick.
                Bitmap previous = this.bitmap;
                this.bitmap = bitmap;
                if (previous != null && !ReferenceEquals(previous, bitmap))
                {
                    GI_Subtitles.Core.Pooling.BitmapPool.Default.Return(previous);
                }

                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;

                BitmapImage bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.UriSource = null;
                bitmapImage.StreamSource = ms;
                bitmapImage.EndInit();
                bitmapImage.Freeze(); // Freeze, so it can be used in multiple threads

                // Set the Source property of the Image control
                Capture.Source = bitmapImage;
            }
        }

        private void RegionButton_Click(object sender, RoutedEventArgs e)
        {
            LoadEngine();

            int idx = 0;
            string configRegion = Config.Get<string>("Region");
            Logger.Log.Debug($"Config Region: {configRegion}");
            foreach (var screen in Screen.AllScreens)
            {
                Logger.Log.Debug($"Capturing screen {idx}: {screen.DeviceName}");
                Logger.Log.Debug($"Bounds: {screen.Bounds.Width}x{screen.Bounds.Height} at {screen.Bounds.Location}");

                // Use 'using' to ensure resources are properly disposed
                using (Bitmap bitmap = new Bitmap(screen.Bounds.Width, screen.Bounds.Height))
                {
                    // Create a Graphics object from the bitmap
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        // Copy the screen contents to the bitmap
                        g.CopyFromScreen(screen.Bounds.Location, System.Drawing.Point.Empty, screen.Bounds.Size);
                    }

                    // Now save the bitmap, which contains the screenshot
                    bitmap.Save($"{idx}.png", System.Drawing.Imaging.ImageFormat.Png);
                    if (bitmap == null)
                    {
                        continue;
                    }
                    var res = engine.DetectText(bitmap);
                    foreach (var i in res.TextBlocks)
                    {
                        Logger.Log.Debug(i);
                        Logger.Log.Debug($"Region:\"{i.BoxPoints[0].X - 400},{i.BoxPoints[0].Y - 20},{i.BoxPoints[1].X - i.BoxPoints[0].X + 800},{i.BoxPoints[2].Y - i.BoxPoints[0].Y + 40}\"");
                    }
                }
                idx++;
            }
            System.Windows.MessageBox.Show("Finished");
        }

        private void OpenAppDataFolder_Click(object sender, RoutedEventArgs e)
        {
            string dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kaption");

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Directly open the explorer and locate to the directory
            Process.Start("explorer.exe", dir);
        }

        private void ConvertButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Subtitle file|*.srt|All files|*.*",
                Multiselect = true,
                Title = "Select the SRT file to convert"
            };

            if (dialog.ShowDialog() == true)
            {
                var processor = new SrtProcessor(this.contentDict);
                int successCount = 0;
                int failCount = 0;
                var errors = new List<string>();

                foreach (var file in dialog.FileNames)
                {
                    try
                    {
                        // Skip the file that has already been converted
                        if (file.EndsWith(".convert.srt", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var subtitles = processor.ReadSrtFile(file);
                        var processedSubtitles = processor.ProcessSubtitles(Matcher, subtitles);

                        // Output the file to the same directory, add the .convert suffix to the file name
                        string outputPath = Path.Combine(
                            Path.GetDirectoryName(file),
                            Path.GetFileNameWithoutExtension(file) + ".convert.srt"
                        );

                        processor.WriteSrtFile(outputPath, processedSubtitles);
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                    }
                }

                // Display the conversion result
                string message = $"Conversion completed!\nSuccess: {successCount} files";
                if (failCount > 0)
                {
                    message += $"\nFailed: {failCount} files";
                    if (errors.Count > 0)
                    {
                        message += "\n\nError details:\n" + string.Join("\n", errors.Take(5));
                        if (errors.Count > 5)
                        {
                            message += $"\n... there are {errors.Count - 5} errors";
                        }
                    }
                }
                System.Windows.MessageBox.Show(message, "Conversion result", MessageBoxButton.OK,
                    failCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
        }

        private void VideoButton_Click(object sender, RoutedEventArgs e)
        {
            if (engine == null)
            {
                LoadEngine();
            }
            if (Matcher == null)
            {
                // Ensure dictionary is loaded before opening video window
                System.Windows.MessageBox.Show("Please load/check data in the current window first, so that the translation dictionary can be built before opening the video extraction window.", "Note",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var video = new Video(engine, Matcher);
            this.Close();
            video.ShowDialog();
        }

        // Modify the InitializeHotkeys method
        public void InitializeHotkeys()
        {
            // Load the hotkeys from the settings
            var settings = HotkeySettingsManager.LoadSettings();

            // Create the available key list (A-Z + 0-9 + special keys).
            // Digits are required: Force-OCR (id 9004) defaults to Ctrl+1, and
            // without digits in the pool its dropdown renders empty and any
            // save overwrites the stored SelectedKey with \0.
            var availableKeys = Enumerable.Range(65, 26).Select(c => (char)c).ToList();          // A-Z
            availableKeys.AddRange(Enumerable.Range(48, 10).Select(c => (char)c));               // 0-9
            availableKeys.Add('`');

            // Initialize the hotkey collection, prefer localized descriptions by Id
            _hotkeys = new ObservableCollection<HotkeyViewModel>(
                settings.Hotkeys.Select(h =>
                {
                    string localizedDescription = null;
                    string info = null;
                    try
                    {
                        var app = System.Windows.Application.Current;
                        localizedDescription = app?.TryFindResource($"Hotkey_{h.Id}_Description") as string;
                        info = app?.TryFindResource($"Hotkey_{h.Id}_Info") as string;
                    }
                    catch
                    {
                        // ignore and fallback
                    }

                    return new HotkeyViewModel
                    {
                        Id = h.Id,
                        Description = string.IsNullOrEmpty(localizedDescription) ? h.Description : localizedDescription,
                        InfoText = info,
                        // Power-user shortcuts go in the Advanced group:
                        // 9002 "Hide subtitles" — situational UI toggle.
                        // 9004 "Force re-translate" — troubleshoot a stuck
                        // OCR match without stopping the session.
                        IsAdvanced = (h.Id == 9002 || h.Id == 9004),
                        IsCtrl = h.IsCtrl,
                        IsShift = h.IsShift,
                        IsAlt = h.IsAlt,
                        SelectedKey = h.SelectedKey,
                        AvailableKeys = availableKeys
                    };
                })
            );

            // In some design-time or early-initialization scenarios, the ListView
            // may not yet be created; guard against null to avoid crashes.
            // Two ListViews share _hotkeys through filtered collection views —
            // IsAdvanced drives which surface a row lands on. Both views are
            // live, so a future config reload doesn't need to refresh both.
            if (hotkeyListView != null)
            {
                var primary = new System.Windows.Data.CollectionViewSource { Source = _hotkeys };
                primary.Filter += (s, ev) => ev.Accepted = !((HotkeyViewModel)ev.Item).IsAdvanced;
                hotkeyListView.ItemsSource = primary.View;
            }
            if (hotkeyListViewAdvanced != null)
            {
                var advanced = new System.Windows.Data.CollectionViewSource { Source = _hotkeys };
                advanced.Filter += (s, ev) => ev.Accepted = ((HotkeyViewModel)ev.Item).IsAdvanced;
                hotkeyListViewAdvanced.ItemsSource = advanced.View;
            }
        }

        // Modify the SaveButton_Click method, add the function to save to the file
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Verify each hotkey. As of session 31, bare keys (no modifier)
            // are permitted -- some users want a single-key global shortcut
            // while gaming. Only the key itself has to be A-Z / 0-9 / `.
            foreach (var hotkey in _hotkeys)
            {
                bool isSpecialKey = hotkey.SelectedKey == '`';
                if (!char.IsLetterOrDigit(hotkey.SelectedKey) && !isSpecialKey)
                {
                    GI_Subtitles.Views.ModernDialog.Warn(
                        owner: this,
                        title: "Invalid key",
                        body: $"\"{hotkey.Description}\" must be bound to a letter, digit, or a supported special key.");
                    return;
                }
            }

            // Check for duplicates
            var hotkeyTexts = _hotkeys.Select(h => h.GetHotkeyText()).ToList();
            if (hotkeyTexts.GroupBy(t => t).Any(g => g.Count() > 1))
            {
                GI_Subtitles.Views.ModernDialog.Warn(
                    owner: this,
                    title: "Duplicate hotkeys",
                    body: "Two or more hotkeys share the same combination. Please change one of them and save again.");
                return;
            }

            // Save the settings
            var settings = new HotkeySettings
            {
                Hotkeys = _hotkeys.Select(h => new HotkeyData
                {
                    Id = h.Id,
                    Description = h.Description,
                    IsCtrl = h.IsCtrl,
                    IsShift = h.IsShift,
                    SelectedKey = h.SelectedKey
                }).ToList()
            };

            HotkeySettingsManager.SaveSettings(settings);
            RegisterAllHotkeys();

            System.Windows.MessageBox.Show("Hotkey settings saved.", "Save successful",
                            MessageBoxButton.OK, MessageBoxImage.Information);

            foreach (var hotkey in _hotkeys)
            {
                hotkey.IsEditing = false;
            }
        }
        public void InitializeKey(IntPtr handle)
        {
            Logger.Log.Debug("OnSourceInitialized");
            _windowHandle = handle;
            RegisterAllHotkeys();
        }


        private void HandleHotkeyPress(int hotkeyId)
        {
            var hotkey = _hotkeys.FirstOrDefault(h => h.Id == hotkeyId);
            if (hotkey != null)
            {
                System.Windows.MessageBox.Show($"Triggered hotkey: {hotkey.Description}\nCombination key: {hotkey.GetHotkeyText()}",
                                "Hotkey triggered", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void RegisterAllHotkeys()
        {
            // First unregister all hotkeys
            UnregisterAllHotkeys();

            // Register all hotkeys
            foreach (var hotkey in _hotkeys)
            {
                RegisterHotkey(hotkey);
            }
        }

        // Add this helper method to your class
        private uint GetVirtualKeyFromChar(char c)
        {
            // For letter/digit characters, the ASCII code maps directly to the
            // Win32 virtual-key code (VK_A..VK_Z = 0x41..0x5A, VK_0..VK_9 = 0x30..0x39).
            if (char.IsLetterOrDigit(c))
            {
                return (uint)char.ToUpper(c);
            }
            // Special keys
            if (c == '`') return 0xC0; // VK_OEM_3

            return 0;
        }

        private void RegisterHotkey(HotkeyViewModel hotkey)
        {
            uint modifiers = 0;
            if (hotkey.IsCtrl) modifiers |= MOD_CTRL;
            if (hotkey.IsShift) modifiers |= MOD_SHIFT;
            if (hotkey.IsAlt) modifiers |= MOD_ALT;

            // Use this custom conversion method
            uint virtualKey = GetVirtualKeyFromChar(hotkey.SelectedKey);



            if (!RegisterHotKey(_windowHandle, hotkey.Id, modifiers, virtualKey))
            {
                // Registration failed, possibly because of hotkey conflict
                System.Windows.MessageBox.Show($"Failed to register hotkey {hotkey.GetHotkeyText()}\nMay be conflicts with other applications.",
                                "Registration failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public void UnregisterAllHotkeys()
        {
            foreach (var hotkey in _hotkeys)
            {
                UnregisterHotKey(_windowHandle, hotkey.Id);
            }
        }


        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (System.Windows.MessageBox.Show("Are you sure you want to restore the default hotkey settings?", "Confirm restore default",
                               MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                InitializeHotkeys();
                RegisterAllHotkeys();
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  HOTKEY CAPTURE UX (click-to-record chip replacing checkbox+dropdown)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Click handler for the hotkey chip button in the Settings > Hotkeys
        /// list. Toggles the row into "recording" mode — the chip's label
        /// flips to "Press a shortcut…" and the next KeyDown with a valid
        /// modifier+key combo commits the binding. Clicking again or pressing
        /// Escape cancels.
        /// </summary>
        private void HotkeyChip_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is System.Windows.Controls.Button btn) ||
                !(btn.DataContext is HotkeyViewModel vm)) return;

            // Cancel any other row that's already recording — only one chip
            // can capture at a time, otherwise keypresses race.
            if (_hotkeys != null)
            {
                foreach (var other in _hotkeys)
                {
                    if (!ReferenceEquals(other, vm)) other.IsEditing = false;
                }
            }

            if (vm.IsEditing)
            {
                // Second click cancels recording without changing anything.
                vm.IsEditing = false;
            }
            else
            {
                vm.IsEditing = true;
                // Force keyboard focus to THIS chip so PreviewKeyDown fires
                // on it — the Click may have been a mouse click from anywhere.
                System.Windows.Input.Keyboard.Focus(btn);
            }
        }

        /// <summary>
        /// Captures a keypress while a hotkey chip is in recording mode.
        /// Accepts A-Z, 0-9, and backtick, optionally combined with Ctrl,
        /// Shift, and/or Alt. Bare keys (no modifier) ARE allowed -- some
        /// users prefer a single-key global shortcut while gaming. The
        /// resulting combination is auto-saved immediately; no Save button.
        /// Rejects combinations that clash with another row (inline warning
        /// via ModernDialog) rather than silently overwriting.
        /// </summary>
        private void HotkeyChip_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!(sender is System.Windows.Controls.Button btn) ||
                !(btn.DataContext is HotkeyViewModel vm)) return;
            if (!vm.IsEditing) return;

            // Escape aborts recording without side effects.
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                vm.IsEditing = false;
                e.Handled = true;
                return;
            }

            // Some keys arrive wrapped as Key.System when Alt is held. Unwrap.
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

            // Ignore pure-modifier presses -- we need a real key before we commit.
            if (IsModifierKey(key))
            {
                e.Handled = true;
                return;
            }

            char? ch = TryKeyToChar(key);
            if (ch == null)
            {
                // Unsupported key -- swallow but keep recording.
                e.Handled = true;
                return;
            }

            var mods = System.Windows.Input.Keyboard.Modifiers;
            bool ctrl = (mods & System.Windows.Input.ModifierKeys.Control) != 0;
            bool shift = (mods & System.Windows.Input.ModifierKeys.Shift) != 0;
            bool alt = (mods & System.Windows.Input.ModifierKeys.Alt) != 0;

            // Duplicate check -- if another row already owns this combo, tell
            // the user and bail without overwriting. Compare against the
            // candidate, not the committed state, so two rows can't race.
            if (_hotkeys != null)
            {
                foreach (var other in _hotkeys)
                {
                    if (ReferenceEquals(other, vm)) continue;
                    if (other.IsCtrl == ctrl && other.IsShift == shift &&
                        other.IsAlt == alt && other.SelectedKey == ch.Value)
                    {
                        GI_Subtitles.Views.ModernDialog.Warn(
                            owner: this,
                            title: L("Hotkey_Duplicate_Title", "Shortcut already in use"),
                            body: string.Format(
                                L("Hotkey_Duplicate_Body",
                                  "\"{0}\" already uses this shortcut. Pick a different key."),
                                other.Description));
                        vm.IsEditing = false;
                        e.Handled = true;
                        return;
                    }
                }
            }

            vm.IsCtrl = ctrl;
            vm.IsShift = shift;
            vm.IsAlt = alt;
            vm.SelectedKey = ch.Value;
            vm.IsEditing = false;
            e.Handled = true;

            // Auto-save: persist + re-register with the OS immediately so the
            // new binding is live before the user even looks up. No Save
            // button means no "did it save?" ambiguity.
            PersistAndReRegisterHotkeys();
        }

        /// <summary>
        /// Persists the current in-memory hotkey list to AppData XML and
        /// re-registers every binding with the Win32 RegisterHotKey API.
        /// Called from auto-save after capture and from the Reset handler.
        /// </summary>
        private void PersistAndReRegisterHotkeys()
        {
            if (_hotkeys == null) return;
            try
            {
                var settings = new HotkeySettings
                {
                    Hotkeys = _hotkeys.Select(h => new HotkeyData
                    {
                        Id = h.Id,
                        Description = h.Description,
                        IsCtrl = h.IsCtrl,
                        IsShift = h.IsShift,
                        IsAlt = h.IsAlt,
                        SelectedKey = h.SelectedKey
                    }).ToList()
                };
                HotkeySettingsManager.SaveSettings(settings);
                RegisterAllHotkeys();
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Auto-save hotkeys failed: {ex}");
            }
        }

        /// <summary>
        /// Cancel recording if the chip loses focus — prevents ghost capture
        /// from bleeding into other controls when the user tabs away.
        /// </summary>
        private void HotkeyChip_LostKeyboardFocus(object sender,
            System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn &&
                btn.DataContext is HotkeyViewModel vm && vm.IsEditing)
            {
                vm.IsEditing = false;
            }
        }

        private static bool IsModifierKey(System.Windows.Input.Key k)
        {
            return k == System.Windows.Input.Key.LeftCtrl || k == System.Windows.Input.Key.RightCtrl
                || k == System.Windows.Input.Key.LeftShift || k == System.Windows.Input.Key.RightShift
                || k == System.Windows.Input.Key.LeftAlt || k == System.Windows.Input.Key.RightAlt
                || k == System.Windows.Input.Key.LWin || k == System.Windows.Input.Key.RWin;
        }

        private static char? TryKeyToChar(System.Windows.Input.Key k)
        {
            if (k >= System.Windows.Input.Key.A && k <= System.Windows.Input.Key.Z)
                return (char)('A' + (k - System.Windows.Input.Key.A));
            if (k >= System.Windows.Input.Key.D0 && k <= System.Windows.Input.Key.D9)
                return (char)('0' + (k - System.Windows.Input.Key.D0));
            if (k >= System.Windows.Input.Key.NumPad0 && k <= System.Windows.Input.Key.NumPad9)
                return (char)('0' + (k - System.Windows.Input.Key.NumPad0));
            if (k == System.Windows.Input.Key.OemTilde) return '`';
            return null;
        }

        protected override void OnClosed(EventArgs e)
        {
            try { App.StartupStatusChanged -= OnAppStartupStatusChanged; }
            catch { /* subscriber cleanup is best-effort */ }
            try { EngineStatusChanged -= OnEngineStatusChanged; }
            catch { /* subscriber cleanup is best-effort */ }
            base.OnClosed(e);
        }

        /// <summary>Handler for the local <see cref="EngineStatusChanged"/>
        /// event. Refreshes the Dashboard pill + failure banner. Fires on
        /// the UI thread (SetEngineStatus marshals), so we can touch
        /// controls directly.</summary>
        private void OnEngineStatusChanged(object sender, EventArgs e)
        {
            try { UpdateDashboardStatus(); }
            catch (Exception ex) { Logger.Log.Warn($"OnEngineStatusChanged: UpdateDashboardStatus threw: {ex.Message}"); }
        }

        private void OnAppStartupStatusChanged(object sender, EventArgs e)
        {
            // SetStartupStatus marshals to the UI thread before firing, so we
            // don't need another Dispatcher hop here — but UpdateDashboardStatus
            // self-marshals anyway if called cross-thread, so this is safe even
            // if the invariant ever slips.

            // Ready transition means the initial paid-pack sync just completed
            // (success or failure). Re-run RefreshTranslationsAsync so the
            // Translations tab flips from "Available / Download" to
            // "Installed" without the user needing to click Refresh. The
            // initial RefreshTranslationsAsync call from SettingsWindow.Load
            // runs BEFORE the download is on disk on a fresh install, so
            // without this we leave stale inventory on screen indefinitely.
            if (App.StartupStatus == App.InitialStartupStatus.Ready)
            {
                try { _ = RefreshTranslationsAsync(); }
                catch (Exception ex) { Logger.Log.Warn($"Post-sync RefreshTranslationsAsync failed: {ex.Message}"); }
            }

            try { UpdateDashboardStatus(); }
            catch (Exception ex) { Logger.Log.Warn($"OnAppStartupStatusChanged: UpdateDashboardStatus threw: {ex.Message}"); }
        }

        private void TextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var textBox = sender as System.Windows.Controls.TextBox;
                if (textBox != null)
                {
                    textBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                }
            }
        }

        private void PreviewRegion_Click(object sender, RoutedEventArgs e)
        {
            notifyIcon.ShowRegionOverlay();
        }

        // ─── Dashboard event handlers ────────────────────────────────

        private void DashToggleOcr_Click(object sender, RoutedEventArgs e)
        {
            OnToggleOCR?.Invoke();
            // UpdateDashboardStatus is called inside the delegate
        }

        private void DashSelectRegion_Click(object sender, RoutedEventArgs e)
        {
            OnSelectRegion?.Invoke();
            // UpdateDashboardRegionInfo is called inside the delegate
        }

        private void DashShowRegion_Click(object sender, RoutedEventArgs e)
        {
            OnShowRegion?.Invoke();
        }

        // CS0162 wraps the whole body: when the feature flag is a compile-time const
        // false, every statement after the early-return is provably unreachable.
#pragma warning disable CS0162
        private void DashSelectAnswerRegion_Click(object sender, RoutedEventArgs e)
        {
            // Defense-in-depth: the UI button is hidden while the answer-translation
            // feature is disabled, but if it's somehow invoked (e.g. via automation,
            // accessibility tools, or a style override) we should no-op instead of
            // popping the region picker. The underlying ChooseAnswerRegion() API stays
            // intact — a future release will re-enable both the button and this path.
            if (!MainWindow.FeatureAnswerTranslationEnabled)
            {
                Logger.Log.Info("DashSelectAnswerRegion ignored — answer translation is temporarily disabled");
                return;
            }

            try
            {
                var notify = (System.Windows.Application.Current.MainWindow as MainWindow)?.notify as Core.UI.INotifyIcon;
                notify?.ChooseAnswerRegion();

                var region = notify?.AnswerRegion;
                if (region != null && region.Length == 4)
                {
                    Logger.Log.Info($"Answer region set: {string.Join(",", region)}");
                    Status.Content = $"Answer region: {region[0]},{region[1]} {region[2]}x{region[3]}";
                }

                UpdateDashboardRegionInfo();
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Select answer region failed: {ex.Message}");
            }
        }
#pragma warning restore CS0162

        /// <summary>
        /// Runs <see cref="AutoRegionService.Detect"/> on a background thread,
        /// then applies the detected dialogue and answer regions to config.
        /// </summary>
        private async void DashAutoDetect_Click(object sender, RoutedEventArgs e)
        {
            DashAutoDetectButton.IsEnabled = false;
            DashAutoDetectStatus.Text = "Scanning...";
            DashAutoDetectStatus.Foreground = new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1E, 0x40, 0xAF));

            try
            {
                var result = await Task.Run(() => AutoRegionService.Detect(Game, engine));

                if (result.Success)
                {
                    Config.Set("Region", result.DialogueRegion);
                    notifyIcon.Region = result.DialogueRegion.Split(',');

                    if (!string.IsNullOrEmpty(result.AnswerRegion))
                    {
                        Config.Set("AnswerRegion", result.AnswerRegion);
                        notifyIcon.AnswerRegion = result.AnswerRegion.Split(',');
                    }

                    DashAutoDetectStatus.Text = $"\u2713 {result.Resolution} ({result.Method})";
                    DashAutoDetectStatus.Foreground = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x05, 0x96, 0x69));

                    // Show detected regions on screen for 5 seconds
                    notifyIcon.ShowRegionOverlay(TimeSpan.FromSeconds(5));

                    UpdateDashboardRegionInfo();

                    // Update region text boxes in Settings tab
                    var parts = result.DialogueRegion.Split(',');
                    if (parts.Length == 4)
                    {
                        RegionX.Text = parts[0];
                        RegionY.Text = parts[1];
                        RegionWidth.Text = parts[2];
                        RegionHeight.Text = parts[3];
                    }

                    // Post-change validation: fires on the UI thread after
                    // the Region has been persisted so MainWindow can compare
                    // its overlay bounds against the new capture rectangle.
                    OnCaptureRegionUserChanged?.Invoke();
                }
                else
                {
                    DashAutoDetectStatus.Text = $"\u2717 {result.Error}";
                    DashAutoDetectStatus.Foreground = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
                }
            }
            catch (Exception ex)
            {
                DashAutoDetectStatus.Text = $"\u2717 {ex.Message}";
                DashAutoDetectStatus.Foreground = new SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
                Logger.Log.Error($"Dashboard auto-detect failed: {ex}");
            }
            finally
            {
                DashAutoDetectButton.IsEnabled = true;
            }
        }

        private void DashSetupGuide_Click(object sender, RoutedEventArgs e)
        {
            OnOpenSetupWizard?.Invoke();
        }

        /// <summary>
        /// User clicked "Retry" on the engine-failure banner. Hand off to
        /// MainWindow via the <see cref="OnRetryEngineLoad"/> delegate — the
        /// handler flips status back to Loading and reruns the background
        /// Task.Run. The banner auto-hides as soon as status changes.
        /// </summary>
        private void DashEngineFailedRetry_Click(object sender, RoutedEventArgs e)
        {
            OnRetryEngineLoad?.Invoke(false);
        }

        /// <summary>
        /// User clicked "Fallback to CPU" — same retry but with
        /// <c>UseGpuOcr</c> forced off, so the DirectML path doesn't re-fail
        /// the same way. Useful when the GPU path is broken (missing
        /// DirectML.dll, RTX driver bug, etc.) but the CPU path would work.
        /// </summary>
        private void DashEngineFailedCpu_Click(object sender, RoutedEventArgs e)
        {
            OnRetryEngineLoad?.Invoke(true);
        }

        private void DashToggleSubtitle_Click(object sender, RoutedEventArgs e)
        {
            OnToggleSubtitles?.Invoke();
            // UpdateDashboardStatus is called inside the delegate
        }

        /// <summary>
        /// "Change" button on the Dashboard's Active-translation card.
        /// Jumps to the Translations tab — which is now the sole editor
        /// for Game / Input / Output in the session-21 redesign.
        /// </summary>
        private void DashChangeTranslation_Click(object sender, RoutedEventArgs e)
        {
            // Jump to the Translations tab. Match by Tag (culture-invariant),
            // not Header — the Header is a DynamicResource and resolves to the
            // user's current UI language ("Tłumaczenia" in PL, etc.).
            SelectTabByTag("Translations");
        }

        /// <summary>
        /// Selects the TabItem whose Tag (case-insensitive) equals <paramref name="tag"/>.
        /// Returns true when found. Used by Dashboard jump-to buttons.
        /// </summary>
        private bool SelectTabByTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            try
            {
                var tabControl = FindTabControl(this);
                if (tabControl == null) return false;
                foreach (var item in tabControl.Items)
                {
                    if (item is System.Windows.Controls.TabItem ti &&
                        (ti.Tag as string)?.Equals(tag, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        ti.IsSelected = true;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"SelectTabByTag({tag}) failed: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Opens <see cref="SendFeedbackWindow"/> as a modal owned by the
        /// Settings window. Second entry point to the feedback flow after
        /// the link on Setup Wizard Step 4 — Settings Dashboard is where
        /// users land most often during normal use.
        /// </summary>
        private void DashSendFeedback_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SendFeedbackWindow { Owner = this };
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"DashSendFeedback: opening feedback dialog failed: {ex}");
            }
        }

        /// <summary>
        /// Opens <see cref="ReferralsWindow"/> as a modal. The dialog loads
        /// the user's code + stats on open, so failure modes (network,
        /// unauthorized) stay inside the dialog rather than leaking here.
        /// </summary>
        private void DashReferrals_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new ReferralsWindow { Owner = this };
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"DashReferrals: opening referrals dialog failed: {ex}");
            }
        }

        /// <summary>
        /// Resource-lookup helper with a guaranteed English fallback so a
        /// missing key never surfaces as a WPF error glyph in the UI.
        /// Mirrors the same helper in SendFeedbackWindow / ReferralsWindow.
        /// </summary>
        private static string L(string key, string fallback)
            => System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

        /// <summary>
        /// Prompt the user to restart after a game switch, localized and
        /// guarded against every exception path. Call after writing the new
        /// value to Config["Game"] — if the user declines, the change still
        /// applies on next launch, so we never "lose" their click.
        ///
        /// Why a restart at all: MainWindow caches the Game key at
        /// construction (MainWindow.xaml.cs line 218), per-game bootstrap
        /// (EnsureGameDataReadyAsync) only runs from SettingsWindow.Load,
        /// and the dialogue-engine/per-game strategy is selected once at
        /// startup. A clean process is the only reliable path until we
        /// refactor those to re-read live.
        /// </summary>
        /// <param name="oldGame">Previous Config["Game"] tag (e.g. "Genshin").</param>
        /// <param name="newGame">Newly selected Config["Game"] tag (e.g. "StarRail").</param>
        private void PromptRestartForGameChange(string oldGame, string newGame)
        {
            try
            {
                string title = L("Dialog_RestartGame_Title", "Restart to switch games");
                string bodyFmt = L("Dialog_RestartGame_Body",
                    "Switching from {0} to {1}. Kaption needs a restart to finish loading the new game's data — some features won't work correctly until it does. Restart now?");
                string primary = L("Dialog_RestartGame_Restart", "Restart now");
                string secondary = L("Dialog_RestartGame_Later", "Later");

                // Prefer the localized friendly name (Game_Genshin → "Genshin Impact")
                // with a clean fallback to the raw tag so an unrecognized Config
                // value still renders something readable.
                string oldLabel = L("Game_" + (oldGame ?? ""), oldGame ?? "");
                string newLabel = L("Game_" + (newGame ?? ""), newGame ?? "");
                string body = string.Format(bodyFmt, oldLabel, newLabel);

                GI_Subtitles.Views.AppRestartPrompt.PromptAndRestart(
                    owner: this,
                    title: title,
                    body: body,
                    restartButtonText: primary,
                    laterButtonText: secondary,
                    severity: GI_Subtitles.Views.DialogSeverity.Question);
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"PromptRestartForGameChange: {ex.Message}");
            }
        }

        /// <summary>Walk the visual tree until we hit the TabControl the Dashboard lives in.</summary>
        private static System.Windows.Controls.TabControl FindTabControl(DependencyObject root)
        {
            if (root == null) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is System.Windows.Controls.TabControl tc) return tc;
                var nested = FindTabControl(child);
                if (nested != null) return nested;
            }
            return null;
        }

        /// <summary>
        /// Refresh the read-only "Active translation" line on the Dashboard
        /// to reflect the current Config triple. Called from
        /// <see cref="UpdateDashboardStatus"/> so any state change that
        /// already triggers a status refresh also refreshes this strip.
        /// </summary>
        private void UpdateDashboardActiveTranslation()
        {
            if (DashActiveTranslationText == null) return;
            try
            {
                string game = Config.Get("Game", "Genshin") ?? "Genshin";
                string input = Config.Get("Input", "EN") ?? "EN";
                string output = Config.Get("Output", "PL") ?? "PL";

                string gameDisplay;
                switch (game.ToLowerInvariant())
                {
                    case "genshin":  gameDisplay = "Genshin Impact";      break;
                    case "starrail": gameDisplay = "Honkai: Star Rail";   break;
                    default:         gameDisplay = game;                   break;
                }

                string inputDisplay = HumanLang(input);
                string outputDisplay = HumanLang(output);

                DashActiveTranslationText.Text = $"{gameDisplay}  \u00B7  {inputDisplay}  \u2192  {outputDisplay}";
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"UpdateDashboardActiveTranslation failed: {ex.Message}");
                DashActiveTranslationText.Text = "\u2014";
            }
        }

        private static string HumanLang(string code)
        {
            if (string.IsNullOrEmpty(code)) return "?";
            switch (code.ToUpperInvariant())
            {
                case "PL":  return "Polski";
                case "EN":  return "English";
                case "DE":  return "Deutsch";
                case "ES":  return "Español";
                case "FR":  return "Français";
                case "ID":  return "Bahasa Indonesia";
                case "IT":  return "Italiano";
                case "JP":  return "日本語";
                case "KR":  return "한국어";
                case "PT":  return "Português";
                case "RU":  return "Русский";
                case "TH":  return "ไทย";
                case "TR":  return "Türkçe";
                case "VI":  return "Tiếng Việt";
                case "CHS": return "简体中文";
                case "CHT": return "繁體中文";
                default:    return code;
            }
        }

        /// <summary>
        /// Updates all Dashboard status indicators.
        /// Call this whenever OCR/engine/dictionary state changes.
        /// </summary>
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
                bool ocrRunning = IsOcrRunning?.Invoke() ?? false;
                bool engineReady = IsEngineReady?.Invoke() ?? false;
                bool subtitleVis = IsSubtitleVisible?.Invoke() ?? true;
                bool dictLoaded = Matcher != null && Matcher.Loaded;
                bool downloading = App.StartupStatus == App.InitialStartupStatus.DownloadingTranslations;
                var engineState = Engine;
                bool engineLoading = engineState == EngineStatus.Loading;
                bool engineFailed = engineState == EngineStatus.Failed;

                // OCR toggle button: stays "Start" while translations download
                // OR while the OCR engine hasn't finished initializing. The
                // click is gated by TryGateInitialDictionarySync /
                // TryGateEngineReady too, but disabling the button here makes
                // the waiting state user-visible — no "I clicked Start and
                // nothing happened" tickets.
                if (DashToggleOcrButton != null)
                {
                    DashToggleOcrButton.Content = ocrRunning
                        ? TryFindResource("Dash_StopOCR") as string ?? "Stop"
                        : TryFindResource("Dash_StartOCR") as string ?? "Start";
                    // Stop is always allowed; Start is gated on initial sync
                    // AND on the engine being live. A failed engine still
                    // disables Start — the Retry banner below is the CTA.
                    DashToggleOcrButton.IsEnabled = ocrRunning || (!downloading && !engineLoading && !engineFailed);
                }

                // OCR status badge — priority order: Running › Downloading ›
                // Engine Failed › Engine Loading › Stopped. The download and
                // engine-loading states both use amber so the three "work in
                // progress" pills read as a single family; red is reserved
                // for Stopped + Engine Failed to signal actionable blockers.
                if (DashOcrStatusBadge != null && DashOcrStatusText != null)
                {
                    if (ocrRunning)
                    {
                        var fg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x05, 0x96, 0x69));
                        DashOcrStatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD1, 0xFA, 0xE5));
                        DashOcrStatusText.Foreground = fg;
                        DashOcrStatusText.Text = TryFindResource("Dash_Status_Running") as string ?? "Running";
                        if (DashOcrStatusIcon != null) DashOcrStatusIcon.Foreground = fg;
                    }
                    else if (downloading)
                    {
                        // Amber — same accent as Engine/Dictionary "Loading…" states so
                        // the three row statuses read as consistent "work in progress".
                        var fg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06));
                        DashOcrStatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xF3, 0xC7));
                        DashOcrStatusText.Foreground = fg;
                        DashOcrStatusText.Text = TryFindResource("Dash_Status_Downloading") as string ?? "Downloading translations…";
                        if (DashOcrStatusIcon != null) DashOcrStatusIcon.Foreground = fg;
                    }
                    else if (engineFailed)
                    {
                        var fg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
                        DashOcrStatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xE2, 0xE2));
                        DashOcrStatusText.Foreground = fg;
                        DashOcrStatusText.Text = TryFindResource("Dash_Status_EngineFailed") as string ?? "Translator failed to load";
                        if (DashOcrStatusIcon != null) DashOcrStatusIcon.Foreground = fg;
                    }
                    else if (engineLoading)
                    {
                        var fg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06));
                        DashOcrStatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xF3, 0xC7));
                        DashOcrStatusText.Foreground = fg;
                        DashOcrStatusText.Text = TryFindResource("Dash_Status_EngineLoading") as string ?? "Loading OCR engine…";
                        if (DashOcrStatusIcon != null) DashOcrStatusIcon.Foreground = fg;
                    }
                    else
                    {
                        var fg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
                        DashOcrStatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xE2, 0xE2));
                        DashOcrStatusText.Foreground = fg;
                        DashOcrStatusText.Text = TryFindResource("Dash_Status_Stopped") as string ?? "Stopped";
                        if (DashOcrStatusIcon != null) DashOcrStatusIcon.Foreground = fg;
                    }
                }

                // Engine-failed banner (non-modal). Collapsed unless the last
                // init attempt threw; shows Retry + "Fallback to CPU" buttons
                // wired to MainWindow.OnRetryEngineLoad. The Retry path
                // flips engine status back to Loading, the banner auto-hides,
                // and the background Task.Run starts again. Failure is
                // already reported to GlitchTip via
                // CrashReportingService.ReportException in MainWindow — this
                // panel is the user-facing counterpart.
                if (DashEngineFailedBanner != null)
                {
                    DashEngineFailedBanner.Visibility = engineFailed ? Visibility.Visible : Visibility.Collapsed;
                    if (engineFailed && DashEngineFailedDetails != null)
                    {
                        var err = LastEngineError;
                        DashEngineFailedDetails.Text = err != null
                            ? $"{err.GetType().Name}: {err.Message}"
                            : TryFindResource("Dash_EngineFailed_UnknownDetails") as string ?? "Unknown error.";
                    }
                }

                // Detail status
                if (DashOcrDetailStatus != null)
                {
                    DashOcrDetailStatus.Text = ocrRunning
                        ? TryFindResource("Dash_Status_Running") as string ?? "Running"
                        : TryFindResource("Dash_Status_Stopped") as string ?? "Stopped";
                    DashOcrDetailStatus.Foreground = new SolidColorBrush(ocrRunning
                        ? System.Windows.Media.Color.FromRgb(0x05, 0x96, 0x69)
                        : System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
                }

                // Engine status
                if (DashEngineStatus != null)
                {
                    if (engineReady)
                    {
                        DashEngineStatus.Text = TryFindResource("Dash_Status_Ready") as string ?? "Ready";
                        DashEngineStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x05, 0x96, 0x69));
                    }
                    else
                    {
                        DashEngineStatus.Text = TryFindResource("Dash_Status_Loading") as string ?? "Loading...";
                        DashEngineStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06));
                    }
                }

                // Dictionary status
                if (DashDictStatus != null)
                {
                    if (dictLoaded)
                    {
                        DashDictStatus.Text = $"{contentDict.Count:N0} entries";
                        DashDictStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x05, 0x96, 0x69));
                    }
                    else
                    {
                        DashDictStatus.Text = TryFindResource("Dash_Status_Loading") as string ?? "Loading...";
                        DashDictStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06));
                    }
                }

                // Prediction engine status
                if (DashPredictionStatus != null)
                {
                    if (ContextEngine?.IsLoaded == true)
                    {
                        DashPredictionStatus.Text = ContextEngine.GetStats();
                        DashPredictionStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x05, 0x96, 0x69));
                    }
                    else if (ContextEngine != null)
                    {
                        DashPredictionStatus.Text = "Loading...";
                        DashPredictionStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06));
                    }
                    else
                    {
                        DashPredictionStatus.Text = "Not loaded";
                        DashPredictionStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF));
                    }
                }

                // Subtitle toggle button
                if (DashToggleSubtitleButton != null)
                {
                    DashToggleSubtitleButton.Content = subtitleVis
                        ? TryFindResource("Dash_HideSubtitles") as string ?? "Hide Subtitles"
                        : TryFindResource("Dash_ShowSubtitles") as string ?? "Show Subtitles";
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"UpdateDashboardStatus error: {ex}");
            }
        }

        /// <summary>
        /// Updates the region info display on the Dashboard tab.
        /// </summary>
        public void UpdateDashboardRegionInfo()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateDashboardRegionInfo));
                return;
            }

            try
            {
                if (DashRegionInfo == null) return;

                string regionStr = Config.Get<string>("Region", "");
                if (string.IsNullOrEmpty(regionStr) || regionStr == "0" || regionStr.StartsWith("0,0,"))
                {
                    DashRegionInfo.Text = TryFindResource("Dash_Region_NotSet") as string ?? "Not set";
                }
                else
                {
                    string[] parts = regionStr.Split(',');
                    if (parts.Length == 4)
                    {
                        DashRegionInfo.Text = $"X: {parts[0]}   Y: {parts[1]}   W: {parts[2]}   H: {parts[3]}";
                    }
                    else
                    {
                        DashRegionInfo.Text = regionStr;
                    }
                }

                // Show answer region info if configured
                string answerStr = Config.Get<string>("AnswerRegion", "");
                if (!string.IsNullOrEmpty(answerStr) && answerStr != "0" && !answerStr.StartsWith("0,0,"))
                {
                    string[] ansParts = answerStr.Split(',');
                    if (ansParts.Length == 4)
                    {
                        DashRegionInfo.Text += $"\nAnswer: X: {ansParts[0]}   Y: {ansParts[1]}   W: {ansParts[2]}   H: {ansParts[3]}";
                    }
                }

                // Also sync the region text boxes on the Settings tab
                if (RegionX != null && notifyIcon?.Region != null && notifyIcon.Region.Length == 4)
                {
                    RegionX.Text = notifyIcon.Region[0];
                    RegionY.Text = notifyIcon.Region[1];
                    RegionWidth.Text = notifyIcon.Region[2];
                    RegionHeight.Text = notifyIcon.Region[3];
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"UpdateDashboardRegionInfo error: {ex}");
            }
        }

        private bool _suppressSliderUpdate = false;

        private void PadVerticalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderUpdate || PadTextBox == null) return;
            int pad = (int)e.NewValue;
            PadTextBox.Text = pad.ToString();
            int padHorizontal = Config.GetPadHorizontal(0);
            Config.Set("Pad", new int[] { pad, padHorizontal });
            UpdateMainWindowPosition();
        }

        private void PadHorizontalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderUpdate || PadHorizontalTextBox == null) return;
            int padHorizontal = (int)e.NewValue;
            PadHorizontalTextBox.Text = padHorizontal.ToString();
            int pad = Config.GetPad(-140);
            Config.Set("Pad", new int[] { pad, padHorizontal });
            UpdateMainWindowPosition();
        }

        private void PadTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(PadTextBox.Text, out int pad))
            {
                _suppressSliderUpdate = true;
                PadVerticalSlider.Value = Math.Max(PadVerticalSlider.Minimum, Math.Min(PadVerticalSlider.Maximum, pad));
                _suppressSliderUpdate = false;
                int padHorizontal = Config.GetPadHorizontal(0);
                Config.Set("Pad", new int[] { pad, padHorizontal });
                UpdateMainWindowPosition();
                OnOverlaySizeUserChanged?.Invoke();
            }
        }

        private void PadHorizontalTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(PadHorizontalTextBox.Text, out int padHorizontal))
            {
                _suppressSliderUpdate = true;
                PadHorizontalSlider.Value = Math.Max(PadHorizontalSlider.Minimum, Math.Min(PadHorizontalSlider.Maximum, padHorizontal));
                _suppressSliderUpdate = false;
                int pad = Config.GetPad(-140);
                Config.Set("Pad", new int[] { pad, padHorizontal });
                UpdateMainWindowPosition();
                OnOverlaySizeUserChanged?.Invoke();
            }
        }

        // Legacy +/- handlers (hidden buttons, kept for compatibility)
        private void PadVerticalIncrease_Click(object sender, RoutedEventArgs e) { }
        private void PadVerticalDecrease_Click(object sender, RoutedEventArgs e) { }
        private void PadHorizontalIncrease_Click(object sender, RoutedEventArgs e) { }
        private void PadHorizontalDecrease_Click(object sender, RoutedEventArgs e) { }

        // --- Display Zone handlers ---

        private void MaxHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderUpdate || MaxHeightTextBox == null) return;
            int val = (int)e.NewValue;
            if (val <= (int)MaxHeightSlider.Minimum)
            {
                MaxHeightTextBox.Text = "Auto";
                Config.Set("MaxOverlayHeight", 0);
            }
            else
            {
                MaxHeightTextBox.Text = val.ToString();
                Config.Set("MaxOverlayHeight", val);
            }
            UpdateMainWindowLayout();
        }

        private void MaxHeightTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (MaxHeightTextBox.Text.Trim().Equals("Auto", StringComparison.OrdinalIgnoreCase) || MaxHeightTextBox.Text.Trim() == "0")
            {
                _suppressSliderUpdate = true;
                MaxHeightSlider.Value = MaxHeightSlider.Minimum;
                _suppressSliderUpdate = false;
                MaxHeightTextBox.Text = "Auto";
                Config.Set("MaxOverlayHeight", 0);
            }
            else if (int.TryParse(MaxHeightTextBox.Text, out int val) && val > 0)
            {
                _suppressSliderUpdate = true;
                MaxHeightSlider.Value = Math.Max(MaxHeightSlider.Minimum, Math.Min(MaxHeightSlider.Maximum, val));
                _suppressSliderUpdate = false;
                Config.Set("MaxOverlayHeight", val);
            }
            OnOverlaySizeUserChanged?.Invoke();
        }

        private void MaxWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderUpdate || MaxWidthTextBox == null) return;
            int val = (int)e.NewValue;
            if (val <= (int)MaxWidthSlider.Minimum)
            {
                MaxWidthTextBox.Text = "Auto";
                Config.Set("MaxOverlayWidth", 0);
            }
            else
            {
                MaxWidthTextBox.Text = val.ToString();
                Config.Set("MaxOverlayWidth", val);
            }
            UpdateMainWindowLayout();
        }

        private void MaxWidthTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (MaxWidthTextBox.Text.Trim().Equals("Auto", StringComparison.OrdinalIgnoreCase) || MaxWidthTextBox.Text.Trim() == "0")
            {
                _suppressSliderUpdate = true;
                MaxWidthSlider.Value = MaxWidthSlider.Minimum;
                _suppressSliderUpdate = false;
                MaxWidthTextBox.Text = "Auto";
                Config.Set("MaxOverlayWidth", 0);
            }
            else if (int.TryParse(MaxWidthTextBox.Text, out int val) && val > 0)
            {
                _suppressSliderUpdate = true;
                MaxWidthSlider.Value = Math.Max(MaxWidthSlider.Minimum, Math.Min(MaxWidthSlider.Maximum, val));
                _suppressSliderUpdate = false;
                Config.Set("MaxOverlayWidth", val);
            }
            OnOverlaySizeUserChanged?.Invoke();
        }

        private void AutoShrinkCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            Config.Set("AutoShrinkText", AutoShrinkCheckBox.IsChecked == true);
            UpdateMainWindowLayout();
        }

        // --- Font handlers ---

        private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderUpdate || FontSizeTextBox == null) return;
            int val = (int)e.NewValue;
            FontSizeTextBox.Text = val.ToString();
            Config.Set("Size", val);
            UpdateMainWindowLayout();
        }

        private void FontSizeTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(FontSizeTextBox.Text, out int val) && val >= 10 && val <= 36)
            {
                _suppressSliderUpdate = true;
                FontSizeSlider.Value = val;
                _suppressSliderUpdate = false;
                Config.Set("Size", val);
                UpdateMainWindowLayout();
            }
        }

        private void FontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FontFamilyComboBox.SelectedItem is ComboBoxItem item && item.Tag is string fontName)
            {
                Config.Set("FontFamily", fontName);
                if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
                {
                    var fontFamily = new System.Windows.Media.FontFamily(fontName);
                    mainWindow.SubtitleText.FontFamily = fontFamily;
                    mainWindow.HeaderText.FontFamily = fontFamily;
                }
            }
        }

        private void UpdateMainWindowPosition()
        {
            if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
            {
                mainWindow.UpdateWindowPosition();
            }
        }

        /// <summary>
        /// Trigger layout recalculation on the main window (for display zone changes).
        /// </summary>
        private void UpdateMainWindowLayout()
        {
            if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
            {
                mainWindow._cachedFontSize = Config.Get<int>("Size", 24);
                mainWindow.UpdateWindowHeightAndTop();
            }
        }

        private void RegionField_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(RegionX.Text, out _) &&
                int.TryParse(RegionY.Text, out _) &&
                int.TryParse(RegionWidth.Text, out _) &&
                int.TryParse(RegionHeight.Text, out _))
            {
                string region = $"{RegionX.Text},{RegionY.Text},{RegionWidth.Text},{RegionHeight.Text}";
                Config.Set("Region", region);
                if (notifyIcon != null) notifyIcon.Region = region.Split(',');
                OnCaptureRegionUserChanged?.Invoke();
            }
        }

        private void OcrIntervalTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(OcrIntervalTextBox.Text, out int val))
            {
                // 50 ms is the floor: at 20 FPS the OCR thread can just about
                // keep up on a GPU-accelerated setup; anything lower starts
                // dropping frames and wastes battery. Ceiling stays 1000 ms —
                // beyond that the typewriter lag is user-visible.
                val = Math.Max(50, Math.Min(1000, val));
                OcrIntervalTextBox.Text = val.ToString();
                Config.Set("OcrInterval", val);
            }
        }

        private void UiRefreshTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(UiRefreshTextBox.Text, out int val))
            {
                val = Math.Max(100, Math.Min(1000, val));
                UiRefreshTextBox.Text = val.ToString();
                Config.Set("UiRefreshInterval", val);
                // Apply live — update the UI timer interval without restart
                var main = System.Windows.Application.Current.MainWindow as MainWindow;
                if (main != null)
                    main.UITimer.Interval = new TimeSpan(0, 0, 0, 0, val);
            }
        }

        private void StabilityWindowTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(StabilityWindowTextBox.Text, out int val))
            {
                val = Math.Max(1, Math.Min(10, val));
                StabilityWindowTextBox.Text = val.ToString();
                Config.Set("StabilityWindow", val);
            }
        }

        private void AutoStartCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            Config.Set("AutoStart", AutoStartCheckBox.IsChecked == true);
        }

        private void EnableAnswerTranslationCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            Config.Set("EnableAnswerTranslation", EnableAnswerTranslationCheckBox.IsChecked == true);
        }

        // ───────────────────────────────────────────────────────────────────
        //  Crash reporting opt-in
        // ───────────────────────────────────────────────────────────────────

        private void CrashReportingCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool enabled = CrashReportingCheckBox.IsChecked == true;
            Config.Set("CrashReportingEnabled", enabled);
            // Tell the scaffold to re-read consent so the change takes effect
            // immediately — no restart required. Once Sentry is wired up this
            // is where the SDK starts/stops.
            try { GI_Subtitles.Services.Observability.CrashReportingService.RefreshConsent(); }
            catch (Exception ex) { Logger.Log.Warn($"CrashReporting refresh failed: {ex.Message}"); }
        }

        // ───────────────────────────────────────────────────────────────────
        //  Auto-update banner — called from MainWindow.HandleUpdateCheckResult.
        //  All UI lives here; MainWindow owns the UpdateService logic.
        // ───────────────────────────────────────────────────────────────────

        private Action _onUpdateBannerInstall;
        private Func<System.Threading.Tasks.Task> _onUpdateBannerWhatsNew;
        private Action _onUpdateBannerRemind;
        private System.Windows.Threading.DispatcherTimer _updateBannerAutoDismissTimer;

        /// <summary>
        /// Fetch admin broadcast announcements and render them above the
        /// Dashboard. Non-blocking — any network / API failure is logged
        /// and the section stays hidden; the user doesn't see a scary
        /// error banner for a feature that may simply not have any
        /// announcements live.
        ///
        /// Currently one-shot on window open. A periodic refresh (30 min?)
        /// can be added later if admins start publishing time-sensitive
        /// announcements mid-session.
        /// </summary>
        private async Task LoadAnnouncementsAsync()
        {
            try
            {
                var api = new Services.Network.KaptionApiClient();
                var items = await api.GetAnnouncementsAsync(CancellationToken.None).ConfigureAwait(true);
                if (items == null || items.Count == 0) return;

                // Back to UI thread — ConfigureAwait(true) above preserves it,
                // but be defensive: Dispatcher.CheckAccess() covers us if the
                // API call returned synchronously from a worker pool thread.
                if (!Dispatcher.CheckAccess())
                {
                    await Dispatcher.InvokeAsync(() => RenderAnnouncementBanners(items));
                }
                else
                {
                    RenderAnnouncementBanners(items);
                }
            }
            catch (Exception ex)
            {
                // Expected failures: offline, backend down, CORS / DNS issues.
                // None are user-actionable — log at Info so we don't raise
                // false alarm in crash telemetry.
                Logger.Log.Info($"Announcements: fetch skipped ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        private void RenderAnnouncementBanners(IReadOnlyList<Services.Network.AnnouncementPublic> items)
        {
            if (AnnouncementsStack == null) return;
            AnnouncementsStack.Children.Clear();
            foreach (var item in items)
            {
                AnnouncementsStack.Children.Add(BuildAnnouncementBorder(item));
            }
            AnnouncementsStack.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Split announcement text on the <c>&lt;br&gt;</c>, <c>&lt;br/&gt;</c>,
        /// <c>&lt;br /&gt;</c> markers admins type in the editor, inserting a
        /// WPF <see cref="System.Windows.Documents.LineBreak"/> between the
        /// resulting segments. Deliberately narrow: we do NOT parse general
        /// HTML — any other tag renders verbatim so admins can't accidentally
        /// (or intentionally) inject styling. This matches what most admins
        /// expect when they type "line 1&lt;br&gt;line 2" into a freeform
        /// body field.
        /// </summary>
        private static void AppendInlinesWithLineBreaks(System.Windows.Controls.TextBlock block, string text)
        {
            block.Inlines.Clear();
            if (string.IsNullOrEmpty(text)) return;

            // Pattern covers <br>, <br/>, <br /> with any spacing / casing.
            var parts = System.Text.RegularExpressions.Regex.Split(
                text, @"<br\s*/?>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) block.Inlines.Add(new System.Windows.Documents.LineBreak());
                if (!string.IsNullOrEmpty(parts[i])) block.Inlines.Add(new Run(parts[i]));
            }
        }

        private Border BuildAnnouncementBorder(Services.Network.AnnouncementPublic a)
        {
            // Severity → palette. Unknown severity renders as info so future
            // migrations (e.g. a new "celebration" severity) don't break
            // layout with a dead banner.
            System.Windows.Media.Color bg, border, title;
            switch ((a?.Severity ?? "info").ToLowerInvariant())
            {
                case "critical":
                    bg = System.Windows.Media.Color.FromRgb(0x4A, 0x1E, 0x1E);
                    border = System.Windows.Media.Color.FromRgb(0xDB, 0x3B, 0x3B);
                    title = System.Windows.Media.Color.FromRgb(0xFF, 0xDD, 0xDD);
                    break;
                case "warn":
                    bg = System.Windows.Media.Color.FromRgb(0x4A, 0x3A, 0x1E);
                    border = System.Windows.Media.Color.FromRgb(0xDB, 0xA0, 0x3B);
                    title = System.Windows.Media.Color.FromRgb(0xFF, 0xEB, 0xB5);
                    break;
                default: // info
                    bg = System.Windows.Media.Color.FromRgb(0x1E, 0x2A, 0x4A);
                    border = System.Windows.Media.Color.FromRgb(0x3B, 0x5B, 0xDB);
                    title = System.Windows.Media.Color.FromRgb(0xDD, 0xE4, 0xFF);
                    break;
            }
            var body = System.Windows.Media.Color.FromRgb(0xA9, 0xB4, 0xD8);

            // NOTE: fully-qualified TextBlock because this file also has
            // `using PaddleOCRSharp;` at the top, which defines its own
            // PaddleOCRSharp.TextBlock (OCR-result record). CS0104 otherwise.
            var panel = new StackPanel();
            var titleBlock = new System.Windows.Controls.TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(title),
                TextWrapping = TextWrapping.Wrap,
            };
            AppendInlinesWithLineBreaks(titleBlock, a?.Title ?? string.Empty);
            panel.Children.Add(titleBlock);
            if (!string.IsNullOrEmpty(a?.Body))
            {
                var bodyBlock = new System.Windows.Controls.TextBlock
                {
                    FontSize = 11,
                    Foreground = new SolidColorBrush(body),
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                };
                AppendInlinesWithLineBreaks(bodyBlock, a.Body);
                panel.Children.Add(bodyBlock);
            }
            if (!string.IsNullOrEmpty(a?.LinkUrl) && !string.IsNullOrEmpty(a?.LinkLabel))
            {
                var link = new System.Windows.Documents.Hyperlink(new Run(a.LinkLabel))
                {
                    NavigateUri = new Uri(a.LinkUrl, UriKind.Absolute),
                    Foreground = new SolidColorBrush(title),
                };
                link.RequestNavigate += (s, args) =>
                {
                    try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
                    catch (Exception ex) { Logger.Log.Warn($"Announcement link open failed: {ex.Message}"); }
                    args.Handled = true;
                };
                panel.Children.Add(new System.Windows.Controls.TextBlock(link)
                {
                    FontSize = 11,
                    Margin = new Thickness(0, 6, 0, 0),
                });
            }

            return new Border
            {
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 10),
                Child = panel,
            };
        }

        /// <summary>
        /// Show the update-available banner on the Dashboard tab. Callbacks
        /// route clicks back to MainWindow. Auto-dismisses after 10 s unless
        /// the user interacts first.
        /// </summary>
        public void ShowUpdateBanner(
            string version,
            string releaseNotesUrl,
            Action onInstall,
            Func<System.Threading.Tasks.Task> onWhatsNew,
            Action onRemindLater)
        {
            try
            {
                _onUpdateBannerInstall = onInstall;
                _onUpdateBannerWhatsNew = onWhatsNew;
                _onUpdateBannerRemind = onRemindLater;

                UpdateBannerTitle.Text = string.IsNullOrEmpty(version)
                    ? "Update available"
                    : $"Kaption {version} is available";
                UpdateBannerBody.Text = string.IsNullOrEmpty(releaseNotesUrl)
                    ? "A newer version is ready to install. This will close Kaption briefly."
                    : "A newer version is ready to install. See what's new before installing.";
                UpdateBanner.Visibility = Visibility.Visible;

                // No auto-dismiss. Update-available is an actionable
                // notification — it should persist on the Dashboard until
                // the user explicitly installs, views the release notes,
                // or chooses "Remind me later". Previous 10-second auto-
                // dismiss was surprising ("I opened the app, saw a banner,
                // blinked, and it was gone") and gave no way to recover it
                // without waiting for the next periodic update check.
                _updateBannerAutoDismissTimer?.Stop();
                _updateBannerAutoDismissTimer = null;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"ShowUpdateBanner failed: {ex.Message}");
            }
        }

        public void HideUpdateBanner()
        {
            try
            {
                _updateBannerAutoDismissTimer?.Stop();
                _updateBannerAutoDismissTimer = null;
                if (UpdateBanner != null)
                    UpdateBanner.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"HideUpdateBanner failed: {ex.Message}");
            }
        }

        private void UpdateBannerInstall_Click(object sender, RoutedEventArgs e)
        {
            _updateBannerAutoDismissTimer?.Stop();
            HideUpdateBanner();
            try { _onUpdateBannerInstall?.Invoke(); }
            catch (Exception ex) { Logger.Log.Error($"UpdateBannerInstall click: {ex}"); }
        }

        private async void UpdateBannerWhatsNew_Click(object sender, RoutedEventArgs e)
        {
            _updateBannerAutoDismissTimer?.Stop();
            try
            {
                if (_onUpdateBannerWhatsNew != null)
                    await _onUpdateBannerWhatsNew.Invoke();
            }
            catch (Exception ex) { Logger.Log.Error($"UpdateBannerWhatsNew click: {ex}"); }
        }

        private void UpdateBannerRemind_Click(object sender, RoutedEventArgs e)
        {
            _updateBannerAutoDismissTimer?.Stop();
            HideUpdateBanner();
            try { _onUpdateBannerRemind?.Invoke(); }
            catch (Exception ex) { Logger.Log.Error($"UpdateBannerRemind click: {ex}"); }
        }

        private void PlayerNameTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            Config.Set("PlayerName", PlayerNameTextBox.Text.Trim());
        }

        private OverlayCardManager _cardManager
        {
            get
            {
                if (System.Windows.Application.Current.MainWindow is MainWindow mw)
                    return mw._cardManager;
                return null;
            }
        }

        private void ShowSecondLangCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            Config.Set("ShowSecondLang", ShowSecondLangCheckBox.IsChecked == true);
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true // Must be true to open in the default browser
                });
            }
            catch (Exception ex)
            {
                Logger.Log.Error(ex);
            }
            e.Handled = true;
        }

        #region Logs Tab

        private string _logLevelFilter = "ALL";

        public void InitializeLogTab()
        {
            LogListView.ItemsSource = LogBuffer.Entries;
            LogBuffer.EntryAdded += OnLogEntryAdded;
        }

        private void OnLogEntryAdded()
        {
            if (LogListView.Items.Count > 0)
            {
                LogListView.ScrollIntoView(LogListView.Items[LogListView.Items.Count - 1]);
            }
        }

        private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
        {
            LogBuffer.Clear();
        }

        private void OpenLogFileButton_Click(object sender, RoutedEventArgs e)
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Kaption", "app.log");
            if (File.Exists(logPath))
            {
                Process.Start(new ProcessStartInfo { FileName = logPath, UseShellExecute = true });
            }
        }

        private void LogLevelFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LogLevelFilter?.SelectedItem is ComboBoxItem selected)
            {
                _logLevelFilter = selected.Tag?.ToString() ?? "ALL";
                ApplyLogFilter();
            }
        }

        private void ApplyLogFilter()
        {
            if (LogListView == null) return;

            if (_logLevelFilter == "ALL")
            {
                LogListView.ItemsSource = LogBuffer.Entries;
            }
            else
            {
                var view = new System.Windows.Data.ListCollectionView(LogBuffer.Entries);
                view.Filter = obj =>
                {
                    if (obj is LogEntry entry)
                    {
                        return string.Equals(entry.Level, _logLevelFilter, StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                };
                LogListView.ItemsSource = view;
            }
        }

        #endregion

        // ════════════════════════════════════════════════════════════════════
        //  Translations Tab
        //  Merged view of "what's actually on disk" + "what the backend
        //  offers for my tier". The point of this tab is to kill the old
        //  confusion where users saw Polish files in %APPDATA%\Kaption\Genshin\
        //  and concluded that DictionarySync had downloaded something — when
        //  in fact those are local builds from the bundled seed JSONs.
        // ════════════════════════════════════════════════════════════════════
        #region Translations Tab

        /// <summary>Bound to <c>TranslationsList.ItemsSource</c>. Regrouped by Game name on each refresh.</summary>
        private readonly ObservableCollection<Models.TranslationPackInfo> _translationPacks
            = new ObservableCollection<Models.TranslationPackInfo>();

        private CancellationTokenSource _translationsScanCts;
        private bool _translationsInitialized;

        /// <summary>
        /// In-flight (game, lang) pairs currently being downloaded via
        /// <see cref="DownloadPackCoreAsync"/>. Used to no-op re-entrant
        /// calls when the user rapid-clicks a row while a download for
        /// the same pack is already running. Keys are
        /// <c>"{game}/{lang}"</c> lower-cased so casing drift between
        /// row-click paths (which use lowercase from the backend row)
        /// and button-click paths (which bind the whole pack object)
        /// can't bypass the guard.
        /// </summary>
        private readonly HashSet<string> _activeDownloads
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Wire up the list + kick off an initial background scan. Runs once
        /// from the constructor; subsequent refreshes go through
        /// <see cref="RefreshTranslationsAsync"/>.
        /// </summary>
        private System.Windows.Data.ListCollectionView _translationsView;

        private void InitializeTranslationsTab()
        {
            if (_translationsInitialized) return;
            _translationsInitialized = true;

            // Group packs by GameDisplayName so Genshin and Star Rail render
            // under separate headers inside the ListView. The grouping stays
            // even with the single-game filter below because users can
            // temporarily clear the filter in the future (toggle TBD) and the
            // grouping cost is near-zero when only one game is visible.
            _translationsView = new System.Windows.Data.ListCollectionView(_translationPacks);
            _translationsView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(Models.TranslationPackInfo.GameDisplayName)));
            _translationsView.Filter = TranslationsFilterByCurrentGame;
            TranslationsList.ItemsSource = _translationsView;

            // Fire-and-forget initial scan. Running off the UI thread keeps
            // window construction snappy even if the API call takes a second.
            _ = RefreshTranslationsAsync();
        }

        /// <summary>
        /// Filter predicate: only show packs matching the currently-selected
        /// <see cref="Game"/>. Users reported "I switched to Star Rail but
        /// still see Genshin files" — root cause was that the scan produced
        /// both games' packs (grouped under headers) but OnGameSelectorChanged
        /// never toggled what's visible. The filter + Refresh() in
        /// OnGameSelectorChanged fixes it without refetching.
        /// </summary>
        private bool TranslationsFilterByCurrentGame(object item)
        {
            if (!(item is Models.TranslationPackInfo pack)) return false;
            if (string.IsNullOrEmpty(Game)) return true; // defensive — no filter until Game is set
            return string.Equals(pack.Game, Game, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Re-run the inventory scan. Cancels any in-flight scan first so
        /// repeated clicks on Refresh don't stack.
        /// </summary>
        private async Task RefreshTranslationsAsync()
        {
            _translationsScanCts?.Cancel();
            _translationsScanCts = new CancellationTokenSource();
            var ct = _translationsScanCts.Token;

            SetTranslationsSummary("Scanning local folder and checking for updates…");
            SetTranslationsControlsEnabled(false);

            try
            {
                // The inventory service handles its own error paths and
                // falls back to local-only when the server is unreachable.
                var inventory = new Services.Translation.DictionaryInventoryService(
                    new Services.Network.KaptionApiClient(),
                    App.LicenseService);

                var scan = await Task.Run(() => inventory.ScanAsync(ct), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;

                // Marshal back to the UI thread before touching ObservableCollection.
                await Dispatcher.InvokeAsync(() =>
                {
                    // Compute the source-language display once per scan.
                    // InputLanguages is {"English":"EN", ...} so we invert
                    // for the lookup. Falls back to the raw code when
                    // Config["Input"] is an unexpected value — users on
                    // pre-shipped tags ("CHS" etc.) still see something
                    // sensible ("CHS → Polski") rather than a blank.
                    string inputCode = Config.Get("Input", "EN") ?? "EN";
                    string inputDisplay = InputLanguages.FirstOrDefault(kv =>
                        string.Equals(kv.Value, inputCode, StringComparison.OrdinalIgnoreCase)).Key ?? inputCode;

                    _translationPacks.Clear();
                    foreach (var pack in scan.Packs)
                    {
                        pack.DirectionLabel = $"{inputDisplay} → {pack.LanguageDisplayName}";
                        _translationPacks.Add(pack);
                    }

                    // Summary line mirrors the one logged to app.log so users
                    // can correlate what they see in the tab with the logs.
                    string summary;
                    int installed = scan.InstalledLocalCache + scan.InstalledPaidCached;
                    if (scan.Packs.Count == 0)
                    {
                        summary = "No translation packs detected yet.";
                    }
                    else if (scan.RemoteQueryOk)
                    {
                        summary =
                            $"{installed} installed · {scan.RemoteAvailable} available to download · {scan.RemoteLocked} locked.";
                    }
                    else
                    {
                        summary = $"{installed} installed locally. Remote catalog unavailable.";
                    }
                    SetTranslationsSummary(summary);

                    if (!string.IsNullOrEmpty(scan.RemoteQueryError))
                    {
                        TranslationsNoticeText.Text = scan.RemoteQueryError;
                        TranslationsNoticeBanner.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        TranslationsNoticeBanner.Visibility = Visibility.Collapsed;
                    }

                    // Empty-state overlay: show the branded "nothing installed
                    // yet, here's what to try" panel when the scan found zero
                    // packs. WPF ListView.Items.IsEmpty is driven off the
                    // ObservableCollection, but we key off the explicit count
                    // to keep the toggle in sync with the same frame that
                    // clears/repopulates the collection above.
                    if (TranslationsEmptyState != null)
                    {
                        TranslationsEmptyState.Visibility = _translationPacks.Count == 0
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }

                    // Pill highlight + IsActiveTarget reconciliation. Done last
                    // so it reads from the fully-populated _translationPacks.
                    SyncTranslationPickerState();
                });
            }
            catch (OperationCanceledException) { /* next scan took over */ }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Translations: scan failed: {ex.Message}");
                await Dispatcher.InvokeAsync(() => SetTranslationsSummary("Scan failed — see logs."));
            }
            finally
            {
                await Dispatcher.InvokeAsync(() => SetTranslationsControlsEnabled(true));
            }
        }

        /// <summary>
        /// Sink for tab-wide status text. Session-32 dissolved the header
        /// summary card, so the TextBlock the old x:Name pointed at is gone;
        /// we keep the method so existing callers inside the scan / sync /
        /// download pipeline still compile and log useful breadcrumbs.
        /// The list itself now communicates state (per-row pills).
        /// </summary>
        private void SetTranslationsSummary(string text)
        {
            Logger.Log.Info($"[Translations] {text}");
        }

        private void SetTranslationsControlsEnabled(bool enabled)
        {
            if (TranslationsList != null) TranslationsList.IsEnabled = enabled;
        }

        /// <summary>
        /// "Refresh" toolbar button — re-runs the local + remote inventory scan.
        /// </summary>
        private void TranslationsRefresh_Click(object sender, RoutedEventArgs e)
        {
            _ = RefreshTranslationsAsync();
        }

        /// <summary>
        /// "Vote for what's next →" button on the Next-Up banner. Opens the
        /// public voting grid on kaption.one in the user's default browser.
        /// The anchor (#vote / #next-language) puts the user directly on the
        /// section that the landing's LanguageVote.astro renders.
        /// </summary>
        private void NextLanguageVote_Click(object sender, RoutedEventArgs e)
        {
            OpenUrlInBrowser("https://kaption.one/#next-language");
        }

        /// <summary>"Join Discord" button on the Community tab.</summary>
        private void CommunityDiscord_Click(object sender, RoutedEventArgs e)
        {
            OpenUrlInBrowser("https://kaption.one/discord");
        }

        /// <summary>"Follow on X" button on the Community tab.</summary>
        private void CommunityX_Click(object sender, RoutedEventArgs e)
        {
            OpenUrlInBrowser("https://x.com/KaptionOne");
        }

        /// <summary>"Open web" button on the Community tab.</summary>
        private void CommunityWebsite_Click(object sender, RoutedEventArgs e)
        {
            OpenUrlInBrowser("https://kaption.one/");
        }

        /// <summary>
        /// Shared ShellExecute helper — keeps the Process.Start ceremony in
        /// one spot, and silences the exception on failure (we don't want to
        /// crash the settings window if the user has no default browser).
        /// </summary>
        private static void OpenUrlInBrowser(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"OpenUrlInBrowser('{url}') failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Per-row "Update" button — appears when <see cref="Models.TranslationPackInfo.CanUpdate"/>
        /// is true (installed pack whose LocalVersion differs from the latest
        /// RemoteVersion for our tier). Delegates to the same download path as
        /// "Download": <see cref="DictionarySyncService"/> writes the new
        /// version to the same per-game folder, so the existing pack is
        /// superseded atomically. We call into the shared Download handler so
        /// there is ONE sync/encrypt/rescan pipeline.
        /// </summary>
        private void TranslationsSyncPack_Click(object sender, RoutedEventArgs e)
        {
            TranslationsDownloadPack_Click(sender, e);
        }

        // ────────────────────────────────────────────────────────────────
        //  Segmented-pill pickers (Game + Input) + row-click selection.
        //  This tab is the sole editor for Config["Game"] / ["Input"] /
        //  ["Output"] as of session 21 — Dashboard still reads these but
        //  no longer writes them. Handlers below translate a pill click
        //  into a Config write + inventory refresh + (for output target
        //  switches) an automatic DictionarySync if the chosen pack
        //  isn't cached yet. Idempotency: DictionarySync.SyncOneAsync
        //  already no-ops when the cached version matches the catalog.
        // ────────────────────────────────────────────────────────────────

        /// <summary>Apply "selected" styling to whichever pill in <paramref name="group"/> has the matching tag.</summary>
        private void ApplyPillSelection(IEnumerable<System.Windows.Controls.Button> group, string selectedTag)
        {
            foreach (var btn in group)
            {
                bool isSelected = string.Equals(btn.Tag?.ToString(), selectedTag, StringComparison.OrdinalIgnoreCase);
                // Fully-qualify Style — SettingsWindow.xaml.cs imports
                // System.Web.UI.WebControls which has its own Style type,
                // making a bare `Style` ambiguous in this file.
                btn.Style = isSelected
                    ? (System.Windows.Style)FindResource("ModernButton")     // filled accent = selected
                    : (System.Windows.Style)FindResource("SecondaryButton"); // outlined      = unselected
            }
        }

        /// <summary>
        /// Paint the three rows of pill controls (Game + Input + active-
        /// target in the output list) to reflect current Config. Called
        /// after every Refresh so a Config write from any source (this
        /// tab, Dashboard legacy path, external edit of Config.json)
        /// converges the UI on the next scan.
        /// </summary>
        private void SyncTranslationPickerState()
        {
            try
            {
                string currentGame = Config.Get("Game", "Genshin") ?? "Genshin";
                string currentInput = Config.Get("Input", "EN") ?? "EN";
                string currentOutput = Config.Get("Output", "PL") ?? "PL";

                if (GamePillGenshin != null && GamePillStarRail != null)
                    ApplyPillSelection(new[] { GamePillGenshin, GamePillStarRail }, currentGame);

                if (InputPillEN != null && InputPillJP != null && InputPillCHS != null)
                    ApplyPillSelection(new[] { InputPillEN, InputPillJP, InputPillCHS }, currentInput);

                // Flip IsActiveTarget on every pack — binding PropertyChanged
                // handles the visual update for the list rows.
                foreach (var pack in _translationPacks)
                {
                    pack.IsActiveTarget =
                        pack.Game.Equals(currentGame, StringComparison.OrdinalIgnoreCase) &&
                        pack.Language.Equals(currentOutput, StringComparison.OrdinalIgnoreCase);
                }

                // Dashboard's "Active translation" strip reads Config["Game"],
                // Config["Input"], Config["Output"] — but it's only refreshed
                // via UpdateDashboardStatus, which the game/input/output pill
                // handlers didn't invoke. Every path that mutates the triple
                // converges here, so do the dashboard repaint in one place.
                UpdateDashboardActiveTranslation();
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"SyncTranslationPickerState failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Game-pill click: write Config, repaint, refresh inventory, and
        /// kick an upstream check for the new game's public TextMaps so
        /// switching from Genshin to HSR (or vice versa) doesn't leave
        /// the user with no source dictionaries for the game they just
        /// selected.
        /// </summary>
        private async void TranslationsGamePill_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is System.Windows.Controls.Button btn)) return;
            string tag = btn.Tag?.ToString();
            if (string.IsNullOrEmpty(tag)) return;

            string current = Config.Get("Game", "Genshin");
            if (string.Equals(tag, current, StringComparison.OrdinalIgnoreCase)) return;

            Config.Set("Game", tag);
            Game = tag; // keep the SettingsWindow's cached copy in sync for other handlers
            Logger.Log.Info($"Translations: game switched to {tag}.");

            // Prompt immediately — if the user picks Restart, we skip the
            // upstream-check Task.Run below that a fresh launch would redo.
            PromptRestartForGameChange(current, tag);

            // Fire the upstream check for the new game's input TextMap.
            // Output is only auto-fetched via this path when it's a
            // mirror-covered language (handled inside the service).
            _ = Task.Run(async () =>
            {
                try
                {
                    var updater = new Services.Translation.GameDataUpdateService();
                    using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
                    {
                        await updater.CheckAndUpdateAsync(
                            tag,
                            Config.Get("Input", "EN"),
                            Config.Get("Output", "PL"),
                            cts.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn($"Game-pill upstream check failed: {ex.Message}");
                }
            });

            await RefreshTranslationsAsync();
        }

        /// <summary>
        /// Input-pill click: write Config, repaint, refresh inventory, and
        /// also check/download the public TextMap for the new reading
        /// language if it isn't already on disk. Without this kick, a
        /// user switching from English → Japanese input gets the inventory
        /// refreshed but no TextMapJP.json ever lands — the next matcher
        /// rebuild on restart then has no Japanese source to hash against.
        /// </summary>
        private async void TranslationsInputPill_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is System.Windows.Controls.Button btn)) return;
            string tag = btn.Tag?.ToString();
            if (string.IsNullOrEmpty(tag)) return;

            string current = Config.Get("Input", "EN");
            if (string.Equals(tag, current, StringComparison.OrdinalIgnoreCase)) return;

            Config.Set("Input", tag);
            InputLanguage = tag;
            Logger.Log.Info($"Translations: input language switched to {tag}.");

            // Fire-and-forget upstream check for the new input's TextMap.
            // GameDataUpdateService is throttled + conditional-GET so a
            // rapid back-and-forth between pills won't hammer upstream.
            _ = Task.Run(async () =>
            {
                try
                {
                    var updater = new Services.Translation.GameDataUpdateService();
                    using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
                    {
                        await updater.CheckAndUpdateAsync(
                            Config.Get("Game", "Genshin"),
                            tag,
                            outputLang: null, // output untouched; only refresh input
                            ct: cts.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn($"Input-pill upstream check failed: {ex.Message}");
                }
            });

            await RefreshTranslationsAsync();
        }

        /// <summary>
        /// Row click: set the pack's language as the active target. If the
        /// pack is already cached (LocalCache / PaidCached), this is just
        /// a Config write. If it's RemoteAvailable, we ALSO kick off a
        /// DictionarySync so the cache is populated before the user restarts.
        /// Reuses the same download-path code as the "Download" button.
        /// </summary>
        private async void TranslationsPackRow_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is Models.TranslationPackInfo pack)) return;
            if (pack == null || string.IsNullOrWhiteSpace(pack.Game) || string.IsNullOrWhiteSpace(pack.Language)) return;

            // If the user clicked the Download/Open-folder button inside the
            // row, let that handler do its thing — don't double-fire.
            //
            // Tricky bit: e.OriginalSource can be an inline Run (from the
            // path TextBlock <Run Text="…"/>), which is a DependencyObject
            // but NOT a Visual — VisualTreeHelper.GetParent throws
            // "System.Windows.Documents.Run is not a Visual" on it. So we
            // climb the LOGICAL tree first from anything that isn't a
            // Visual/Visual3D, land on the enclosing TextBlock (or other
            // Visual), and then switch to the visual tree for the usual
            // Button-ancestor walk.
            if (e.OriginalSource is DependencyObject dep)
            {
                while (dep != null && !(dep is System.Windows.Media.Visual) && !(dep is System.Windows.Media.Media3D.Visual3D))
                {
                    dep = System.Windows.LogicalTreeHelper.GetParent(dep);
                }

                var btn = dep as FrameworkElement;
                while (btn != null)
                {
                    if (btn is System.Windows.Controls.Button) return;
                    btn = System.Windows.Media.VisualTreeHelper.GetParent(btn) as FrameworkElement;
                }
            }

            // Config["Game"] must match the row's game so OCR runs on the
            // right game-data. If the user clicks a StarRail row while
            // Genshin is active, flip the Game too — one-click "use this
            // translation" is the whole point of the redesign.
            bool gameChanged = !string.Equals(pack.Game, Game, StringComparison.OrdinalIgnoreCase);
            string previousGame = Game;
            if (gameChanged)
            {
                Config.Set("Game", pack.Game);
                Game = pack.Game;
                Logger.Log.Info($"Translations: game switched to {pack.Game} via row click.");
            }

            bool outputChanged = !string.Equals(pack.Language, OutputLanguage, StringComparison.OrdinalIgnoreCase);
            if (outputChanged)
            {
                Config.Set("Output", pack.Language);
                OutputLanguage = pack.Language;
                Logger.Log.Info($"Translations: output switched to {pack.Language} via row click.");
            }

            // Offer restart BEFORE the (possibly long) download so an end-user
            // who just wanted to switch games doesn't sit through a download
            // that a clean relaunch would redo on next bootstrap anyway. If
            // they pick Later the download proceeds.
            if (gameChanged)
            {
                PromptRestartForGameChange(previousGame, pack.Game);
            }

            // Download if the pack isn't cached yet. The SyncAsync path is
            // already idempotent — if the user clicked an already-installed
            // row, DictionarySync either no-ops (manifest up-to-date) or
            // re-downloads when versions diverge, both safe.
            if (!pack.IsInstalled && pack.Source == Models.TranslationPackSource.RemoteAvailable)
            {
                await DownloadPackCoreAsync(pack);
            }
            else
            {
                // No download needed — just repaint so the newly-active row
                // gets its accent stripe.
                SyncTranslationPickerState();
            }
        }

        /// <summary>
        /// Shared download path used by both the per-row Download button
        /// and the row-click-triggered auto-download. Returns after the
        /// sync has settled + the inventory has been re-scanned.
        /// </summary>
        private async Task DownloadPackCoreAsync(Models.TranslationPackInfo pack)
        {
            var license = App.LicenseService;
            if (license?.CurrentActivation == null)
            {
                GI_Subtitles.Views.ModernDialog.Warn(
                    owner: this,
                    title: "Sign in required",
                    body: "Sign in to your Kaption account to download translation packs.");
                return;
            }

            // Debounce: if a download for this exact (game, lang) pair is
            // already running, no-op. Prevents the "rapid-click rows"
            // pathology where each click fires another SyncAsync — the
            // log from session 21 showed 8 concurrent calls within 2s.
            // Key is lowercased so "Genshin"+"PL" (from row click) and
            // "genshin"+"pl" (from already-stored backend row) collapse.
            string dedupeKey = $"{(pack.Game ?? "").ToLowerInvariant()}/{(pack.Language ?? "").ToLowerInvariant()}";
            lock (_activeDownloads)
            {
                if (_activeDownloads.Contains(dedupeKey))
                {
                    Logger.Log.Debug($"DownloadPackCore: {dedupeKey} already in flight, ignoring re-entry.");
                    return;
                }
                _activeDownloads.Add(dedupeKey);
            }

            SetTranslationsSummary($"Downloading {pack.LanguageDisplayName} for {pack.GameDisplayName}\u2026");

            try
            {
                var sync = new Services.Translation.DictionarySyncService(
                    new Services.Network.KaptionApiClient(),
                    license,
                    Services.Security.FileProtectionFactory.Create());
                using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
                {
                    // Normalise game/lang casing on the wire so the backend's
                    // case-insensitive D1 lookup matches regardless of which
                    // casing flows through the desktop. Without this, a
                    // "Genshin/PL" (from the pill) + "genshin/pl" (from a
                    // pack row's stored value) drift produced zero-match.
                    string wireGame = (pack.Game ?? "").ToLowerInvariant();
                    string wireLang = (pack.Language ?? "").ToLowerInvariant();
                    var result = await Task.Run(() => sync.SyncAsync(wireGame, wireLang, cts.Token));
                    if (!result.Ok && result.Messages.Count > 0)
                    {
                        TranslationsNoticeText.Text = result.Messages[0];
                        TranslationsNoticeBanner.Visibility = Visibility.Visible;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"DownloadPackCore: {pack.Game}/{pack.Language} failed: {ex.Message}");
                GI_Subtitles.Views.ModernDialog.Error(
                    owner: this,
                    title: "Download failed",
                    body: $"Couldn't download {pack.LanguageDisplayName} for {pack.GameDisplayName}. Check your connection and try again.",
                    technicalDetails: ex.ToString());
            }
            finally
            {
                lock (_activeDownloads) { _activeDownloads.Remove(dedupeKey); }
            }

            await RefreshTranslationsAsync();
        }

        /// <summary>
        /// Per-row "Download" button. Fires when the user clicks Download
        /// on an Available pack; triggers <see cref="DictionarySyncService"/>
        /// scoped to that single (game, language) pair rather than the
        /// app-wide configured pair. Button is disabled during the
        /// download and the inventory is re-scanned on completion so the
        /// row flips from Available → Installed (Kaption pack).
        ///
        /// The `Tag` binding from the XAML carries the full
        /// <see cref="Models.TranslationPackInfo"/> so we don't have to
        /// re-look it up; the binding stays live across ObservableCollection
        /// mutations as long as the instance itself isn't replaced.
        /// </summary>
        private async void TranslationsDownloadPack_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is System.Windows.Controls.Button btn)) return;
            if (!(btn.Tag is Models.TranslationPackInfo pack)) return;
            if (pack == null || string.IsNullOrWhiteSpace(pack.Game) || string.IsNullOrWhiteSpace(pack.Language))
                return;

            var license = App.LicenseService;
            if (license?.CurrentActivation == null)
            {
                GI_Subtitles.Views.ModernDialog.Warn(
                    owner: this,
                    title: "Sign in required",
                    body: "Sign in to your Kaption account to download translation packs.");
                return;
            }

            // Optimistic UI: lock the button + show spinner-ish text so
            // a repeat click can't queue parallel downloads of the same
            // pack. The refresh at the end rebuilds the row, so we don't
            // need to restore the button state ourselves.
            string originalContent = btn.Content as string ?? "Download";
            btn.IsEnabled = false;
            btn.Content = "Downloading\u2026";

            SetTranslationsSummary($"Downloading {pack.LanguageDisplayName} for {pack.GameDisplayName}\u2026");

            try
            {
                var sync = new Services.Translation.DictionarySyncService(
                    new Services.Network.KaptionApiClient(),
                    license,
                    Services.Security.FileProtectionFactory.Create());
                using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
                {
                    var result = await Task.Run(() => sync.SyncAsync(pack.Game, pack.Language, cts.Token));

                    if (!result.Ok && result.Messages.Count > 0)
                    {
                        TranslationsNoticeText.Text = result.Messages[0];
                        TranslationsNoticeBanner.Visibility = Visibility.Visible;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"TranslationsDownloadPack: {pack.Game}/{pack.Language} failed: {ex.Message}");
                GI_Subtitles.Views.ModernDialog.Error(
                    owner: this,
                    title: "Download failed",
                    body: $"Couldn't download {pack.LanguageDisplayName} for {pack.GameDisplayName}. Check your connection and try again.",
                    technicalDetails: ex.ToString());
            }
            finally
            {
                // Best-effort restore — if the ObservableCollection has
                // already swapped the row out from under us (as Refresh
                // does), these writes land on a detached button instance
                // and don't matter.
                btn.Content = originalContent;
                btn.IsEnabled = true;
            }

            // Re-scan so the row reflects reality (Installed / still Available
            // if download failed mid-way / Missing if backend yanked it etc.).
            await RefreshTranslationsAsync();
        }

        /// <summary>
        /// Per-row "Open folder" button. Uses the pack's LocalPath to jump
        /// straight to the parent folder in Explorer.
        /// </summary>
        private void TranslationsOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is System.Windows.Controls.Button btn) || !(btn.Tag is string localPath))
                return;
            if (string.IsNullOrEmpty(localPath)) return;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(localPath);
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return;
                // /select, highlights the file within the opened folder so the
                // user can immediately see which pack was referenced.
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{localPath}\"");
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Translations: open folder failed: {ex.Message}");
            }
        }

        #endregion

        // X button hides the Settings window — the app itself lives in the
        // tray and the subtitle overlay. Only the tray "Exit" menu actually
        // shuts the app down, which now goes through RealClose() → REAL_CLOSE
        // flag. Before session 32 this handler called Application.Shutdown()
        // on X, which made sense when ShowDialog was the only entry point but
        // crashed the user's flow once the update-balloon click started
        // opening Settings non-modally: close with X → whole app exits.
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!REAL_CLOSE)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnClosing(e);
        }

        // Provide a method to manually close the window (e.g. when the program exits)
        public void RealClose()
        {
            REAL_CLOSE = true;
            try
            {
                engine.Dispose();
            }
            catch
            {

            }
            this.Close();
        }

        private void OpenDialogueLogFolder_Click(object sender, RoutedEventArgs e)
        {
            string logPath = Services.DialogueLog.GetCurrentLogPath();
            string dir = System.IO.Path.GetDirectoryName(logPath);
            if (System.IO.Directory.Exists(dir))
                System.Diagnostics.Process.Start("explorer.exe", dir);
        }

        // ─── Matcher blob v3 save / v2→v3 migration helpers ──────────────
        //
        // Keeping these as private methods on SettingsWindow (as opposed
        // to a static helper class) buys two things:
        //   1. direct access to _protectionService via its concrete type
        //      so we can call the internal EncryptStreamToV3 path.
        //   2. the logging uses the same Logger.Log target as the rest of
        //      the load pipeline, so failures surface in the usual place.

        /// <summary>
        /// Serialise <paramref name="matcher"/> as a plaintext KMX blob,
        /// then wrap it in the v3 AES-CTR container at
        /// <paramref name="targetPath"/>. Fire-and-catches all errors —
        /// the legacy GSMX save still runs in the same rebuild, so a
        /// dropped v3 write just means the next launch rebuilds the
        /// matcher through GSMX instead of mmap'ing it.
        /// </summary>
        private void TryWriteMatcherBlobV3(string targetPath, OptimizedMatcher matcher,
            string game, string language)
        {
            if (string.IsNullOrEmpty(targetPath) || matcher == null) return;

            try
            {
                var meta = new MatcherBlobSchema.MatcherMeta
                {
                    FormatVersion = 1,
                    EntryCount = (uint)matcher.EntryCount,
                    AvgKeyLength = 0,
                    CreatedUtcTicks = DateTime.UtcNow.Ticks,
                    CorpusVersion = string.Empty,
                    Game = game ?? string.Empty,
                    Language = language ?? string.Empty,
                };

                // MatcherBlobWriter needs a seekable stream (it back-patches
                // the header), and the v3 encryptor wants a known plaintext
                // length up front. Build the blob in a MemoryStream then
                // stream it into EncryptStreamToV3 so we never materialise
                // a second ciphertext copy. The v3 path is now part of the
                // IFileProtectionService surface — same ServerKey-derived
                // keys as the v2 CBC path.
                using (var ms = new MemoryStream())
                {
                    matcher.Save(ms, meta);
                    long plaintextLen = ms.Length;
                    ms.Position = 0;

                    _protectionService.EncryptStreamToV3(ms, plaintextLen, targetPath);
                }

                long sizeKb = new FileInfo(targetPath).Length / 1024;
                Logger.Log.Info($"Saved FST matcher blob v3 ({sizeKb:N0} KB) — {targetPath}");
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Failed to save v3 FST matcher blob: {ex.Message}");
            }
        }

        /// <summary>
        /// Transparent migration: rewrite the v2 .kmx.gisub at
        /// <paramref name="path"/> as v3 so the next app launch can
        /// memory-map it. Best-effort — failure leaves the v2 file in
        /// place and the next launch simply falls back to the v2 load
        /// path again.
        /// </summary>
        private void TryMigrateMatcherBlobToV3(string path, OptimizedMatcher matcher,
            string game, string language)
        {
            if (string.IsNullOrEmpty(path) || matcher == null) return;
            try
            {
                Logger.Log.Info($"Migrating matcher blob v2→v3: {path}");
                TryWriteMatcherBlobV3(path, matcher, game, language);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Matcher blob v2→v3 migration failed (will retry on next rebuild): {ex.Message}");
            }
        }
    }
}
