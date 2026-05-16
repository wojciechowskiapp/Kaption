using OpenCvSharp;
using OpenCvSharp.Extensions;
using PaddleOCRSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Path = System.IO.Path;
using System.Media;
using GI_Subtitles.Services.Capture;
using System.Reflection;
using System.Threading.Tasks;
using System.Net;
using System.Diagnostics;
using Newtonsoft.Json;
using System.Net.Http;
using GI_Subtitles.Core.Cache;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Core.Pooling;
using GI_Subtitles.Core.Runtime;
using GI_Subtitles.Core.UI;
using GI_Subtitles.Models;
using GI_Subtitles.Services.OCR;
using GI_Subtitles.Services.Translation;
using GI_Subtitles.Common;
using GI_Subtitles.Core.Screen;
using GI_Subtitles.Services.Rendering;
using static GI_Subtitles.Core.Config.Config;
using MatchSource = GI_Subtitles.Services.Rendering.MatchSource;
using System.Windows.Threading;

[assembly: log4net.Config.XmlConfigurator(Watch = true)]
namespace GI_Subtitles.Views
{
    public static class Logger
    {
        public static log4net.ILog Log = log4net.LogManager.GetLogger("LogFileAppender");
    }


    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : System.Windows.Window
    {
        /// <summary>
        /// Resource-lookup helper used by MainWindow-scoped ModernDialog calls
        /// so the Polish translations kick in when Strings.pl-PL.xaml is
        /// active. The English fallback keeps call sites safe if a key is
        /// missing at runtime.
        /// </summary>
        private static string LocalizedString(string key, string fallback)
            => System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

        private static int OCR_TIMER = 0;
        private static int UI_TIMER = 0;
        private Mat _lastBinaryFrame = null;       // last frame for stability check
        private Mat _lastOcrBinaryFrame = null;    // frame at last OCR for subtitle-change check
        private volatile bool _isOcrRunning = false;
        private volatile bool _engineReady = false;
        private const double ChangeThreshold = 0.01;

        // ── Feature flags ────────────────────────────────────────────────────
        //
        // TEMPORARY: Answer-translation is disabled for now while we stabilize the
        // core dialogue-translation path. The feature works end-to-end (OCR of a
        // separate answer region, translation via AnswerTranslationService, display
        // in the subtitle card) but introduced extra complexity — multiple OCR calls
        // per tick, window-height recalc when the answer panel appears/disappears,
        // and its own stability heuristics. Keeping it off lets us validate the
        // dialogue path without those confounders.
        //
        // To re-enable:
        //   1. Flip this flag to true.
        //   2. Unhide EnableAnswerTranslationCheckBox in SettingsWindow.xaml.
        //   3. Remove the "(temporarily disabled)" panel in SetupWizardWindow and
        //      restore normal step-2 navigation (see SetupWizardWindow.xaml.cs).
        // The underlying code (AnswerTranslationService, answer OCR, display path)
        // is fully intact — this gate is the only thing stopping it from running.
        internal const bool FeatureAnswerTranslationEnabled = false;
        internal readonly OverlayCardManager _cardManager = new OverlayCardManager();
        private DateTime _lastOcrTime = DateTime.MinValue;
        // Live-reload: read from Config on each access so changes apply without restart.
        // Default 200ms matches the UI timer and was the stable pre-prediction value; going
        // tighter (150ms) amplified typewriter-phase re-triggers and caused visible flicker.
        // Per-game pacing. Genshin's typewriter wants a longer stability window;
        // HSR renders instantly and cycles lines faster so it wants a tighter
        // one. GameOcrTuning.* falls back to the user's Config override, then
        // the GameRegionProfile for Config["Game"], then a global default.
        private static TimeSpan MinOcrInterval =>
            TimeSpan.FromMilliseconds(GI_Subtitles.Services.Detection.GameOcrTuning.OcrIntervalMs());
        // Windowed stability: compare current frame against a frame from N ticks ago.
        // During typewriter animation, characters accumulate over the window → detectable change.
        // Default 5 (≈500ms window) — shrinking below this risked OCR firing on partial
        // typewriter frames, producing wrong partial matches and visible text flicker.
        private readonly Queue<Mat> _stabilityBuffer = new Queue<Mat>();
        private static int StabilityWindowSize => GI_Subtitles.Services.Detection.GameOcrTuning.StabilityWindow();
        // Consecutive stable frame counter for eager preview OCR
        private int _consecutiveStableFrames = 0;
        // Track whether the currently displayed content came from a fully stable OCR
        private volatile bool _lastOcrWasFullyStable = false;
        // Track when screen first diverged from last OCR frame (for forced re-check)
        private DateTime _changedVsOcrSince = DateTime.MinValue;
        // Per-game via GameOcrTuning; see GameRegionProfile.ForceOcrAfterSeconds.
        // Read each tick so a mid-session game switch picks up the new value.
        private static double ForceOcrAfterChangeSeconds
            => GI_Subtitles.Services.Detection.GameOcrTuning.ForceOcrAfterSeconds();
        // Predictive pre-display: show chain prediction before OCR confirms.
        // Gated behind Config("PredictivePreDisplay", false) — off by default because
        // replacing a wrong prediction with the actual OCR result causes a visible swap.
        // When enabled, _lastDisplayedWasPredicted suppresses the fade-in during the
        // predicted→actual handoff so the swap is instant instead of two fade animations.
        private volatile string _predictedContent = null;
        private volatile bool _lastDisplayedWasPredicted = false;
        private const double ClearStaleSubtitleSeconds = 0.3;
        private bool _isFallbackText = false;
        private string _detectedNpcName = "";
        string ocrText = "";
        private NotifyIcon notifyIcon;
        string lastHeader = null;
        string lastContent = null;
        // Track recently shown content to suppress blink when same text
        // re-appears after a brief empty OCR frame
        string _recentContent = null;
        DateTime _recentContentTime = DateTime.MinValue;
        // Use an LRU cache to limit memory usage to 100 entries
        readonly LRUCache<string, string> resDict = new LRUCache<string, string>(100);
        public System.Windows.Threading.DispatcherTimer OCRTimer = new System.Windows.Threading.DispatcherTimer();
        public System.Windows.Threading.DispatcherTimer UITimer = new System.Windows.Threading.DispatcherTimer();
        readonly bool debug = Config.Get<bool>("Debug", false);
        readonly string server = Config.Get<string>("Server", "https://mp3.2langs.com/download");
        readonly string token = Config.Get<string>("Token", "ENGI");
        readonly int distant = Config.Get<int>("Distant", 3);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int SetWindowPos(IntPtr hWnd, int hWndInsertAfter, int x, int y, int Width, int Height, int flags);
        [DllImport("User32.dll")]
        private static extern int GetDpiForSystem();
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // Click-through Win32 interop
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;

        // Screen capture exclusion (Windows 10 2004+)
        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        // DWM flush — waits for compositor to render the current frame
        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        // DWM glass extension — creates transparency without WS_EX_LAYERED (allows WDA_EXCLUDEFROMCAPTURE)
        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int Left, Right, Top, Bottom;
        }

        private IntPtr _mainWindowHandle = IntPtr.Zero;
        private System.Windows.Threading.DispatcherTimer _clickThroughRestoreTimer;

        private const int HOTKEY_ID_1 = 9000; // Custom hotkey ID
        private const int HOTKEY_ID_2 = 9001; // Custom hotkey ID
        private const int HOTKEY_ID_3 = 9002; // Custom hotkey ID
        private const int HOTKEY_ID_4 = 9003;
        private const int HOTKEY_ID_5 = 9004;
        private const int HOTKEY_ID_6 = 9005;
        private const uint MOD_CTRL = 0x0002; // Ctrl key
        private const uint MOD_SHIFT = 0x0004; // Shift key
        private const uint VK_S = 0x53; // Virtual key code for S
        private const uint VK_R = 0x52; // Virtual key code for R
        private const uint VK_H = 0x48; // Virtual key code for H
        private const uint VK_D = 0x44;
        private const uint VK_E = 0x45; // Virtual key code for E
        private double Scale = GetDpiForSystem() / 96f;
        // Use an LRU cache to limit memory usage to 30 entries (mapping from image hash to OCR text)
        LRUCache<string, string> BitmapDict = new LRUCache<string, string>(30);
        // Cache for NPC names detected by color, keyed by image hash
        LRUCache<string, string> NpcNameCache = new LRUCache<string, string>(30);
        // Consecutive empty-OCR frames since last non-empty dialogue detection.
        // When this crosses EmptyOcrCacheClearThreshold, BitmapDict + NpcNameCache
        // are flushed so FindSimilarImageHash (Hamming-distance fuzzy match) can't
        // resurrect a prior dialog's translation on a visually-similar open-world
        // frame. Without this, the Statue interaction prompt (and similar flavor
        // text) kept ghost-popping up minutes after the dialogue ended.
        private int _consecutiveEmptyOcrFrames = 0;
        private const int EmptyOcrCacheClearThreshold = 3;
        // Sensible defaults match SettingsWindow. Reading Config without a
        // fallback on a clean install returns null, which silently breaks
        // the pipeline — DictionarySync skips with "game or language not
        // configured", Inventory reports 0 packs, the missing-pack dialog
        // never fires, and the user sees nothing. Default to the same
        // tuple SettingsWindow assumes so first launch behaves like a user
        // who opened Settings once and hit Save.
        string InputLanguage = Config.Get("Input", "EN") ?? "EN";
        string OutputLanguage = Config.Get("Output", "PL") ?? "PL";
        string Game = Config.Get<string>("Game") ?? "Genshin";
        // DEPRECATED: Config("Update") was a static JSON manifest URL used by the
        // legacy CheckAndUpdate path. It is no longer consulted — updates are
        // now handled by UpdateService (Services/Update/UpdateService.cs) which
        // delegates to Velopack's UpdateManager against Config["UpdateFeedUrl"]
        // (default: https://files.kaption.one/releases/stable/). The config
        // key is retained only so old installs don't trip on an unexpected
        // schema; nothing reads this field any more.
        string Update = Config.Get<string>("Update");
        string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();

        /// <summary>
        /// Background auto-updater. Non-blocking — started from MainWindow_Loaded
        /// after a 5-second settle delay so it doesn't fight with first-launch
        /// engine loading and UI painting.
        /// </summary>
        private readonly GI_Subtitles.Services.Update.UpdateService _updateService =
            new GI_Subtitles.Services.Update.UpdateService();
        private CancellationTokenSource _updateCheckCts;
        private GI_Subtitles.Services.Update.UpdateCheckResult _pendingUpdate;

