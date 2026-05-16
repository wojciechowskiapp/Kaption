using System;
using System.Runtime;

namespace GI_Subtitles.Core.Runtime
{
    /// <summary>
    /// RAII-style helper that temporarily overrides <see cref="GCSettings.LatencyMode"/>
    /// and restores the previous mode on disposal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used around the OCR tick body so the GC favours short pauses over throughput while a
    /// frame is in flight. <see cref="GCLatencyMode.SustainedLowLatency"/> tells the runtime
    /// to avoid blocking Gen2 / LOH compactions until the scope is torn down — at the cost
    /// of higher memory footprint if left on for long stretches, which is exactly why this
    /// is scoped per-tick rather than process-wide.
    /// </para>
    /// <para>
    /// Works with both Workstation and Server GC. The <c>finally</c>-style restore in
    /// <see cref="Dispose"/> is load-bearing: if we leak the scope we leak memory.
    /// </para>
    /// </remarks>
    internal readonly struct LatencyModeScope : IDisposable
    {
        private readonly GCLatencyMode _previous;
        private readonly bool _applied;

        /// <summary>
        /// Captures the current <see cref="GCSettings.LatencyMode"/> and sets the runtime
        /// to <paramref name="mode"/>. Pass <see cref="GCLatencyMode.SustainedLowLatency"/>
        /// around latency-sensitive work.
        /// </summary>
        public LatencyModeScope(GCLatencyMode mode)
        {
            _previous = GCSettings.LatencyMode;
            // Cheap guard: skip the assignment + dispose restore if we're already in the
            // requested mode (e.g. nested scopes). Also avoids a no-op write on the hot path.
            if (_previous == mode)
            {
                _applied = false;
            }
            else
            {
                GCSettings.LatencyMode = mode;
                _applied = true;
            }
        }

        /// <summary>
        /// Enters a <see cref="GCLatencyMode.SustainedLowLatency"/> scope — recommended
        /// for the OCR tick body and other bounded real-time sections.
        /// </summary>
        public static LatencyModeScope SustainedLowLatency()
        {
            return new LatencyModeScope(GCLatencyMode.SustainedLowLatency);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_applied)
            {
                // Restoring the previous mode is CRITICAL — leaving SustainedLowLatency
                // on permanently blocks Gen2 compactions and leaks memory over time.
                GCSettings.LatencyMode = _previous;
            }
        }
    }
}
