using System;

namespace GI_Subtitles.Services.QuickTranslate
{
    /// <summary>
    /// Countdown state for one popup. The window feeds it elapsed time; it decides when to close.
    /// Hover pauses, any interaction pins, and a null duration (Manual mode) starts pinned.
    /// </summary>
    public sealed class QuickTranslateDismissal
    {
        public static readonly TimeSpan MinAfterResume = TimeSpan.FromSeconds(1.5);

        private readonly TimeSpan _total;
        private TimeSpan _remaining;
        private bool _paused;

        public bool IsPinned { get; private set; }

        public double RemainingFraction =>
            IsPinned || _total <= TimeSpan.Zero ? 1.0 : Math.Clamp(_remaining.TotalMilliseconds / _total.TotalMilliseconds, 0, 1);

        public QuickTranslateDismissal(TimeSpan? duration)
        {
            if (duration == null || duration.Value <= TimeSpan.Zero)
            {
                IsPinned = true;
                _total = TimeSpan.Zero;
            }
            else
            {
                _total = duration.Value;
            }
            _remaining = _total;
        }

        public void Pause() => _paused = true;

        public void Resume()
        {
            if (!_paused) return;
            _paused = false;
            if (_remaining < MinAfterResume) _remaining = MinAfterResume;
        }

        public void Pin() => IsPinned = true;

        /// <returns>True when the popup should close now.</returns>
        public bool Tick(TimeSpan elapsed)
        {
            if (IsPinned || _paused) return false;
            _remaining -= elapsed;
            return _remaining <= TimeSpan.Zero;
        }
    }
}
