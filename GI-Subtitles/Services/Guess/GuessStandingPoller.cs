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
        /// <summary>The player has no place this month (most installs never play ranked). A parsed standing still fired the event.</summary>
        NoStanding,
        /// <summary>Not signed in: nothing was sent.</summary>
        NoSession,
        /// <summary>401 or 403: skipped at Info. The heartbeat alone decides that a session is gone.</summary>
        Refused,
        /// <summary>Offline, a server error, an unknown route, an unreadable body.</summary>
        Failed,
    }

    /// <summary>
    /// Reads the player's guess standing in the background (R4 section 2.3): 2 minutes after start, then every 4 hours,
    /// every 12 hours while the player has no place this month, and 15 min → 1 h → 4 h after failures. It never touches
    /// the licence: a 401 or 403 is logged at Info and the next read waits the normal interval. Never on the OCR tick.
    /// </summary>
    public sealed class GuessStandingPoller : IDisposable
    {
        public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan Interval = TimeSpan.FromHours(4);
        public static readonly TimeSpan IdleInterval = TimeSpan.FromHours(12);
        public static readonly TimeSpan NoSessionRecheck = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan[] Backoff = { TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(4) };

        private readonly Func<CancellationToken, Task<GuessStanding>> _fetch;
        private readonly Func<bool> _hasSession;
        private readonly object _gate = new();
        private Timer _timer;
        private CancellationTokenSource _cts;
        private int _failures;
        private int _polling;
        private bool _disposed;

        /// <summary>Raised on a thread-pool thread with every parsed standing, with or without a place this month.</summary>
        public event Action<GuessStanding> StandingRead;

        public GuessStandingPoller(Func<CancellationToken, Task<GuessStanding>> fetch, Func<bool> hasSession)
        {
            _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
            _hasSession = hasSession ?? (() => true);
        }

        public bool IsRunning
        {
            get { lock (_gate) return _timer != null; }
        }

        /// <summary>Starts (or restarts) the schedule: the first read after <see cref="FirstDelay"/>.</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_disposed) return;
                StopLocked();
                _failures = 0;
                _cts = new CancellationTokenSource();
                _timer = new Timer(_ => _ = TickAsync(), null, FirstDelay, Timeout.InfiniteTimeSpan);
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
                _timer.Change(NextDelay(outcome, _failures), Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>One read. Thrown only for cancellation; every other failure is an outcome.</summary>
        public async Task<GuessPollOutcome> PollOnceAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _polling, 1) == 1) return GuessPollOutcome.Failed;
            try
            {
                if (!_hasSession()) return GuessPollOutcome.NoSession;
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
                    Logger.Log.Info("Guess standing: the server did not accept the session (401). Skipped; the session is left alone.");
                    _failures = 0;
                    return GuessPollOutcome.Refused;
                }
                catch (ForbiddenException)
                {
                    Logger.Log.Info("Guess standing: refused (403). Skipped.");
                    _failures = 0;
                    return GuessPollOutcome.Refused;
                }
                catch (Exception ex)
                {
                    _failures++;
                    Logger.Log.Info($"Guess standing unavailable ({ex.GetType().Name}: {ex.Message}); retry in {NextDelay(GuessPollOutcome.Failed, _failures)}.");
                    return GuessPollOutcome.Failed;
                }
                _failures = 0;
                if (standing == null) return GuessPollOutcome.NoStanding;
                // A read without a place is still news: it replaces what the strip said (a new month, say).
                try { StandingRead?.Invoke(standing); }
                catch (Exception ex) { Logger.Log.Warn($"Guess standing handler failed: {ex.Message}"); }
                return standing.Global == null ? GuessPollOutcome.NoStanding : GuessPollOutcome.Read;
            }
            finally
            {
                Volatile.Write(ref _polling, 0);
            }
        }

        /// <summary>The wait before the next read. <paramref name="failures"/> counts the failures in a row, this one included.</summary>
        public static TimeSpan NextDelay(GuessPollOutcome outcome, int failures) => outcome switch
        {
            GuessPollOutcome.Read => Interval,
            GuessPollOutcome.NoStanding => IdleInterval,
            GuessPollOutcome.NoSession => NoSessionRecheck,
            GuessPollOutcome.Refused => Interval,
            _ => Backoff[Math.Clamp(failures - 1, 0, Backoff.Length - 1)],
        };

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