        /// <summary>
        /// Cancellation source for the paid-dictionary sync background task.
        /// Cancelled on window close so a slow download doesn't keep the
        /// process alive after the user quit.
        /// </summary>
        private CancellationTokenSource _dictionarySyncCts;
        string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kaption");
        internal INotifyIcon notify;
        SettingsWindow data;
        private System.Drawing.Rectangle screenBounds = Screen.PrimaryScreen.Bounds;
        bool ShowText = true;
        bool ChooseRegion = false;
        // Net8 migration: SocketsHttpHandler + 5-min pooled connection lifetime
        // so CF edge reshuffles don't pin us to a stale IP, + Brotli decompression
        // (new in SocketsHttpHandler on net6+) + HTTP/2 multiplexing. Same pattern
        // as KaptionApiClient.CreateHttpClient. See the deep-analysis doc §4.11.
        private static readonly HttpClient _sharedHttpClient = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
        }) { Timeout = TimeSpan.FromSeconds(30) };
        internal int _cachedFontSize;
        private volatile int failedCount = 0;
        private bool usingRegion2 = false;

        // Quick-translate independent popup
        private System.Windows.Window _quickTranslatePopup;
        private System.Windows.Threading.DispatcherTimer _quickTranslateTimer;
        // Single-flight guard: rapid Ctrl+Q presses were stacking translucent
        // Screenshot.GetRegion overlays (visible as "white screen") and racing
        // parallel OCR pipelines. A press while one is in flight is a no-op;
        // the 300ms debounce also absorbs keyboard auto-repeat.
        private volatile bool _quickTranslateBusy;
        private DateTime _lastQuickTranslateUtc = DateTime.MinValue;

        // Subtitle layout engine
        private ISubtitleLayoutEngine _layoutEngine = new DefaultSubtitleLayoutEngine();

        // Answer region translation (volatile: written on Task.Run, read on UI thread)
        private volatile string[] _translatedAnswers;
        private readonly AnswerTranslationService _answerService = new AnswerTranslationService();
        private volatile bool _isAnswerOcrRunning;
        private DateTime _lastAnswerOcrTime = DateTime.MinValue;
        private long _lastAnswerPixelSum;

        // Screen capture backend — DXGI preferred (GPU), GDI fallback
        private IScreenCapture _captureBackend;
        private bool _wdaExcludeActive = false; // True only if SetWindowDisplayAffinity succeeded

        private volatile DetectedTextResult _lastDetectedText;

        // P2: Auto-hide subtitle after idle
        private System.Windows.Threading.DispatcherTimer _idleHideTimer;
        private bool _subtitleVisible = false;
        private DateTime _lastContentChangeTime = DateTime.MinValue;
        private bool _hiddenForFocusLoss = false;

        // Drag positioning: suppress pad saves during programmatic moves
        private volatile bool _isUserDragging = false;
        private bool _clickThroughEnabled = false;

        // ── OCR active-time accumulator (session 26 — referrals) ──────────────
        //
        // The referral program credits the inviter with bonus paid-tier days
        // only when the invitee actually uses Kaption, not just when they sign
        // up. The backend expects the desktop to report cumulative "active OCR
        // seconds" since the previous heartbeat in the heartbeat body. We use
        // a UTC timestamp snapshot rather than a Stopwatch because:
        //   1. Stopwatch doesn't survive machine sleep/wake consistently across
        //      SKUs (some hypervisors freeze QPC, some don't).
        //   2. A timestamp-diff approach also naturally bounds the contribution
        //      from a single tick to the OCR interval (≤ a few hundred ms), so
        //      a hung timer can't inflate the counter by hours.
        // Access pattern: incremented from the UI thread in GetOCR ticks,
        // snapshot-and-reset from LicenseService heartbeat thread via
        // Interlocked.
        private long _ocrActiveMillisAccumulator = 0;
        private DateTime _ocrTickWindowStartUtc = DateTime.MinValue;

        // P2: Auto-pause when game minimized
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);


        public MainWindow()
        {
            Logger.Log.Debug("Start App");
            InitializeComponent();
            // Start with the main window fully transparent to avoid showing incomplete UI during heavy startup work.
            // Using Opacity instead of Visibility to ensure Loaded is still raised and initialization runs as usual.
            this.Opacity = 0;
            Loaded += MainWindow_Loaded;
            // NOTE: the legacy CheckAndUpdate(Update) call previously lived here.
            // It has been replaced by UpdateService, scheduled from
            // MainWindow_Loaded → StartBackgroundUpdateCheck() with a 5s settle
            // delay so it runs AFTER the first-launch experience, not during it.
            DispatcherTimer _hideButtonTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2),
                IsEnabled = false
            };
            _hideButtonTimer.Tick += (s, e) =>
            {
                _hideButtonTimer.Stop();
            };
        }


        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Get the window handle
            IntPtr handle = new WindowInteropHelper(this).Handle;
            _mainWindowHandle = handle;
            // Listen to window messages
            HwndSource source = HwndSource.FromHwnd(handle);
            source.AddHook(WndProc);
            // Click-through is OFF by default — WPF AllowsTransparency with Background="Transparent"
            // on the Grid already passes clicks through transparent areas to the game.
            // Only the opaque SubtitleBackground catches mouse input (for drag-to-reposition).
            // User can still toggle via Ctrl+Shift+D if needed.

            // Try to exclude overlay from screen capture.
            // WDA_EXCLUDEFROMCAPTURE doesn't work with AllowsTransparency (WS_EX_LAYERED),
            // but MaskOverlayAreas compensates by blacking out the overlay region in captures.
            _wdaExcludeActive = TryExcludeFromCapture(_mainWindowHandle);
            if (!_wdaExcludeActive)
            {
                Logger.Log.Info("Using MaskOverlayAreas to exclude overlay from captures (WPF layered window)");
            }

            _cachedFontSize = Config.Get<int>("Size", 22);

            // Initialize screen capture: try DXGI (GPU, fast) first, fall back to GDI
            var dxgi = new DxgiScreenCapture();
            if (dxgi.IsAvailable)
            {
                _captureBackend = dxgi;
                Logger.Log.Info("Using DXGI Desktop Duplication for screen capture (GPU-accelerated)");
            }
            else
            {
                dxgi.Dispose();
                _captureBackend = new GdiScreenCapture();
                Logger.Log.Info("Using GDI CopyFromScreen for screen capture (DXGI unavailable)");
            }

            notify = new INotifyIcon();
            notifyIcon = notify.InitializeNotifyIcon(Scale);
            // Construct SettingsWindow inside a guarded block so a corrupted
            // license session (no per-device file-protection secret available)
            // surfaces as "please sign in again" rather than as an opaque
            // SettingsWindow ctor crash. The factory throws on missing secret;
            // any path other than the foreground bootstrap getting us here is
            // a desync between LicenseService.CurrentActivation and disk that
            // we can't safely recover from in-process. Force a clean re-auth.
            try
            {
                data = new SettingsWindow(version, notify, Scale);
            }
            catch (InvalidOperationException ex) when (ex.Message.IndexOf("FileProtectionFactory.Create", StringComparison.Ordinal) >= 0)
            {
                Logger.Log.Error(
                    $"MainWindow_Loaded: file-protection secret missing — forcing sign-out. {ex.Message}");
                ForceReauthAndExit(
                    "Your sign-in needs to be refreshed.",
                    "Kaption couldn't load your session. Please launch the app again to sign in. Your translations and settings are preserved.");
                return;
            }
            data.InitializeKey(handle);
            notify.SetData(data);

            // Always kick off Load(): it runs GameDataBootstrapService first
            // (auto-downloads TextMapEN from GitHub + TextMapPL from R2 if
            // missing), then builds the matcher. Before v2.0 this was gated
            // on FileExists(), which silently did nothing on fresh installs —
            // that's the bug that left the matcher null and spammed
            // "Matcher not loaded yet, skipping translation".
            Task.Run(async () => await data.Load());

            // Upstream "a newer pack is available" balloon — only meaningful
            // when we already have a local copy to compare against. Same
            // reason it was originally gated: no local date means no "this
            // is newer than yours" claim to make.
            if (data.FileExists())
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var modify = await data.GetRepositoryModificationDate(data.repoUrl, Game);
                        DateTime inputDate = data.GetLocalFileDates(InputLanguage, OutputLanguage, Game);

                        if (DateTime.TryParse(modify, out DateTime repoDate))
                        {
                            if (repoDate > inputDate)
                            {
                                // BeginInvoke (not Invoke): this runs on a
                                // background Task and we don't need to wait
                                // for the balloon tip / title-flash work to
                                // finish. Invoke would block this Task on
                                // the UI thread, which is a latent cause of
                                // cross-thread freezes when UI is busy.
                                _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    notifyIcon.ShowBalloonTip(3000, "Language pack update notification", $"Repository update time: {repoDate}, local modification time: {inputDate}", ToolTipIcon.Info);
                                    string originalTitle = data.Title;
                                    data.Title = $"[Language pack update]{originalTitle}";
                                    data.Title = originalTitle;
                                }));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log.Error(ex);
                    }
                });
            }
            // Wire up Dashboard actions
            data.OnToggleOCR = () =>
            {
                if (OCRTimer.IsEnabled)
                {
                    // STOP is always allowed — even a revoked user should be
                    // able to turn off what's already running.
                    OCRTimer.Stop();
                    UITimer.Stop();
                    while (_stabilityBuffer.Count > 0)
                        _stabilityBuffer.Dequeue().Dispose();
                    ResetActiveOcrWindow();
                    ClearReadyPlaceholderIfActive();
                    // Clear the "Continue anyway" override so the next Start
                    // gets a fresh overlap evaluation. Without this, a user
                    // who bypassed once stays bypassed for the whole app
                    // session — any new overlap created mid-session goes
                    // un-warned until they restart Kaption.
                    _overlapOverrideAccepted = false;
                    SystemSounds.Hand.Play();
                    SwitchIcon("kaption.ico");
                }
                else
                {
                    // START is gated on: (1) a live license session, (2) the
                    // initial translation sync having finished, (3) the OCR
                    // engine having finished loading, (4) a capture region
                    // actually being configured (otherwise GetOCR crashes on
                    // notify.Region[1] IndexOutOfRange), and (5) the target
                    // game actually being running. Each gate surfaces its own
                    // localized dialog so the user knows which one tripped —
                    // silent failures here lead to a "it says OCR but nothing
                    // happens" support ticket.
                    if (!TryGateOcrStart()) return;
                    if (!TryGateInitialDictionarySync()) return;
                    if (!TryGateEngineReady()) return;
                    if (!TryGateRegionConfigured()) return;
                    if (!TryGateGameRunning()) return;
                    if (!TryGateFullscreenTip()) return;
                    if (!TryGateOverlayNotInRegion()) return;
                    UpdateWindowPosition();
                    ResetActiveOcrWindow();
                    OCRTimer.Start();
                    UITimer.Start();
                    SystemSounds.Exclamation.Play();
                    SwitchIcon("kaption-running.ico");
                    // Show a "ready" placeholder so the box is visibly
                    // grabbable even before the first translation arrives.
                    // UpdateText replaces it the moment real content comes in.
                    ShowReadyPlaceholderIfEmpty();
                }
                data.UpdateDashboardStatus();
            };
            data.OnSelectRegion = () =>
            {
                if (!ChooseRegion)
                {
                    ChooseRegion = true;
                    notify.ChooseRegion();
                    ChooseRegion = false;
                    data.UpdateDashboardRegionInfo();
                    OnCaptureRegionUserChange(dialogOwner: data);
                }
            };
            data.OnCaptureRegionUserChanged = () => OnCaptureRegionUserChange(dialogOwner: data);
            data.OnOverlaySizeUserChanged = () => OnOverlaySizeUserChange(dialogOwner: data);
            data.OnShowRegion = () =>
            {
                notify.ShowRegionOverlay();
            };
            data.OnToggleSubtitles = () =>
            {
                ShowText = !ShowText;
                SubtitleText.Visibility = ShowText ? Visibility.Visible : Visibility.Collapsed;
                HeaderText.Visibility = ShowText ? Visibility.Visible : Visibility.Collapsed;
                if (ShowText) SystemSounds.Hand.Play();
                else SystemSounds.Exclamation.Play();
                data.UpdateDashboardStatus();
            };
            data.IsOcrRunning = () => OCRTimer.IsEnabled;
            data.IsSubtitleVisible = () => ShowText;
            data.IsEngineReady = () => _engineReady;
            data.OnOpenSetupWizard = () => ShowSetupWizard();

            // First-run: show setup wizard if not completed yet
            if (!Config.Get("SetupCompleted", false))
            {
                ShowSetupWizard();
            }

            // Always open settings window on startup (non-modal so UI stays responsive)
            data.Show();

            // Schedule the auto-updater. 5-second internal delay happens inside
            // StartBackgroundUpdateCheck so this call is fire-and-forget.
            StartBackgroundUpdateCheck();

            // Schedule the paid-dictionary sync. Pulls newer .gisub-dist files
            // from the backend, decrypts via the distribution key, re-encrypts
            // machine-bound, drops them into %APPDATA%\Kaption\<Game>\.
            // Fire-and-forget — failures (offline, no paid tier, etc.) only
            // log; OCR keeps working with whatever's already cached locally.
            StartBackgroundDictionarySync();


            // Wire the Dashboard's "Retry" / "Fallback to CPU" buttons on the
            // engine-failure banner. Flipping UseGpuOcr here means the retry
            // loop doesn't re-fail on the same DirectML path that blew up
            // the first time.
            data.OnRetryEngineLoad = (forceCpu) =>
            {
                if (forceCpu)
                {
                    try { Config.Set("UseGpuOcr", false); }
                    catch (Exception ex) { Logger.Log.Warn($"Retry-as-CPU: failed to persist UseGpuOcr=false: {ex.Message}"); }
                }
                KickOffEngineInit();
            };

            // Load OCR engine on a background Task so MainWindow_Loaded
            // returns in milliseconds even though ONNX DirectML
            // InferenceSession construction can take 2–10 seconds on cold
            // boot. Status transitions (Loading → Ready / Failed) are
            // surfaced on data.Engine + data.EngineStatusChanged, which the
            // Dashboard and the overlay loading strip both subscribe to.
            KickOffEngineInit();

            OCRTimer.Interval = new TimeSpan(0, 0, 0, 0, 200);
            OCRTimer.Tick += GetOCR;    // Delegate: method to execute


            UITimer.Interval = new TimeSpan(0, 0, 0, 0, Config.Get<int>("UiRefreshInterval", 200));
            UITimer.Tick += UpdateText;    // Delegate: method to execute

            // Wire OCR-active-seconds reporting to the heartbeat so the
            // referral program can credit inviters for real usage. Safe to
            // call even when LicenseService is momentarily null (first
            // launch race) — we simply skip registration and the server
            // sees body-less heartbeats like before.
            try
            {
                App.LicenseService?.SetActiveSecondsReporter(
                    provider: SnapshotActiveSeconds,
                    onAcknowledged: _ => ResetActiveSeconds());
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Could not wire active-seconds reporter: {ex.Message}");
            }

            SetWindowPos(new WindowInteropHelper(this).Handle, -1, 0, 0, 0, 0, 1 | 2);
            this.Width = screenBounds.Width;
            this.Top = screenBounds.Bottom / Scale - this.Height;
            this.Left = screenBounds.Left / Scale;
            this.LocationChanged += MainWindow_LocationChanged;

            // Apply saved capture region position immediately so overlay doesn't start at bottom
            if (notify?.Region != null && notify.Region.Length >= 4 && notify.Region[1] != "0")
            {
                UpdateWindowPosition();
            }

            // Apply configurable subtitle background opacity (0-255, default 176 = #B0)
            int bgOpacity = Config.Get<int>("SubtitleBgOpacity", 176);
            bgOpacity = Math.Max(0, Math.Min(255, bgOpacity));
            SubtitleBackground.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)bgOpacity, 0, 0, 0));

            // P2: idle-hide timer — fade out subtitle after 1s of no new text
            _idleHideTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _idleHideTimer.Tick += (s, args) =>
            {
                _idleHideTimer.Stop();
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500));
                SubtitleBackground.BeginAnimation(OpacityProperty, fadeOut);
                _subtitleVisible = false;

                // Clear retained text so EI mode doesn't re-show stale content
                lastContent = null;
                lastHeader = null;

                // Hide answer elements on fade-out
                _translatedAnswers = null;
                AnswerSeparator.Visibility = Visibility.Collapsed;
                AnswerText.Visibility = Visibility.Collapsed;

                // Reset dialogue prediction context when conversation ends
                data?.ContextEngine?.Reset();
            };

            // Apply saved font family
            string savedFont = Config.Get("FontFamily", "Segoe UI");
            var fontFamily = new System.Windows.Media.FontFamily(savedFont);
            SubtitleText.FontFamily = fontFamily;
            HeaderText.FontFamily = fontFamily;
            AnswerText.FontFamily = fontFamily;

            // Show the main window immediately so the user sees the loading indicator.
            this.Opacity = 1;

            // Start hidden, then show a transient loading message while the OCR engine initialises.
            SubtitleBackground.Opacity = 0;

            // Show inline loading indicator on the subtitle overlay. Prior
            // implementation polled `_engineReady` on a 500 ms timer; now we
            // subscribe to data.EngineStatusChanged so the fade-out fires
            // the instant the background init completes (no up-to-500 ms
            // delay) and doesn't tick forever on a failed load.
            if (data.Engine == SettingsWindow.EngineStatus.Ready)
            {
                // Hot-restart / retry case — engine may already be live by
                // the time we hit this codepath. Keep the overlay idle.
                SubtitleText.Text = "";
                SubtitleBackground.Opacity = 0;
                _subtitleVisible = false;
            }
            else
            {
                SubtitleText.Text = LocalizedString(
                    "Overlay_EngineLoading",
                    "Loading OCR engine…");
                SubtitleBackground.Opacity = 0.7;
                _subtitleVisible = true;

                // Stable handler reference so we can unhook on Ready.
                EventHandler onEngineChanged = null;
                onEngineChanged = (ls, le) =>
                {
                    try
                    {
                        var state = data.Engine;
                        if (state == SettingsWindow.EngineStatus.Ready)
                        {
                            data.EngineStatusChanged -= onEngineChanged;
                            SubtitleText.Text = "";
                            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(
                                0.7, 0, TimeSpan.FromMilliseconds(300));
                            SubtitleBackground.BeginAnimation(OpacityProperty, fadeOut);
                            _subtitleVisible = false;
                        }
                        else if (state == SettingsWindow.EngineStatus.Failed)
                        {
                            // Swap the hint to a short failure line. Don't
                            // unsubscribe — a Retry click will flip us back
                            // to Loading and then Ready, at which point the
                            // Ready branch above finishes the job.
                            SubtitleText.Text = LocalizedString(
                                "Overlay_EngineFailed",
                                "Translator failed to load — see Settings.");
                        }
                        else // Loading
                        {
                            SubtitleText.Text = LocalizedString(
                                "Overlay_EngineLoading",
                                "Loading OCR engine…");
                            SubtitleBackground.Opacity = 0.7;
                            _subtitleVisible = true;
                        }
                    }
                    catch (Exception ex) { Logger.Log.Warn($"Overlay engine-status handler threw: {ex.Message}"); }
                };
                data.EngineStatusChanged += onEngineChanged;
            }
        }

        /// <summary>
        /// Kick off (or restart) the background OCR engine init. Fires on a
        /// <see cref="Task.Run"/> so MainWindow_Loaded returns immediately and
        /// the UI stays responsive while ONNX Runtime spins up DirectML
        /// sessions (2–10 s on cold boot). Status transitions are published
        /// via <see cref="SettingsWindow.SetEngineStatus"/> so the Dashboard
        /// pill + overlay loading strip can react without a polling timer.
        ///
        /// Used by the initial Loaded path AND by the Dashboard's Retry /
        /// "Fallback to CPU" buttons — safe to call repeatedly; the previous
        /// engine (if any) is disposed inside <see cref="SettingsWindow.LoadEngine()"/>
        /// before the new InferenceSessions are built. Errors are reported
        /// to GlitchTip via <see cref="CrashReportingService.ReportException"/>
        /// <summary>
        /// Recovery path for "session loaded but file-protection secret is
        /// missing" — a desync between LicenseService.CurrentActivation and
        /// the on-disk activation.dat that we can't safely reconcile in
        /// process. Show a clear message, sign the user out (clears the
        /// activation), and exit. Next launch lands on LoginWindow with a
        /// clean slate.
        ///
        /// Translations and game data on disk are preserved — only the
        /// activation.dat is cleared. After re-auth the foreground bootstrap
        /// fetches a fresh per-device secret and DictionarySync sees the
        /// existing files as up-to-date (sha matches).
        /// </summary>
        private void ForceReauthAndExit(string title, string body)
        {
            try
            {
                ModernDialog.Error(
                    owner: null,
                    title: title,
                    body: body,
                    technicalDetails: null);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"ForceReauthAndExit: dialog failed: {ex.Message}");
            }

            try
            {
                App.LicenseService?.SignOut();
                Logger.Log.Info("ForceReauthAndExit: SignOut complete, exiting with code 4 for re-auth.");
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"ForceReauthAndExit: SignOut failed: {ex.Message}");
            }

            try
            {
                System.Windows.Application.Current.Shutdown(4);
            }
            catch
            {
                Environment.Exit(4);
            }
        }

        private void KickOffEngineInit()
        {
            // Volatile flag flips back off here (not inside the catch) so
            // any OCR tick that fires during the retry window sees the
            // correct state — _engineReady=false while init is in flight.
            _engineReady = false;
            data.SetEngineStatus(SettingsWindow.EngineStatus.Loading);

            Task.Run(() =>
            {
                try
                {
                    data.LoadEngine();
                    _engineReady = true;
                    Logger.Log.Debug("OCR engine loaded and ready");
                    data.SetEngineStatus(SettingsWindow.EngineStatus.Ready);
                    // Dashboard refresh is redundant with the event handler
                    // (OnEngineStatusChanged calls UpdateDashboardStatus) but
                    // cheap, and belt-and-braces matters on a cold boot
                    // where the first refresh drives the first visible pill.
                    Dispatcher.BeginInvoke(new Action(() => data.UpdateDashboardStatus()));
                }
                catch (Exception ex)
                {
                    Logger.Log.Error("Failed to load OCR engine: " + ex.Message, ex);
                    // Surface to telemetry — we only see GPU-init failures
                    // on the user's machine, so server-side visibility
                    // matters. CrashReportingService honors the user's opt-in
                    // so this is a no-op if crash reporting is disabled.
                    try { GI_Subtitles.Services.Observability.CrashReportingService.ReportException(ex, "engine-init"); }
                    catch (Exception reportEx) { Logger.Log.Warn($"Could not report engine-init failure: {reportEx.Message}"); }
                    data.SetEngineStatus(SettingsWindow.EngineStatus.Failed, ex);
                    Dispatcher.BeginInvoke(new Action(() => data.UpdateDashboardStatus()));
                }
            });
        }

        private bool IsGameInForeground()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return true;
                // If our own app window is focused (e.g. Settings), keep overlay visible
                GetWindowThreadProcessId(fg, out uint fgPid);
                if (fgPid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
                    return true;
                var sb = new StringBuilder(256);
                GetWindowText(fg, sb, sb.Capacity);
                string title = sb.ToString().ToLowerInvariant();
                string gameLower = (Game ?? "").ToLowerInvariant();
                // Match common game window titles
                return title.Contains(gameLower) ||
                       title.Contains("genshin") ||
                       title.Contains("star rail") ||
                       title.Contains("zenless") ||
                       title.Contains("wuthering") ||
                       title.Contains("endfield") ||
                       title.Contains("崩坏") ||
                       title.Contains("原神") ||
                       title.Contains("绝区零") ||
                       string.IsNullOrEmpty(title); // fullscreen games sometimes report empty title
            }
            catch
            {
                return true; // default to running if check fails
            }
        }

        public void GetOCR(object sender, EventArgs e)
        {
            if (notify.isContextMenuOpen)
            {
                return;
            }
            // Runtime guard is in "drag me" mode — the subtitle box is
            // showing its last translation over the dialogue area and the
            // user is about to move it. Running OCR here would feed the
            // existing translated text straight back into the matcher, so
            // the guard pauses the loop entirely until the user resolves
            // the overlap. CheckRuntimeOverlapAndApply (called from
            // layout updates + Window_MouseDown drag-completion) is what
            // clears this flag.
            if (_inRuntimeOverlapDragMode)
            {
                return;
            }
            // Credit this tick toward the referral active-seconds accumulator
            // BEFORE the foreground check so the window anchor advances
            // regardless of whether we actually OCR'd. Gating on foreground
            // would keep the anchor stuck while the user alt-tabs, and the
            // NEXT in-foreground tick would then credit the whole absence.
            AccumulateActiveOcrTick();

            // Auto-pause when game is not in foreground — hide overlay
            if (!IsGameInForeground())
            {
                if (!_hiddenForFocusLoss && _subtitleVisible)
                {
                    _hiddenForFocusLoss = true;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SubtitleBackground.Visibility = Visibility.Collapsed;
                    }));
                }
                return;
            }
            // Game regained focus — restore subtitle if it was hidden for focus loss
            if (_hiddenForFocusLoss)
            {
                _hiddenForFocusLoss = false;
                if (_subtitleVisible)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SubtitleBackground.BeginAnimation(OpacityProperty, null);
                        SubtitleBackground.Opacity = 1;
                        SubtitleBackground.Visibility = Visibility.Visible;
                    }));
                }
            }
            if (Interlocked.Exchange(ref OCR_TIMER, 1) == 0)
            {
                if (!_engineReady) { Interlocked.Exchange(ref OCR_TIMER, 0); return; }
                // Belt-and-braces: Start-path gates should have prevented us
                // from entering the loop without a region, but if Region is
                // ever wiped at runtime (e.g. user re-runs Select Region and
                // cancels mid-flow), bail out cleanly and halt the timer rather
                // than throw IndexOutOfRangeException 5× per second until the
                // log rotates. The user still has Stop available and can fix
                // the region via Dashboard → Select Region.
                if (!HasConfiguredRegion())
                {
                    Interlocked.Exchange(ref OCR_TIMER, 0);
                    try
                    {
                        OCRTimer.Stop();
                        UITimer.Stop();
                        SwitchIcon("kaption.ico");
                        Logger.Log.Warn("OCR stopped: capture region was cleared while running.");
                        Dispatcher.BeginInvoke(new Action(() => data?.UpdateDashboardStatus()));
                    }
                    catch (Exception ex) { Logger.Log.Warn($"Auto-stop after region loss failed: {ex.Message}"); }
                    return;
                }
                // Scope the runtime into SustainedLowLatency for the duration of the tick so
                // GC avoids blocking Gen2 / LOH compactions while capture + preprocess + OCR
                // are running. Disposed via using => previous mode restored even if we throw.
                // See Core/Runtime/LatencyModeScope.cs for why restore is load-bearing.
                using (LatencyModeScope.SustainedLowLatency())
                {
                try
                {
                    Bitmap target;
                    if (notify.Region[1] == "0")
                    {
                        notify.ChooseRegion();
                    }

                    bool isRegion2Valid = notify.Region2 != null && notify.Region2.Length == 4 &&
                                         int.TryParse(notify.Region2[2], out int region2Width) && region2Width > 0 &&
                                         int.TryParse(notify.Region2[3], out int region2Height) && region2Height > 0;

                    if (failedCount > 4 && isRegion2Valid)
                    {
                        if (usingRegion2)
                        {
                            target = CaptureAndMask(notify.Region);
                        }
                        else
                        {
                            target = CaptureAndMask(notify.Region2);
                        }
                        Interlocked.Exchange(ref failedCount, 0);
                        usingRegion2 = !usingRegion2;
                    }
                    else
                    {
                        if (usingRegion2 && isRegion2Valid)
                        {
                            target = CaptureAndMask(notify.Region2);
                        }
                        else
                        {
                            target = CaptureAndMask(notify.Region);
                        }
                    }

                    // Capture answer region alongside main region (both on UI thread).
                    // Feature flag short-circuits before the Config read so the entire
                    // answer pipeline (region capture → OCR → translation → display)
                    // stays cold while the feature is temporarily off.
                    Bitmap answerTarget = null;
                    bool answerEnabled = FeatureAnswerTranslationEnabled
                        && Config.Get("EnableAnswerTranslation", false);
                    var answerRegion = notify.AnswerRegion;
                    bool isAnswerRegionValid = answerEnabled && answerRegion != null && answerRegion.Length == 4 &&
                        int.TryParse(answerRegion[2], out int ansRegW) && ansRegW > 0 &&
                        int.TryParse(answerRegion[3], out int ansRegH) && ansRegH > 0;
                    if (isAnswerRegionValid)
                    {
                        try { answerTarget = CaptureAndMask(answerRegion); }
                        catch (Exception ex) { Logger.Log.Error($"Answer capture failed: {ex.Message}"); }
                    }

                    bool passedToOcr = false;
                    Mat frameMat = null;
                    Mat currentBinary = null;
                    Mat diffFrame = null;
                    try
                    {
                        // DXGI access can be revoked transiently (Ctrl+Alt+Del, UAC prompt,
                        // display mode change, vendor driver update). When that happens the
                        // backend re-initializes on the next call, but the current frame may
                        // come back null. Fall through to the finally block (which disposes
                        // target/answerTarget/frameMat/etc.) instead of letting
                        // BitmapConverter.ToMat throw ArgumentNullException("src") on null.
                        if (target == null)
                        {
                            Logger.Log.Debug("Capture returned null bitmap (transient DXGI state) — skipping tick");
                        }
                        else
                        {
                        frameMat = target.ToMat();
                        currentBinary = PreprocessToBinary(frameMat);

                        if (currentBinary == null || currentBinary.Empty())
                        {
                            if (!_isOcrRunning)
                            {
                                if (IsOcrIntervalReady())
                                {
                                    SetWindowPos(new WindowInteropHelper(this).Handle, -1, 0, 0, 0, 0, 1 | 2);
                                    TriggerOcrAsync(frameMat.Clone(), target, answerTarget);
                                    passedToOcr = true;
                                }
                                else
                                {
                                    Logger.Log.Debug("Skip OCR (fallback) due to min interval limit");
                                }
                            }
                        }
                        else
                        {
                            // Check stability vs previous frame
                            bool isStableVsPrev = true;
                            if (_lastBinaryFrame != null)
                            {

                                if (currentBinary.Size() != _lastBinaryFrame.Size() ||
            currentBinary.Channels() != _lastBinaryFrame.Channels())
                                {
                                    isStableVsPrev = false;
                                    if (debug)
                                    {
                                        Logger.Log.Debug("Last binary frame size mismatch, reset cache");
                                    }
                                }
                                else
                                {
                                    // Absdiff allocates its output itself; rent a blank Mat shell.
                                    diffFrame = MatPool.Default.RentBlank();
                                    Cv2.Absdiff(currentBinary, _lastBinaryFrame, diffFrame);
                                    int nonZeroPrev = Cv2.CountNonZero(diffFrame);
                                    double changePrev = (double)nonZeroPrev / (diffFrame.Rows * diffFrame.Cols);
                                    if (debug)
                                    {
                                        Logger.Log.Debug($"Subtitle changeRatio(prev)={changePrev:F4}");
                                    }
                                    isStableVsPrev = changePrev <= ChangeThreshold;
                                }

                            }

                            // Track consecutive stable frames for eager preview
                            if (isStableVsPrev)
                                _consecutiveStableFrames++;
                            else
                                _consecutiveStableFrames = 0;

                            // Check change vs last OCR frame
                            bool changedVsOcr = false;
                            if (_lastOcrBinaryFrame != null)
                            {
                                if (currentBinary.Size() != _lastOcrBinaryFrame.Size() ||
            currentBinary.Channels() != _lastOcrBinaryFrame.Channels())
                                {
                                    changedVsOcr = true;
                                    if (debug)
                                    {
                                        Logger.Log.Debug("Last binary frame size mismatch, run ocr");
                                    }
                                }
                                else
                                {
                                    Mat diffToOcr = MatPool.Default.RentBlank();
                                    try
                                    {
                                        Cv2.Absdiff(currentBinary, _lastOcrBinaryFrame, diffToOcr);
                                        int nonZeroOcr = Cv2.CountNonZero(diffToOcr);
                                        double changeOcr = (double)nonZeroOcr / (diffToOcr.Rows * diffToOcr.Cols);
                                        if (debug)
                                        {
                                            Logger.Log.Debug($"Subtitle changeRatio(ocr)={changeOcr:F4}");
                                        }
                                        changedVsOcr = changeOcr > ChangeThreshold;
                                    }
                                    finally
                                    {
                                        MatPool.Default.Return(diffToOcr);
                                    }
                                }
                            }
                            else
                            {
                                // No OCR baseline yet, force initial OCR when frame is stable
                                changedVsOcr = true;
                            }

                            // Update previous-frame baseline for next cycle
                            if (_lastBinaryFrame != null)
                            {
                                _lastBinaryFrame.Dispose();
                            }
                            _lastBinaryFrame = currentBinary.Clone();

                            // Windowed stability check: compare current frame against a frame
                            // from StabilityWindowSize ticks ago (~500ms). During typewriter
                            // animation, characters accumulate over this window creating a
                            // detectable difference (>1%), while single-frame diffs are too
                            // small (~0.3% per character) to catch.
                            _stabilityBuffer.Enqueue(currentBinary.Clone());
                            bool isStableOverWindow = false;
                            if (_stabilityBuffer.Count > StabilityWindowSize)
                            {
                                Mat oldFrame = _stabilityBuffer.Dequeue();
                                if (currentBinary.Size() == oldFrame.Size() &&
                                    currentBinary.Channels() == oldFrame.Channels())
                                {
                                    using (Mat windowDiff = new Mat())
                                    {
                                        Cv2.Absdiff(currentBinary, oldFrame, windowDiff);
                                        int nonZero = Cv2.CountNonZero(windowDiff);
                                        double changeOverWindow = (double)nonZero / (windowDiff.Rows * windowDiff.Cols);
                                        isStableOverWindow = changeOverWindow <= ChangeThreshold;
                                        if (debug)
                                        {
                                            Logger.Log.Debug($"Stability window changeRatio={changeOverWindow:F4}, stable={isStableOverWindow}");
                                        }
                                    }
                                }
                                oldFrame.Dispose();
                            }

                            // Track how long the screen has diverged from the last OCR frame
                            if (changedVsOcr)
                            {
                                if (_changedVsOcrSince == DateTime.MinValue)
                                {
                                    _changedVsOcrSince = DateTime.UtcNow;

                                    // Predictive pre-display: paint chain-predicted translation
                                    // before OCR completes to cut perceived latency by 300-500ms.
                                    // OFF BY DEFAULT: when the prediction disagrees with OCR (graph
                                    // branches can be conditional on quest state, etc.), the user
                                    // sees a visible text swap. Enable via Config("PredictivePreDisplay").
                                    if (Config.Get("PredictivePreDisplay", false)
                                        && data.ContextEngine?.IsLoaded == true
                                        && data.ContextEngine.HasSingleChainPrediction)
                                    {
                                        var pred = data.ContextEngine.GetSingleChainPrediction();
                                        if (pred != null && !string.IsNullOrEmpty(pred.Value.Translation))
                                        {
                                            _predictedContent = pred.Value.Translation;
                                            Logger.Log.Debug($"Predictive pre-display: \"{pred.Value.Translation}\"");
                                        }
                                    }
                                }
                            }
                            else
                            {
                                _changedVsOcrSince = DateTime.MinValue;
                                _predictedContent = null;
                            }

                            // Decide whether to run OCR:
                            // 1) subtitle changed vs last OCR frame
                            // 2) text stable over window OR N consecutive stable frames (eager preview)
                            // 3) Force: screen changed for >1s but never stabilized
                            //
                            // Stable-frame threshold is configurable. Defaults:
                            //   - Chain prediction active: 2 (was 1 — single-frame trigger at 100ms
                            //     interval fires during typewriter pauses, causing partial-match flicker)
                            //   - No chain prediction:     3 (preserves "eager preview" feel)
                            // Hard floor of 2 frames prevents regressions even if a user sets these to 0/1.
                            int stableFramesChain = Math.Max(2, GI_Subtitles.Services.Detection.GameOcrTuning.StableFramesChain());
                            int stableFramesDefault = Math.Max(2, GI_Subtitles.Services.Detection.GameOcrTuning.StableFramesDefault());
                            int stableFramesNeeded = (data.ContextEngine?.IsLoaded == true
                                && data.ContextEngine.HasSingleChainPrediction)
                                ? stableFramesChain
                                : stableFramesDefault;
                            bool readyForOcr = changedVsOcr && (isStableOverWindow || _consecutiveStableFrames >= stableFramesNeeded);

                            bool forceOcr = !readyForOcr && changedVsOcr
                                && _changedVsOcrSince > DateTime.MinValue
                                && (DateTime.UtcNow - _changedVsOcrSince).TotalSeconds > ForceOcrAfterChangeSeconds;
                            if (forceOcr)
                            {
                                readyForOcr = true;
                            }

                            if (readyForOcr)
                            {
                                if (!_isOcrRunning && IsOcrIntervalReady())
                                {
                                    if (_lastOcrBinaryFrame != null)
                                    {
                                        _lastOcrBinaryFrame.Dispose();
                                    }
                                    _lastOcrBinaryFrame = currentBinary.Clone();
                                    _consecutiveStableFrames = 0;
                                    _lastOcrWasFullyStable = isStableOverWindow;
                                    _changedVsOcrSince = DateTime.MinValue;

                                    Logger.Log.Debug(forceOcr
                                        ? "Forced OCR re-check (screen changed >1.5s without stability)"
                                        : isStableOverWindow
                                            ? "Subtitle stable over window, start OCR"
                                            : "Subtitle eager preview (4+ stable frames), start OCR");
                                    SetWindowPos(new WindowInteropHelper(this).Handle, -1, 0, 0, 0, 0, 1 | 2);
                                    TriggerOcrAsync(frameMat.Clone(), target, answerTarget);
                                    passedToOcr = true;
                                }
                                else
                                {
                                    Logger.Log.Debug("Subtitle changed/stable but skip OCR due to running or min interval limit");
                                }
                            }
                            else
                            {
                                // Clear stale subtitle when screen has changed but OCR hasn't run yet
                                // This prevents the old translation from lingering during dialogue transitions
                                if (changedVsOcr && _changedVsOcrSince > DateTime.MinValue
                                    && (DateTime.UtcNow - _changedVsOcrSince).TotalSeconds > ClearStaleSubtitleSeconds
                                    && ocrText.Length > 1)
                                {
                                    ocrText = "";
                                    _translatedAnswers = null;
                                    _predictedContent = null;
                                }

                                if (debug)
                                {
                                    Logger.Log.Debug("Subtitle considered unstable or unchanged vs OCR, skip OCR");
                                }
                            }
                        }
                        } // end else (target != null)
                    }
                    finally
                    {
                        bool answerPassedToAsync = false;

                        // Independent answer region polling: when dialogue is stable
                        // (not triggering full OCR) but answer region may have changed
                        if (!passedToOcr && answerTarget != null && ocrText.Length > 1 && !IsLikelyGameUI(ocrText)
                            && !_isAnswerOcrRunning && !_isOcrRunning
                            && (DateTime.UtcNow - _lastAnswerOcrTime).TotalMilliseconds > 400)
                        {
                            // Quick change detection: pixel sum comparison
                            try
                            {
                                using (var answerMat = answerTarget.ToMat())
                                using (var gray = new Mat())
                                {
                                    Cv2.CvtColor(answerMat, gray, OpenCvSharp.ColorConversionCodes.BGR2GRAY);
                                    long pixelSum = (long)gray.Sum().Val0;
                                    if (Math.Abs(pixelSum - _lastAnswerPixelSum) > pixelSum / 20) // >5% change
                                    {
                                        _lastAnswerPixelSum = pixelSum;
                                        RunAnswerOnlyOcrAsync(answerTarget);
                                        answerPassedToAsync = true;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log.Error($"Answer change detection failed: {ex.Message}");
                            }
                        }

                        if (!passedToOcr)
                        {
                            ReleaseCapturedBitmap(target);
                            // answerTarget is also pool-rented now — Return instead of Dispose
                            // so both buckets stay populated across ticks.
                            if (!answerPassedToAsync) ReleaseCapturedBitmap(answerTarget);
                        }

                        frameMat?.Dispose();
                        currentBinary?.Dispose();
                        if (diffFrame != null)
                        {
                            MatPool.Default.Return(diffFrame);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.Error(ex);
                }
                } // end using LatencyModeScope — restore default GC latency before releasing tick gate
                Interlocked.Exchange(ref OCR_TIMER, 0);
            }
        }

        public void UpdateWindowPosition()
        {
            // Guard against fresh installs where notify.Region is empty/null
            // because the user hasn't picked a capture region yet. Returning
            // early is safe — Main window just keeps its default placement
            // until a region is configured. Without this guard, clicking
            // "Start translating" in the setup wizard before selecting a
            // region crashed with IndexOutOfRangeException (session 21).
            // Mirrors the same check already present in UpdateWindowHeightAndTop.
            if (notify?.Region == null || notify.Region.Length < 4) return;

            // Base vertical position near the OCR region; precise Top/Height
            // will be adjusted later. Default matches the layout-engine
            // PadVertical default below (-140) so a fresh install lands the
            // overlay in the same spot whichever path renders it first. The
            // old `Config.GetPad()` (no arg, default 0) caused the overlay
            // to start at the raw OCR-region top until the user nudged the
            // slider, which then wrote the -140 and re-rendered correctly.
            double baseTop = Convert.ToInt16(notify.Region[1]) / Scale + Config.GetPad(-140);

            foreach (var screen in Screen.AllScreens)
            {
                if (screen.WorkingArea.Contains(
                        new System.Drawing.Point(
                            Convert.ToInt16(notify.Region[0]),
                            Convert.ToInt16(notify.Region[1]))))
                {
                    double scale = GetScaleForScreen(screen);
                    double left = screen.Bounds.Left / scale;

                    // Width based on OCR region width with extra padding
                    double width = Convert.ToInt16(notify.Region[2]) / scale + 200;

                    this.Left = left + (screen.Bounds.Width / scale - width) / 2 + Config.GetPadHorizontal();
                    this.Width = width;
                    this.Top = baseTop;
                }
            }
            // Height is now content-driven; do not hard-code here

            // Runtime overlap guard: if Config.Pad puts the overlay inside
            // the capture region, hide it and show the red alert. Mirrors
            // the call at the end of UpdateWindowHeightAndTop.
            CheckRuntimeOverlapAndApply();
        }

        /// <summary>
        /// True when the user has a 4-component capture region configured
        /// (picked via the setup wizard or Dashboard -> Select region).
        /// Used to gate flows that would otherwise crash on empty Region.
        /// </summary>
        private bool HasConfiguredRegion()
        {
            return notify?.Region != null && notify.Region.Length >= 4
                   && notify.Region[2] != null && notify.Region[3] != null;
        }

        /// <summary>
        /// Adjust window Height and Top based on actual subtitle content size.
        /// Uses the layout engine for safe zone protection (MaxHeight, auto-shrink, screen clamping).
        /// </summary>
        internal void UpdateWindowHeightAndTop()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (notify?.Region == null || notify.Region.Length < 4) return;

                    Screen targetScreen = null;
                    foreach (var screen in Screen.AllScreens)
                    {
                        if (screen.WorkingArea.Contains(
                                new System.Drawing.Point(
                                    Convert.ToInt16(notify.Region[0]),
                                    Convert.ToInt16(notify.Region[1]))))
                        {
                            targetScreen = screen;
                            break;
                        }
                    }
                    if (targetScreen == null)
                        targetScreen = Screen.PrimaryScreen;

                    double screenScale = GetScaleForScreen(targetScreen);
                    int fontSize = Config.Get<int>("Size", 22);

                    // Get detected text blocks for Embedded Illusion mode
                    var detectedBlocks = _lastDetectedText?.DialogueBlocks;

                    var layoutParams = new SubtitleLayoutParams
                    {
                        Text = SubtitleText.Text,
                        Header = HeaderText.Visibility == Visibility.Visible ? HeaderText.Text : null,
                        FontSize = fontSize,
                        CaptureRegion = new int[]
                        {
                            Convert.ToInt16(notify.Region[0]),
                            Convert.ToInt16(notify.Region[1]),
                            Convert.ToInt16(notify.Region[2]),
                            Convert.ToInt16(notify.Region[3])
                        },
                        Scale = Scale,
                        PadVertical = Config.GetPad(-140),
                        PadHorizontal = Config.GetPadHorizontal(0),
                        ScreenBounds = new System.Windows.Rect(
                            targetScreen.Bounds.Left / screenScale,
                            targetScreen.Bounds.Top / screenScale,
                            targetScreen.Bounds.Width / screenScale,
                            targetScreen.Bounds.Height / screenScale),
                        MaxHeight = Config.Get<int>("MaxOverlayHeight", 0),
                        MaxWidth = Config.Get<int>("MaxOverlayWidth", 900),
                        AutoShrinkText = Config.Get<bool>("AutoShrinkText", true),
                        MinFontSize = 12,
                        StrokeThickness = SubtitleText.StrokeThickness,
                        PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip,
                        DetectedBlocks = detectedBlocks
                    };

                    var layout = _layoutEngine.CalculateLayout(layoutParams);

                    // Apply font size if auto-shrink changed it
                    if (Math.Abs(layout.EffectiveFontSize - SubtitleText.FontSize) > 0.5)
                    {
                        SubtitleText.FontSize = layout.EffectiveFontSize;
                        // Scale header proportionally
                        HeaderText.FontSize = Math.Max(layout.EffectiveFontSize - 2, 10);
                    }

                    // Apply max text width constraint
                    if (layout.EffectiveMaxTextWidth > 0)
                    {
                        SubtitleText.MaxTextWidth = layout.EffectiveMaxTextWidth;
                        HeaderText.MaxTextWidth = layout.EffectiveMaxTextWidth;
                        AnswerText.MaxTextWidth = layout.EffectiveMaxTextWidth;
                    }

                    {
                        // Content-driven height with screen clamping
                        SubtitleBackground.Measure(new System.Windows.Size(this.ActualWidth, double.PositiveInfinity));
                        double contentHeight = SubtitleBackground.DesiredSize.Height;
                        if (contentHeight <= 0)
                            contentHeight = fontSize + 30;

                        double desiredHeight = contentHeight + 10;

                        // Expand max height for visible answer section
                        double effectiveMaxHeight = layout.Height;
                        if (AnswerText.Visibility == Visibility.Visible && effectiveMaxHeight > 0)
                        {
                            AnswerText.Measure(new System.Windows.Size(this.ActualWidth, double.PositiveInfinity));
                            effectiveMaxHeight += AnswerText.DesiredSize.Height + AnswerSeparator.Height + 20;
                        }

                        // Apply MaxHeight clamp from layout engine
                        if (effectiveMaxHeight > 0 && desiredHeight > effectiveMaxHeight)
                            desiredHeight = effectiveMaxHeight;

                        double screenTop = targetScreen.Bounds.Top / screenScale;
                        double screenBottom = targetScreen.Bounds.Bottom / screenScale;
                        double newTop = this.Top;

                        if (newTop < screenTop)
                            newTop = screenTop;
                        if (newTop + desiredHeight > screenBottom)
                            newTop = screenBottom - desiredHeight;

                        this.Top = newTop;
                        this.Height = desiredHeight;
                    }

                    // Runtime overlap guard: the layout engine has just
                    // placed the overlay — if it ended up inside a capture
                    // region (because the user moved the region, grew the
                    // overlay, or the region sits so close to the overlay's
                    // clamped position that there's no room), hide the
                    // subtitle and show a red alert until the user fixes
                    // it. Runs on every layout update so it self-recovers
                    // the moment the region moves clear.
                    CheckRuntimeOverlapAndApply();
                }
                catch (Exception ex)
                {
                    Logger.Log.Error($"Error updating window height/top: {ex}");
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        public void UpdateText(object sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref UI_TIMER, 1) == 0)
            {
                try
                {
                    string res = "";
                    string key = "";
                    string header = "";
                    string content = "";

                    if (ocrText.Length > 1 && !IsLikelyGameUI(ocrText))
                    {
                        if (data.Matcher == null)
                        {
                            Logger.Log.Warn("Matcher not loaded yet, skipping translation");
                        }
                        else if (resDict.TryGetValue(ocrText, out string cachedRes))
                        {
                            res = cachedRes;
                            key = resDict[res];
                            string[] parts = res.Split(new[] { "\n\n" }, StringSplitOptions.None);
                            if (parts.Length >= 2)
                            {
                                header = parts[0];
                                content = parts[1];
                            }
                            else
                            {
                                content = res;
                            }
                        }
                        else
                        {
                            // --- Pre-load: when NPC name is detected, pre-load ALL their dialogue ---
                            // This happens BEFORE text matching so the hot cache is ready.
                            // Wrapped defensively: a partial/updated translation dict (e.g. EN
                            // TextMap updated for a new game version before PL catches up) could
                            // expose edge cases in graph traversal. An exception here must not
                            // abort the translation for the current line.
                            if (!string.IsNullOrEmpty(_detectedNpcName) && data.ContextEngine?.IsLoaded == true)
                            {
                                try
                                {
                                    data.ContextEngine.PreloadForNpc(_detectedNpcName, data.contentDict);
                                }
                                catch (Exception preEx)
                                {
                                    Logger.Log.Error($"PreloadForNpc threw for \"{_detectedNpcName}\": {preEx}");
                                }
                            }

                            // --- Stage 0: Hot cache prediction check ---
                            // If DialogueContextEngine predicted this line, match is near-instant
                            bool hotCacheHit = false;
                            bool isPartialMatch = false;
                            if (data.ContextEngine?.IsLoaded == true)
                            {
                                try
                                {
                                    string normalized = Services.Translation.OptimizedMatcher.NormalizeInput(ocrText, data.Matcher?.isEng ?? true);
                                    string hotResult = data.ContextEngine.TryHotCacheMatch(normalized, out string hotKey, out isPartialMatch);
                                    if (hotResult != null)
                                    {
                                        header = "";
                                        content = hotResult;
                                        key = hotKey;
                                        hotCacheHit = true;
                                        Logger.Log.Debug($"HOT CACHE {(isPartialMatch ? "PREFIX" : "HIT")} for \"{ocrText}\": \"{content}\"");
                                    }
                                }
                                catch (Exception hotEx)
                                {
                                    Logger.Log.Error($"TryHotCacheMatch threw for \"{ocrText}\": {hotEx}");
                                    hotCacheHit = false;
                                }
                            }

                            if (!hotCacheHit)
                            {
                                // Null-safe matcher calls: a game-version update that adds new EN
                                // strings before the PL translation catches up means the matcher
                                // may return null/empty for those lines. Treat null as "no match"
                                // and fall through to the fallback path instead of letting a
                                // later string.IsNullOrEmpty call mishandle it.
                                if (!string.IsNullOrEmpty(_detectedNpcName))
                                {
                                    header = "";
                                    try
                                    {
                                        content = data.Matcher.FindClosestMatch(ocrText, out key) ?? "";
                                    }
                                    catch (Exception matchEx)
                                    {
                                        Logger.Log.Error($"FindClosestMatch threw for \"{ocrText}\": {matchEx}");
                                        content = "";
                                        key = "";
                                    }
                                    Logger.Log.Debug($"Color-detected NPC=\"{_detectedNpcName}\" (discarded), body match for \"{ocrText}\": content=\"{content}\"");
                                }
                                else
                                {
                                    // No color detection — use text-based header separation
                                    // but discard the header (NPC name/role) since the game
                                    // already displays it natively
                                    try
                                    {
                                        var matchResult = data.Matcher.FindMatchWithHeaderSeparated(ocrText, out key);
                                        header = "";
                                        content = matchResult.Content ?? "";
                                    }
                                    catch (Exception matchEx)
                                    {
                                        Logger.Log.Error($"FindMatchWithHeaderSeparated threw for \"{ocrText}\": {matchEx}");
                                        content = "";
                                        key = "";
                                    }
                                }

                            }

                            // Guard against null key flowing into downstream string ops.
                            if (key == null) key = "";

                            res = string.IsNullOrEmpty(header) ? content : (header + "\n\n" + content);

                            // Update dialogue context for next-line prediction.
                            // Skip for partial matches (typewriter) — chain should only
                            // advance when the full line is captured.
                            //
                            // Note: We intentionally do NOT populate _translatedAnswers from
                            // graph predictions here. The dialogue graph's nextDialogIds can
                            // include branches that are runtime-conditional (quest state, NPC
                            // state, time of day), so painting them directly often showed
                            // choices that weren't actually on screen — then OCR would
                            // overwrite with the real choices, producing visible flicker and
                            // a window-resize jump. Answer UI is now driven exclusively by
                            // answer-region OCR (see TriggerOcrAsync + RunAnswerOnlyOcrAsync).
                            // The graph predictions are still valuable — AnswerTranslationService
                            // uses them as the highest-priority match source for OCR output
                            // (see AnswerTranslationService.cs:54-63), so graph knowledge is
                            // preserved but no longer drives the UI directly.
                            //
                            // Exception-safe context update. If the process dies between this
                            // point and the next "Convert ocrResult" log line below, we know
                            // the crash is inside OnTextMatched or downstream. No dedicated
                            // exit-heartbeat — the existing "Convert ocrResult" debug log
                            // serves as the implicit success marker (avoids doubling log volume).
                            if (!string.IsNullOrEmpty(key) && !isPartialMatch &&
                                data.ContextEngine?.IsLoaded == true)
                            {
                                try
                                {
                                    data.ContextEngine.OnTextMatched(key, _detectedNpcName, data.contentDict);
                                }
                                catch (Exception ctxEx)
                                {
                                    // ContextEngine corruption should NOT take down the app —
                                    // log and continue. Worst case, chain prediction stops
                                    // working for this line; next match re-syncs.
                                    Logger.Log.Error($"OnTextMatched threw for key=\"{key}\" npc=\"{_detectedNpcName}\" (chain prediction may be degraded): {ctxEx}");
                                }
                            }

                            Logger.Log.Debug($"Convert ocrResult for {ocrText}: header={header}, content={content}, key={key}");

                            // Cache still uses the concatenated result for compatibility
                            if (!resDict.ContainsKey(ocrText))
                            {
                                resDict[ocrText] = res;
                                resDict[res] = key;
                            }
                        }
                    }

                    // If no translation found, check predictive pre-display
                    bool displayingPrediction = false;
                    if (string.IsNullOrEmpty(content) && ocrText.Length <= 1)
                    {
                        // No OCR text yet — use chain prediction if available
                        string predicted = _predictedContent;
                        if (!string.IsNullOrEmpty(predicted))
                        {
                            content = predicted;
                            header = "";
                            displayingPrediction = true;
                        }
                    }
                    else if (string.IsNullOrEmpty(content) && ocrText.Length > 1)
                    {
                        content = "";
                        header = "";
                    }

                    // Clear prediction once OCR confirms (content is set from OCR match)
                    if (!string.IsNullOrEmpty(content) && _predictedContent != null && ocrText.Length > 1)
                        _predictedContent = null;

                    _isFallbackText = false;

                    // Replace "Traveler" with player's custom name in translations
                    string playerName = Config.Get<string>("PlayerName", "");
                    if (!string.IsNullOrEmpty(playerName) && !string.IsNullOrEmpty(content))
                    {
                        content = content.Replace("Traveler", playerName).Replace("traveler", playerName);
                    }

                    // When content becomes empty but subtitle is visible, keep showing
                    // the current text to prevent flicker from brief empty OCR frames.
                    // But restart the idle timer so the subtitle hides after the delay.
                    if (string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(lastContent))
                    {
                        content = lastContent;
                        header = lastHeader;
                        // Nudge idle timer: if not already running, start countdown
                        if (_subtitleVisible && !_idleHideTimer.IsEnabled)
                        {
                            _lastContentChangeTime = DateTime.UtcNow;
                            _idleHideTimer.Start();
                        }
                    }
                    else if (!string.IsNullOrEmpty(content))
                    {
                        // Real content arrived — stop idle timer, reset timestamp
                        _idleHideTimer.Stop();
                        _lastContentChangeTime = DateTime.UtcNow;
                    }

                    // Check whether the content has changed (mainly check content, which is the main text)
                    bool contentChanged = content != lastContent;
                    bool headerChanged = header != lastHeader;

                    // Consolidate layout updates: each call queues a Dispatcher.BeginInvoke
                    // which runs Measure/layout and sets Height/Top. Calling it 2-4 times per
                    // UpdateText tick caused visible window "jumping". Set this flag at any
                    // point that would require a re-layout and fire exactly one call at the end.
                    bool needsLayoutUpdate = false;

                    if (contentChanged || headerChanged)
                    {
                        // Set header and content separately
                        if (headerChanged)
                        {
                            lastHeader = header;
                            if (!string.IsNullOrEmpty(header))
                            {
                                HeaderText.Text = header;
                                HeaderText.Visibility = Visibility.Visible;
                            }
                            else
                            {
                                HeaderText.Visibility = Visibility.Collapsed;
                            }
                        }

                        if (contentChanged)
                        {
                            lastContent = content;

                            // Check if this is the same content that was just showing
                            // (e.g., brief empty OCR frame caused a clear, now same text is back).
                            // Skip the fade-in animation to prevent visible blink.
                            bool isRecentRestore = !string.IsNullOrEmpty(content) &&
                                content == _recentContent &&
                                (DateTime.UtcNow - _recentContentTime).TotalSeconds < 3;

                            // If the previous frame painted a graph-based prediction and the current
                            // frame is the real OCR-confirmed translation, swap WITHOUT fade-in.
                            // This kills the "double fade-in" flicker that was visible when prediction
                            // differed from actual OCR. See Fix #2 in the flicker remediation pass.
                            bool isPredictionRefinement = _lastDisplayedWasPredicted && ocrText.Length > 1
                                && !displayingPrediction;

                            if (!isRecentRestore && !isPredictionRefinement)
                            {
                                // Animate text change: quick fade in
                                SubtitleText.Opacity = 0;
                                SubtitleText.Text = content;
                                var textFadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
                                SubtitleText.BeginAnimation(OpacityProperty, textFadeIn);
                            }
                            else
                            {
                                // Restore/refinement without animation
                                SubtitleText.Text = content;
                                SubtitleText.BeginAnimation(OpacityProperty, null);
                                SubtitleText.Opacity = 1;
                            }
                            // First real translation replaces the ready-
                            // placeholder. Not strictly required — we already
                            // assigned `content` to SubtitleText.Text above —
                            // but clears the internal placeholder flag so the
                            // next OCR stop doesn't re-blank text the user
                            // is actively reading.
                            if (_placeholderActive)
                            {
                                _placeholderActive = false;
                                _readyPlaceholderText = null;
                            }

                            // Track whether what we're painting is a prediction — next update
                            // consults this to decide whether to skip the fade-in handoff.
                            _lastDisplayedWasPredicted = displayingPrediction;

                            // Track recent content
                            if (!string.IsNullOrEmpty(content))
                            {
                                _recentContent = content;
                                _recentContentTime = DateTime.UtcNow;
                            }
                            SubtitleText.FontSize = _cachedFontSize;

                            // Apply visual style: italic + semi-transparent for fallback, normal for translation
                            if (_isFallbackText)
                            {
                                SubtitleText.FontStyle = FontStyles.Italic;
                                SubtitleText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 255, 255, 255));
                            }
                            else
                            {
                                SubtitleText.FontStyle = FontStyles.Normal;
                                SubtitleText.Foreground = new SolidColorBrush(Colors.White);

                                // Log matched translation to dialogue log (skip for predicted display —
                                // wait until OCR confirms the actual match to avoid logging guesses).
                                if (!displayingPrediction)
                                    Services.DialogueLog.Log(header, content, ocrText, null);
                            }
                        }

                        // Mark for layout update — actual call issued once at the end of UpdateText.
                        bool hasContent2 = !string.IsNullOrEmpty(content) || !string.IsNullOrEmpty(header);
                        if (hasContent2) needsLayoutUpdate = true;
                    }

                    // --- Answer Translation Display (runs every tick, independent of content changes) ---
                    // Belt-and-suspenders: only render answers if the feature flag is on,
                    // the user has explicitly enabled it in config, AND an answer region is
                    // configured. The feature flag (first clause) short-circuits both Config
                    // reads and region parsing while the feature is temporarily disabled.
                    bool answerDisplayAllowed = FeatureAnswerTranslationEnabled
                        && Config.Get("EnableAnswerTranslation", false)
                        && notify?.AnswerRegion != null && notify.AnswerRegion.Length == 4
                        && int.TryParse(notify.AnswerRegion[2], out int _ansW) && _ansW > 0
                        && int.TryParse(notify.AnswerRegion[3], out int _ansH) && _ansH > 0;
                    var answers = answerDisplayAllowed ? _translatedAnswers : null; // snapshot volatile ref
                    if (answers != null && answers.Length > 0 && !string.IsNullOrEmpty(lastContent))
                    {
                        // Set visibility BEFORE text + measure so the consolidated
                        // UpdateWindowHeightAndTop below includes the answer section in the
                        // window height calculation.
                        bool wasHidden = AnswerSeparator.Visibility != Visibility.Visible;
                        if (wasHidden)
                        {
                            AnswerSeparator.Visibility = Visibility.Visible;
                            AnswerText.Visibility = Visibility.Visible;
                        }

                        string answerDisplay = string.Join("\n", answers.Select(a => $"\u25B8 {a}"));
                        if (!string.IsNullOrEmpty(playerName))
                            answerDisplay = answerDisplay.Replace("Traveler", playerName).Replace("traveler", playerName);
                        if (AnswerText.Text != answerDisplay || wasHidden)
                        {
                            AnswerText.Text = answerDisplay;
                            AnswerText.FontSize = Math.Max(12, _cachedFontSize - 3);
                            needsLayoutUpdate = true;
                        }
                    }
                    else if (AnswerSeparator.Visibility == Visibility.Visible)
                    {
                        AnswerSeparator.Visibility = Visibility.Collapsed;
                        AnswerText.Visibility = Visibility.Collapsed;
                        needsLayoutUpdate = true;
                    }

                    // Single consolidated layout update for this UpdateText tick.
                    // Replaces 2-4 separate UpdateWindowHeightAndTop() calls that used to fire
                    // on content change, header change, and answer show/hide — each of which
                    // queued its own Dispatcher.BeginInvoke and produced visible window jumps.
                    if (needsLayoutUpdate) UpdateWindowHeightAndTop();

                    // Always check show/hide — even when content hasn't changed, hide if empty
                    bool hasContent = !string.IsNullOrEmpty(lastContent) || !string.IsNullOrEmpty(lastHeader);
                    if (hasContent)
                    {
                        if (!_subtitleVisible)
                        {
                            _idleHideTimer.Stop();
                            _lastContentChangeTime = DateTime.UtcNow;
                            SubtitleBackground.Visibility = Visibility.Visible;
                            SubtitleBackground.BeginAnimation(OpacityProperty, null);
                            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
                            SubtitleBackground.BeginAnimation(OpacityProperty, fadeIn);
                            _subtitleVisible = true;
                        }
                    }
                    else
                    {
                        // No content — start fade-out timer if not already running
                        if (_subtitleVisible && !_idleHideTimer.IsEnabled)
                        {
                            _idleHideTimer.Start();
                        }
                    }
                    // Forced OCR (in GetOCR) ensures re-check even when screen won't stabilize.
                }
                catch (Exception ex)
                {
                    Logger.Log.Error(ex);
                }
                Interlocked.Exchange(ref UI_TIMER, 0);
            }
        }

        /// <summary>
        /// Update the header position by dynamically calculating the upward offset based on the actual height of the content (supports multiple lines)
        /// </summary>
        private void UpdateHeaderPosition()
        {
            // Header is now inside a StackPanel above SubtitleText,
            // so layout is automatic. Just trigger a height/position refresh.
            UpdateWindowHeightAndTop();
        }

        /// <summary>
        /// Filters out game UI text that OCR picks up but isn't dialogue.
        /// Examples: "Lv.50", "5015/7380", "1:23", score numbers, menu labels.
        /// Real dialogue has mostly alphabetical characters and is longer.
        /// </summary>
        /// <remarks>
        /// The previous implementation did a naive <c>Contains()</c> against a flat list of
        /// keywords that included common English words like "wish", "sword", "bow", "shop",
        /// "mail", "character", "def", "atk". This caused valid dialogue such as
        /// <c>"...I Wish I Could Get Back..."</c> to be silently classified as game UI —
        /// the translation would be dropped, the idle-hide timer would arm, and the
        /// subtitle would fade out mid-conversation until the player advanced the dialogue.
        ///
        /// The rewrite uses two lists with different match strategies:
        ///   • <see cref="GameUIPhrases"/>   — unambiguous multi-word expressions that don't
        ///     appear in normal speech (e.g. "base atk", "crit rate"). Safe to substring-match.
        ///   • <see cref="GameUIWords"/>     — single words rare enough to not appear
        ///     mid-sentence (e.g. "attributes", "polearm"). Word-boundary match, and only
        ///     evaluated on short text so a long dialogue that happens to contain the word
        ///     isn't flagged.
        /// Overly-generic single words that previously caused false positives have been
        /// removed from both lists entirely.
        /// </remarks>
        // Multi-word UI phrases — unambiguous menu/stat labels; substring-matched at any length.
        private static readonly string[] GameUIPhrases = new[]
        {
            "base atk", "crit rate", "crit dmg", "energy recharge", "elemental mastery",
            "adventurer handbook", "battle pass", "party setup", "co-op",
        };

        // Single-word UI keywords — matched only as whole tokens AND only when the overall
        // text is short (see length gate below). Keep this list small and unambiguous.
        private static readonly HashSet<string> GameUIWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "attributes", "constellation", "polearm", "claymore", "refinement",
        };

        // Max length at which short-word UI keywords are allowed to flag text.
        // Real dialogue tends to exceed this; UI labels almost never do.
        private const int GameUIWordGateLength = 40;

        private static bool IsLikelyGameUI(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            string trimmed = text.Trim();
            if (trimmed.Length < 4) return true;

            // Count letter vs non-letter characters
            int letters = 0;
            int digits = 0;
            foreach (char c in trimmed)
            {
                if (char.IsLetter(c)) letters++;
                else if (char.IsDigit(c)) digits++;
            }

            // If more digits than letters, it's likely UI (HP, level, timer, score)
            if (digits > 0 && digits >= letters) return true;

            // Very short text with digits is likely UI ("Lv.50", "HP 100")
            if (trimmed.Length < 10 && digits > 0) return true;

            string lower = trimmed.ToLowerInvariant();

            // Multi-word phrase check — safe at any length because these phrases
            // don't occur in conversational English.
            foreach (var phrase in GameUIPhrases)
            {
                if (lower.Contains(phrase)) return true;
            }

            // Single-word check — only fires on short text. A long sentence containing
            // a keyword as a normal English word (e.g. "I wish I could...") won't trip
            // this branch.
            if (trimmed.Length <= GameUIWordGateLength)
            {
                if (ContainsGameUIWord(lower)) return true;
            }

            return false;
        }

        /// <summary>
        /// Tokenize <paramref name="lowerText"/> on non-letter boundaries and return true
        /// if any whole token matches an entry in <see cref="GameUIWords"/>. Whole-token
        /// matching avoids false positives like "shoplifter" matching "shop".
        /// </summary>
        private static bool ContainsGameUIWord(string lowerText)
        {
            int i = 0;
            int n = lowerText.Length;
            while (i < n)
            {
                // Skip non-letters
                while (i < n && !char.IsLetter(lowerText[i])) i++;
                int start = i;
                while (i < n && char.IsLetter(lowerText[i])) i++;
                if (i > start)
                {
                    string token = lowerText.Substring(start, i - start);
                    if (GameUIWords.Contains(token)) return true;
                }
            }
            return false;
        }


        /// <summary>
        /// Masks overlay window areas from a captured bitmap to prevent OCR from reading its own output.
        /// Fills overlapping areas with black pixels which are below the OCR threshold.
        /// </summary>
        private void MaskOverlayAreas(Bitmap bitmap, int captureX, int captureY, int captureW, int captureH)
        {
            try
            {
                // Collect all overlay windows that could interfere
                var overlayRects = new List<System.Drawing.Rectangle>();

                // Main subtitle overlay (skipped automatically in EI mode because Opacity
                // is set to 0 before capture, making the Opacity > 0 check false)
                if (this.Visibility == Visibility.Visible && this.Opacity > 0 && _subtitleVisible)
                {
                    int olX = (int)(this.Left * Scale);
                    int olY = (int)(this.Top * Scale);
                    int olW = (int)(this.Width * Scale);
                    int olH = (int)(this.Height * Scale);
                    overlayRects.Add(new System.Drawing.Rectangle(olX, olY, olW, olH));
                }

                // Quick-translate popup
                if (_quickTranslatePopup != null && _quickTranslatePopup.IsVisible)
                {
                    int qX = (int)(_quickTranslatePopup.Left * Scale);
                    int qY = (int)(_quickTranslatePopup.Top * Scale);
                    int qW = (int)(_quickTranslatePopup.ActualWidth * Scale);
                    int qH = (int)(_quickTranslatePopup.ActualHeight * Scale);
                    overlayRects.Add(new System.Drawing.Rectangle(qX, qY, qW, qH));
                }

                var captureRect = new System.Drawing.Rectangle(captureX, captureY, captureW, captureH);

                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    foreach (var overlayRect in overlayRects)
                    {
                        // Calculate intersection
                        var intersection = System.Drawing.Rectangle.Intersect(overlayRect, captureRect);
                        if (!intersection.IsEmpty)
                        {
                            // Convert to bitmap-relative coordinates
                            int relX = intersection.X - captureX;
                            int relY = intersection.Y - captureY;
                            g.FillRectangle(System.Drawing.Brushes.Black, relX, relY, intersection.Width, intersection.Height);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"MaskOverlayAreas error: {ex}");
            }
        }

        /// <summary>
        /// Captures a screen region and masks any overlay windows within it to prevent OCR feedback loops.
        /// </summary>
        private Bitmap CaptureAndMask(string[] region)
        {
            if (region == null || region.Length < 4) return CaptureRegionFromBackend(region);

            if (int.TryParse(region[0], out int x) && int.TryParse(region[1], out int y) &&
                int.TryParse(region[2], out int w) && int.TryParse(region[3], out int h))
            {
                Bitmap bmp = CaptureRegionFromBackend(region);
                // Only skip MaskOverlayAreas if WDA_EXCLUDEFROMCAPTURE is active
                // (overlay invisible in captures). Otherwise always mask.
                if (bmp != null && !_wdaExcludeActive)
                    MaskOverlayAreas(bmp, x, y, w, h);
                return bmp;
            }
            return CaptureRegionFromBackend(region);
        }

        /// <summary>
        /// Capture a screen region using the active backend (DXGI or GDI).
        /// DXGI is preferred (GPU-accelerated, respects WDA_EXCLUDEFROMCAPTURE).
        /// Rents a <see cref="Bitmap"/> from <see cref="BitmapPool.Default"/>; the caller
        /// MUST return it via <see cref="ReleaseCapturedBitmap"/> (not <see cref="IDisposable.Dispose"/>)
        /// so the pool can hand it to the next tick.
        /// </summary>
        private Bitmap CaptureRegionFromBackend(string[] region)
        {
            if (region == null || region.Length < 4)
            {
                Logger.Log.Error($"Invalid region array: length={region?.Length ?? 0}");
                throw new ArgumentException("Region array must have at least 4 elements", nameof(region));
            }

            if (!int.TryParse(region[0], out int x) ||
                !int.TryParse(region[1], out int y) ||
                !int.TryParse(region[2], out int width) ||
                !int.TryParse(region[3], out int height))
            {
                Logger.Log.Error($"Invalid region values: x={region[0]}, y={region[1]}, width={region[2]}, height={region[3]}");
                throw new ArgumentException("Region values must be valid integers", nameof(region));
            }

            if (width <= 0 || height <= 0)
            {
                Logger.Log.Error($"Invalid region dimensions: width={width}, height={height}");
                throw new ArgumentException($"Region dimensions must be positive: width={width}, height={height}");
            }

            // Rent a destination from the pool, copy screen pixels into it. On backend
            // failure we either fall back to GDI (DXGI→GDI once) or rethrow — either way
            // the rented bitmap goes back to the pool first so we don't leak a bucket slot
            // on transient errors.
            Bitmap rented = BitmapPool.Default.Rent(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                Bitmap captured = _captureBackend.CaptureRegionInto(x, y, width, height, rented);
                if (captured == null)
                {
                    // Backend returned no frame (e.g. DXGI transient) — recycle, propagate null.
                    BitmapPool.Default.Return(rented);
                    return null;
                }
                return captured;
            }
            catch (Exception ex)
            {
                BitmapPool.Default.Return(rented);

                // If DXGI fails at runtime, fall back to GDI (same fallback semantics as before).
                if (_captureBackend is DxgiScreenCapture dxgi)
                {
                    Logger.Log.Warn($"DXGI capture failed, falling back to GDI: {ex.Message}");
                    dxgi.Dispose();
                    _captureBackend = new GdiScreenCapture();

                    Bitmap fallbackRented = BitmapPool.Default.Rent(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    try
                    {
                        Bitmap fallbackCaptured = _captureBackend.CaptureRegionInto(x, y, width, height, fallbackRented);
                        if (fallbackCaptured == null)
                        {
                            BitmapPool.Default.Return(fallbackRented);
                            return null;
                        }
                        return fallbackCaptured;
                    }
                    catch
                    {
                        BitmapPool.Default.Return(fallbackRented);
                        throw;
                    }
                }
                Logger.Log.Error($"Capture failed: x={x}, y={y}, w={width}, h={height}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Static helper for external callers (test harnesses, one-shot Quick Translate).
        /// Uses GDI capture and rents the destination from <see cref="BitmapPool.Default"/>
        /// so the returned bitmap participates in pool recycling. Callers MUST release
        /// the bitmap via <see cref="ReleaseCapturedBitmap"/> rather than
        /// <see cref="IDisposable.Dispose"/>; Dispose-then-Return is safe (the pool drops
        /// already-disposed instances silently) but skips the reuse benefit.
        /// </summary>
        public static Bitmap CaptureRegion(string[] region)
        {
            if (region == null || region.Length < 4)
                throw new ArgumentException("Region array must have at least 4 elements", nameof(region));

            if (!int.TryParse(region[0], out int x) || !int.TryParse(region[1], out int y) ||
                !int.TryParse(region[2], out int width) || !int.TryParse(region[3], out int height))
                throw new ArgumentException("Region values must be valid integers", nameof(region));

            if (width <= 0 || height <= 0)
                throw new ArgumentException($"Region dimensions must be positive: {width}x{height}");

            Bitmap rented = BitmapPool.Default.Rent(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                using (var gdi = new GdiScreenCapture())
                    return gdi.CaptureRegionInto(x, y, width, height, rented);
            }
            catch
            {
                BitmapPool.Default.Return(rented);
                throw;
            }
        }

        /// <summary>
        /// Return a Bitmap obtained from <see cref="CaptureRegion"/> back to the shared pool.
        /// Safe to pass <c>null</c>. Use this on the capture hot path instead of
        /// <see cref="IDisposable.Dispose"/> — the backend now rents its destination from
        /// the pool and writes screen pixels into it directly, so the instance you got is
        /// already owned by <see cref="BitmapPool.Default"/> and Return puts it back in
        /// rotation. MaxPerKey caps growth; extras are disposed automatically.
        /// </summary>
        public static void ReleaseCapturedBitmap(Bitmap bitmap)
        {
            BitmapPool.Default.Return(bitmap);
        }

        /// <summary>
        /// Check whether OCR can be executed according to the minimum interval.
        /// If allowed, this method will also update the last OCR time.
        /// </summary>
        /// <returns>true if OCR is allowed now; otherwise false.</returns>
        private bool IsOcrIntervalReady()
        {
            var now = DateTime.UtcNow;
            if (now - _lastOcrTime < MinOcrInterval)
            {
                return false;
            }

            _lastOcrTime = now;
            return true;
        }

        // ══════════════════════════════════════════════════════════════════
        //  OCR ACTIVE-TIME ACCUMULATOR (session 26 — referrals bonus-days)
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Invoked on each OCR tick to advance the "active since last
        /// heartbeat" accumulator. Computes wall-clock elapsed since the
        /// previous tick and adds it to the counter, bounded per-call so a
        /// paused timer (sleep / UAC prompt) can't suddenly contribute a
        /// giant slab.
        ///
        /// Called from <see cref="GetOCR"/> only while <see cref="OCRTimer"/>
        /// is running — a stopped timer stops accruing.
        /// </summary>
        private void AccumulateActiveOcrTick()
        {
            var now = DateTime.UtcNow;
            var prev = _ocrTickWindowStartUtc;
            _ocrTickWindowStartUtc = now;

            if (prev == DateTime.MinValue) return;   // first tick of a run
            var delta = now - prev;
            if (delta <= TimeSpan.Zero) return;
            // Per-tick ceiling ≈ 3× the configured OCR interval. If the machine
            // slept or the dispatcher starved, we don't credit the full gap.
            int maxDeltaMs = Math.Max(1000, GI_Subtitles.Services.Detection.GameOcrTuning.OcrIntervalMs() * 3);
            long ms = (long)delta.TotalMilliseconds;
            if (ms > maxDeltaMs) ms = maxDeltaMs;
            Interlocked.Add(ref _ocrActiveMillisAccumulator, ms);
        }

        /// <summary>
        /// Reporter called by <see cref="Services.Security.LicenseService"/>
        /// immediately before a heartbeat POST. Returns the number of active
        /// OCR seconds to ship (0 = field omitted). Thread-safe: read-only
        /// snapshot via <see cref="Interlocked.Read"/>.
        /// </summary>
        private int SnapshotActiveSeconds()
        {
            long ms = Interlocked.Read(ref _ocrActiveMillisAccumulator);
            if (ms <= 0) return 0;
            long seconds = ms / 1000;
            if (seconds > 3600) seconds = 3600; // server caps at this anyway
            return (int)seconds;
        }

        /// <summary>
        /// Reset the accumulator after a heartbeat succeeded. Subtracts the
        /// exact number of milliseconds represented by <paramref name="_"/>
        /// rather than zeroing, so seconds that accumulated between the
        /// snapshot and this call stay in the bucket for next heartbeat.
        /// </summary>
        private void ResetActiveSeconds()
        {
            // Simple and correct: zero-out is fine because the snapshot is
            // taken immediately before the POST, and OCR-tick additions
            // between snapshot and POST-completion are insignificant (≤ one
            // heartbeat RTT of a ~200ms tick = a few hundred ms). The server
            // caps at 3600 anyway, so the "correct" subtraction semantics
            // would make no user-visible difference.
            Interlocked.Exchange(ref _ocrActiveMillisAccumulator, 0);
        }

        /// <summary>
        /// Reset the per-tick window anchor when OCR starts or stops. Without
        /// this, a Start after a long pause would credit the entire pause
        /// interval to the first post-Start tick.
        /// </summary>
        private void ResetActiveOcrWindow()
        {
            _ocrTickWindowStartUtc = DateTime.MinValue;
        }

        /// <summary>
        /// Stop OCR immediately, idempotently. Called from
        /// <see cref="App.OnActivationStateChanged"/> right before the
        /// re-login dialog appears, because the modal dialog runs a nested
        /// dispatcher frame — dispatcher-based timers (OCRTimer / UITimer)
        /// keep firing behind it and would otherwise continue OCRing a
        /// revoked user's screen until they re-authenticated. Marshals to
        /// the UI thread so it's safe to call from any context.
        /// </summary>
        public void ForceStopOcr()
        {
            if (!Dispatcher.CheckAccess())
            {
                try { Dispatcher.BeginInvoke((Action)ForceStopOcr); }
                catch (Exception ex) { Logger.Log.Warn($"ForceStopOcr dispatch failed: {ex.Message}"); }
                return;
            }
            try
            {
                bool wasRunning = OCRTimer != null && OCRTimer.IsEnabled;
                if (OCRTimer != null && OCRTimer.IsEnabled) OCRTimer.Stop();
                if (UITimer != null && UITimer.IsEnabled) UITimer.Stop();
                ResetActiveOcrWindow();
                if (_stabilityBuffer != null)
                {
                    while (_stabilityBuffer.Count > 0)
                    {
                        try { _stabilityBuffer.Dequeue()?.Dispose(); }
                        catch (Exception ex) { Logger.Log.Warn($"ForceStopOcr buffer drain: {ex.Message}"); }
                    }
                }
                try { SwitchIcon("kaption.ico"); } catch { /* non-fatal */ }
                try { data?.UpdateDashboardStatus(); } catch { /* non-fatal */ }
                if (wasRunning)
                    Logger.Log.Info("OCR stopped by license-state change.");
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"ForceStopOcr: {ex}");
            }
        }

        /// <summary>
        /// License-aware gate for every "start OCR" entry point (Dashboard
        /// button, global hotkey, Setup Wizard). Returns true when OCR may
        /// start; returns false (and handles the UX) when the local session
        /// is missing, hard-expired, or clock-rolled. On the happy path also
        /// fires an opportunistic heartbeat so a server-side revocation is
        /// surfaced within ~30 minutes of activity instead of up to an hour.
        ///
        /// STOP paths deliberately do NOT call this — a revoked user should
        /// still be able to turn off whatever is running.
        /// </summary>
        private bool TryGateOcrStart()
        {
            var svc = App.LicenseService;
            if (svc == null)
            {
                Logger.Log.Warn("OCR start blocked: LicenseService not initialized.");
                GI_Subtitles.Views.ModernDialog.Info(
                    owner: this,
                    title: LocalizedString("Dialog_StillStarting_Title", "Still starting up"),
                    body: LocalizedString("Dialog_StillStarting_Body", "Kaption is still getting ready. Please try again in a moment."));
                return false;
            }

            if (svc.IsActivated)
            {
                // Fire-and-forget opportunistic re-check. The heartbeat path
                // itself clears the session on 401 and fires
                // ActivationStateChanged, which App.OnActivationStateChanged
                // already wires to a re-login dialog — so we don't need to
                // handle that case here.
                _ = svc.EnsureFreshAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
                return true;
            }

            Logger.Log.Info("OCR start blocked: session not active — prompting re-activation.");
            try
            {
                var login = new Views.LoginWindow(svc) { Owner = this };
                bool? ok = login.ShowDialog();
                if (ok != true || !svc.IsActivated)
                    return false;

                // Fresh session — restart the heartbeat timer so the new JWT
                // is kept alive. (SignOut disposed the old timer.)
                svc.StartHeartbeatTimer();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"TryGateOcrStart: re-login dialog failed: {ex}");
                GI_Subtitles.Views.ModernDialog.Error(
                    owner: this,
                    title: LocalizedString("Dialog_SignInOpenFailed_Title", "Couldn't open sign-in"),
                    body: LocalizedString("Dialog_SignInOpenFailed_Body", "Kaption couldn't open the sign-in window. Please close the app and launch it again."),
                    technicalDetails: ex.ToString());
                return false;
            }
        }

        /// <summary>
        /// Block OCR start when the initial paid-dictionary download hasn't
        /// finished yet. Without this gate, clicking Start on a fresh install a
        /// second after login fires the matcher against an empty Polish pack and
        /// the user sees English subtitles. Returns true when the sync is done
        /// (or was never scheduled because the account can't download paid packs)
        /// and OCR can proceed.
        /// </summary>
        private bool TryGateInitialDictionarySync()
        {
            if (App.StartupStatus != App.InitialStartupStatus.DownloadingTranslations)
                return true;

            Logger.Log.Info("OCR start blocked: initial dictionary sync still in flight.");
            GI_Subtitles.Views.ModernDialog.Info(
                owner: this,
                title: LocalizedString("Dialog_StillDownloading_Title", "Translations still downloading"),
                body: LocalizedString("Dialog_StillDownloading_Body", "Kaption is still downloading the translation pack. It usually takes just a few seconds on a first launch — try again in a moment."));
            return false;
        }

        /// <summary>
        /// Block OCR start when the OCR engine hasn't finished initializing,
        /// or the last init attempt threw. Without this gate, a click on
        /// Start during the 2–10 s cold-boot window would pass the other
        /// four gates, flip the OCRTimer on, and then short-circuit inside
        /// <see cref="GetOCR"/> on the <c>_engineReady</c> check — leaving
        /// the user staring at a ticking timer with no translations and no
        /// explanation. Surfacing a localized "still loading" / "failed"
        /// dialog makes the failure mode obvious.
        ///
        /// <para>On <see cref="SettingsWindow.EngineStatus.Failed"/>, the
        /// dialog points at the Dashboard's Retry banner rather than
        /// offering its own retry — one CTA location means no "where did I
        /// click Retry?" confusion. The data.Show() pulls the Dashboard
        /// above the wizard/main window so the banner is visible.</para>
        /// </summary>
        private bool TryGateEngineReady()
        {
            var state = data?.Engine ?? SettingsWindow.EngineStatus.Loading;
            if (state == SettingsWindow.EngineStatus.Ready && _engineReady)
                return true;

            if (state == SettingsWindow.EngineStatus.Failed)
            {
                Logger.Log.Info("OCR start blocked: engine init failed.");
                try { if (data != null && !data.IsVisible) data.Show(); }
                catch { /* non-fatal */ }
                var err = data?.LastEngineError;
                GI_Subtitles.Views.ModernDialog.Error(
                    owner: this,
                    title: LocalizedString("Dialog_EngineFailed_Title", "Translator didn't load"),
                    body: LocalizedString("Dialog_EngineFailed_Body", "The OCR engine hit an error starting up. Open Dashboard and click Retry — the banner at the top has a fallback option if the GPU path keeps failing."),
                    technicalDetails: err?.ToString());
                return false;
            }

            Logger.Log.Info("OCR start blocked: engine still initializing.");
            GI_Subtitles.Views.ModernDialog.Info(
                owner: this,
                title: LocalizedString("Dialog_EngineLoading_Title", "Translator still loading"),
                body: LocalizedString("Dialog_EngineLoading_Body", "Give it a moment — the OCR engine is still warming up. Try Start again when the pill under the Start button shows Ready."));
            return false;
        }

        /// <summary>
        /// Block OCR start when the user hasn't picked a capture region yet.
        /// Without this gate, <see cref="GetOCR"/> crashes on
        /// <c>notify.Region[1]</c> with an IndexOutOfRangeException every tick —
        /// each failure is swallowed by the dispatcher's unhandled-exception
        /// handler, but the OCRTimer keeps firing, so the log fills with the
        /// same exception 4–5 times per second until the user stops OCR. The
        /// Setup Wizard's Start path already has this check inline (pointing
        /// the user back to its Region step); this helper covers the Dashboard
        /// button, the Ctrl+Shift+S hotkey, and the Ctrl+` force-OCR path.
        /// </summary>
        private bool TryGateRegionConfigured()
        {
            if (HasConfiguredRegion()) return true;

            Logger.Log.Info("OCR start blocked: no capture region configured.");
            GI_Subtitles.Views.ModernDialog.Warn(
                owner: this,
                title: LocalizedString("Dialog_RegionMissing_Title", "Capture region missing"),
                body: LocalizedString("Dialog_RegionMissing_Body", "Pick the part of the screen where the game's dialogue appears before starting translation."),
                details: LocalizedString("Dialog_RegionMissing_Details", "Go back to the wizard's Region step, or open Settings \u203A Regions and click \"Select Region\" — then try again."));
            return false;
        }

        /// <summary>
        /// Block OCR start when the target game's process isn't running. Reading
        /// a region from a non-existent window is legal but gives no signal —
        /// the user would see "OCR running" with no subtitles and no indication
        /// why. This surfaces a plain error ("start the game first") so the
        /// failure mode is obvious.
        ///
        /// Uses the configured Game key (defaults to "Genshin") to look up the
        /// profile's ProcessNames. Unknown games (no process names registered)
        /// are allowed through so we never block on something we can't check.
        /// </summary>
        private bool TryGateGameRunning()
        {
            try
            {
                // Dev escape hatch for testing against recorded gameplay
                // video when the target game isn't installed locally. Set
                // "DevSkipGameGate": true in %APPDATA%\Kaption\Config.json.
                // Intentionally not exposed in the UI — end users should
                // never need this; a stray true here would silently hide
                // the "Game isn't running" feedback.
                if (Config.Get("DevSkipGameGate", false))
                {
                    // Debug-level: the gate is hit on every OCR-start request
                    // (Dashboard click, hotkey, force-OCR), so Info spams the
                    // log several times a second in dev testing. A one-time
                    // startup banner would be nicer, but keeping per-call at
                    // Debug preserves an audit trail for support sessions
                    // without polluting INFO-level logs.
                    Logger.Log.Debug("TryGateGameRunning: bypassed by DevSkipGameGate=true (Config.json).");
                    return true;
                }

                string gameKey = Config.Get<string>("Game", "Genshin");
                var profile = GI_Subtitles.Services.Detection.GameRegionProfile.Get(gameKey);
                if (profile?.ProcessNames == null || profile.ProcessNames.Length == 0)
                    return true; // Unknown game — we can't verify, so don't block.

                foreach (var procName in profile.ProcessNames)
                {
                    if (string.IsNullOrWhiteSpace(procName)) continue;
                    try
                    {
                        if (System.Diagnostics.Process.GetProcessesByName(procName).Length > 0)
                            return true;
                    }
                    catch (Exception ex)
                    {
                        // If process enumeration itself is broken (AV hook, policy
                        // restriction), don't strand the user — let them through.
                        Logger.Log.Warn($"TryGateGameRunning: GetProcessesByName({procName}) threw: {ex.Message}");
                        return true;
                    }
                }

                // Session-scoped escape hatch: once the user confirms "I'm on
                // cloud gaming / I know what I'm doing", we stop re-asking
                // within this app run. Next app launch reverts to the gate.
                if (_gameGateBypassedThisSession)
                {
                    Logger.Log.Debug($"TryGateGameRunning: bypassed via prior session grant (game '{gameKey}' not local).");
                    return true;
                }

                Logger.Log.Info($"OCR start blocked: no process match for '{gameKey}' ({string.Join(", ", profile.ProcessNames)}).");
                bool bypass = GI_Subtitles.Views.ModernDialog.Confirm(
                    owner: this,
                    title: LocalizedString("Dialog_GameNotFound_Title", "Game isn't running"),
                    body: LocalizedString("Dialog_GameNotFound_Body", "Kaption can't find the game window. Start the game, wait for the main menu, then click Start."),
                    details: LocalizedString("Dialog_GameNotFound_Details", "Playing via GeForce Now or another cloud gaming service? The game doesn't run on this PC — click Continue anyway to start."),
                    primaryText: LocalizedString("Dialog_GameNotFound_Primary", "Continue anyway"),
                    secondaryText: LocalizedString("Dialog_GameNotFound_Secondary", "Cancel"),
                    severity: GI_Subtitles.Views.DialogSeverity.Warn,
                    dangerousPrimary: true);

                if (bypass)
                {
                    _gameGateBypassedThisSession = true;
                    Logger.Log.Info($"TryGateGameRunning: user granted bypass for '{gameKey}' (cloud gaming / advanced setup).");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"TryGateGameRunning threw: {ex.Message}");
                return true; // Fail open — don't strand the user on a buggy check.
            }
        }

        /// <summary>
        /// Show the Genshin display-mode setup tip on the user's first
        /// "Start OCR" press per install. After they click "Got it" the
        /// Config flag persists and they never see it again.
        ///
        /// Replaces the old detection-based warning that tried to tell
        /// exclusive fullscreen from borderless from outside the game
        /// process — unreliable because Windows 10/11 Fullscreen
        /// Optimizations make even "exclusive" mode DWM-composed. Now we
        /// just educate every new user about the right setup once and
        /// trust them to apply it.
        ///
        /// "Got it" button is held disabled for 2 seconds inside the
        /// dialog so muscle-memory Enter doesn't dismiss before the eyes
        /// scan the steps. Cancel lets the user back out without
        /// acknowledging — they'll see the tip again next time.
        /// </summary>
        private bool TryGateFullscreenTip()
        {
            try
            {
                if (Config.Get("FullscreenTipAcknowledged", false))
                    return true;

                Logger.Log.Info("TryGateFullscreenTip: showing one-time Genshin display-mode tip.");

                var dialog = new GI_Subtitles.Views.FullscreenTipWindow();
                try { dialog.Owner = this; } catch { /* ignore — dialog still works without an owner */ }

                bool? result = dialog.ShowDialog();
                if (dialog.Acknowledged)
                {
                    Config.Set("FullscreenTipAcknowledged", true);
                    Logger.Log.Info("TryGateFullscreenTip: user acknowledged — won't show again.");
                    return true;
                }

                Logger.Log.Info("TryGateFullscreenTip: user cancelled — tip will show again next start.");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"TryGateFullscreenTip threw: {ex.Message}");
                return true; // Fail open — never block OCR on a UI bug.
            }
        }

        /// <summary>
        /// Block OCR start when the subtitle overlay's bounds intersect any of
        /// the configured capture regions. When that happens OCR reads its own
        /// translated output → translates it → reads THAT → forever. The
        /// existing MaskOverlayAreas defense blacks out the overlap in the
        /// captured bitmap before OCR, which stops the literal loop but also
        /// destroys the very text we wanted to read — so the user sees
        /// "translation broken" without understanding why. This gate surfaces
        /// the real problem and points them at the fix.
        ///
        /// <para>Uses the overlay's LIVE bounds when it has rendered at least
        /// once, falls back to a conservative layout-engine projection when
        /// it hasn't (first-run wizard Start). The session override matches
        /// the cloud-gaming bypass pattern: once the user deliberately clicks
        /// "Continue anyway", we stop nagging until they change the region or
        /// the overlay — which clears the flag automatically — or restart the
        /// app.</para>
        /// </summary>
        private bool TryGateOverlayNotInRegion()
        {
            try
            {
                if (_overlapOverrideAccepted) return true;
                if (notify == null) return true;

                var check = EvaluateOverlayRegionOverlap();
                if (!check.HasOverlap) return true;

                Logger.Log.Info($"OCR start blocked: overlay overlaps {check.Kind} region "
                                + $"(overlay={check.OverlayRect}, region={check.RegionRect}, projected={check.OverlayWasProjected}).");

                bool bypass = GI_Subtitles.Views.ModernDialog.Confirm(
                    owner: this,
                    title: LocalizedString("Dialog_OverlayInRegion_Title", "⚠ Subtitles cover the game's dialogue"),
                    body: LocalizedString("Dialog_OverlayInRegion_Body", "The subtitle box is sitting on top of the game's dialogue — Kaption would start reading its own translation and loop forever. Drag the subtitle box out of the way, or press Ctrl+Shift+R to redraw the dialogue area. Translation resumes as soon as the box is clear."),
                    details: LocalizedString("Dialog_OverlayInRegion_Details", "Still overlapping after moving things around? In Settings → Display, lower Max subtitle height or raise vertical padding so the subtitle box sits further above the dialogue. Guide with diagrams: kaption.one/how-to/region#overlap"),
                    primaryText: LocalizedString("Dialog_OverlayInRegion_Primary", "Continue anyway"),
                    secondaryText: LocalizedString("Dialog_OverlayInRegion_Secondary", "Reselect region"),
                    severity: GI_Subtitles.Views.DialogSeverity.Warn,
                    dangerousPrimary: true);

                if (bypass)
                {
                    _overlapOverrideAccepted = true;
                    Logger.Log.Info("TryGateOverlayNotInRegion: user granted bypass (overlap tolerated this session).");
                    return true;
                }

                // User chose "Reselect region" — fire the region picker instead
                // of just saying no. Cheaper than making them dig through menus
                // to find the button after the dialog already told them what
                // to do.
                try
                {
                    if (!ChooseRegion)
                    {
                        ChooseRegion = true;
                        notify.ChooseRegion();
                        ChooseRegion = false;
                        NotifyRegionChanged();
                        data?.UpdateDashboardRegionInfo();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn($"TryGateOverlayNotInRegion: re-pick region failed: {ex.Message}");
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"TryGateOverlayNotInRegion threw: {ex.Message}");
                return true; // Fail open — never strand the user on a validator bug.
            }
        }

        /// <summary>
        /// Compute overlap between the overlay and the three capture regions,
        /// using live rendered bounds when available and layout-engine
        /// projection otherwise. Shared by the gate and the region-change
        /// event handlers so a single code path owns the math and the
        /// projection-vs-live decision.
        /// </summary>
        internal GI_Subtitles.Services.Validation.OverlapCheckResult EvaluateOverlayRegionOverlap()
        {
            // Overlay bounds: prefer actual rendered size; fall back to a
            // projection for first-run wizard / brand-new overlays that
            // haven't laid out yet.
            System.Windows.Rect overlayRect;
            bool projected;

            if (this.IsLoaded && this.ActualWidth > 0 && this.ActualHeight > 0)
            {
                overlayRect = new System.Windows.Rect(
                    this.Left * Scale,
                    this.Top * Scale,
                    this.ActualWidth * Scale,
                    this.ActualHeight * Scale);
                projected = false;
            }
            else
            {
                overlayRect = ProjectOverlayFromDialogueRegion();
                projected = true;
            }

            return GI_Subtitles.Services.Validation.OverlayRegionValidator.Check(
                overlayRect,
                notify?.Region,
                notify?.Region2,
                notify?.AnswerRegion,
                projected);
        }

        /// <summary>
        /// Call after any code path that mutates the capture region. Clears
        /// the overlap-override flag so the next OCR start revalidates with
        /// the new geometry.
        /// </summary>
        internal void NotifyRegionChanged()
        {
            _overlapOverrideAccepted = false;
        }

        /// <summary>
        /// Post-change notification: clears the session override and, if the
        /// new region creates an overlay-in-region overlap, surfaces a
        /// non-blocking Warn dialog so the user sees the problem BEFORE they
        /// try to start OCR and hit the gate. Called from every region-edit
        /// call site (Dashboard "Select Region", Ctrl+Shift+R hotkey, wizard,
        /// auto-detect). Safe to call with any <paramref name="dialogOwner"/>
        /// (Settings window, Setup Wizard, MainWindow) — the dialog centres
        /// on it.
        /// </summary>
        internal void OnCaptureRegionUserChange(System.Windows.Window dialogOwner)
            => ShowOverlapWarnIfNeeded(dialogOwner, clearOverride: true);

        /// <summary>
        /// Post-change notification for Settings → Display edits that resize
        /// or reposition the overlay (MaxOverlayHeight/Width, Pad). Fires the
        /// same Warn dialog as the capture-region path so the user gets
        /// symmetric coverage: changing EITHER side of the equation surfaces
        /// the warning. Clears the session override because the new overlay
        /// geometry might have made the previously-accepted overlap worse OR
        /// better — either way, re-evaluate on next Start.
        /// </summary>
        internal void OnOverlaySizeUserChange(System.Windows.Window dialogOwner)
            => ShowOverlapWarnIfNeeded(dialogOwner, clearOverride: true);

        /// <summary>
        /// Called when OCR starts. If the subtitle box has no content yet
        /// (fresh launch, game not in a dialogue scene), show a small
        /// placeholder so the box is visibly on screen and the user can
        /// grab + reposition it BEFORE any dialogue appears. The first
        /// real translation overwrites the placeholder naturally via
        /// UpdateText; stopping OCR clears it in
        /// <see cref="ClearReadyPlaceholderIfActive"/>.
        /// </summary>
        private void ShowReadyPlaceholderIfEmpty()
        {
            try
            {
                if (_inRuntimeOverlapDragMode) return; // drag-fix owns the text
                if (SubtitleText == null) return;
                if (!string.IsNullOrWhiteSpace(SubtitleText.Text) && SubtitleText.Text != _readyPlaceholderText)
                    return; // real translation already on screen — don't overwrite

                _readyPlaceholderText = LocalizedString(
                    "Runtime_ReadyPlaceholder",
                    "⇕  Kaption — ready. Drag me to reposition.");
                SubtitleText.Text = _readyPlaceholderText;
                SubtitleText.Visibility = System.Windows.Visibility.Visible;
                if (SubtitleBackground != null)
                    SubtitleBackground.Visibility = System.Windows.Visibility.Visible;
                // Force the window to a reasonable size so the placeholder
                // renders at a grabbable scale on first draw.
                if (this.ActualWidth < 320) this.Width = 360;
                if (this.ActualHeight < 48) this.Height = 56;
                _placeholderActive = true;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"ShowReadyPlaceholderIfEmpty: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear the ready-placeholder when real translation arrives or
        /// when OCR stops. Safe to call when there's no active placeholder.
        /// </summary>
        private void ClearReadyPlaceholderIfActive()
        {
            if (!_placeholderActive) return;
            try
            {
                if (SubtitleText != null && SubtitleText.Text == _readyPlaceholderText)
                    SubtitleText.Text = string.Empty;
                _placeholderActive = false;
                _readyPlaceholderText = null;
            }
            catch { /* cosmetic, never fatal */ }
        }

        private bool _placeholderActive;
        private string _readyPlaceholderText;

        /// <summary>
        /// Runtime guard — call after every layout recomputation
        /// (<see cref="UpdateWindowPosition"/>, <see cref="UpdateWindowHeightAndTop"/>).
        /// If the overlay's bounds now intersect any capture region, HIDE the
        /// overlay (zero opacity) and show a persistent red alert banner
        /// pointing at the fix. When the overlap clears on a subsequent tick
        /// (user redrew the region, bumped padding, shrank MaxHeight), the
        /// overlay is restored.
        ///
        /// <para>Why hide instead of mask: MaskOverlayAreas paints overlap
        /// pixels black in the OCR bitmap — that stops the literal feedback
        /// loop but the user still sees garbled output over their dialogue.
        /// Hiding the overlay removes the source of the loop entirely AND
        /// makes the failure mode explicit ("subtitle is gone, banner says
        /// why"). MaskOverlayAreas stays as a belt-and-braces for timing
        /// windows where the overlay renders one frame before this guard
        /// runs.</para>
        ///
        /// <para>Session override: respects <see cref="_overlapOverrideAccepted"/>.
        /// If the user has deliberately clicked "Continue anyway" on the
        /// start-OCR gate, the runtime guard also steps aside — we don't
        /// second-guess a deliberate decision.</para>
        /// </summary>
        private void CheckRuntimeOverlapAndApply()
        {
            try
            {
                if (_overlapOverrideAccepted)
                {
                    // User clicked "Continue anyway" on the start-OCR gate.
                    // Step aside — don't second-guess a deliberate choice.
                    if (_overlapAlertOverlay != null && _overlapAlertOverlay.IsShown)
                        _overlapAlertOverlay.Hide();
                    if (_inRuntimeOverlapDragMode) ExitRuntimeOverlapState();
                    return;
                }

                var check = EvaluateOverlayRegionOverlap();
                if (check.HasOverlap)
                {
                    EnterRuntimeOverlapState(check);
                }
                else
                {
                    ExitRuntimeOverlapState();
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"CheckRuntimeOverlapAndApply: {ex.Message}");
            }
        }

        private void EnterRuntimeOverlapState(GI_Subtitles.Services.Validation.OverlapCheckResult check)
        {
            // Keep the subtitle box VISIBLE but wrap it in a red "drag me"
            // treatment so the user can grab it and move it out of the way.
            // Hiding the overlay (old behaviour) made the failure mode
            // obvious but denied the user the most natural fix — drag.
            // OCR is paused via the _inRuntimeOverlapDragMode flag (see
            // GetOCR's early-return) so the overlay's text can't start a
            // feedback loop while the user repositions.
            if (!_inRuntimeOverlapDragMode)
            {
                _inRuntimeOverlapDragMode = true;
                try
                {
                    if (SubtitleBackground != null)
                    {
                        _subtitleBgBeforeOverlap = SubtitleBackground.Background;
                        _subtitleMinWidthBeforeOverlap = SubtitleBackground.MinWidth;
                        _subtitleMinHeightBeforeOverlap = SubtitleBackground.MinHeight;

                        // Dark red tint with solid border so the box is
                        // unmistakeable against any game background.
                        SubtitleBackground.Background = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromArgb(0xE0, 0xB0, 0x1F, 0x1F));
                        SubtitleBackground.BorderBrush = System.Windows.Media.Brushes.Red;
                        SubtitleBackground.BorderThickness = new System.Windows.Thickness(3);
                        SubtitleBackground.Cursor = System.Windows.Input.Cursors.SizeAll;
                        // Force a grabbable size — without this the Border
                        // collapses to just its padding (~38×26 px) when the
                        // translator is paused and SubtitleText goes blank,
                        // and the user sees nothing to drag.
                        SubtitleBackground.MinWidth = 480;
                        SubtitleBackground.MinHeight = 90;
                    }

                    // Replace the (possibly blank) translation text with a
                    // drag hint so the red box has readable, clickable
                    // content and the user knows what to do. Save the
                    // previous text/header so we can restore when overlap
                    // clears. CRITICAL: also force Visibility on every
                    // element in the chain — any one collapsed node makes
                    // the whole thing disappear and the user sees nothing
                    // to grab. We restore originals on exit.
                    if (SubtitleText != null)
                    {
                        _subtitleTextBeforeOverlap = SubtitleText.Text;
                        _subtitleTextVisibilityBeforeOverlap = SubtitleText.Visibility;
                        SubtitleText.Text = LocalizedString(
                            "Runtime_OverlapDragHint",
                            "⇕  Drag me out of the dialogue area");
                        SubtitleText.Visibility = System.Windows.Visibility.Visible;
                    }
                    if (HeaderText != null)
                    {
                        _headerTextBeforeOverlap = HeaderText.Text;
                        _headerVisibilityBeforeOverlap = HeaderText.Visibility;
                        HeaderText.Text = string.Empty;
                        HeaderText.Visibility = System.Windows.Visibility.Collapsed;
                    }
                    // SubtitleBackground can be collapsed by the game-
                    // lost-focus path (GetOCR ~line 694). It can also be
                    // hidden by the "toggle subtitles" hotkey. Either way,
                    // in drag-fix mode we override both.
                    if (SubtitleBackground != null)
                    {
                        _subtitleBgVisibilityBeforeOverlap = SubtitleBackground.Visibility;
                        SubtitleBackground.Visibility = System.Windows.Visibility.Visible;
                    }
                    // Force MainWindow itself visible + opaque. Some config
                    // paths drop Opacity (e.g. user slider for "subtle"
                    // overlays) or collapse the window entirely when the
                    // game isn't foreground. In drag-fix mode the user has
                    // to see it to grab it.
                    _opacityBeforeOverlap = this.Opacity;
                    if (this.Opacity < 1.0) this.Opacity = 1.0;
                    if (this.Visibility != System.Windows.Visibility.Visible)
                        this.Visibility = System.Windows.Visibility.Visible;
                    // Clear any focus-loss hide flag so the SubtitleBackground
                    // doesn't get re-collapsed on the next timer tick.
                    _hiddenForFocusLoss = false;
                    // Force a reasonable window size so the box is obviously
                    // visible regardless of what the layout engine computed
                    // from the now-hint-only text content.
                    if (this.ActualWidth < 480) this.Width = 520;
                    if (this.ActualHeight < 90) this.Height = 110;

                    // While in drag-fix mode the box MUST be hit-testable
                    // even if the user had previously enabled click-through
                    // — otherwise the drag attempt falls through to the
                    // game and nothing happens.
                    SetClickThrough(false);
                }
                catch (Exception ex) { Logger.Log.Warn($"EnterRuntimeOverlapState visual: {ex.Message}"); }
            }

            if (_overlapAlertOverlay == null)
                _overlapAlertOverlay = new OverlapAlertOverlay(Scale);

            // Unified with the gate/warn dialog: one Title + Body string for
            // every user-facing "box covers dialogue" surface (dialog, warn,
            // runtime alert). Short badge variant lives in Overlap_Badge_Warning.
            string title = LocalizedString("Dialog_OverlayInRegion_Title",
                "⚠ Subtitles cover the game's dialogue");
            string body = LocalizedString("Dialog_OverlayInRegion_Body",
                "The subtitle box is sitting on top of the game's dialogue — Kaption would start reading its own translation and loop forever. Drag the subtitle box out of the way, or press Ctrl+Shift+R to redraw the dialogue area. Translation resumes as soon as the box is clear.");

            _overlapAlertOverlay.Show(
                regionScreenPx: check.RegionRect,
                intersectionScreenPx: check.IntersectionRect,
                titleText: title,
                bodyText: body);
        }

        private void ExitRuntimeOverlapState()
        {
            if (_inRuntimeOverlapDragMode)
            {
                _inRuntimeOverlapDragMode = false;
                try
                {
                    if (SubtitleBackground != null)
                    {
                        // Restore the original brush; fall back to the XAML
                        // default if we somehow lost the reference.
                        SubtitleBackground.Background = _subtitleBgBeforeOverlap ??
                            new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromArgb(0xB0, 0, 0, 0));
                        SubtitleBackground.BorderBrush = null;
                        SubtitleBackground.BorderThickness = new System.Windows.Thickness(0);
                        SubtitleBackground.Cursor = System.Windows.Input.Cursors.Hand;
                        SubtitleBackground.MinWidth = _subtitleMinWidthBeforeOverlap;
                        SubtitleBackground.MinHeight = _subtitleMinHeightBeforeOverlap;
                    }
                    _subtitleBgBeforeOverlap = null;

                    // Restore the original texts + visibility. We deliberately
                    // don't re-measure the window here — the next OCR tick
                    // (now unpaused) will call UpdateWindowHeightAndTop and
                    // resize the box to fit whatever new translation comes
                    // in. Until then the box shows the prior translation.
                    if (SubtitleText != null)
                    {
                        if (_subtitleTextBeforeOverlap != null)
                            SubtitleText.Text = _subtitleTextBeforeOverlap;
                        SubtitleText.Visibility = _subtitleTextVisibilityBeforeOverlap;
                    }
                    _subtitleTextBeforeOverlap = null;

                    if (HeaderText != null)
                    {
                        if (_headerTextBeforeOverlap != null)
                            HeaderText.Text = _headerTextBeforeOverlap;
                        HeaderText.Visibility = _headerVisibilityBeforeOverlap;
                    }
                    _headerTextBeforeOverlap = null;

                    if (SubtitleBackground != null)
                        SubtitleBackground.Visibility = _subtitleBgVisibilityBeforeOverlap;

                    // Restore opacity only if the user had a lower preference.
                    if (_opacityBeforeOverlap > 0)
                        this.Opacity = _opacityBeforeOverlap;

                    // Restore click-through to the user's configured state.
                    SetClickThrough(_clickThroughEnabled);
                }
                catch (Exception ex) { Logger.Log.Warn($"ExitRuntimeOverlapState visual: {ex.Message}"); }
            }

            if (_overlapAlertOverlay != null && _overlapAlertOverlay.IsShown)
                _overlapAlertOverlay.Hide();
        }

        private void ShowOverlapWarnIfNeeded(System.Windows.Window dialogOwner, bool clearOverride)
        {
            try
            {
                if (clearOverride) NotifyRegionChanged();

                // Refresh the runtime overlap state whenever the user
                // changes region or overlay geometry. Without this, the
                // runtime red alert continues to show the OLD region
                // outline even after the user fixes things with
                // Ctrl+Shift+R or by tweaking Settings — and the drag-fix
                // mode never exits because nothing re-evaluates. Running
                // this before the Warn dialog means the dialog ALSO
                // reflects the new state (if the user's edit resolved the
                // overlap, the warning simply doesn't fire).
                CheckRuntimeOverlapAndApply();

                var check = EvaluateOverlayRegionOverlap();
                if (!check.HasOverlap) return;

                Logger.Log.Info($"User-edit overlap warning: overlay overlaps {check.Kind} region "
                                + $"(overlay={check.OverlayRect}, region={check.RegionRect}, projected={check.OverlayWasProjected}).");

                // Warn (not Confirm): this fires AFTER the edit, so there's
                // no "continue or cancel" decision to make — we're just
                // telling the user what they just did. The actual blocking
                // happens at the OCR-start gate.
                GI_Subtitles.Views.ModernDialog.Warn(
                    owner: dialogOwner ?? this,
                    title: LocalizedString("Dialog_OverlayInRegion_Title", "⚠ Subtitles cover the game's dialogue"),
                    body: LocalizedString("Dialog_OverlayInRegion_Body", "The subtitle box is sitting on top of the game's dialogue — Kaption would start reading its own translation and loop forever. Drag the subtitle box out of the way, or press Ctrl+Shift+R to redraw the dialogue area. Translation resumes as soon as the box is clear."),
                    details: LocalizedString("Dialog_OverlayInRegion_Details", "Still overlapping after moving things around? In Settings → Display, lower Max subtitle height or raise vertical padding so the subtitle box sits further above the dialogue. Guide with diagrams: kaption.one/how-to/region#overlap"));
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"ShowOverlapWarnIfNeeded: {ex.Message}");
            }
        }

        /// <summary>
        /// Project the overlay rectangle for the current dialogue region,
        /// using MainWindow's screen + DPI context. Returns Rect.Empty when
        /// no region is configured or the screen lookup fails — callers must
        /// guard against the empty case.
        /// </summary>
        private System.Windows.Rect ProjectOverlayFromDialogueRegion()
        {
            try
            {
                var region = GI_Subtitles.Services.Validation.OverlayRegionValidator.ParseRegion(notify?.Region);
                if (region.IsEmpty) return System.Windows.Rect.Empty;

                Screen targetScreen = null;
                foreach (var screen in Screen.AllScreens)
                {
                    if (screen.WorkingArea.Contains(
                            new System.Drawing.Point((int)region.X, (int)region.Y)))
                    {
                        targetScreen = screen;
                        break;
                    }
                }
                if (targetScreen == null) targetScreen = Screen.PrimaryScreen;

                var screenBounds = new System.Windows.Rect(
                    targetScreen.Bounds.Left,
                    targetScreen.Bounds.Top,
                    targetScreen.Bounds.Width,
                    targetScreen.Bounds.Height);

                return GI_Subtitles.Services.Validation.OverlayRegionValidator
                    .ProjectOverlayRectUsingConfig(region, screenBounds, Scale);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"ProjectOverlayFromDialogueRegion failed: {ex.Message}");
                return System.Windows.Rect.Empty;
            }
        }

        /// <summary>
        /// Opens the setup wizard window. Can be called on first run or via Dashboard.
        /// </summary>
        private void ShowSetupWizard()
        {
            var wizard = new SetupWizardWindow(notify);
            wizard.OnStartOCR = () =>
            {
                if (!OCRTimer.IsEnabled)
                {
                    // Explicit region check: previously UpdateWindowPosition
                    // blew up with IndexOutOfRangeException when notify.Region
                    // was empty (fresh install, user clicked Start before
                    // completing the region-selection step). The position
                    // call is now guarded internally, but we also want to
                    // tell the user WHY nothing's happening rather than
                    // silently starting a timer that has no region to read.
                    if (!HasConfiguredRegion())
                    {
                        GI_Subtitles.Views.ModernDialog.Warn(
                            owner: wizard,
                            title: LocalizedString("Dialog_RegionMissing_Title", "Capture region missing"),
                            body: LocalizedString("Dialog_RegionMissing_Body", "Pick the part of the screen where the game's dialogue appears before starting translation."),
                            details: LocalizedString("Dialog_RegionMissing_Details", "Go back to the wizard's Region step, or open Settings \u203A Regions and click \"Select Region\" — then try again."));
                        return;
                    }

                    if (!TryGateOcrStart()) return;
                    if (!TryGateInitialDictionarySync()) return;
                    if (!TryGateEngineReady()) return;
                    if (!TryGateGameRunning()) return;
                    if (!TryGateFullscreenTip()) return;
                    if (!TryGateOverlayNotInRegion()) return;
                    UpdateWindowPosition();
                    ShowReadyPlaceholderIfEmpty();
                    ResetActiveOcrWindow();
                    OCRTimer.Start();
                    UITimer.Start();
                    System.Media.SystemSounds.Exclamation.Play();
                    SwitchIcon("kaption-running.ico");
                    data.UpdateDashboardStatus();
                }
            };
            wizard.OnOpenSettings = () =>
            {
                if (data != null && !data.IsVisible)
                    data.Show();
            };
            wizard.GetEngine = () => data?.engine;
            wizard.GetGameId = () => Game;
            wizard.OnCaptureRegionUserChanged = () => OnCaptureRegionUserChange(dialogOwner: wizard);
            wizard.Show();
        }

        /// <summary>
        /// Force an immediate OCR + translation, bypassing stability checks and interval throttle.
        /// Triggered by hotkey.
        /// </summary>
        private void ForceOcrTranslate()
        {
            if (_isOcrRunning) return;

            // Same gates as the regular Start path — a force-OCR hotkey press
            // with no region configured was crashing inside CaptureAndMask.
            // Show the same localized dialog so the user knows which precondition
            // is missing rather than getting silent no-ops.
            if (!TryGateOcrStart()) return;
            if (!TryGateInitialDictionarySync()) return;
            if (!TryGateEngineReady()) return;
            if (!TryGateRegionConfigured()) return;
            if (!TryGateGameRunning()) return;
            if (!TryGateFullscreenTip()) return;
            if (!TryGateOverlayNotInRegion()) return;

            try
            {
                Bitmap target = CaptureAndMask(notify.Region);
                if (target == null) return;

                Mat frameMat = target.ToMat();
                if (frameMat == null || frameMat.Empty())
                {
                    // target came from the pool-rented backend — Return, don't Dispose.
                    ReleaseCapturedBitmap(target);
                    frameMat?.Dispose();
                    return;
                }

                _lastOcrTime = DateTime.UtcNow;
                SetWindowPos(new WindowInteropHelper(this).Handle, -1, 0, 0, 0, 0, 1 | 2);
                TriggerOcrAsync(frameMat, target, null);
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"ForceOcrTranslate error: {ex}");
            }
        }

        private async Task QuickTranslateRegion()
        {
            // Use the 5th gate so users who hit Ctrl+Q during cold-boot get the
            // localized "engine loading" dialog instead of a silent no-op.
            // See b42569d for the gate contract and the matching call sites
            // in OnToggleOCR / ForceOcrTranslate / WndProc / Setup Wizard.
            if (!TryGateEngineReady()) return;

            // Reject re-entry while a picker/OCR is already in flight, and
            // swallow auto-repeat inside a 300ms window. A press with no
            // picker up but a stale popup still dismisses the popup below.
            if (_quickTranslateBusy) return;
            if ((DateTime.UtcNow - _lastQuickTranslateUtc).TotalMilliseconds < 300) return;
            _lastQuickTranslateUtc = DateTime.UtcNow;
            _quickTranslateBusy = true;

            CloseQuickTranslatePopup();

            try
            {
                // Let user draw a rectangle on screen
                var rect = Screenshot.Screenshot.GetRegion();
                if (rect.Width <= 0 || rect.Height <= 0) return;

                double regionCenterX = rect.TopLeft.X + rect.Width / 2;
                double regionBottomY = rect.TopLeft.Y + rect.Height;

                // Build a temporary region array from the selected rectangle
                string[] tempRegion = new[]
                {
                    ((int)rect.TopLeft.X).ToString(),
                    ((int)rect.TopLeft.Y).ToString(),
                    ((int)rect.Width).ToString(),
                    ((int)rect.Height).ToString()
                };

                // Capture the selected region (rented from BitmapPool.Default).
                System.Drawing.Bitmap target = CaptureRegion(tempRegion);
                if (target == null) return;

                OpenCvSharp.Mat frameMat = target.ToMat();
                if (frameMat == null || frameMat.Empty())
                {
                    ReleaseCapturedBitmap(target);
                    frameMat?.Dispose();
                    return;
                }

                // Run OCR + matching on background thread (independent from main OCR loop)
                string resultText = await Task.Run(() =>
                {
                    try
                    {
                        OCRResult ocrResult = data.engine.DetectTextFromMat(frameMat);
                        string text = ocrResult?.Text ?? "";
                        if (string.IsNullOrWhiteSpace(text)) return "";

                        // Try to find translation
                        string match = data.Matcher.FindClosestMatch(text, out string key);
                        string result = !string.IsNullOrEmpty(match) ? match : text;
                        return result.Replace("\\n", "\n");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log.Error($"QuickTranslate OCR error: {ex}");
                        return "";
                    }
                    finally
                    {
                        frameMat?.Dispose();
                        // target rented from BitmapPool — Return, don't Dispose.
                        ReleaseCapturedBitmap(target);
                    }
                });

                if (!string.IsNullOrEmpty(resultText))
                {
                    ShowQuickTranslatePopup(resultText, regionCenterX / Scale, regionBottomY / Scale);
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"QuickTranslateRegion error: {ex}");
            }
            finally
            {
                _quickTranslateBusy = false;
            }
        }

        private void ShowQuickTranslatePopup(string text, double centerX, double bottomY)
        {
            CloseQuickTranslatePopup();

            var textBlock = new OutlinedTextBlock
            {
                Text = text,
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxTextWidth = 500,
                StrokeBrush = System.Windows.Media.Brushes.Black,
                StrokeThickness = 2
            };

            var border = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xB0, 0, 0, 0)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 8, 16, 12),
                Child = textBlock
            };

            _quickTranslatePopup = new System.Windows.Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = border
            };

            // Position below the selected region, centered
            _quickTranslatePopup.Loaded += (s, e) =>
            {
                _quickTranslatePopup.Left = centerX - _quickTranslatePopup.ActualWidth / 2;
                _quickTranslatePopup.Top = bottomY + 8;
            };

            // Click anywhere on popup to dismiss immediately
            _quickTranslatePopup.MouseDown += (s, e) => CloseQuickTranslatePopup();

            _quickTranslatePopup.Show();
            // Exclude quick-translate popup from screen capture
            var qtHandle = new WindowInteropHelper(_quickTranslatePopup).Handle;
            TryExcludeFromCapture(qtHandle);

            // Auto-close after 4 seconds with fade-out
            _quickTranslateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _quickTranslateTimer.Tick += (s, e) =>
            {
                _quickTranslateTimer.Stop();
                if (_quickTranslatePopup != null)
                {
                    var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500));
                    fadeOut.Completed += (s2, e2) => CloseQuickTranslatePopup();
                    _quickTranslatePopup.BeginAnimation(System.Windows.Window.OpacityProperty, fadeOut);
                }
            };
            _quickTranslateTimer.Start();
        }

        private void CloseQuickTranslatePopup()
        {
            _quickTranslateTimer?.Stop();
            _quickTranslateTimer = null;
            if (_quickTranslatePopup != null)
            {
                try { _quickTranslatePopup.Close(); } catch { }
                _quickTranslatePopup = null;
            }
        }

        /// <summary>
        /// Async trigger OCR: execute the time-consuming OCR and hash matching logic in the background thread, only call when the subtitle pixel changes significantly.
        /// </summary>
        /// <param name="frameToProcess">Image Mat for OCR (caller has already Clone)</param>
        /// <param name="target">Original screenshot Bitmap, used for debugging and setting preview image</param>
        /// <param name="answerTarget">Optional answer region bitmap (null if no answer region configured)</param>
        private async void TriggerOcrAsync(Mat frameToProcess, Bitmap target, Bitmap answerTarget)
        {
            _isOcrRunning = true;
            var cts = new CancellationTokenSource(5000);
            try
            {
                await Task.Run(() =>
                {
                    cts.Token.ThrowIfCancellationRequested();
                    try
                    {
                        if (frameToProcess == null || frameToProcess.Empty())
                        {
                            return;
                        }

                        string bitStr = ImageProcessor.ComputeRobustHash(frameToProcess);

                        if (BitmapDict.TryGetValue(bitStr, out string cachedOcrText))
                        {
                            ocrText = cachedOcrText;
                            _detectedNpcName = NpcNameCache.TryGetValue(bitStr, out string cachedNpc) ? cachedNpc : "";
                        }
                        else
                        {
                            // Fuzzy Hamming-distance match (FindSimilarImageHash) removed 2026-04-18.
                            // It caused cross-dialog ghost subtitles: merchant frames from one
                            // conversation kept fuzzy-matching visually-similar frames long after
                            // the conversation ended, and every fuzzy hit touched the LRU entry
                            // which refreshed its position — a positive feedback loop that kept
                            // the stale entry alive indefinitely. The "4+ stable frames" gate
                            // before OCR already guarantees bit-identical frames for the exact-
                            // hash path above, so the fuzzy fallback was pure downside.
                            OCRResult ocrResult = data.engine.DetectTextFromMat(frameToProcess);

                            // Color-based NPC name detection with position preservation
                            if (ocrResult.TextBlocks != null && ocrResult.TextBlocks.Count > 0)
                            {
                                var detectedResult = ImageProcessor.ClassifyTextBlocksWithPositions(
                                    frameToProcess, ocrResult.TextBlocks);

                                _detectedNpcName = detectedResult.NpcName;
                                ocrText = detectedResult.DialogueText;
                                _lastDetectedText = detectedResult;

                                Logger.Log.Debug($"Color classification: NPC=\"{_detectedNpcName}\", " +
                                    $"Dialogue=\"{ocrText}\", DialogueBlocks={detectedResult.DialogueBlocks.Count}");
                            }
                            else
                            {
                                _detectedNpcName = "";
                                ocrText = ocrResult.Text;
                                _lastDetectedText = null;
                            }

                            if (debug)
                            {
                                try
                                {
                                    string fileName = DateTime.Now.ToString("yyyy-MM-dd_HH_mm_ss_ffffff") + ".png";
                                    Logger.Log.Debug(fileName);
                                    target.Save(Path.Combine(dataDir, fileName));
                                    Logger.Log.Debug($"OCR Text: {ocrText}");
                                }
                                catch (Exception ex)
                                {
                                    Logger.Log.Error($"Failed to save debug image: {ex}");
                                }
                            }

                            BitmapDict[bitStr] = ocrText;
                            if (!string.IsNullOrEmpty(_detectedNpcName))
                                NpcNameCache[bitStr] = _detectedNpcName;
                        }

                        Logger.Log.Debug($"OCR Content: {ocrText}");

                        if (ocrText.Length < 2)
                        {
                            Interlocked.Increment(ref failedCount);

                            // Drain stale OCR/NPC caches once we've seen the dialog region
                            // stay empty for a few frames in a row — that's the "dialog
                            // ended" signal. Keeps the fuzzy-match fast path from
                            // resurrecting the previous dialog's text on unrelated frames.
                            int empties = Interlocked.Increment(ref _consecutiveEmptyOcrFrames);
                            if (empties == EmptyOcrCacheClearThreshold)
                            {
                                BitmapDict.Clear();
                                NpcNameCache.Clear();
                                Logger.Log.Debug(
                                    $"OCR caches cleared after {EmptyOcrCacheClearThreshold} empty frames " +
                                    "(dialog ended — prevents stale fuzzy-hash matches).");
                            }
                        }
                        else
                        {
                            Interlocked.Exchange(ref _consecutiveEmptyOcrFrames, 0);
                        }

                        // --- Answer Region OCR (validates/supplements graph predictions) ---
                        // Skip answer OCR when dialogue text is game UI (menus, stats, etc.)
                        if (answerTarget != null && ocrText.Length > 1 && !IsLikelyGameUI(ocrText))
                        {
                            try
                            {
                                using (var answerMat = answerTarget.ToMat())
                                {
                                    var answerOcr = data.engine.DetectTextFromMat(answerMat);
                                    if (answerOcr?.TextBlocks != null && answerOcr.TextBlocks.Count > 0)
                                    {
                                        var answerLines = MergeAnswerBlocks(answerOcr.TextBlocks);
                                        if (answerLines.Length > 0)
                                        {
                                            var ocrTranslated = _answerService.TranslateAnswers(
                                                answerLines, data.contentDict, data.Matcher, data.ContextEngine);
                                            // Only override graph predictions if OCR produced
                                            // better results (more matches or fewer ~unmatched~)
                                            if (ocrTranslated.Length > 0)
                                            {
                                                _translatedAnswers = ocrTranslated;
                                            }
                                            // else: keep existing predictions from graph
                                        }
                                        Logger.Log.Debug($"Answer OCR: {answerLines.Length} merged lines, translated: {_translatedAnswers?.Length ?? 0}");
                                    }
                                    // else: OCR found nothing — keep graph predictions if any
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log.Error($"Answer OCR failed: {ex.Message}");
                                // Keep existing predictions on error
                            }
                            _lastAnswerOcrTime = DateTime.UtcNow;
                        }
                        else if (ocrText.Length <= 1)
                        {
                            // No dialogue detected — clear answers and predictions
                            _translatedAnswers = null;
                            _predictedContent = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log.Error(ex);
                    }
                }, cts.Token);

                // After OCR, update the window position, preview, and force immediate translation
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        UpdateWindowPosition();

                        // SetImage keeps a long-lived reference (for the Settings → test-OCR
                        // fallback path); when it's visible we hand ownership to SetImage and
                        // do NOT return to pool. Otherwise we recycle the Bitmap immediately.
                        if (data.IsVisible)
                        {
                            data.SetImage(target);
                        }
                        else
                        {
                            ReleaseCapturedBitmap(target);
                        }

                        // Force immediate translation instead of waiting for next UI timer tick (saves 0-200ms)
                        UpdateText(null, null);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log.Error(ex);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                Logger.Log.Warn("OCR timed out after 5 seconds");
            }
            finally
            {
                _isOcrRunning = false;
                frameToProcess?.Dispose();
                // answerTarget is pool-rented (see CaptureRegionFromBackend) — Return instead
                // of Dispose so the answer bucket is reused next tick.
                ReleaseCapturedBitmap(answerTarget);
                cts.Dispose();
            }
        }

        /// <summary>
        /// Merge OCR text blocks into logical answer lines by spatial proximity.
        /// Blocks within the same speech bubble (small vertical gap) are joined.
        /// </summary>
        private string[] MergeAnswerBlocks(List<PaddleOCRSharp.TextBlock> blocks)
        {
            if (blocks == null || blocks.Count == 0)
                return Array.Empty<string>();

            // Compute center Y and height for each block
            var items = blocks
                .Where(b => !string.IsNullOrWhiteSpace(b.Text) && b.BoxPoints != null && b.BoxPoints.Length == 4)
                .Select(b =>
                {
                    float minY = Math.Min(Math.Min(b.BoxPoints[0].Y, b.BoxPoints[1].Y),
                                          Math.Min(b.BoxPoints[2].Y, b.BoxPoints[3].Y));
                    float maxY = Math.Max(Math.Max(b.BoxPoints[0].Y, b.BoxPoints[1].Y),
                                          Math.Max(b.BoxPoints[2].Y, b.BoxPoints[3].Y));
                    return new { Text = b.Text.Trim(), Top = minY, Bottom = maxY, Height = maxY - minY };
                })
                .Where(x => x.Text.Length > 0)
                .OrderBy(x => x.Top)
                .ToList();

            if (items.Count == 0)
                return Array.Empty<string>();

            // Average line height — used to determine merge threshold.
            // Only merge lines within the SAME speech bubble (very tight gap).
            // Genshin answer bubbles have ~1x line height gap between them,
            // while wrapped text within a bubble has ~0.2x gap.
            float avgHeight = items.Average(x => x.Height);
            float mergeThreshold = avgHeight * 0.35f;

            var merged = new List<string>();
            string current = items[0].Text;
            float currentBottom = items[0].Bottom;

            for (int i = 1; i < items.Count; i++)
            {
                float gap = items[i].Top - currentBottom;
                if (gap < mergeThreshold)
                {
                    // Same answer — merge with space
                    current += " " + items[i].Text;
                }
                else
                {
                    // New answer
                    merged.Add(current);
                    current = items[i].Text;
                }
                currentBottom = items[i].Bottom;
            }
            merged.Add(current);

            return merged.Where(l => l.Length > 1).ToArray();
        }

        /// <summary>
        /// Run answer-only OCR independently of dialogue OCR.
        /// Called when answer region content changes while dialogue is stable.
        /// </summary>
        private async void RunAnswerOnlyOcrAsync(Bitmap answerBitmap)
        {
            if (_isAnswerOcrRunning) return;
            _isAnswerOcrRunning = true;
            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        using (var answerMat = answerBitmap.ToMat())
                        {
                            var answerOcr = data.engine.DetectTextFromMat(answerMat);
                            if (answerOcr?.TextBlocks != null && answerOcr.TextBlocks.Count > 0)
                            {
                                var answerLines = MergeAnswerBlocks(answerOcr.TextBlocks);
                                if (answerLines.Length > 0)
                                {
                                    var ocrTranslated = _answerService.TranslateAnswers(
                                        answerLines, data.contentDict, data.Matcher, data.ContextEngine);
                                    if (ocrTranslated.Length > 0)
                                        _translatedAnswers = ocrTranslated;
                                }
                                Logger.Log.Debug($"Answer-only OCR: {answerLines.Length} lines, translated: {_translatedAnswers?.Length ?? 0}");
                            }
                            // else: keep graph predictions if any
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log.Error($"Answer-only OCR failed: {ex.Message}");
                    }
                });
            }
            finally
            {
                _isAnswerOcrRunning = false;
                _lastAnswerOcrTime = DateTime.UtcNow;
                // answerBitmap comes from the pool-rented capture path — Return to pool.
                ReleaseCapturedBitmap(answerBitmap);
            }
        }

        /// <summary>
        /// Preprocess the subtitle region image to binary image (only retain high-light/white pixels), used for stable pixel difference detection.
        /// </summary>
        /// <param name="src">Original Mat (BGR)</param>
        /// <returns>Binary Mat; if failed, return null</returns>
        private Mat PreprocessToBinary(Mat src)
        {
            if (src == null || src.Empty())
            {
                return null;
            }

            // `binary` is handed back to the caller (cloned then disposed every tick) —
            // leave as a plain allocation. `gray` is throwaway, so it goes through the pool.
            Mat gray = MatPool.Default.RentBlank();
            Mat binary = new Mat();
            try
            {
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.Threshold(gray, binary, 220, 255, ThresholdTypes.Binary);
                return binary;
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"PreprocessToBinary failed: {ex}");
                binary?.Dispose();
                return null;
            }
            finally
            {
                MatPool.Default.Return(gray);
            }
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }
            _isUserDragging = true;
            bool wasInDragFixMode = _inRuntimeOverlapDragMode;
            DragMove(); // blocking until mouse released
            _isUserDragging = false;

            // If the user just dragged the subtitle box out of the runtime
            // overlap state, persist their new vertical offset as the Pad
            // so we don't reset them back on the next layout tick, then
            // re-evaluate overlap. If clear now, translation resumes; if
            // still inside the region, the red treatment + alert stay up.
            if (wasInDragFixMode)
            {
                PersistManualPadAfterDrag();
                CheckRuntimeOverlapAndApply();
            }
        }

        /// <summary>
        /// After the user hand-drags the subtitle box (runtime drag-fix
        /// mode), compute a new <c>Pad</c> (vertical offset relative to
        /// the dialogue area) from the window's current Top so the next
        /// auto-layout tick doesn't snap it back. Horizontal offset is
        /// similarly preserved.
        /// </summary>
        private void PersistManualPadAfterDrag()
        {
            try
            {
                if (notify?.Region == null || notify.Region.Length < 4) return;
                if (!int.TryParse(notify.Region[0], out int regionX)) return;
                if (!int.TryParse(notify.Region[1], out int regionY)) return;
                if (!int.TryParse(notify.Region[2], out int regionW)) return;

                // Pad is stored in logical (DIP) units — the same space
                // this.Left / this.Top live in. Formula mirrors what
                // UpdateWindowPosition used to APPLY a pad: the final
                // Top = regionY/scale + Pad, so Pad = Top - regionY/scale.
                double scale = Scale > 0 ? Scale : 1.0;
                int newPadV = (int)Math.Round(this.Top - regionY / scale);

                // Horizontal pad: overlay is centred on screen + PadHorizontal.
                // Derive from where the user dropped the box.
                Screen targetScreen = null;
                foreach (var screen in Screen.AllScreens)
                {
                    if (screen.Bounds.Contains(new System.Drawing.Point(regionX, regionY)))
                    {
                        targetScreen = screen;
                        break;
                    }
                }
                if (targetScreen == null) targetScreen = Screen.PrimaryScreen;
                double screenScale = GetScaleForScreen(targetScreen);
                double expectedLeft = targetScreen.Bounds.Left / screenScale
                                      + (targetScreen.Bounds.Width / screenScale - (regionW / screenScale + 200)) / 2;
                int newPadH = (int)Math.Round(this.Left - expectedLeft);

                Config.Set("Pad", new int[] { newPadV, newPadH });
                Logger.Log.Info($"Drag-fix: saved new Pad = [{newPadV},{newPadH}] (top={this.Top}, left={this.Left}).");
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"PersistManualPadAfterDrag: {ex.Message}");
            }
        }


        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Stop the OCR pump BEFORE disposing the engine so no new
            // DetectTextFromMat call kicks off during teardown. The engine's
            // gate serialises Dispose vs. in-flight Run, but halting fresh
            // ticks keeps shutdown snappy — we only wait on the current Run.
            try { OCRTimer?.Stop(); } catch { }
            try { UITimer?.Stop(); } catch { }

            notifyIcon.Dispose();
            notifyIcon = null;
            data.UnregisterAllHotkeys();
            data.RealClose();

            // Drain pooled native buffers before the process exits so Mat finalizers
            // don't race with OpenCvSharp's native unloader on shutdown.
            try { BitmapPool.Default.Dispose(); } catch (Exception ex) { Logger.Log.Warn($"BitmapPool dispose failed: {ex.Message}"); }
            try { MatPool.Default.Dispose(); } catch (Exception ex) { Logger.Log.Warn($"MatPool dispose failed: {ex.Message}"); }
        }

        private void MainWindow_LocationChanged(object sender, EventArgs e)
        {
            if (!_isUserDragging) return; // only save on user drag, not programmatic moves

            try
            {
                int pad = Convert.ToInt16(this.Top - Convert.ToInt16(notify.Region[1]) / Scale);

                // Compute horizontal offset relative to where UpdateWindowPosition would place us
                int padHorizontal = 0;
                foreach (var screen in Screen.AllScreens)
                {
                    if (screen.WorkingArea.Contains(
                        new System.Drawing.Point(
                            Convert.ToInt16(notify.Region[0]),
                            Convert.ToInt16(notify.Region[1]))))
                    {
                        double scale = GetScaleForScreen(screen);
                        double left = screen.Bounds.Left / scale;
                        double width = Convert.ToInt16(notify.Region[2]) / scale + 200;
                        double expectedLeft = left + (screen.Bounds.Width / scale - width) / 2;
                        padHorizontal = (int)(this.Left - expectedLeft);
                        break;
                    }
                }

                Config.Set("Pad", new int[] { pad, padHorizontal });
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"LocationChanged pad save failed: {ex.Message}");
            }
        }


        public void SwitchIcon(string iconName)
        {
            Uri iconUri = new Uri($"pack://application:,,,/Resources/{iconName}");
            Stream iconStream = System.Windows.Application.GetResourceStream(iconUri).Stream;

            // Create a new Icon object
            Icon newIcon = new Icon(iconStream);

            // Update the NotifyIcon's icon
            notifyIcon.Icon = newIcon;
        }

        // Handle window messages
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY)
            {
                if (wParam.ToInt32() == HOTKEY_ID_1)
                {
                    if (OCRTimer.IsEnabled)
                    {
                        OCRTimer.Stop();
                        UITimer.Stop();
                        while (_stabilityBuffer.Count > 0)
                            _stabilityBuffer.Dequeue().Dispose();
                        ResetActiveOcrWindow();
                        ClearReadyPlaceholderIfActive();
                        _overlapOverrideAccepted = false;
                        SystemSounds.Hand.Play();
                        SwitchIcon("kaption.ico");
                    }
                    else if (TryGateOcrStart()
                          && TryGateInitialDictionarySync()
                          && TryGateEngineReady()
                          && TryGateRegionConfigured()
                          && TryGateGameRunning()
                          && TryGateFullscreenTip()
                          && TryGateOverlayNotInRegion())
                    {
                        UpdateWindowPosition();
                        ShowReadyPlaceholderIfEmpty();
                        ResetActiveOcrWindow();
                        OCRTimer.Start();
                        UITimer.Start();
                        SystemSounds.Exclamation.Play();
                        SwitchIcon("kaption-running.ico");
                    }
                    data.UpdateDashboardStatus();
                    handled = true;
                }
                else if (wParam.ToInt32() == HOTKEY_ID_2)
                {
                    if (!ChooseRegion)
                    {
                        ChooseRegion = true;
                        notify.ChooseRegion();
                        ChooseRegion = false;
                        data.UpdateDashboardRegionInfo();
                        OnCaptureRegionUserChange(dialogOwner: data?.IsVisible == true ? (System.Windows.Window)data : this);
                    }
                }
                else if (wParam.ToInt32() == HOTKEY_ID_3)
                {
                    ShowText = !ShowText;
                    SubtitleText.Visibility = ShowText ? Visibility.Visible : Visibility.Collapsed;
                    HeaderText.Visibility = ShowText ? Visibility.Visible : Visibility.Collapsed;
                    if (ShowText)
                    {
                        SystemSounds.Hand.Play();
                    }
                    else
                    {
                        SystemSounds.Exclamation.Play();
                    }
                    data.UpdateDashboardStatus();
                }
                else if (wParam.ToInt32() == HOTKEY_ID_4)
                {
                    // Ctrl+Shift+D was historically two features in one — it
                    // showed the region overlay AND toggled click-through as
                    // a side effect. Users who just wanted to peek at the
                    // region ended up with a subtitle box they couldn't
                    // drag anymore and no way to know why. Split so this
                    // hotkey only does the diagnostic (show region); the
                    // subtitle box stays draggable by default.
                    notify.ShowRegionOverlay();
                    SystemSounds.Hand.Play();
                    handled = true;
                }
                else if (wParam.ToInt32() == HOTKEY_ID_5)
                {
                    // Force-OCR-now (backtick). Same license gate as the
                    // OCR-start paths — no OCR without a live session.
                    if (TryGateOcrStart())
                        ForceOcrTranslate();
                    handled = true;
                }
                else if (wParam.ToInt32() == HOTKEY_ID_6)
                {
                    // Quick-translate popup (Ctrl+Q). One-shot OCR → same gate.
                    // Discard the Task: the handler owns its own single-flight
                    // guard and exceptions are logged inside.
                    if (TryGateOcrStart())
                        _ = QuickTranslateRegion();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }



        // Voice-playback feature (PlayAudio/PlayAudioFromUrl/StopAudio) was removed —
        // it downloaded audio from mp3.2langs.com (a Chinese-project CDN) and used NAudio
        // for output. The feature is not applicable to the EN→PL translation use case.

        public static double GetScaleForScreen(Screen screen)
        {
            // Get the center point of the screen's working area
            System.Drawing.Point screenCenter = new System.Drawing.Point(
                screen.Bounds.Left + screen.Bounds.Width / 2,
                screen.Bounds.Top + screen.Bounds.Height / 2
            );

            // Get the screen handle
            IntPtr monitorHandle = NativeMethods.MonitorFromPoint(screenCenter, 2); // MONITOR_DEFAULTTONEAREST

            // Get DPI value
            uint dpiX, dpiY;
            NativeMethods.GetDpiForMonitor(monitorHandle, NativeMethods.MonitorDpiType.EffectiveDpi, out dpiX, out dpiY);

            // Calculate scale factor (base DPI is 96)
            return dpiX / 96.0;
        }


        // ───────────────────────────────────────────────────────────────────
        //  Auto-updater (SHA-256 verified, non-blocking)
        //  Replaces the legacy Config("Update") JSON manifest flow. All the
        //  heavy lifting lives in Services/Update/UpdateService.cs — this is
        //  just the UI hookup.
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Kick off a background paid-dictionary sync ~10s after MainWindow is
        /// loaded. Non-blocking, single-shot. Pulls anything new from the
        /// backend and re-encrypts machine-bound for the local cache.
        /// Cancelled on window close.
        /// </summary>
        private void StartBackgroundDictionarySync()
        {
            _dictionarySyncCts?.Cancel();
            _dictionarySyncCts = new CancellationTokenSource();
            var ct = _dictionarySyncCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    // App.OnStartup already kicks off the primary sync the moment
                    // activation completes — running in parallel with MainWindow
                    // construction + the OCR engine load. If that task is still
                    // in flight we wait on it here instead of starting a duplicate
                    // (which would race on the manifest file and waste bandwidth).
                    // When it's already done we proceed directly to the post-sync
                    // inventory/upstream checks below.
                    var primary = App.InitialSyncTask;
                    if (primary != null)
                    {
                        try { await primary.ConfigureAwait(false); }
                        catch { /* App.KickOffInitialDictionarySync already logged */ }
                    }
                    else
                    {
                        // Fallback path: no primary sync ran (LicenseService was
                        // missing at App.OnStartup, or the session became valid
                        // mid-session after a re-login). 1s settle is enough to
                        // let MainWindow finish laying out before we hit the
                        // network — the old 10s delay was overkill now that the
                        // primary sync doesn't race with this one.
                        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

                        var license = App.LicenseService;
                        if (license == null)
                        {
                            Logger.Log.Info("DictionarySync: skipped — no LicenseService yet.");
                            return;
                        }

                        var sync = new GI_Subtitles.Services.Translation.DictionarySyncService(
                            new GI_Subtitles.Services.Network.KaptionApiClient(),
                            license,
                            GI_Subtitles.Services.Security.FileProtectionFactory.Create());

                        var result = await sync.SyncAsync(Game, OutputLanguage, ct).ConfigureAwait(false);
                        Logger.Log.Info(
                            $"DictionarySync (fallback): done — downloaded={result.Downloaded} " +
                            $"upToDate={result.UpToDate} skipped={result.Skipped} failed={result.Failed}");
                        foreach (var msg in result.Messages)
                            Logger.Log.Info($"DictionarySync: {msg}");
                    }

                    // After sync settles, check whether the configured target
                    // language actually has anything the OCR pipeline can
                    // translate with. Worth one extra inventory scan: catches
                    // the case where a fresh install has no local cache AND
                    // the backend doesn't have a pack for this tier yet.
                    // Language-agnostic — when we add Korean/Arabic/etc. this
                    // same path fires for them.
                    await CheckTargetLanguageAvailabilityAsync(ct).ConfigureAwait(false);

                    // Finally: check upstream game-data (DimbreathBot for
                    // Genshin, Dimbreath's GitLab for HSR) for a newer
                    // TextMap<Input>/<Output>.json. Conditional-GET-driven
                    // so a repeat launch is cheap. When something actually
                    // updates, prompt the user to restart — the matcher
                    // caches were blown away inside the service and the
                    // rebuild on next launch picks up the fresh plaintext.
                    await CheckUpstreamGameDataAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Window closing — benign.
                }
                catch (Exception ex)
                {
                    // Top-level safety net. The service catches network errors
                    // internally; only logic errors reach here.
                    Logger.Log.Warn($"DictionarySync: unexpected failure: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Surface a modal warning when the user's configured target language
        /// has no pack available — not locally, not from the backend for their
        /// tier. Designed to be language-agnostic so future non-English target
        /// languages (Korean, Arabic, etc.) reuse this same gate without code
        /// changes — the message pulls the display name from the inventory
        /// service's language table.
        ///
        /// Fires exactly once per process lifetime (sentinel flag on
        /// <see cref="_missingPackWarned"/>), after the DictionarySync
        /// background task has had a chance to pull anything new. Silent when
        /// the pack is already present, when a tier-accessible remote pack
        /// exists (sync will grab it), or when DictionarySync hasn't run yet.
        /// </summary>
        private async Task CheckTargetLanguageAvailabilityAsync(CancellationToken ct)
        {
            if (_missingPackWarned) return;

            try
            {
                var inventory = new GI_Subtitles.Services.Translation.DictionaryInventoryService(
                    new GI_Subtitles.Services.Network.KaptionApiClient(),
                    App.LicenseService);
                var scan = await inventory.ScanAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;

                bool isSignedIn = App.LicenseService?.CurrentActivation != null;

                string message = GI_Subtitles.Services.Translation.DictionaryInventoryService.ExplainMissingPack(
                    scan, Game, OutputLanguage, isSignedIn, scan.RemoteQueryOk);

                if (string.IsNullOrEmpty(message)) return; // pack present or on the way

                // Marshal to UI so the ModernDialog opens on the dispatcher
                // and centres over MainWindow (falls back to top-of-screen
                // when no owner is known).
                _missingPackWarned = true;
                await Dispatcher.InvokeAsync(() =>
                {
                    // Fully-qualify Application — MainWindow's using block
                    // pulls in System.Windows.Forms, so a bare `Application`
                    // reference is ambiguous in this file.
                    var wpfApp = System.Windows.Application.Current;
                    GI_Subtitles.Views.ModernDialog.Warn(
                        owner: wpfApp?.MainWindow == this ? this : null,
                        title: LocalizedString("Dialog_PackMissing_Title", "Translation pack missing"),
                        body: message,
                        details: LocalizedString("Dialog_PackMissing_Details", "Subtitles will show the raw English text until a pack is available. You can change the target language in Settings \u203A Dashboard."));
                });
            }
            catch (OperationCanceledException) { /* window closing */ }
            catch (Exception ex)
            {
                Logger.Log.Warn($"CheckTargetLanguageAvailability failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sentinel — we only want to nag the user once per process. If they
        /// ignore the dialog and nothing changes, repeating it on every sync
        /// pass would be a hostile UX. Reset would happen on an explicit
        /// "retry" button if we ever wire one up.
        /// </summary>
        private bool _missingPackWarned;

        /// <summary>
        /// Session-scoped bypass for the "game isn't running" gate. Set to
        /// true after the user explicitly clicks "Continue anyway" on the
        /// Dialog_GameNotFound confirm (e.g. when playing via GeForce Now or
        /// another cloud-gaming service where the game process is not local).
        /// Resets on app restart — never persisted — so a one-time dev test
        /// or a session on a friend's PC doesn't permanently disarm the gate.
        /// </summary>
        private bool _gameGateBypassedThisSession;

        /// <summary>
        /// Session-scoped bypass for the overlay-overlap gate. True after the
        /// user explicitly accepted "Continue anyway" on the
        /// Dialog_OverlayInRegion confirm. Cleared automatically whenever the
        /// user edits the capture region OR the overlay size/position — those
        /// edits can make the situation either better or worse, so we
        /// revalidate fresh.
        /// </summary>
        private bool _overlapOverrideAccepted;

        /// <summary>
        /// Persistent red alert shown while the runtime overlap guard is
        /// active. Built lazily on first overlap and reused across flip-
        /// flops (show/hide — the window itself stays allocated until app
        /// shutdown).
        /// </summary>
        private OverlapAlertOverlay _overlapAlertOverlay;

        /// <summary>
        /// True while the runtime guard has the subtitle box in "drag me
        /// out of the way" mode: overlay stays visible but is wrapped in
        /// a red border and translation updates are paused so the user
        /// can grab it and move it to a safe position. Cleared when
        /// overlap resolves (either by the user dragging the box or by
        /// them redrawing the capture region).
        /// </summary>
        private bool _inRuntimeOverlapDragMode;

        /// <summary>
        /// Saved background brush of <c>SubtitleBackground</c> from before
        /// the runtime guard swapped it to the red "drag me" treatment.
        /// Null when the overlay is in its normal state.
        /// </summary>
        private System.Windows.Media.Brush _subtitleBgBeforeOverlap;

        /// <summary>
        /// Saved SubtitleText content from before the drag-fix overlay
        /// replaced it with the drag hint. Restored on exit. Null when
        /// the overlay is in its normal state.
        /// </summary>
        private string _subtitleTextBeforeOverlap;
        private string _headerTextBeforeOverlap;
        private System.Windows.Visibility _headerVisibilityBeforeOverlap;
        private System.Windows.Visibility _subtitleTextVisibilityBeforeOverlap;
        private System.Windows.Visibility _subtitleBgVisibilityBeforeOverlap;
        private double _opacityBeforeOverlap = 1.0;
        private double _subtitleMinWidthBeforeOverlap;
        private double _subtitleMinHeightBeforeOverlap;

        /// <summary>
        /// Ask upstream (public game-data mirrors) whether the TextMap
        /// JSON files on disk are stale. If any were refreshed, the
        /// service already deleted the stale matcher caches; we just
        /// prompt the user to restart so VoiceContentHelper rebuilds
        /// cleanly from the new plaintext.
        ///
        /// Runs once per process (see <see cref="_gameDataUpdateChecked"/>),
        /// non-blocking, never throws. Throttled internally so repeat
        /// launches don't hit GitHub/GitLab more than once every 6 hours.
        /// </summary>
        private async Task CheckUpstreamGameDataAsync(CancellationToken ct)
        {
            if (_gameDataUpdateChecked) return;
            _gameDataUpdateChecked = true;

            try
            {
                var updater = new GI_Subtitles.Services.Translation.GameDataUpdateService();
                var result = await updater.CheckAndUpdateAsync(Game, InputLanguage, OutputLanguage, ct)
                                          .ConfigureAwait(false);

                if (!result.AnyUpdated) return;

                // One refreshed file is enough to make the merged matcher
                // cache stale. We pop the restart prompt once per check,
                // regardless of how many langs refreshed. Uses the generic
                // AppRestartPrompt so the user gets a one-click "Restart now"
                // button instead of an info dialog that leaves them guessing
                // how to apply the update.
                string body = BuildUpdateDialogBody(result);
                await Dispatcher.InvokeAsync(() =>
                {
                    var wpfApp = System.Windows.Application.Current;
                    var owner = wpfApp?.MainWindow == this ? this : null;
                    GI_Subtitles.Views.AppRestartPrompt.PromptAndRestart(
                        owner: owner,
                        title: LocalizedString("Dialog_GameDataUpdated_Title", "Game data updated"),
                        body: LocalizedString("Dialog_GameDataUpdated_Body", "Kaption pulled a newer version of the public game data. Restart Kaption to rebuild the translation index with the new text."),
                        details: body,
                        severity: GI_Subtitles.Views.DialogSeverity.Info);
                });
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                Logger.Log.Warn($"CheckUpstreamGameDataAsync failed: {ex.Message}");
            }
        }

        private static string BuildUpdateDialogBody(
            GI_Subtitles.Services.Translation.GameDataUpdateResult result)
        {
            var lines = new System.Text.StringBuilder();
            foreach (var l in result.Languages)
            {
                if (l.Outcome != GI_Subtitles.Services.Translation.GameDataUpdateOutcome.Updated)
                    continue;
                lines.AppendLine($"• {l.Game}/{l.Language} refreshed");
            }
            if (lines.Length == 0) return string.Empty;
            return lines.ToString().TrimEnd();
        }

        /// <summary>
        /// Sentinel — the upstream-check is idempotent inside
        /// <see cref="GI_Subtitles.Services.Translation.GameDataUpdateService"/>
        /// (throttle + conditional GET), but we also gate it per-process to
        /// avoid showing the restart dialog twice if somebody wires it to
        /// another trigger later.
        /// </summary>
        private bool _gameDataUpdateChecked;

        /// <summary>
        /// How often the background update check fires after the initial
        /// startup probe. 6h strikes the balance: fast enough that a user who
        /// keeps the app running all day catches a release published during
        /// their session, slow enough that we aren't hammering the feed.
        /// </summary>
        private const double UpdateCheckIntervalHours = 6.0;

        /// <summary>
        /// Kick off an initial update check 5 s after MainWindow loads, then
        /// re-check every <see cref="UpdateCheckIntervalHours"/>. Each run is
        /// protected by <see cref="_updateCheckBusy"/> so a long-running probe
        /// can't overlap with the next tick. Cancelled on window close.
        /// </summary>
        private void StartBackgroundUpdateCheck()
        {
            _updateCheckCts?.Cancel();
            _updateCheckCts = new CancellationTokenSource();
            var ct = _updateCheckCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    // 5 s settle so we don't compete with engine loading,
                    // dictionary downloads, and first-paint work.
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    await RunSingleUpdateProbeAsync(ct).ConfigureAwait(false);

                    // Then re-probe every N hours until cancellation.
                    var interval = TimeSpan.FromHours(UpdateCheckIntervalHours);
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(interval, ct).ConfigureAwait(false);
                        await RunSingleUpdateProbeAsync(ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Window is closing — benign.
                }
                catch (Exception ex)
                {
                    // CheckAsync already swallows network errors; anything that
                    // reaches here is genuinely unexpected. Log, don't surface.
                    Logger.Log.Warn($"Background update check failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Single update probe — shared by the startup check and the periodic
        /// re-check. Guarded by a volatile busy flag so a slow network call
        /// on one tick can't stack against the next.
        /// </summary>
        private volatile bool _updateCheckBusy;
        private async Task RunSingleUpdateProbeAsync(CancellationToken ct)
        {
            if (_updateCheckBusy) return;
            _updateCheckBusy = true;
            try
            {
                var result = await _updateService.CheckAsync(ct).ConfigureAwait(false);
                if (result == null || result.Status == GI_Subtitles.Services.Update.UpdateStatus.NoUpdate)
                    return;

                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                {
                    // BeginInvoke (not Invoke): HandleUpdateCheckResult only
                    // mutates local state and pops UI — nothing the caller
                    // needs to wait on. Invoke blocks this Task on the UI
                    // dispatcher which is fine 99% of the time but becomes
                    // a cross-thread freeze when UI is saturated (OCR tick,
                    // file dialog, etc.). Fire-and-forget is safer.
                    try { _ = Dispatcher.BeginInvoke(new Action(() => HandleUpdateCheckResult(result))); }
                    catch (System.ComponentModel.Win32Exception) { /* window gone */ }
                    catch (TaskCanceledException) { /* dispatcher shutting down */ }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Update probe failed: {ex.Message}");
            }
            finally
            {
                _updateCheckBusy = false;
            }
        }

        private void HandleUpdateCheckResult(GI_Subtitles.Services.Update.UpdateCheckResult result)
        {
            _pendingUpdate = result;

            // Optional update. Honour the 24h skip contract. (Forced-update /
            // min-supported-version path was dropped in the Velopack migration —
            // see .plan/todo/VELOPACK-MIGRATION.md for the rationale.)
            if (!_updateService.ShouldNag(result))
            {
                Logger.Log.Info($"Update {result.Info.Version} is available but user skipped within 24h.");
                return;
            }

            // Same version as the last one we showed a banner for? Don't
            // re-notify (the banner is still visible inside the Settings
            // window). Otherwise we'd stack balloons every check interval.
            if (string.Equals(_lastNotifiedUpdateVersion, result.Info?.Version, StringComparison.Ordinal))
            {
                Logger.Log.Info($"Update {result.Info.Version} already surfaced — skipping re-notify.");
                return;
            }
            _lastNotifiedUpdateVersion = result.Info?.Version;

            // Primary surface: system-tray balloon (visible even when the
            // Settings window is closed — the user can't miss it). Click opens
            // Settings so they can see the banner + install buttons.
            string title = TryFindResource("Update_Balloon_Title") as string ?? "Kaption update available";
            string bodyFormat = TryFindResource("Update_Balloon_Body_Format") as string
                                ?? "Version {0} is ready to install. Click to open Settings.";
            string body = string.Format(bodyFormat, result.Info?.Version ?? "?");
            notify?.ShowBalloonTip(title, body, onClick: () =>
            {
                try
                {
                    if (data != null)
                    {
                        if (!data.IsVisible) data.Show();
                        data.Activate();
                    }
                }
                catch (Exception ex) { Logger.Log.Warn($"Balloon → Settings open failed: {ex.Message}"); }
            });

            // Secondary surface (persistent): the in-Settings banner. Stays
            // available so a user who missed the balloon still finds the
            // update + install buttons when they open Settings.
            if (data != null)
            {
                data.ShowUpdateBanner(
                    result.Info.Version,
                    result.Info.ReleaseNotesUrl,
                    // BeginUpdateInstall is async Task — discarded intentionally
                    // because the Install button is fire-and-forget from the UI's
                    // perspective. The method itself shuts the app down on success.
                    onInstall: () => { _ = BeginUpdateInstall(result); },
                    onWhatsNew: () => ShowReleaseNotesAsync(result),
                    onRemindLater: () =>
                    {
                        _updateService.RememberSkipped(result);
                        data.HideUpdateBanner();
                    });
            }
        }

        /// <summary>
        /// Last version we already surfaced a balloon for — prevents the
        /// periodic timer from re-firing the same notification every
        /// <see cref="UpdateCheckIntervalHours"/>. Reset (to null) on user
        /// "Remind me later" if the app keeps running long enough to hit
        /// the 24h skip expiry.
        /// </summary>
        private string _lastNotifiedUpdateVersion;

        /// <summary>
        /// Fetches release notes from <c>release_notes_url</c> and shows them in a
        /// simple modal. Falls back to a short stub on any failure so the user
        /// never sees a hung "Loading…" dialog.
        /// </summary>
        private async Task ShowReleaseNotesAsync(GI_Subtitles.Services.Update.UpdateCheckResult result)
        {
            string notes;
            if (string.IsNullOrWhiteSpace(result.Info.ReleaseNotesUrl))
            {
                notes = $"Version {result.Info.Version} is available.";
            }
            else
            {
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, result.Info.ReleaseNotesUrl))
                    using (var resp = await _sharedHttpClient.SendAsync(req).ConfigureAwait(true))
                    {
                        if (resp.IsSuccessStatusCode)
                        {
                            notes = await resp.Content.ReadAsStringAsync().ConfigureAwait(true);
                            if (string.IsNullOrWhiteSpace(notes))
                                notes = $"Version {result.Info.Version} is available.";
                        }
                        else
                        {
                            Logger.Log.Warn($"Release-notes fetch HTTP {(int)resp.StatusCode}.");
                            notes = $"Version {result.Info.Version} is available.\n\n(Release notes could not be loaded.)";
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.Warn($"Release-notes fetch failed: {ex.Message}");
                    notes = $"Version {result.Info.Version} is available.\n\n(Release notes could not be loaded.)";
                }
            }

            // Branded modal — the `details` argument puts the long release-notes
            // text in the subdued sub-paragraph slot so the top line stays
            // scannable. Full text scrolls inside the dialog's body area.
            GI_Subtitles.Views.ModernDialog.Info(
                owner: this,
                title: string.Format(
                    LocalizedString("Update_WhatsNew_Title_Format", "Kaption {0} — What's new"),
                    result.Info.Version),
                body: notes);
        }

        /// <summary>
        /// Download → apply → restart. UI-driven: shows a progress dialog while
        /// Velopack streams the delta (or full, on fallback), then calls
        /// ApplyUpdatesAndRestart which exits the app and relaunches it on the
        /// new version. We don't have to shut down ourselves — Velopack's
        /// updater waits for our process to die before it swaps files.
        /// </summary>
        private async Task BeginUpdateInstall(GI_Subtitles.Services.Update.UpdateCheckResult result)
        {
            if (result?.Info == null || result.VelopackInfo == null) return;

            var progressDialog = new System.Windows.Window
            {
                Title = string.Format(
                    LocalizedString("Update_Downloading_Title_Format", "Kaption — Downloading {0}"),
                    result.Info.Version),
                Width = 420, Height = 140,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                Owner = System.Windows.Application.Current.MainWindow,
                Topmost = true,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x2E)),
            };
            var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(16) };
            var label = new System.Windows.Controls.TextBlock
            {
                Text = LocalizedString("Update_Downloading", "Downloading update…"),
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 13,
                Margin = new System.Windows.Thickness(0, 0, 0, 10),
            };
            var bar = new System.Windows.Controls.ProgressBar
            {
                Minimum = 0, Maximum = 100, Height = 18, Value = 0,
            };
            panel.Children.Add(label);
            panel.Children.Add(bar);
            progressDialog.Content = panel;
            progressDialog.Show();

            var progress = new Progress<double>(p =>
            {
                bar.Value = p * 100;
                if (p >= 1.0) label.Text = LocalizedString("Update_Applying", "Applying…");
            });

            try
            {
                bool downloaded = await _updateService
                    .DownloadAsync(result, progress, CancellationToken.None)
                    .ConfigureAwait(true);

                if (!downloaded)
                {
                    progressDialog.Close();
                    GI_Subtitles.Views.ModernDialog.Error(
                        owner: this,
                        title: LocalizedString("Update_Failed_Title", "Update failed"),
                        body: LocalizedString("Dialog_UpdateDownloadFailed_Body", "The update couldn't be downloaded. Please check your internet connection and try again later."));
                    return;
                }

                bool restartQueued = _updateService.VerifyAndInstall(result);
                progressDialog.Close();

                if (!restartQueued)
                {
                    GI_Subtitles.Views.ModernDialog.Error(
                        owner: this,
                        title: LocalizedString("Update_Failed_Title", "Update failed"),
                        body: LocalizedString("Dialog_UpdateRestartFailed_Body", "The update downloaded successfully but the restart couldn't be queued. Please try again later."));
                    return;
                }

                // Velopack has spawned the updater and is waiting for us to
                // exit. Shut down cleanly so file-swap can proceed without
                // "file in use" errors — Velopack will re-launch Kaption once
                // the swap completes.
                Logger.Log.Info("Update apply+restart queued, shutting down.");
                System.Windows.Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Update install failed: {ex}");
                try { progressDialog.Close(); } catch { /* already closed */ }

                GI_Subtitles.Views.ModernDialog.Error(
                    owner: this,
                    title: LocalizedString("Update_Failed_Title", "Update failed"),
                    body: LocalizedString("Dialog_UpdateInstallFailed_Body", "We couldn't finish updating Kaption. Try again later; if the problem persists, please reinstall."),
                    technicalDetails: ex.ToString());
            }
        }
        /// <summary>
        /// Attempts to make a window invisible to screen capture APIs (Windows 10 2004+).
        /// Returns true if the API call succeeded.
        /// </summary>
        private static bool TryExcludeFromCapture(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            try
            {
                return SetWindowDisplayAffinity(hWnd, WDA_EXCLUDEFROMCAPTURE);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Enables or disables click-through mode on the overlay window.
        /// When enabled, mouse input passes through the window to whatever is beneath it.
        /// </summary>
        private void SetClickThrough(bool enable)
        {
            if (_mainWindowHandle == IntPtr.Zero)
                return;

            int exStyle = GetWindowLong(_mainWindowHandle, GWL_EXSTYLE);
            if (enable)
                exStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
            else
                exStyle &= ~WS_EX_TRANSPARENT;
            SetWindowLong(_mainWindowHandle, GWL_EXSTYLE, exStyle);
        }

        /// <summary>
        /// Temporarily disables click-through for a given number of seconds, then re-enables it.
        /// Called when the user wants to reposition the overlay via the drag handle.
        /// </summary>
        private void TemporarilyDisableClickThrough(int seconds = 8)
        {
            SetClickThrough(false);

            if (_clickThroughRestoreTimer == null)
            {
                _clickThroughRestoreTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(seconds)
                };
                _clickThroughRestoreTimer.Tick += (s, e) =>
                {
                    _clickThroughRestoreTimer.Stop();
                    SetClickThrough(true);
                };
            }
            else
            {
                _clickThroughRestoreTimer.Stop();
                _clickThroughRestoreTimer.Interval = TimeSpan.FromSeconds(seconds);
            }
            _clickThroughRestoreTimer.Start();
        }

        public class NativeMethods
        {
            public enum MonitorDpiType
            {
                EffectiveDpi = 0,
                AngularDpi = 1,
                RawDpi = 2
            }

            [DllImport("Shcore.dll")]
            public static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

            [DllImport("User32.dll")]
            public static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, uint flags);
        }
    }
}
