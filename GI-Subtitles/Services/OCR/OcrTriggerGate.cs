using System;

namespace GI_Subtitles.Services.OCR
{
    /// <summary>
    /// The "should we run OCR on this frame?" state machine.
    ///
    /// <para>Extracted from <c>MainWindow.GetOCR</c> so that it has exactly one
    /// implementation. It used to be inline, which meant the only way to reason
    /// about pacing was to read a 200-line block interleaved with Mat pooling,
    /// window handles and dispatcher calls — and the only way to measure it was
    /// to play the game and watch. The benchmark harness
    /// (<c>benchmark/Kaption.Benchmark</c>) drives this same class over recorded
    /// frames, so a change to the gate is reflected in the numbers without anyone
    /// having to keep a copy in sync.</para>
    ///
    /// <para><b>Deliberately owns no pixels and no clock.</b> Callers do the Mat
    /// diffing (via <see cref="BinaryFrameMetrics"/>) and pass the resulting
    /// ratios in; callers also supply "now". The app passes
    /// <c>DateTime.UtcNow</c>; the benchmark passes a virtual clock derived from
    /// frame index, which is what makes a replay reproducible.</para>
    ///
    /// <para>Not thread-safe. The OCR tick is single-threaded on the WPF
    /// dispatcher, and the benchmark replays serially.</para>
    /// </summary>
    public sealed class OcrTriggerGate
    {
        /// <summary>
        /// Fraction of the lit text pixels that must change before a frame counts
        /// as different. Normalised by <see cref="BinaryFrameMetrics"/>, so this
        /// is ~5% of glyph pixels rather than 1% of the whole crop.
        /// </summary>
        public const double DefaultChangeThreshold = 0.01;

        /// <summary>
        /// Hard floor on the consecutive-stable-frame requirement. A single-frame
        /// trigger at a 100 ms interval fires during typewriter pauses and produces
        /// partial-match flicker, so 2 is enforced even if a profile or Config key
        /// asks for less.
        /// </summary>
        public const int MinStableFrames = 2;

        private readonly double _changeThreshold;

        private int _consecutiveStableFrames;
        private DateTime _changedVsOcrSince = DateTime.MinValue;

        public OcrTriggerGate(double changeThreshold = DefaultChangeThreshold)
        {
            _changeThreshold = changeThreshold;
        }

        /// <summary>Consecutive stable frames seen so far. Diagnostic.</summary>
        public int ConsecutiveStableFrames => _consecutiveStableFrames;

        /// <summary>
        /// When the screen first diverged from the OCR baseline, or
        /// <see cref="DateTime.MinValue"/> when it currently matches. Callers use
        /// this for their own staleness timers.
        /// </summary>
        public DateTime ChangedVsOcrSince => _changedVsOcrSince;

        /// <summary>
        /// One tick's worth of measurements.
        ///
        /// <para>The three ratios use <c>null</c> for "no sample available" and
        /// <see cref="double.PositiveInfinity"/> for "incomparable, treat as
        /// maximally changed" (frame size or channel-count mismatch). That keeps
        /// the threshold comparison in one place instead of leaving each caller to
        /// resolve its own booleans.</para>
        /// </summary>
        public struct Inputs
        {
            /// <summary>Change vs the immediately preceding tick's frame.
            /// null = no previous frame yet, which counts as stable (a first frame
            /// is not evidence of motion).</summary>
            public double? ChangeVsPrevious;

            /// <summary>Change vs the frame OCR last ran on.
            /// null = no OCR baseline yet, which counts as changed so the first
            /// stable frame gets read.</summary>
            public double? ChangeVsOcrBaseline;

            /// <summary>Change vs the frame N ticks back.
            /// null = the window has not filled yet, which counts as NOT stable —
            /// this is the check that catches typewriter animation, so it must not
            /// pass by default.</summary>
            public double? ChangeOverWindow;

            /// <summary>Whether the dialogue engine currently has a single
            /// forward edge, which lowers the stable-frame requirement.</summary>
            public bool HasSingleChainPrediction;

            /// <summary>Stable frames required when chain prediction is active.</summary>
            public int StableFramesChain;

            /// <summary>Stable frames required otherwise.</summary>
            public int StableFramesDefault;

            /// <summary>Escape hatch: read anyway once the screen has been
            /// changing this long without ever settling.</summary>
            public double ForceOcrAfterSeconds;

            /// <summary>Caller-supplied clock.</summary>
            public DateTime NowUtc;
        }

