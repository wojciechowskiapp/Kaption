using System;
using System.Collections.Generic;

namespace GI_Subtitles.Services.OCR
{
    /// <summary>
    /// Outcome of a single <see cref="OcrRecoveryPolicy.RecordTimeout"/> call.
    /// Counters are captured inside the policy lock so the log line and the
    /// recovery decision always describe the same instant.
    /// </summary>
    internal readonly struct OcrRecoveryDecision
    {
        public OcrRecoveryDecision(bool shouldRecover, int consecutiveTimeouts, int timeoutsInWindow)
        {
            ShouldRecover = shouldRecover;
            ConsecutiveTimeouts = consecutiveTimeouts;
            TimeoutsInWindow = timeoutsInWindow;
        }

        /// <summary>True when the caller should escalate to the next runtime-recovery level.</summary>
        public bool ShouldRecover { get; }

        /// <summary>Timeouts since the last successful inference.</summary>
        public int ConsecutiveTimeouts { get; }

        /// <summary>Timeouts inside <see cref="OcrRecoveryPolicy.RecoveryWindow"/>, successes included.</summary>
        public int TimeoutsInWindow { get; }
    }

    /// <summary>
    /// Decides when a working-but-stalling OCR provider has failed enough to
    /// justify rebuilding the engine one rung down (GPU → CPU → compatibility
    /// model).
    ///
    /// Two independent triggers, because the two failure shapes look different
    /// in the log:
    ///   * <b>Consecutive</b> — a provider that never completes a frame. Two in
    ///     a row is already conclusive.
    ///   * <b>Rolling window</b> — a provider that completes just often enough
    ///     to reset a consecutive counter while still stalling most frames.
    ///     That is exactly the PP-OCRv6 + DirectML pattern from the 2026-08-17
    ///     field report: successes interleaved with 15 s stalls meant the
    ///     consecutive-only rule never tripped and the user got no subtitles
    ///     for a whole session.
    ///
    /// Pure decision logic with no Config or engine dependency so it can be
    /// unit-tested with a synthetic clock. One instance per MainWindow; every
    /// method takes the internal lock because the two timeout call sites can
    /// run on the UI thread or on an async continuation depending on where
    /// TriggerOcrAsync was invoked from.
    /// </summary>
    internal sealed class OcrRecoveryPolicy
    {
        /// <summary>Consecutive timeouts (no success in between) that force recovery.</summary>
        public const int ConsecutiveTimeoutsBeforeRecovery = 2;

        /// <summary>Timeouts inside <see cref="RecoveryWindow"/> that force recovery even with successes interleaved.</summary>
        public const int WindowedTimeoutsBeforeRecovery = 3;

        /// <summary>Rolling window used by the windowed trigger.</summary>
        public static readonly TimeSpan RecoveryWindow = TimeSpan.FromSeconds(120);

        // Hard cap on the timestamp list. The window prune already bounds it
        // (a timeout costs at least OcrInferenceTimeoutSeconds), but a clock
        // jump must not be able to grow it without limit.
        private const int MaxTrackedTimeouts = 32;

        private readonly object _gate = new object();
        private readonly List<DateTime> _timeouts = new List<DateTime>();
        private int _consecutiveTimeouts;

        /// <summary>Timeouts since the last success. Snapshot — may be stale on return.</summary>
        public int ConsecutiveTimeouts
        {
            get { lock (_gate) { return _consecutiveTimeouts; } }
        }

        /// <summary>
        /// Records a live-frame timeout and reports whether runtime recovery
        /// should be scheduled.
        /// </summary>
        /// <param name="utcNow">Timeout instant, UTC. Injected so tests can drive a synthetic clock.</param>
        public OcrRecoveryDecision RecordTimeout(DateTime utcNow)
        {
            lock (_gate)
            {
                _consecutiveTimeouts++;
                PruneLocked(utcNow);
                _timeouts.Add(utcNow);
                if (_timeouts.Count > MaxTrackedTimeouts)
                    _timeouts.RemoveRange(0, _timeouts.Count - MaxTrackedTimeouts);

                bool shouldRecover =
                    _consecutiveTimeouts >= ConsecutiveTimeoutsBeforeRecovery ||
                    _timeouts.Count >= WindowedTimeoutsBeforeRecovery;

                return new OcrRecoveryDecision(
                    shouldRecover, _consecutiveTimeouts, _timeouts.Count);
            }
        }

        /// <summary>
        /// Records a completed inference. Clears the consecutive counter but
        /// deliberately keeps the rolling window — a single good frame does not
        /// undo the evidence that the provider is stalling.
        /// </summary>
        public void RecordSuccess()
        {
            lock (_gate)
            {
                _consecutiveTimeouts = 0;
            }
        }

        /// <summary>
        /// Drops all history. Called after an engine rebuild so the freshly
        /// loaded provider is judged on its own frames.
        /// </summary>
        public void Reset()
        {
            lock (_gate)
            {
                _consecutiveTimeouts = 0;
                _timeouts.Clear();
            }
        }

        /// <summary>Timeouts currently inside the rolling window. Test/diagnostics helper.</summary>
        public int CountTimeoutsInWindow(DateTime utcNow)
        {
            lock (_gate)
            {
                PruneLocked(utcNow);
                return _timeouts.Count;
            }
        }

        private void PruneLocked(DateTime utcNow)
        {
            DateTime cutoff = utcNow - RecoveryWindow;
            // Entries newer than "now" can only come from a backwards system
            // clock adjustment; drop them too rather than let them linger for
            // the rest of the session.
            _timeouts.RemoveAll(t => t <= cutoff || t > utcNow);
        }
    }
}
