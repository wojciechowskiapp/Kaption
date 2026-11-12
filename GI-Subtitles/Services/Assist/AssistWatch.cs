using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Capture;
using GI_Subtitles.Services.Games;
using OpenCvSharp;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Assist
{
    /// <summary>What the watch needs from the window that owns the capture tick. Everything is asked on the UI thread.</summary>
    public interface IAssistHost
    {
        /// <summary>The backend the tick captures with; null when there is none.</summary>
        IScreenCapture Capture { get; }

        /// <summary>
        /// A number that changes whenever the frame the backend holds does. A backend that cannot tell gives a new
        /// number every time, so nothing is ever taken for unchanged.
        /// </summary>
        long FrameSerial { get; }

        /// <summary>The selected game, as Config spells it.</summary>
        string SelectedGame { get; }

        /// <summary>The game's client area in screen pixels (not its window: a windowed game has a title bar).</summary>
        bool TryGetGameClient(out Rectangle client);

        /// <summary>Kaption's own windows that can be in the frame, in screen pixels; empty when none can.</summary>
        IReadOnlyList<Rectangle> OwnWindowAreas();

        /// <summary>Reads one cut-out line when the text engine is free; null while there is no engine.</summary>
        AssistLineReader LineReader { get; }

        /// <summary>Runs on the UI thread later; false when the app is closing and it never will.</summary>
        bool Post(Action onUiThread);
    }

    /// <summary>
    /// The only scheduler of what the app reads off the game screen besides the dialogue. The capture tick calls
    /// <see cref="OnCaptureTick"/> after its own captures; with every reader off that is one comparison. When
    /// something is due the watch copies the small rectangles it needs out of the frame the tick already holds
    /// (never a new frame), and hands them to one background look at a time: screen anchors are compared at their
    /// declared places, at most once a second, and a reader looks only when it is due and its screen is showing. A
    /// reader takes the text engine only when it is free. No reader on, a game without <c>desktop.assist</c>, a
    /// window of an unmeasured shape, a game on another monitor, a busy engine: each gives no reading and no error.
    /// </summary>
    public sealed class AssistWatch : IThreadPoolWorkItem, IDisposable
    {
        /// <summary>Screens are probed at most this often.</summary>
        public const int ProbeMs = 1000;
        /// <summary>The fallback backend pays a screen copy per rectangle: one rectangle a second.</summary>
        public const int RationMsPerRectangle = 1000;
        /// <summary>How often the selected game is looked up again while it has nothing switched on.</summary>
        public const int NoReaderRecheckMs = 5000;
        /// <summary>The pause after the game turned out to be on a screen the backend does not capture.</summary>
        public const int QuietMs = 60000;
        /// <summary>The pause after a copy that found no frame (a locked PC, the first tick).</summary>
        public const int RetryMs = 1000;
        /// <summary>An anchor this close to its threshold (as a share of it) is a near miss worth a sample picture.</summary>
        public const double NearMissShare = 0.8;
        private static readonly TimeSpan CostEvery = TimeSpan.FromMinutes(10);

        private readonly IAssistHost _host;
        private readonly AssistReaderFactory _create;
        private readonly Func<string, bool> _isOn;
        private readonly AssistStore _store;
        private readonly Func<string, GameManifest> _findGame;
        private readonly Func<IReadOnlyList<GameManifest>> _listedGames;
        private readonly Func<long> _clock;
        private readonly Action<Action> _background;
        private readonly Action<IThreadPoolWorkItem> _queue;
        private readonly Func<string, string, AssistTemplate> _loadTemplate;
        private readonly AssistLineReader _timedReader;
        private readonly ThrottledLogger _failureLog = new ThrottledLogger(TimeSpan.FromMinutes(10));

        private volatile bool _on;
        private string _onSignature;
        private bool _preparing;
        private string _gameKey;
        private AssistSession _session;
        private int _busy;
        private long _nextLook, _rationedUntil;
        private int _rationCursor;

        // The look in flight: written on the tick while no look runs, read by the one look that follows.
        private struct Item
        {
            public bool IsScreen;
            public int Index, FirstRegion, Regions;
        }
        private readonly List<Item> _plan = new List<Item>();
        private Rectangle[] _regions = new Rectangle[8];
        private int _regionCount;
        private byte[] _buffer;
        private AssistSession _lookSession;
        private Rectangle _client;
        private long _capturedTick, _freshMs, _lookSerial;
        private DateTimeOffset _capturedAt;
        private AssistLineReader _lineReader;
        private bool _saveSamples;
        private long _readTicks;
        private int _readsInLook;

        // Cost since the last summary. The tick's share is written on the UI thread, the rest by one look at a time.
        private long _copyTicks, _worstCopyTicks;
        private int _copies;
        private int _looks, _reads, _probes;
        private long _lookTicks, _worstLookTicks, _readTicksTotal, _probeTicks, _worstProbeTicks;
        private long _costFrom;
        private string _lastCost;

        /// <summary>A reader read a value. Raised on the UI thread.</summary>
        public event Action<AssistReading> Read;

        /// <param name="findGame">The selected game's manifest, by the name Config holds.</param>
        /// <param name="listedGames">Every game the app lists (the switches of all of them decide whether the watch is on).</param>
        public AssistWatch(IAssistHost host, AssistReaderFactory create, Func<string, bool> isOn, AssistStore store,
            Func<string, GameManifest> findGame, Func<IReadOnlyList<GameManifest>> listedGames, Func<long> clock = null, Action<Action> background = null, Action<IThreadPoolWorkItem> queue = null,
            Func<string, string, AssistTemplate> loadTemplate = null)
        {
            _loadTemplate = loadTemplate;
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _create = create ?? throw new ArgumentNullException(nameof(create));
            _isOn = isOn ?? throw new ArgumentNullException(nameof(isOn));
            _store = store;
            _findGame = findGame ?? throw new ArgumentNullException(nameof(findGame));
            _listedGames = listedGames ?? throw new ArgumentNullException(nameof(listedGames));
            _clock = clock ?? (() => Environment.TickCount64);
            _background = background ?? (work => System.Threading.Tasks.Task.Run(work));
            _queue = queue ?? (item => ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: false));
            _timedReader = ReadLineTimed;
            SettingsChanged();
        }

        /// <summary>Whether any reader is switched on; off, the tick pays one comparison.</summary>
        public bool IsOn => _on;

        /// <summary>A switch moved (UI thread): the readers of the selected game are put together again when their set changed.</summary>
        public void SettingsChanged()
        {
            string signature = string.Join(",", _listedGames()
                .Where(g => g.Desktop?.Assist?.Readers != null)
                .SelectMany(g => g.Desktop.Assist.Readers.Keys.Where(_isOn).Select(id => g.Id + "/" + id)));
            if (signature == _onSignature) return;
            _onSignature = signature;
            _gameKey = null; // the next tick prepares the selected game again, once no look is in flight
            _nextLook = 0;
            _on = signature.Length > 0;
        }

        /// <summary>
        /// On the capture tick (UI thread), after the dialogue captures, so their frame is untouched. One comparison
        /// when nothing is switched on or nothing is due. Never throws into the tick.
        /// </summary>
        public void OnCaptureTick()
        {
            if (!_on) return;
            long now = _clock();
            if (now < _nextLook || _preparing || Volatile.Read(ref _busy) != 0) return;
            try
            {
                PlanAndCopy(now);
            }
            catch (Exception ex)
            {
                // A capture that fails here fails for the dialogue too, which reports it; the watch just waits.
                _nextLook = now + QuietMs;
                if (_failureLog.ShouldLog("tick", out int suppressed))
                    Logger.Log.Warn($"In-game helper: the look on the capture tick failed: {ex.GetType().Name}: {ex.Message}" + _failureLog.SuppressionSuffix(suppressed));
            }
        }

        private void PlanAndCopy(long now)
        {
            string key = _host.SelectedGame;
            if (!string.Equals(key, _gameKey, StringComparison.Ordinal))
            {
                Prepare(key);
                return;
            }
            AssistSession s = _session;
            if (s == null || s.Readers.Length == 0)
            {
                _nextLook = now + NoReaderRecheckMs;
                return;
            }
            if (!AnythingDue(s, now))
            {
                _nextLook = Soonest(s, now);
                return;
            }
            IScreenCapture capture = _host.Capture;
            if (capture == null || !_host.TryGetGameClient(out Rectangle client))
            {
                _nextLook = now + RetryMs;
                return;
            }
            bool rationed = !capture.HoldsFrame;
            if (rationed && now < _rationedUntil)
            {
                _nextLook = _rationedUntil;
                return;
            }

            bool fixedPlaces = s.Canvas != null && AssistGeometry.IsChecked(s.Checked, client.Size);
            IReadOnlyList<Rectangle> own = _host.OwnWindowAreas();
            _plan.Clear();
            _regionCount = 0;
            long serial = _host.FrameSerial;

            for (int i = 0; i < s.Screens.Length; i++)
            {
                AssistSession.Screen screen = s.Screens[i];
                screen.Told = false;
                // A reader that is due on this screen brings the probe with it: it reads a frame only together with
                // the answer to "is this still that screen" for the same frame.
                if (now < screen.Next && !ReaderDueOn(s, i, now)) continue;
                if (!fixedPlaces)
                {
                    // A window of a shape nobody measured: its screens are not told apart, so they are not showing.
                    screen.Next = now + ProbeMs;
                    s.Presence.Observe(i, false);
                    continue;
                }
                if (rationed)
                {
                    // The fallback backend holds no frame: an anchor and a reader's box would be two screen copies a
                    // second apart, so a reader could get a menu that replaced the one its anchor just vouched for.
                    // Fixed screens are not told apart there at all; readers that find their own anchor still look.
                    screen.Next = now + ProbeMs;
                    s.Presence.Observe(i, false);
                    continue;
                }
                if (screen.Probed && screen.ProbedSerial == serial)
                {
                    // The frame is still the one this screen was last probed on: that probe counts again, no pixels.
                    screen.Next = now + ProbeMs;
                    s.Presence.Repeat(i);
                    screen.Told = true;
                    continue;
                }
                int first = _regionCount;
                bool placed = true;
                foreach (AssistSession.Anchor anchor in screen.Anchors)
                {
                    if (!AssistGeometry.TryPlace(anchor.Spec, s.Canvas, client, ScreenProbe.Slack, out Rectangle patch) || Touches(patch, own))
                    {
                        placed = false;
                        break;
                    }
                    AddRegion(patch);
                }
                if (!placed)
                {
                    // Covered by one of Kaption's own windows, or the window is too small: no glimpse this time.
                    _regionCount = first;
                    screen.Next = now + ProbeMs;
                    continue;
                }
                _plan.Add(new Item { IsScreen = true, Index = i, FirstRegion = first, Regions = _regionCount - first });
                screen.Told = true;
            }

            for (int i = 0; i < s.Readers.Length; i++)
            {
                AssistSession.Slot slot = s.Readers[i];
                if (now < slot.Next || (slot.Screen >= 0 && !s.Presence.IsPresent(slot.Screen))) continue;
                Rectangle area = slot.Reader.Area(client, fixedPlaces);
                // No answer about its screen this time (the anchor is covered, the window too small): no look either.
                if ((slot.Screen >= 0 && !s.Screens[slot.Screen].Told)
                    || area.Width <= 0 || area.Height <= 0 || (!slot.Reader.BlacksOutOwnWindows && Touches(area, own)))
                {
                    slot.Next = now + slot.Reader.PeriodSeconds * 1000L;
                    continue;
                }
                _plan.Add(new Item { IsScreen = false, Index = i, FirstRegion = _regionCount, Regions = 1 });
                AddRegion(area);
            }
            if (_plan.Count == 0)
            {
                _nextLook = Soonest(s, now);
                return;
            }
            if (rationed && _plan.Count > 1)
            {
                // The fallback backend: one item a look, each in its turn; the others stay due.
                Item only = _plan[_rationCursor++ % _plan.Count];
                Array.Copy(_regions, only.FirstRegion, _regions, 0, only.Regions);
                only.FirstRegion = 0;
                _regionCount = only.Regions;
                _plan.Clear();
                _plan.Add(only);
            }

            long freshMs = long.MaxValue;
            foreach (Item item in _plan)
            {
                if (item.IsScreen)
                {
                    s.Screens[item.Index].Next = now + ProbeMs;
                    freshMs = Math.Min(freshMs, ProbeMs);
                }
                else
                {
                    long period = s.Readers[item.Index].Reader.PeriodSeconds * 1000L;
                    s.Readers[item.Index].Next = now + period;
                    freshMs = Math.Min(freshMs, period);
                }
            }
            if (rationed) _rationedUntil = now + (long)RationMsPerRectangle * _regionCount;

            ReadOnlySpan<Rectangle> regions = _regions.AsSpan(0, _regionCount);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(FrameRegions.BytesFor(regions));
            bool handedOver = false;
            try
            {
                long started = Stopwatch.GetTimestamp();
                FrameCopy copied = capture.TryCopyFromLastFrame(regions, buffer);
                if (copied != FrameCopy.Copied)
                {
                    // On another monitor than the one captured: quiet for a while, not a throw every second.
                    _nextLook = now + (copied == FrameCopy.OutsideOutput ? QuietMs : RetryMs);
                    if (copied == FrameCopy.OutsideOutput && Logger.IsDebugEnabled)
                        Logger.Log.Debug("In-game helper: the game is not on the captured screen; looking again in a minute.");
                    return;
                }
                BlackOutOwnWindows(s, buffer, own);
                long spent = Stopwatch.GetTimestamp() - started;
                _copyTicks += spent;
                _copies++;
                if (spent > _worstCopyTicks) _worstCopyTicks = spent;

                _buffer = buffer;
                _lookSession = s;
                _client = client;
                _capturedTick = now;
                _lookSerial = serial;
                _freshMs = freshMs;
                // The moment of the picture, not of the read: the look may start late.
                _capturedAt = DateTimeOffset.UtcNow;
                _lineReader = _host.LineReader;
                _saveSamples = AssistSettings.SaveSamples;
                Volatile.Write(ref _busy, 1);
                try
                {
                    _queue(this);
                    handedOver = true;
                }
                catch
                {
                    Volatile.Write(ref _busy, 0);
                    throw;
                }
                _nextLook = Soonest(s, now);
            }
            finally
            {
                if (!handedOver)
                {
                    _buffer = null;
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        private static bool ReaderDueOn(AssistSession s, int screen, long now)
        {
            if (!s.Presence.IsPresent(screen)) return false;
            foreach (AssistSession.Slot slot in s.Readers)
            {
                if (slot.Screen == screen && now >= slot.Next) return true;
            }
            return false;
        }

        private static bool AnythingDue(AssistSession s, long now)
        {
            foreach (AssistSession.Screen screen in s.Screens)
            {
                if (now >= screen.Next) return true;
            }
            foreach (AssistSession.Slot slot in s.Readers)
            {
                if (now >= slot.Next && (slot.Screen < 0 || s.Presence.IsPresent(slot.Screen))) return true;
            }
            return false;
        }

        private static long Soonest(AssistSession s, long now)
        {
            long soonest = long.MaxValue;
            foreach (AssistSession.Screen screen in s.Screens) soonest = Math.Min(soonest, screen.Next);
            foreach (AssistSession.Slot slot in s.Readers)
            {
                // A reader whose screen is not showing waits for the probe, which is already counted.
                if (slot.Screen < 0 || s.Presence.IsPresent(slot.Screen)) soonest = Math.Min(soonest, slot.Next);
            }
            return soonest == long.MaxValue ? now + NoReaderRecheckMs : Math.Max(soonest, now + 1);
        }

        private void AddRegion(Rectangle region)
        {
            if (_regionCount == _regions.Length) Array.Resize(ref _regions, _regions.Length * 2);
            _regions[_regionCount++] = region;
        }

        private static bool Touches(Rectangle area, IReadOnlyList<Rectangle> own)
        {
            for (int i = 0; i < own.Count; i++)
            {
                if (area.IntersectsWith(own[i])) return true;
            }
            return false;
        }

        /// <summary>Black where one of Kaption's own windows covers a reader's area, so it never reads its own text.</summary>
        private void BlackOutOwnWindows(AssistSession s, byte[] buffer, IReadOnlyList<Rectangle> own)
        {
            if (own.Count == 0) return;
            int offset = 0;
            foreach (Item item in _plan)
            {
                bool black = !item.IsScreen && s.Readers[item.Index].Reader.BlacksOutOwnWindows;
                for (int k = 0; k < item.Regions; k++)
                {
                    Rectangle region = _regions[item.FirstRegion + k];
                    for (int i = 0; black && i < own.Count; i++)
                    {
                        Rectangle hit = Rectangle.Intersect(region, own[i]);
                        if (hit.Width <= 0 || hit.Height <= 0) continue;
                        for (int y = hit.Top; y < hit.Bottom; y++)
                        {
                            Span<byte> row = buffer.AsSpan(offset + ((y - region.Top) * region.Width + (hit.Left - region.Left)) * 4, hit.Width * 4);
                            row.Clear();
                            for (int alpha = 3; alpha < row.Length; alpha += 4) row[alpha] = 255;
                        }
                    }
                    offset += FrameRegions.BytesFor(region);
                }
            }
        }

        /// <summary>
        /// Puts the selected game's readers together off the tick (templates are decoded, a stored reading loaded)
        /// and retires the old ones; nothing is looked at meanwhile.
        /// </summary>
        private void Prepare(string gameKey)
        {
            AssistSession retired = _session;
            _session = null;
            _gameKey = gameKey;
            _preparing = true;
            _background(() =>
            {
                AssistSession next = null;
                try
                {
                    retired?.Dispose();
                    next = AssistSession.Build(_findGame(gameKey), _create, _isOn, _loadTemplate);
                }
                catch (Exception ex)
                {
                    next?.Dispose();
                    next = null;
                    Logger.Log.Warn($"In-game helper: not ready for {gameKey}: {ex.GetType().Name}: {ex.Message}");
                }
                AssistSession ready = next;
                bool posted = _host.Post(() =>
                {
                    // Still the game this was prepared for: the tick starts no second preparation while one runs.
                    _session = ready;
                    _costFrom = _clock();
                    _preparing = false;
                    if (ready == null || ready.Readers.Length == 0) return;
                    _store?.SessionStarted(ready.Game.GameId, ready.ReaderIds);
                    Logger.Log.Info($"In-game helper ready for {ready.Game.Id}: {string.Join(", ", ready.ReaderIds)}"
                                    + (ready.Screens.Length > 0 ? $"; {ready.Screens.Length} screen(s) probed once a second" : "")
                                    + ", while subtitles run.");
                });
                if (!posted) ready?.Dispose();
            });
        }

        /// <summary>The background look. One at a time: the tick starts the next only after this one let go.</summary>
        void IThreadPoolWorkItem.Execute()
        {
            byte[] buffer = _buffer;
            AssistSession s = _lookSession;
            try
            {
                // A picture that waited longer than its period for its turn is dropped: the next one is due.
                if (_clock() - _capturedTick > _freshMs) return;
                unsafe
                {
                    fixed (byte* pixels = buffer)
                    {
                        int offset = 0, region = 0;
                        foreach (Item item in _plan)
                        {
                            if (item.IsScreen)
                            {
                                ProbeScreen(s, item, pixels, ref offset, ref region);
                                continue;
                            }
                            AssistSession.Slot slot = s.Readers[item.Index];
                            if (slot.Screen >= 0 && !(s.Presence.IsPresent(slot.Screen) && s.Screens[slot.Screen].LastSeen))
                            {
                                // Planned while its screen counted; the probe of this frame says the screen is gone. Its
                                // pixels are skipped, not read: a number on the menu that replaced it is not its number.
                                offset += FrameRegions.BytesFor(_regions[region++]);
                                continue;
                            }
                            LookWith(s, slot.Reader, pixels, ref offset, ref region);
                        }
                    }
                }
                if (_clock() - _costFrom >= (long)CostEvery.TotalMilliseconds) LogCost();
            }
            catch (Exception ex)
            {
                if (_failureLog.ShouldLog(ex.GetType().Name, out int suppressed))
                    Logger.Log.Warn($"In-game helper: a look failed: {ex.GetType().Name}: {ex.Message}" + _failureLog.SuppressionSuffix(suppressed));
            }
            finally
            {
                _buffer = null;
                _lookSession = null;
                _lineReader = null;
                ArrayPool<byte>.Shared.Return(buffer);
                Volatile.Write(ref _busy, 0);
            }
        }

        private unsafe void ProbeScreen(AssistSession s, Item item, byte* pixels, ref int offset, ref int region)
        {
            long started = Stopwatch.GetTimestamp();
            AssistSession.Screen screen = s.Screens[item.Index];
            bool seen = true;
            for (int k = 0; k < item.Regions; k++, region++)
            {
                Rectangle r = _regions[region];
                int bytes = FrameRegions.BytesFor(r);
                AssistSession.Anchor anchor = screen.Anchors[k];
                double score = s.Probe.Match(anchor.Template, new ReadOnlySpan<byte>(pixels + offset, bytes), r.Width, r.Height);
                if (score < anchor.Spec.Threshold)
                {
                    seen = false;
                    if (_saveSamples && score >= anchor.Spec.Threshold * NearMissShare)
                        SaveAnchorSample(s, screen, anchor, score, new AssistPixels((IntPtr)(pixels + offset), r.Width, r.Height));
                }
                offset += bytes;
            }
            s.Presence.Observe(item.Index, seen);
            // Only now is this frame the one the screen was probed on: a look that was dropped or failed before
            // here leaves the screen to be probed again, never repeated from an older frame.
            screen.Probed = true;
            screen.ProbedSerial = _lookSerial;
            screen.LastSeen = seen;
            long spent = Stopwatch.GetTimestamp() - started;
            _probes++;
            _probeTicks += spent;
            if (spent > _worstProbeTicks) _worstProbeTicks = spent;
        }

        private unsafe void LookWith(AssistSession s, IAssistReader reader, byte* pixels, ref int offset, ref int region)
        {
            Rectangle r = _regions[region++];
            var area = new AssistPixels((IntPtr)(pixels + offset), r.Width, r.Height);
            offset += FrameRegions.BytesFor(r);
            _readTicks = 0;
            _readsInLook = 0;
            long started = Stopwatch.GetTimestamp();
            AssistReading reading = reader.Look(area, _client.Size, _capturedAt, _lineReader == null ? null : _timedReader);
            long spent = Stopwatch.GetTimestamp() - started - _readTicks;
            _looks++;
            _lookTicks += spent;
            if (spent > _worstLookTicks) _worstLookTicks = spent;
            _reads += _readsInLook;
            _readTicksTotal += _readTicks;

            if (reading.Outcome == AssistOutcome.Read)
            {
                _store?.MarkSeen(s.Game.GameId, reader.Id, reading.At);
                _host.Post(() => Read?.Invoke(reading));
            }
            else if (reading.Outcome == AssistOutcome.Refused && Logger.IsDebugEnabled)
            {
                Logger.Log.Debug($"In-game helper: {s.Game.Id}/{reader.Id} refused a read ({reading.Refusal}).");
            }
        }

        private bool ReadLineTimed(Mat line, out string text, out float score)
        {
            long started = Stopwatch.GetTimestamp();
            bool free = _lineReader(line, out text, out score);
            if (free)
            {
                _readTicks += Stopwatch.GetTimestamp() - started;
                _readsInLook++;
            }
            return free;
        }

        private void SaveAnchorSample(AssistSession s, AssistSession.Screen screen, AssistSession.Anchor anchor, double score, AssistPixels patch)
        {
            try
            {
                var saver = new AssistSampleSaver(AssistSampleSaver.FolderFor(s.Game.GameId), AssistSettings.SampleLimit, AssistSettings.SampleBytes);
                using Mat picture = patch.AsMat();
                saver.Save(picture, string.Create(CultureInfo.InvariantCulture, $"near-{screen.Id}-{anchor.Spec.Template}-{score * 100:00}"), _capturedAt);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                if (_failureLog.ShouldLog("save", out _)) Logger.Log.Warn($"In-game helper: a sample picture was not saved: {ex.Message}");
            }
        }

        private void LogCost()
        {
            if (_looks > 0 || _probes > 0)
            {
                double ms = 1000.0 / Stopwatch.Frequency;
                int copies = Math.Max(1, _copies);
                _lastCost = string.Create(CultureInfo.InvariantCulture,
                    $"{_copies} copies on the capture tick, {_copyTicks * ms / copies:0.00} ms each (worst {_worstCopyTicks * ms:0.00}); "
                    + $"{_looks} reader looks off the UI thread, {(_looks > 0 ? _lookTicks * ms / _looks : 0):0.0} ms each (worst {_worstLookTicks * ms:0.0}); "
                    + $"{_reads} text reads, {(_reads > 0 ? _readTicksTotal * ms / _reads : 0):0.0} ms each; "
                    + $"{_probes} screen probes, {(_probes > 0 ? _probeTicks * ms / _probes : 0):0.000} ms each (worst {_worstProbeTicks * ms:0.000}).");
                Logger.Log.Info("In-game helper cost: " + _lastCost);
            }
            // The tick's counters are only ever a little off when it writes between these two lines: a summary, not a ledger.
            _copies = 0;
            _copyTicks = _worstCopyTicks = 0;
            _looks = _reads = _probes = 0;
            _lookTicks = _worstLookTicks = _readTicksTotal = _probeTicks = _worstProbeTicks = 0;
            _costFrom = _clock();
        }

        /// <summary>One line for the diagnostic bundle: what is on, how it is doing, what it last cost. No reading is in it.</summary>
        public string Describe()
        {
            var parts = new List<string> { _on ? "on (" + _onSignature + ")" : "off" };
            if (_store != null)
            {
                foreach (GameManifest game in _listedGames().Where(g => g.Desktop?.Assist?.Readers != null))
                {
                    foreach (AssistReaderHealth h in _store.Health(game.GameId))
                    {
                        parts.Add(string.Create(CultureInfo.InvariantCulture,
                            $"{game.Id}/{h.ReaderId}: last read {(h.LastSeenUtc is DateTimeOffset at ? at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "never")}, "
                            + $"not seen for {h.SessionsSinceSeen} session(s)"));
                    }
                }
            }
            if (_lastCost != null) parts.Add("last cost: " + _lastCost);
            return string.Join("; ", parts);
        }

        public void Dispose()
        {
            _on = false;
        }
    }
}
