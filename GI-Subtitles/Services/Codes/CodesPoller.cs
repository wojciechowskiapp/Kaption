using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Services.Observability;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Codes
{
    public enum CodesFetchOutcome
    {
        /// <summary>A list was read (with or without active codes).</summary>
        Read,
        /// <summary>Nothing sent: no game with a codes page is selected, or the window is not on screen.</summary>
        Skipped,
        /// <summary>Another read was running: this one is remembered and runs after it (the timer is not rearmed).</summary>
        Busy,
        /// <summary>Offline, DNS, TLS, a timeout or a dropped connection.</summary>
        NetworkFailed,
        /// <summary>429, or 503 with a Retry-After: the server asked for a wait.</summary>
        RateLimited,
        /// <summary>Another 5xx.</summary>
        ServerFailed,
        /// <summary>A body that is not the list, or a 4xx.</summary>
        BadResponse,
    }

    /// <summary>A game's last good list and when it was read.</summary>
    public sealed record CodesSnapshot(string Game, RedeemCodeList List, DateTimeOffset ReadAt);

    /// <summary>
    /// Reads the selected game's codes for the Start page card on a <see cref="CodesSchedule"/>. Nothing is sent while
    /// the window is off screen; <see cref="RequestRefresh"/> (window shown, game switched) reads at once when the
    /// game's list is older than the interval and no backoff is running. Each game keeps its last good list, so a
    /// failed read never blanks the card by itself (the card model hides a list past StaleAfter).
    /// Logging (one line per failure kind, not per try): the first failure of a kind in a streak is a Warn, repeats
    /// are Debug, the first good read after failures is an Info; a changed list is an Info with the Worker's counts;
    /// the timing of every read is Debug. A failure kind that changes leaves a Sentry breadcrumb (game, kind, wait),
    /// never an event, so a later crash report shows that the API was unreachable. No player data is sent or kept.
    /// </summary>
    public sealed class CodesPoller : IDisposable
    {
        private readonly Func<string, CancellationToken, Task<RedeemCodeList>> _fetch;
        private readonly Func<string> _game;
        private readonly Func<bool> _isActive;
        private readonly Func<CodesSchedule> _schedule;
        private readonly Func<DateTimeOffset> _now;
        private readonly object _gate = new();
        private readonly Dictionary<string, CodesSnapshot> _lists = new(StringComparer.Ordinal);
        private Timer _timer;
        private CancellationTokenSource _cts;
        private int _polling;
        private int _refreshPending;
        private int _failures;
        private CodesFetchOutcome? _lastLoggedFailure;
        private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;
        private bool _disposed;

        /// <summary>Raised on a thread-pool thread after each good read, and by RequestRefresh with a cached list.</summary>
        public event Action<CodesSnapshot> ListRead;

        /// <summary>Raised on a thread-pool thread after a failed read, so the card can drop a list that went stale.</summary>
        public event Action<CodesFetchOutcome> ReadFailed;

        /// <param name="fetch">Reads one game's list (wire id); throws on failure (KaptionApiClient.GetRedeemCodesAsync).</param>
        /// <param name="game">The selected game's wire id when it has a codes page, else null.</param>
        /// <param name="isActive">Whether the card can be seen (the window is on screen).</param>
        public CodesPoller(Func<string, CancellationToken, Task<RedeemCodeList>> fetch, Func<string> game, Func<bool> isActive = null,
                           Func<CodesSchedule> schedule = null, Func<DateTimeOffset> now = null)
        {
            _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _isActive = isActive ?? (() => true);
            _schedule = schedule ?? (() => CodesSchedule.Default);
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        public CodesSchedule Schedule => _schedule();

        /// <summary>Whether a read is in flight.</summary>
        public bool IsReading => Volatile.Read(ref _polling) == 1;

        /// <summary>Failures in a row since the last good read.</summary>
        public int Failures => Volatile.Read(ref _failures);

        /// <summary>No read is sent before this (a failure's wait); MinValue when none is running.</summary>
        public DateTimeOffset BackoffUntil
        {
            get { lock (_gate) return _backoffUntil; }
        }

        /// <summary>The game's last good list, or null.</summary>
        public CodesSnapshot Last(string game)
        {
            if (game == null) return null;
            lock (_gate) return _lists.TryGetValue(game, out var s) ? s : null;
        }

        /// <summary>Starts the schedule: the first read after FirstDelay.</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_disposed) return;
                StopLocked();
                _cts = new CancellationTokenSource();
                _timer = new Timer(_ => _ = TickAsync(), null, Schedule.FirstDelay, Timeout.InfiniteTimeSpan);
            }
        }

        public void Stop()
        {
            lock (_gate) StopLocked();
        }

        /// <summary>
        /// The window came on screen or the game changed: hands the cached list over at once (ListRead), and reads now
        /// when it is older than the interval (or missing) and no backoff is running. During a read the request is
        /// remembered and runs right after it, for whichever game is selected then. Returns whether a read is coming.
        /// </summary>
        public bool RequestRefresh()
        {
            string game = _game();
            CodesSnapshot cached = Last(game);
            if (cached != null) Raise(cached);
            if (game == null) return false;
            DateTimeOffset now = _now();
            lock (_gate)
            {
                if (_timer == null || _disposed) return false;
                if (cached != null && now - cached.ReadAt < Schedule.Interval) return false;
                if (now < _backoffUntil) return false;
                // Remembered first: a read finishing right now checks the flag under this lock before it rearms.
                _refreshPending = 1;
                if (!IsReading) _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                return true;
            }
        }

        private void StopLocked()
        {
            _timer?.Dispose();
            _timer = null;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        private async Task TickAsync()
        {
            CancellationToken ct;
            lock (_gate)
            {
                if (_timer == null || _cts == null) return;
                ct = _cts.Token;
            }
            (CodesFetchOutcome outcome, TimeSpan delay) result;
            try
            {
                result = await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // stopped
            }
            catch (Exception ex)
            {
                // A handler's own bug: keep the schedule, never take the app down.
                Logger.Log.Warn($"Codes: read failed unexpectedly ({ex.GetType().Name}: {ex.Message}).");
                result = (CodesFetchOutcome.BadResponse, Schedule.Interval);
            }
            if (result.outcome == CodesFetchOutcome.Busy) return; // the running read rearms the timer
            lock (_gate)
            {
                if (_timer == null || ct.IsCancellationRequested) return;
                TimeSpan delay = result.delay;
                // A refresh asked for during this read (a game switch, the window shown): now, when still needed.
                if (Interlocked.Exchange(ref _refreshPending, 0) == 1 && NeedsRead()) delay = TimeSpan.Zero;
                _timer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>The selected game can be seen and its list is missing or older than the interval, with no backoff running.</summary>
        private bool NeedsRead()
        {
            string game = _game();
            if (game == null || !_isActive()) return false;
            DateTimeOffset now = _now();
            if (now < _backoffUntil) return false;
            CodesSnapshot cached = Last(game);
            return cached == null || now - cached.ReadAt >= Schedule.Interval;
        }

        /// <summary>One read and the wait before the next. Thrown only for cancellation; every failure is an outcome.</summary>
        public async Task<(CodesFetchOutcome Outcome, TimeSpan Next)> PollOnceAsync(CancellationToken ct)
        {
            CodesSchedule schedule = Schedule;
            if (Interlocked.Exchange(ref _polling, 1) == 1)
            {
                Interlocked.Exchange(ref _refreshPending, 1);
                return (CodesFetchOutcome.Busy, schedule.Interval);
            }
            try
            {
                string game = _game();
                if (game == null || !_isActive()) return (CodesFetchOutcome.Skipped, schedule.Interval);

                var watch = Stopwatch.StartNew();
                RedeemCodeList list;
                TimeSpan? serverWait = null;
                CodesFetchOutcome failure;
                string detail;
                try
                {
                    list = await _fetch(game, ct).ConfigureAwait(false);
                    if (list == null) throw new CodesFormatException("no body");
                    return (Succeeded(game, list, watch.Elapsed), schedule.Interval);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (RateLimitedException ex)
                {
                    failure = CodesFetchOutcome.RateLimited;
                    serverWait = ex.RetryAfter;
                    detail = $"the server asked to wait {ex.RetryAfter.TotalSeconds:N0} s";
                }
                catch (Exception ex)
                {
                    failure = Classify(ex);
                    detail = $"{ex.GetType().Name}: {Describe(ex)}";
                }

                int failures = Interlocked.Increment(ref _failures);
                TimeSpan wait = NextDelay(failure, failures, serverWait, schedule);
                lock (_gate) _backoffUntil = _now() + wait;
                Logged(game, failure, failures, wait, detail, watch.Elapsed);
                try { ReadFailed?.Invoke(failure); }
                catch (Exception ex) { Logger.Log.Warn($"Codes: card update failed: {ex.Message}"); }
                return (failure, wait);
            }
            finally
            {
                Volatile.Write(ref _polling, 0);
            }
        }

        private CodesFetchOutcome Succeeded(string game, RedeemCodeList list, TimeSpan took)
        {
            int failedBefore = Interlocked.Exchange(ref _failures, 0);
            var snapshot = new CodesSnapshot(game, list, _now());
            CodesSnapshot previous;
            lock (_gate)
            {
                _lists.TryGetValue(game, out previous);
                _lists[game] = snapshot;
                _backoffUntil = DateTimeOffset.MinValue;
            }
            if (failedBefore > 0)
            {
                Logger.Log.Info($"Codes: api reachable again after {failedBefore} failed read(s).");
                _lastLoggedFailure = null;
            }
            if (Logger.IsDebugEnabled)
                Logger.Log.Debug($"Codes: {game} read in {took.TotalMilliseconds:N0} ms ({list.Active.Count} active, {list.ExpiredCount} expired).");
            if (previous == null || !SameCodes(previous.List, list))
                Logger.Log.Info($"Codes: {game} {(previous == null ? "first read" : "list changed")}: {list.Active.Count} active, "
                                + $"{list.ExpiredCount} expired in 30 days, checked {(list.CheckedAt?.ToString("u") ?? "never")}.");
            Raise(snapshot);
            return CodesFetchOutcome.Read;
        }

        private void Logged(string game, CodesFetchOutcome failure, int failures, TimeSpan wait, string detail, TimeSpan took)
        {
            if (_lastLoggedFailure != failure)
            {
                _lastLoggedFailure = failure;
                Logger.Log.Warn($"Codes: {game} list unavailable ({failure}, {detail}); next try in {wait}. Repeats of this are logged at Debug.");
                Breadcrumb(game, failure, wait);
            }
            else if (Logger.IsDebugEnabled)
            {
                Logger.Log.Debug($"Codes: {game} still unavailable ({failure}, failure {failures} in a row, {took.TotalMilliseconds:N0} ms); next try in {wait}.");
            }
        }

        private static void Breadcrumb(string game, CodesFetchOutcome failure, TimeSpan wait)
        {
            if (!CrashReportingService.IsEnabled) return;
            try
            {
                Sentry.SentrySdk.AddBreadcrumb($"codes list unavailable: {failure}", "codes", "http",
                    new Dictionary<string, string> { ["game"] = game, ["kind"] = failure.ToString(), ["next_try_s"] = ((int)wait.TotalSeconds).ToString() },
                    Sentry.BreadcrumbLevel.Warning);
            }
            catch
            {
                // A breadcrumb is a nicety; never fail a read over it.
            }
        }

        private void Raise(CodesSnapshot snapshot)
        {
            try { ListRead?.Invoke(snapshot); }
            catch (Exception ex) { Logger.Log.Warn($"Codes: card update failed: {ex.Message}"); }
        }

        /// <summary>The failure kind of an exception from the fetch.</summary>
        public static CodesFetchOutcome Classify(Exception ex) => ex switch
        {
            RateLimitedException => CodesFetchOutcome.RateLimited,
            ApiUnavailableException { InnerException: System.Net.Http.HttpRequestException { StatusCode: not null } h } when (int)h.StatusCode >= 500 => CodesFetchOutcome.ServerFailed,
            ApiUnavailableException or System.Net.Http.HttpRequestException or TimeoutException or OperationCanceledException => CodesFetchOutcome.NetworkFailed,
            _ => CodesFetchOutcome.BadResponse,
        };

        /// <summary>The wait before the next read after <paramref name="outcome"/>; failures count this one.</summary>
        public static TimeSpan NextDelay(CodesFetchOutcome outcome, int failures, TimeSpan? serverWait, CodesSchedule schedule)
        {
            schedule ??= CodesSchedule.Default;
            return outcome is CodesFetchOutcome.Read or CodesFetchOutcome.Skipped or CodesFetchOutcome.Busy
                ? schedule.Interval
                : schedule.RetryAfter(Math.Max(1, failures), serverWait);
        }

        private static bool SameCodes(RedeemCodeList a, RedeemCodeList b) =>
            a.Checked == b.Checked && a.Active.Select(c => c.Code).SequenceEqual(b.Active.Select(c => c.Code), StringComparer.Ordinal);

        private static string Describe(Exception ex) => ex.InnerException == null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})";

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                StopLocked();
            }
        }
    }
}
