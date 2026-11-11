using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using GI_Subtitles.Common;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Core.Pooling;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Guess;
using GI_Subtitles.Services.Stamina;
using GI_Subtitles.Views.Shell;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using PaddleOCRSharp;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The stamina number read off the game screen (Original Resin, Trailblaze Power, Waveplates), for a player who
    /// switched it on. While subtitles run and the game is in front, the capture tick copies one small strip of the top
    /// of the game picture every few seconds; a background task looks for the game's stamina icon in it and reads the
    /// number beside it (Services/Stamina). The timer that follows lives in Config on this PC: nothing is sent anywhere,
    /// and the game's process, memory and files are never touched. One tray notice says when the pool is nearly full,
    /// never over a game.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>How often the reader's cost is summed up in the log while it samples.</summary>
        private static readonly TimeSpan StaminaStatsEvery = TimeSpan.FromMinutes(10);
        /// <summary>The longest the notice timer sleeps before it looks again (a clock change, a new reading elsewhere).</summary>
        private static readonly TimeSpan StaminaNoticeMaxSleep = TimeSpan.FromHours(1);

        /// <summary>How often "the number on screen is what the timer expected" is written to Config (the card's "read … ago").</summary>
        private static readonly TimeSpan StaminaSeenSaveEvery = TimeSpan.FromMinutes(1);
        /// <summary>The pause after a strip could not be captured (the game window moved to another screen mid-session).</summary>
        private static readonly TimeSpan StaminaCaptureRetry = TimeSpan.FromMinutes(1);
        /// <summary>
        /// How often the selected game is looked up again while it has no measured screen: the player may switch to one
        /// that has. One Config read each time, nothing else.
        /// </summary>
        private static readonly TimeSpan StaminaNoGameRecheck = TimeSpan.FromSeconds(5);

        private volatile bool _staminaOn;
        /// <summary>A reader for the selected game is being put together off the tick; nothing is sampled meanwhile.</summary>
        private bool _staminaPreparing;
        private string _staminaGameKey;
        private GameManifest _staminaGame;
        private StaminaScreenReader _staminaReader;
        private StaminaTracker _staminaTracker;
        private StaminaSampleSaver _staminaSaver;
        private long _staminaNextSample;
        private int _staminaBusy;
        private DateTimeOffset _staminaSavedAt;
        private DispatcherTimer _staminaNoticeTimer;
        private readonly ThrottledLogger _staminaFailureLog = new ThrottledLogger(TimeSpan.FromMinutes(10));

        // Cost of the reader since the last summary; touched by one background sample at a time.
        private int _staminaLooks, _staminaReads;
        private double _staminaLookMs, _staminaWorstLookMs, _staminaReadMs;
        private long _staminaStatsFrom;
        // The tick's own share (the region copy on the UI thread), in Stopwatch ticks; written there, read by the summary.
        private long _staminaCaptureTicks, _staminaWorstCaptureTicks;

        private void InitStamina()
        {
            _staminaOn = StaminaSettings.Enabled;
            StaminaSettings.Changed += OnStaminaSettingsChanged;
            _staminaNoticeTimer = new DispatcherTimer(DispatcherPriority.Background);
            _staminaNoticeTimer.Tick += (_, __) => CheckStaminaNotice();
            // A notice that fell due while Kaption was closed is not shown late: the Start card says where things stand.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (GameManifest game in GameRegistry.Current.Listed.Where(g => g.Stamina != null))
            {
                StaminaReading reading = StaminaSettings.LoadReading(game.Id);
                if (reading != null && StaminaSettings.LoadArmed(game.Id) && now >= StaminaNotice.DueAt(game.Stamina, reading, StaminaSettings.NoticeLead))
                    StaminaSettings.SaveArmed(game.Id, false);
            }
            CheckStaminaNotice();
            Closed += (_, __) =>
            {
                StaminaSettings.Changed -= OnStaminaSettingsChanged;
                _staminaNoticeTimer?.Stop();
                _staminaOn = false;
            };
        }

        private void OnStaminaSettingsChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(OnStaminaSettingsChanged));
                return;
            }
            _staminaOn = StaminaSettings.Enabled;
            if (_staminaOn) Logger.Log.Info("Stamina reader switched on: the number beside the stamina icon is read while subtitles run.");
            else Logger.Log.Info("Stamina reader switched off.");
            CheckStaminaNotice();
        }

        /// <summary>
        /// On the capture tick (UI thread), after the dialogue captures. Costs one comparison when the reader is off or
        /// not due; when due, one region copy from the frame the tick already holds (DXGI), or one small GDI copy on
        /// the fallback path. Never throws into the tick.
        /// </summary>
        private void SampleStaminaIfDue()
        {
            if (!_staminaOn) return;
            long tick = Environment.TickCount64;
            if (tick < _staminaNextSample || _staminaPreparing || Volatile.Read(ref _staminaBusy) != 0) return;
            try
            {
                string gameKey = Config.Get<string>("Game", Game ?? "Genshin");
                if (!string.Equals(gameKey, _staminaGameKey, StringComparison.Ordinal))
                {
                    PrepareStaminaGame(gameKey);
                    return;
                }
                GameStaminaScreen spec = _staminaGame?.Desktop?.StaminaScreen;
                if (_staminaReader == null || spec == null)
                {
                    _staminaNextSample = tick + (long)StaminaNoGameRecheck.TotalMilliseconds;
                    return;
                }
                _staminaNextSample = tick + spec.SampleSeconds * 1000L;

                if (!GameWindowDetector.TryGetClientArea(GetForegroundWindow(), out Rectangle client)) return;
                Rectangle area = StaminaLocator.SearchArea(client, spec);
                if (area.IsEmpty || _captureBackend == null) return;

                long started = Stopwatch.GetTimestamp();
                Bitmap shot = BitmapPool.Default.Rent(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    // DXGI: from the frame this tick's dialogue capture already holds. Asking for a region the usual way
                    // would wait up to 16 ms for the next frame, here on the UI thread.
                    Bitmap copied = _captureBackend is GI_Subtitles.Services.Capture.DxgiScreenCapture dxgi
                        ? dxgi.CopyFromHeldFrame(area.X, area.Y, area.Width, area.Height, shot)
                        : _captureBackend.CaptureRegionInto(area.X, area.Y, area.Width, area.Height, shot);
                    if (copied == null)
                    {
                        BitmapPool.Default.Return(shot);
                        return;
                    }
                    if (OverlayCanBeInFrame) MaskOverlayAreas(shot, area.X, area.Y, area.Width, area.Height);
                }
                catch
                {
                    BitmapPool.Default.Return(shot);
                    throw;
                }
                // The moment of the picture, not of the read: the background task may start late.
                DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
                long spent = Stopwatch.GetTimestamp() - started;
                Interlocked.Add(ref _staminaCaptureTicks, spent);
                if (spent > Interlocked.Read(ref _staminaWorstCaptureTicks)) Interlocked.Exchange(ref _staminaWorstCaptureTicks, spent);

                StaminaScreenReader reader = _staminaReader;
                StaminaSampleSaver saver = StaminaSettings.SaveSamples ? _staminaSaver : null;
                PaddleOCREngine engine = data != null && data.Engine == SettingsWindow.EngineStatus.Ready ? data.engine : null;
                string gameId = _staminaGame.Id;
                System.Drawing.Size clientSize = client.Size;
                TimeSpan fresh = TimeSpan.FromSeconds(spec.SampleSeconds);
                Interlocked.Exchange(ref _staminaBusy, 1);
                try
                {
                    Task.Run(() =>
                    {
                        try
                        {
                            // A picture that waited longer than one sample period for its turn is dropped: the next is due.
                            if (DateTimeOffset.UtcNow - capturedAt <= fresh)
                                ReadStaminaSample(shot, area.Size, clientSize, reader, engine, saver, gameId, capturedAt);
                        }
                        catch (Exception ex)
                        {
                            if (_staminaFailureLog.ShouldLog(ex.GetType().Name, out int suppressed))
                                Logger.Log.Warn($"Stamina reader: a look failed: {ex.GetType().Name}: {ex.Message}" + _staminaFailureLog.SuppressionSuffix(suppressed));
                        }
                        finally
                        {
                            BitmapPool.Default.Return(shot);
                            Interlocked.Exchange(ref _staminaBusy, 0);
                        }
                    });
                }
                catch
                {
                    // The task never started: the picture and the "one look at a time" flag are still this method's.
                    BitmapPool.Default.Return(shot);
                    Interlocked.Exchange(ref _staminaBusy, 0);
                    throw;
                }
            }
            catch (Exception ex)
            {
                // A capture that fails here fails for the dialogue too, which reports it; this reader just waits.
                _staminaNextSample = tick + (long)StaminaCaptureRetry.TotalMilliseconds;
                if (_staminaFailureLog.ShouldLog("capture", out int suppressed))
                    Logger.Log.Warn($"Stamina reader: capture failed: {ex.Message}" + _staminaFailureLog.SuppressionSuffix(suppressed));
            }
        }

        /// <summary>
        /// Swaps the reader for the selected game. Called on the tick while no sample is in flight, and it only hands
        /// the work over: the icon is decoded, the stored reading loaded and the old reader retired on a background
        /// task, and the result comes back to the UI thread. Sampling waits meanwhile (<see cref="_staminaPreparing"/>).
        /// </summary>
        private void PrepareStaminaGame(string gameKey)
        {
            StaminaScreenReader retired = _staminaReader;
            _staminaReader = null;
            _staminaTracker = null;
            _staminaSaver = null;
            _staminaGame = null;
            _staminaGameKey = gameKey;
            _staminaPreparing = true;
            Task.Run(() =>
            {
                GameManifest game = null;
                StaminaScreenReader reader = null;
                StaminaTracker tracker = null;
                StaminaSampleSaver saver = null;
                try
                {
                    retired?.Dispose();
                    game = GameRegistry.Current.Find(gameKey);
                    reader = StaminaScreenReader.ForGame(game);
                    if (reader != null)
                    {
                        GameStaminaScreen spec = game.Desktop.StaminaScreen;
                        tracker = new StaminaTracker(game.Stamina, spec.Confirmations, spec.RefillConfirmations,
                            TimeSpan.FromSeconds(spec.SampleSeconds * (spec.RefillConfirmations + 1)), StaminaSettings.LoadReading(game.Id));
                        saver = new StaminaSampleSaver(game.GameId, StaminaSettings.SampleLimit);
                    }
                }
                catch (Exception ex)
                {
                    reader?.Dispose();
                    reader = null;
                    Logger.Log.Warn($"Stamina reader: not ready for {gameKey}: {ex.GetType().Name}: {ex.Message}");
                }
                if (Dispatcher.HasShutdownStarted)
                {
                    reader?.Dispose();
                    return;
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Still the game this was prepared for (the tick starts no second preparation while one runs).
                    _staminaGame = game;
                    _staminaReader = reader;
                    _staminaTracker = tracker;
                    _staminaSaver = saver;
                    _staminaStatsFrom = Environment.TickCount64;
                    _staminaPreparing = false;
                    if (reader == null)
                    {
                        Logger.Log.Info($"Stamina reader: nothing to read for {gameKey} (its screen is not measured yet).");
                        return;
                    }
                    Logger.Log.Info($"Stamina reader ready for {game.Id}: {game.Stamina.Name}, a look every {game.Desktop.StaminaScreen.SampleSeconds} s while subtitles run.");
                }));
            });
        }

        /// <summary>Background: find the icon, read the number, hand a value to the UI thread. Owns nothing it was given.</summary>
        /// <param name="capturedAt">When the picture was taken on the capture tick: the moment the value is true for.</param>
        private void ReadStaminaSample(Bitmap shot, System.Drawing.Size areaSize, System.Drawing.Size client, StaminaScreenReader reader,
            PaddleOCREngine engine, StaminaSampleSaver saver, string gameId, DateTimeOffset capturedAt)
        {
            DateTimeOffset now = capturedAt;
            using Mat frame = BitmapConverter.ToMat(shot);
            using var strip = new Mat(frame, new Rect(0, 0, Math.Min(areaSize.Width, frame.Width), Math.Min(areaSize.Height, frame.Height)));
            double readMs = 0;
            var clock = Stopwatch.StartNew();
            StaminaSample sample = reader.Read(strip, client, (Mat line, out string text, out float score) =>
            {
                text = null;
                score = 0;
                if (engine == null) return false;
                var t = Stopwatch.StartNew();
                bool free = engine.TryRecognizeLine(line, out text, out score);
                readMs = t.Elapsed.TotalMilliseconds;
                return free;
            });
            double lookMs = clock.Elapsed.TotalMilliseconds - readMs;

            _staminaLooks++;
            _staminaLookMs += lookMs;
            _staminaWorstLookMs = Math.Max(_staminaWorstLookMs, lookMs);
            if (readMs > 0)
            {
                _staminaReads++;
                _staminaReadMs += readMs;
            }
            if (Logger.IsDebugEnabled && sample.Outcome != StaminaSampleOutcome.NoIcon)
                Logger.Log.Debug($"Stamina look: {sample.Outcome}, icon {sample.IconScore:0.00}, text {sample.TextScore:0.00}, look {lookMs:0.0} ms, read {readMs:0.0} ms" +
                                 (sample.Reused ? " (unchanged picture)" : ""));
            if (Environment.TickCount64 - _staminaStatsFrom >= (long)StaminaStatsEvery.TotalMilliseconds) LogStaminaCost();

            if (saver != null)
            {
                try { saver.Save(strip, sample, now); }
                catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
                {
                    if (_staminaFailureLog.ShouldLog("save", out _)) Logger.Log.Warn($"Stamina reader: a sample picture was not saved: {ex.Message}");
                }
            }
            if (sample.Outcome == StaminaSampleOutcome.Read && !Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(() => OnStaminaValue(gameId, sample.Value, now)));
        }

        private void LogStaminaCost()
        {
            if (_staminaLooks > 0)
            {
                double perTick = 1000.0 / Stopwatch.Frequency;
                double captureMs = Interlocked.Exchange(ref _staminaCaptureTicks, 0) * perTick / _staminaLooks;
                double worstCaptureMs = Interlocked.Exchange(ref _staminaWorstCaptureTicks, 0) * perTick;
                Logger.Log.Info(string.Create(CultureInfo.InvariantCulture,
                    $"Stamina reader cost: {_staminaLooks} looks; on the capture tick {captureMs:0.00} ms each (worst {worstCaptureMs:0.00}); "
                    + $"off the UI thread {_staminaLookMs / _staminaLooks:0.0} ms each (worst {_staminaWorstLookMs:0.0}); "
                    + $"{_staminaReads} number reads, {(_staminaReads > 0 ? _staminaReadMs / _staminaReads : 0):0.0} ms each."));
            }
            _staminaLooks = _staminaReads = 0;
            _staminaLookMs = _staminaWorstLookMs = _staminaReadMs = 0;
            _staminaStatsFrom = Environment.TickCount64;
        }

        /// <summary>UI thread: a value read off the screen goes to the tracker; a believed one moves the timer.</summary>
        private void OnStaminaValue(string gameId, int value, DateTimeOffset at)
        {
            GameManifest game = _staminaGame;
            StaminaTracker tracker = _staminaTracker;
            if (game == null || tracker == null || !string.Equals(game.Id, gameId, StringComparison.Ordinal) || !_staminaOn) return;

            StaminaVerdict verdict = tracker.Offer(value, at);
            if (verdict == StaminaVerdict.Waiting) return;
            StaminaReading reading = tracker.Reading;
            if (verdict == StaminaVerdict.Accepted)
            {
                StaminaSettings.SaveReading(game.Id, reading);
                // Spending, a refill or the first reading starts a new fill, so the notice is owed again (or not, when
                // the pool is already that close). A one-point correction is the same fill: what is owed stays owed.
                if (tracker.LastChange != StaminaChange.Corrected)
                    StaminaSettings.SaveArmed(game.Id, StaminaNotice.Arms(game.Stamina, reading, StaminaSettings.NoticeLead));
                StaminaSettings.SaveSeen(game.Id, at);
                _staminaSavedAt = at;
                Logger.Log.Info($"Stamina reader: {game.Stamina.Name} {reading.Value}/{game.Stamina.Max} read off the screen; full at "
                                + $"{StaminaClock.FullAt(game.Stamina, reading).ToLocalTime():HH:mm}.");
                CheckStaminaNotice();
            }
            else if (at - _staminaSavedAt >= StaminaSeenSaveEvery)
            {
                // The number on screen is what the timer expected: only "last read" moves. The reading stays, so a pool
                // seen full again keeps the moment it filled.
                StaminaSettings.SaveSeen(game.Id, at);
                _staminaSavedAt = at;
            }
            data?.RefreshStaminaCard();
        }

        /// <summary>Shows the "nearly full" notice of any game whose turn it is, or sets the timer for the next look.</summary>
        private void CheckStaminaNotice()
        {
            if (_staminaNoticeTimer == null) return;
            _staminaNoticeTimer.Stop();
            if (!StaminaSettings.Enabled || !StaminaSettings.Notice) return;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan lead = StaminaSettings.NoticeLead;
            TimeSpan? sleep = null;
            bool? mayInterrupt = null;
            foreach (GameManifest game in GameRegistry.Current.Listed.Where(g => g.Stamina != null))
            {
                if (!StaminaSettings.LoadArmed(game.Id)) continue;
                StaminaReading reading = StaminaSettings.LoadReading(game.Id);
                // Asked only once something is due: the answer needs the foreground process.
                bool due = reading != null && now >= StaminaNotice.DueAt(game.Stamina, reading, lead);
                if (due) mayInterrupt ??= !OCRTimer.IsEnabled && !GuessNoticeGate.AnyGameInFront()
                                          && GuessNoticeGate.QueryNotificationState() == GuessNoticeGate.QunsAcceptsNotifications;
                StaminaNoticeDecision next = StaminaNotice.Next(game.Stamina, reading, true, true, lead, now, mayInterrupt == true);
                switch (next.Step)
                {
                    case StaminaNoticeStep.Show:
                        StaminaNoticeText text = StaminaText.Notice(game.Stamina, reading, now, StaminaL, CultureInfo.CurrentUICulture);
                        string url = StaminaLinks.TimerUrl(game, UiLanguage.Current, reading, StaminaSettings.LoadSeen(game.Id));
                        notify?.ShowBalloonTip(text.Title, text.Body, onClick: url == null ? null : () => OpenStaminaTimer(url));
                        StaminaSettings.SaveArmed(game.Id, false);
                        Logger.Log.Info($"Stamina notice shown for {game.Id}.");
                        mayInterrupt = false; // one balloon at a time; another game's waits for the next look
                        sleep = Shorter(sleep, StaminaSettings.GateRetry);
                        break;
                    case StaminaNoticeStep.Wait:
                        sleep = Shorter(sleep, StaminaSettings.GateRetry);
                        break;
                    case StaminaNoticeStep.Sleep:
                        sleep = Shorter(sleep, next.At.Value - now);
                        break;
                }
            }
            if (sleep is not TimeSpan wait) return;
            _staminaNoticeTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 1, StaminaNoticeMaxSleep.TotalSeconds));
            _staminaNoticeTimer.Start();
        }

        private static TimeSpan Shorter(TimeSpan? a, TimeSpan b) => a is TimeSpan x && x < b ? x : b;

        private static void OpenStaminaTimer(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Log.Warn($"Stamina timer page not opened: {ex.Message}"); }
        }

        private string StaminaL(string key, string fallback) => TryFindResource(key) as string ?? fallback;
    }
}
