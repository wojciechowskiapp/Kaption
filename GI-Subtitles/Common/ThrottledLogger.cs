using System;
using System.Collections.Generic;

namespace GI_Subtitles.Common
{
    /// <summary>
    /// Collapses a message that repeats on every OCR tick down to roughly one
    /// line per interval per distinct key, and reports how many occurrences
    /// were swallowed when logging resumes.
    ///
    /// Motivating case (2026-08-14 field log): DXGI lost the desktop to a
    /// secure-desktop prompt, GDI then failed with "invalid handle" five times
    /// a second, and the app wrote three error lines per tick for over an hour.
    /// The information content of line 2 through line 54,000 is zero, and the
    /// flood buries everything else in the log.
    ///
    /// Deliberately tiny: a dictionary, a lock and a cap. Nothing here is a
    /// general-purpose logging framework — call sites keep writing their own
    /// messages, they just ask first.
    /// </summary>
    internal sealed class ThrottledLogger
    {
        // Distinct keys are exception messages, so a pathological caller could
        // grow this without bound. Clearing wholesale past the cap is fine:
        // the worst case is one extra line per key after a reset.
        private const int MaxTrackedKeys = 64;

        private readonly TimeSpan _interval;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        public ThrottledLogger(TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interval));
            _interval = interval;
        }

        /// <summary>
        /// True when the caller should emit its message now.
        /// <paramref name="suppressedSinceLastLog"/> is the number of calls
        /// swallowed since the previous emit for this key (0 on the first one).
        /// </summary>
        public bool ShouldLog(string key, out int suppressedSinceLastLog)
            => ShouldLog(key, DateTime.UtcNow, out suppressedSinceLastLog);

        /// <summary>Clock-injectable overload for unit tests.</summary>
        public bool ShouldLog(string key, DateTime utcNow, out int suppressedSinceLastLog)
        {
            key = key ?? string.Empty;
            lock (_gate)
            {
                if (_entries.Count >= MaxTrackedKeys && !_entries.ContainsKey(key))
                    _entries.Clear();

                if (!_entries.TryGetValue(key, out Entry entry))
                {
                    _entries[key] = new Entry { LastLoggedUtc = utcNow, Suppressed = 0 };
                    suppressedSinceLastLog = 0;
                    return true;
                }

                if (utcNow - entry.LastLoggedUtc < _interval && utcNow >= entry.LastLoggedUtc)
                {
                    entry.Suppressed++;
                    _entries[key] = entry;
                    suppressedSinceLastLog = 0;
                    return false;
                }

                suppressedSinceLastLog = entry.Suppressed;
                _entries[key] = new Entry { LastLoggedUtc = utcNow, Suppressed = 0 };
                return true;
            }
        }

        /// <summary>
        /// Forgets a key so the next occurrence logs immediately. Call after a
        /// recovery so the first failure of the next episode is not swallowed
        /// by the previous episode's window.
        /// </summary>
        public void Reset(string key)
        {
            lock (_gate)
            {
                _entries.Remove(key ?? string.Empty);
            }
        }

        /// <summary>Forgets every key.</summary>
        public void ResetAll()
        {
            lock (_gate)
            {
                _entries.Clear();
            }
        }

        /// <summary>
        /// Formats the standard " (N identical errors suppressed…)" suffix, or
        /// an empty string when nothing was swallowed.
        /// </summary>
        public string SuppressionSuffix(int suppressedSinceLastLog)
        {
            return suppressedSinceLastLog <= 0
                ? string.Empty
                : $" ({suppressedSinceLastLog} identical occurrences suppressed in the last " +
                  $"{_interval.TotalSeconds:F0}s)";
        }

        private struct Entry
        {
            public DateTime LastLoggedUtc;
            public int Suppressed;
        }
    }
}
