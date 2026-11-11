using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Services.Codes
{
    /// <summary>Values pinned in Config (docs/CONFIG.md, "Game codes card"); null where the file says nothing.</summary>
    public sealed record CodesSchedulePins(int? FirstDelaySeconds, int? RefreshMinutes, IReadOnlyList<int> RetryMinutes)
    {
        public const string FirstDelaySecondsKey = "Codes.FirstDelaySeconds";
        public const string RefreshMinutesKey = "Codes.RefreshMinutes";
        public const string RetryMinutesKey = "Codes.RetryMinutes";

        public static readonly CodesSchedulePins None = new(null, null, null);

        /// <summary>The pins in Config; a missing key, or one that is not a number, is no pin.</summary>
        public static CodesSchedulePins FromConfig() => From(key => Config.Has(key) ? Config.Get<JsonNode>(key, null) : null);

        public static CodesSchedulePins From(Func<string, JsonNode> read)
        {
            int[] steps = read(RetryMinutesKey) is JsonArray a ? a.Select(Number).Where(n => n.HasValue).Select(n => n.Value).ToArray() : null;
            return new CodesSchedulePins(Number(read(FirstDelaySecondsKey)), Number(read(RefreshMinutesKey)), steps);
        }

        private static int? Number(JsonNode n)
        {
            if (n is not JsonValue v) return null;
            if (v.TryGetValue(out int i)) return i;
            if (v.TryGetValue(out double d) && double.IsFinite(d)) return (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue);
            return null;
        }
    }

    /// <summary>
    /// When the Start page's codes card reads <c>/api/app/codes</c>: shortly after the window opens, then every
    /// <see cref="Interval"/> while the window is on screen, and the <see cref="Retry"/> steps after failures. A
    /// server's Retry-After only ever lengthens a wait, up to <see cref="MaxRetryAfter"/>. Each value: a Config pin,
    /// else the default, then clamped, so a bad file can neither hammer the API nor freeze the list for days.
    /// </summary>
    public sealed record CodesSchedule(TimeSpan FirstDelay, TimeSpan Interval, IReadOnlyList<TimeSpan> Retry)
    {
        public static readonly TimeSpan MaxFirstDelay = TimeSpan.FromMinutes(5);
        /// <summary>The Worker caches the list for 60 s and the codes worker checks every 15 min: faster buys nothing.</summary>
        public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan MaxInterval = TimeSpan.FromHours(6);
        public static readonly TimeSpan MinRetry = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan MaxRetry = TimeSpan.FromHours(6);
        /// <summary>The longest wait a Retry-After header can ask for; a larger (or hostile) value is cut to this.</summary>
        public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(6);
        public const int MaxRetrySteps = 8;
        /// <summary>A list kept through failures is shown for this many intervals after it was read, then hidden.</summary>
        public const int StaleIntervals = 2;

        /// <summary>3 s after the window opens, then every 30 min; 5 → 15 → 30 min after failures in a row.</summary>
        public static readonly CodesSchedule Default = new(
            TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(30),
            new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30) });

        /// <summary>How long a list read earlier may stay on screen while new reads fail: two intervals (1 h by default).</summary>
        public TimeSpan StaleAfter => TimeSpan.FromTicks(Interval.Ticks * StaleIntervals);

        public static CodesSchedule Resolve(CodesSchedulePins pins)
        {
            pins ??= CodesSchedulePins.None;
            var d = Default;
            var retry = pins.RetryMinutes is { Count: > 0 } r
                ? r.Take(MaxRetrySteps).Select(m => Clamp(TimeSpan.FromMinutes(m), MinRetry, MaxRetry)).ToArray()
                : d.Retry;
            return new CodesSchedule(
                Clamp(pins.FirstDelaySeconds is int s ? TimeSpan.FromSeconds(s) : d.FirstDelay, TimeSpan.Zero, MaxFirstDelay),
                Clamp(pins.RefreshMinutes is int m ? TimeSpan.FromMinutes(m) : d.Interval, MinInterval, MaxInterval),
                retry);
        }

        /// <summary>The wait after the n-th failure in a row (1-based); the last step repeats.</summary>
        public TimeSpan RetryAfter(int failures) => Retry[Math.Clamp(failures - 1, 0, Retry.Count - 1)];

        /// <summary>The wait after a failure, lengthened (never shortened) by the server's Retry-After.</summary>
        public TimeSpan RetryAfter(int failures, TimeSpan? serverRetryAfter)
        {
            TimeSpan step = RetryAfter(failures);
            if (serverRetryAfter is not TimeSpan asked) return step;
            TimeSpan capped = Clamp(asked, TimeSpan.Zero, MaxRetryAfter);
            return capped > step ? capped : step;
        }

        public bool Equals(CodesSchedule other) =>
            other is not null && FirstDelay == other.FirstDelay && Interval == other.Interval && Retry.SequenceEqual(other.Retry);

        public override int GetHashCode() => HashCode.Combine(FirstDelay, Interval, Retry.Count);

        private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) => v < min ? min : v > max ? max : v;
    }
}
