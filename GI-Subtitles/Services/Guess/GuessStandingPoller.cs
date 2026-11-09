using System;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Services.Network;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Guess
{
    public enum GuessPollOutcome
    {
        /// <summary>A standing with a global place.</summary>
        Read,
        /// <summary>Played this month without a place yet (0 points): read at the normal interval, a place may come any time.</summary>
        Unplaced,
        /// <summary>Nothing played this month (most installs never play ranked), or an answer without a standing.</summary>
        NoStanding,
        /// <summary>Not signed in: nothing was sent.</summary>
        NoSession,
        /// <summary>401 or 403: skipped at Info. The heartbeat alone decides that a session is gone.</summary>
        Refused,
        /// <summary>Offline, a server error, an unknown route, an unreadable body.</summary>
        Failed,
    }

    /// <summary>
    /// Reads the player's guess standing in the background (R4 section 2.3) on a <see cref="GuessPollSchedule"/>: the
    /// first read after FirstDelay, then every Interval, every IdleInterval while nothing is played this month, and the
    /// Retry steps after failures. The schedule comes from Config pins and the server's <c>poll</c> (the last standing).
    /// It never touches the licence: a 401 or 403 is logged at Info and the next read waits the normal interval. Never
    /// on the OCR tick.
    /// </summary>
    public sealed class GuessStandingPoller : IDisposable
    {
        private readonly Func<CancellationToken, Task<GuessStanding>> _fetch;
        private readonly Func<bool> _hasSession;
        private readonly Func<GuessStanding, GuessPollSchedule> _schedule;
        private readonly object _gate = new();
        private Timer _timer;
        private CancellationTokenSource _cts;
        private int _failures;
        private int _polling;
        private bool _disposed;
        private bool _noSessionLogged;
        private GuessStanding _last;

        /// <summary>Raised on a thread-pool thread with every parsed standing, with or without a place this month.</summary>
        public event Action<GuessStanding> StandingRead;

        /// <param name="schedule">The schedule for a standing (null before the first read); default: no Config pins.</param>
        public GuessStandingPoller(Func<CancellationToken, Task<GuessStanding>> fetch, Func<bool> hasSession,
                                   Func<GuessStanding, GuessPollSchedule> schedule = null)
        {
            _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
            _hasSession = hasSession ?? (() => true);
            _schedule = schedule ?? (s => GuessPollSchedule.Resolve(GuessPollPins.None, s?.PollEvery, s?.PollIdle));
        }

        public bool IsRunning
        {
            get { lock (_gate) return _timer != null; }
        }

        /// <summary>The schedule now: Config pins, then the last standing's <c>poll</c>, then the defaults.</summary>
        public GuessPollSchedule Schedule => _schedule(Volatile.Read(ref _last));

        /// <summary>Starts (or restarts) the schedule: the first read after the schedule's FirstDelay.</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_disposed) return;
                StopLocked();
                _failures = 0;
                _cts = new CancellationTokenSource();
                _timer = new Timer(_ => _ = TickAsync(), null, Schedule.FirstDelay, Timeout.InfiniteTimeSpan);
            }
        }

        public void Stop()
        {
            lock (_gate) StopLocked();
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
            GuessPollOutcome outcome;
            try
            {
                outcome = await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // stopped
            }
            lock (_gate)
            {
                if (_timer == null || ct.IsCancellationRequested) return;
                _timer.Change(NextDelay(outcome, _failures, Schedule), Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>One read. Thrown only for cancellation; every other failure is an outcome.</summary>
        public async Task<GuessPollOutcome> PollOnceAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _polling, 1) == 1) return GuessPollOutcome.Failed;
            try
            {
                if (!_hasSession())
                {
                    // Once per sign-out, not every recheck.
                    if (!_noSessionLogged)
                        Logger.Log.Info($"Guess standing: not signed in, nothing sent; checking again every {Schedule.NoSessionRecheck}.");
                    _noSessionLogged = true;
                    return GuessPollOutcome.NoSession;
                }
                _noSessionLogged = false;
                GuessStanding standing;
                try
                {
                    standing = await _fetch(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (UnauthorizedException)
                {
                    _failures = 0;
                    Logger.Log.Info($"Guess standing: the server did not accept the session (401). Skipped, the session is left alone; next read in {NextDelay(GuessPollOutcome.Refused, 0, Schedule)}.");
                    return GuessPollOutcome.Refused;
                }
                catch (ForbiddenException)
                {
                    _failures = 0;
                    Logger.Log.Info($"Guess standing: refused (403). Skipped; next read in {NextDelay(GuessPollOutcome.Refused, 0, Schedule)}.");
                    return GuessPollOutcome.Refused;
                }
                catch (Exception ex)
                {
                    _failures++;
                    Logger.Log.Info($"Guess standing unavailable ({ex.GetType().Name}: {ex.Message}); retry in {NextDelay(GuessPollOutcome.Failed, _failures, Schedule)}.");
                    return GuessPollOutcome.Failed;
                }
                _failures = 0;
                if (standing == null)
                {
                    Logger.Log.Info($"Guess standing: the answer held no standing; next read in {NextDelay(GuessPollOutcome.NoStanding, 0, Schedule)}.");
                    return GuessPollOutcome.NoStanding;
                }
                Volatile.Write(ref _last, standing);
                // A read without a place is still news: it replaces what the strip said (a new month, say).
                try { StandingRead?.Invoke(standing); }
                catch (Exception ex) { Logger.Log.Warn($"Guess standing handler failed: {ex.Message}"); }
                return OutcomeOf(standing);
            }
            finally
            {
                Volatile.Write(ref _polling, 0);
            }
        }

        /// <summary>A parsed standing's outcome: a place, a play without one, or nothing played.</summary>
        public static GuessPollOutcome OutcomeOf(GuessStanding standing) =>
            standing == null ? GuessPollOutcome.NoStanding
            : standing.Global != null ? GuessPollOutcome.Read
            : standing.PlayedThisMonth ? GuessPollOutcome.Unplaced
            : GuessPollOutcome.NoStanding;

        /// <summary>The wait after reading <paramref name="standing"/> (its own <c>poll</c> applies), for the read's log line.</summary>
        public TimeSpan DelayAfter(GuessStanding standing) => NextDelay(OutcomeOf(standing), 0, _schedule(standing));

        /// <summary>The wait before the next read. <paramref name="failures"/> counts the failures in a row, this one included.</summary>
        public static TimeSpan NextDelay(GuessPollOutcome outcome, int failures, GuessPollSchedule schedule)
        {
            schedule ??= GuessPollSchedule.Default;
            return outcome switch
            {
                GuessPollOutcome.Read or GuessPollOutcome.Unplaced or GuessPollOutcome.Refused => schedule.Interval,
                GuessPollOutcome.NoStanding => schedule.IdleInterval,
                GuessPollOutcome.NoSession => schedule.NoSessionRecheck,
                _ => schedule.RetryAfter(failures),
            };
        }

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