        /// <summary>The gate's verdict plus the reasoning behind it, so callers
        /// can log and the harness can attribute each read to a path.</summary>
        public struct Decision
        {
            /// <summary>Run OCR on this frame (subject to the caller's own
            /// in-flight and min-interval guards).</summary>
            public bool ReadyForOcr;

            /// <summary>The windowed-stability path fired — the line has settled.</summary>
            public bool StableOverWindow;

            /// <summary>The consecutive-stable-frames "eager preview" path fired.</summary>
            public bool EagerPreview;

            /// <summary>Only the timeout fired; the screen never settled. On a
            /// typewriter game this is the path that samples mid-animation.</summary>
            public bool Forced;

            /// <summary>Screen differs from the OCR baseline.</summary>
            public bool ChangedVsOcr;

            /// <summary>This tick is the first at which the screen diverged from
            /// the baseline. Hook for predictive pre-display.</summary>
            public bool ChangedVsOcrJustStarted;

            /// <summary>Stable-frame count after this tick.</summary>
            public int ConsecutiveStableFrames;

            /// <summary>Threshold actually applied, after the hard floor.</summary>
            public int StableFramesNeeded;
        }

        /// <summary>
        /// Advance the state machine by one tick and report whether OCR should run.
        ///
        /// <para>Mutates internal counters, so call exactly once per tick. When the
        /// caller then actually starts OCR it must call
        /// <see cref="NotifyOcrStarted"/>, and when the new baseline frame is
        /// committed, <see cref="NotifyBaselineCommitted"/>.</para>
        /// </summary>
        public Decision Evaluate(in Inputs input)
        {
            // Stable vs previous frame. No previous frame => stable.
            bool isStableVsPrev = !input.ChangeVsPrevious.HasValue
                || input.ChangeVsPrevious.Value <= _changeThreshold;

            if (isStableVsPrev) _consecutiveStableFrames++;
            else _consecutiveStableFrames = 0;

            // Changed vs the OCR baseline. No baseline => changed.
            bool changedVsOcr = !input.ChangeVsOcrBaseline.HasValue
                || input.ChangeVsOcrBaseline.Value > _changeThreshold;

            // Stable over the lookback window. Window not filled => not stable.
            bool isStableOverWindow = input.ChangeOverWindow.HasValue
                && input.ChangeOverWindow.Value <= _changeThreshold;

            // Track how long we have diverged from the baseline.
            bool justStarted = false;
            if (changedVsOcr)
            {
                if (_changedVsOcrSince == DateTime.MinValue)
                {
                    _changedVsOcrSince = input.NowUtc;
                    justStarted = true;
                }
            }
            else
            {
                _changedVsOcrSince = DateTime.MinValue;
            }

            int stableFramesNeeded = input.HasSingleChainPrediction
                ? Math.Max(MinStableFrames, input.StableFramesChain)
                : Math.Max(MinStableFrames, input.StableFramesDefault);

            bool readyForOcr = changedVsOcr
                && (isStableOverWindow || _consecutiveStableFrames >= stableFramesNeeded);

            bool forced = !readyForOcr
                && changedVsOcr
                && _changedVsOcrSince > DateTime.MinValue
                && (input.NowUtc - _changedVsOcrSince).TotalSeconds > input.ForceOcrAfterSeconds;

            if (forced) readyForOcr = true;

            return new Decision
            {
                ReadyForOcr = readyForOcr,
                StableOverWindow = isStableOverWindow,
                EagerPreview = readyForOcr && !isStableOverWindow && !forced,
                Forced = forced,
                ChangedVsOcr = changedVsOcr,
                ChangedVsOcrJustStarted = justStarted,
                ConsecutiveStableFrames = _consecutiveStableFrames,
                StableFramesNeeded = stableFramesNeeded,
            };
        }

        /// <summary>Call when OCR is actually dispatched for this tick.</summary>
        public void NotifyOcrStarted() => _consecutiveStableFrames = 0;

        /// <summary>Call when the OCR baseline frame has been replaced, so the
        /// divergence timer restarts from the new reference.</summary>
        public void NotifyBaselineCommitted() => _changedVsOcrSince = DateTime.MinValue;

        /// <summary>Clear all state — conversation end, OCR stop, region change.</summary>
        public void Reset()
        {
            _consecutiveStableFrames = 0;
            _changedVsOcrSince = DateTime.MinValue;
        }
    }
}
