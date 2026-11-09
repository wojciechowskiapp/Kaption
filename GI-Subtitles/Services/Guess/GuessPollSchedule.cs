using System;
using System.Collections.Generic;
using System.Linq;

namespace GI_Subtitles.Services.Guess
{
    /// <summary>
    /// Values pinned in Config (docs/CONFIG.md, "Guess game notices"); null where the file says nothing.
    /// <see cref="RetryMinutes"/> null or empty keeps the default steps.
    /// </summary>
    public sealed record GuessPollPins(int? FirstDelaySeconds, int? PollMinutes, int? IdlePollMinutes, int? NoSessionRecheckMinutes,
                                       IReadOnlyList<int> RetryMinutes, int? GateRetrySeconds)
    {
        public static readonly GuessPollPins None = new(null, null, null, null, null, null);
    }

    /// <summary>
    /// When the ranking notice reads its standing and re-checks a waiting notice. Each value: a Config pin wins, else
    /// what the server sent with the last standing (<c>poll</c>, the two read intervals only), else the default; then
    /// clamped, so a bad file or a bad answer can neither hammer the API nor leave the notice quiet for days.
    /// </summary>
    public sealed record GuessPollSchedule(TimeSpan FirstDelay, TimeSpan Interval, TimeSpan IdleInterval, TimeSpan NoSessionRecheck,
                                           IReadOnlyList<TimeSpan> Retry, TimeSpan GateRetry)
    {
        public static readonly TimeSpan MinFirstDelay = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan MaxFirstDelay = TimeSpan.FromHours(1);
        /// <summary>The server's bounds too (backend lib/guess.ts appPoll).</summary>
        public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan MaxInterval = TimeSpan.FromHours(24);
        public static readonly TimeSpan MaxIdleInterval = TimeSpan.FromHours(48);
        public static readonly TimeSpan MinNoSessionRecheck = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan MaxNoSessionRecheck = TimeSpan.FromHours(4);
        public static readonly TimeSpan MinRetry = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan MaxRetry = TimeSpan.FromHours(24);
        public const int MaxRetrySteps = 8;
        public static readonly TimeSpan MinGateRetry = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan MaxGateRetry = TimeSpan.FromMinutes(15);

        /// <summary>
        /// The shipped cadence (R4 section 2.3): 2 min after start, then every 4 h, every 12 h for a player who has
        /// played nothing this month, 15 min while signed out, 15 min → 1 h → 4 h after failures, and a waiting notice
        /// re-checked every 60 s.
        /// </summary>
        public static readonly GuessPollSchedule Default = new(
            TimeSpan.FromMinutes(2), TimeSpan.FromHours(4), TimeSpan.FromHours(12), TimeSpan.FromMinutes(15),
            new[] { TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(4) }, TimeSpan.FromSeconds(60));

        public static GuessPollSchedule Resolve(GuessPollPins pins, TimeSpan? serverInterval, TimeSpan? serverIdle)
        {
            pins ??= GuessPollPins.None;
            var d = Default;
            var retry = pins.RetryMinutes is { Count: > 0 } r
                ? r.Take(MaxRetrySteps).Select(m => Clamp(TimeSpan.FromMinutes(m), MinRetry, MaxRetry)).ToArray()
                : d.Retry;
            return new GuessPollSchedule(
                Clamp(Seconds(pins.FirstDelaySeconds) ?? d.FirstDelay, MinFirstDelay, MaxFirstDelay),
                Clamp(Minutes(pins.PollMinutes) ?? serverInterval ?? d.Interval, MinInterval, MaxInterval),
                Clamp(Minutes(pins.IdlePollMinutes) ?? serverIdle ?? d.IdleInterval, MinInterval, MaxIdleInterval),
                Clamp(Minutes(pins.NoSessionRecheckMinutes) ?? d.NoSessionRecheck, MinNoSessionRecheck, MaxNoSessionRecheck),
                retry,
                Clamp(Seconds(pins.GateRetrySeconds) ?? d.GateRetry, MinGateRetry, MaxGateRetry));
        }

        /// <summary>The wait after the n-th failure in a row (1-based); the last step repeats.</summary>
        public TimeSpan RetryAfter(int failures) => Retry[Math.Clamp(failures - 1, 0, Retry.Count - 1)];

        // Records compare lists by reference: compare the steps instead, so Resolve(None) equals Default.
        public bool Equals(GuessPollSchedule other) =>
            other is not null && FirstDelay == other.FirstDelay && Interval == other.Interval && IdleInterval == other.IdleInterval
            && NoSessionRecheck == other.NoSessionRecheck && GateRetry == other.GateRetry && Retry.SequenceEqual(other.Retry);

        public override int GetHashCode() => HashCode.Combine(FirstDelay, Interval, IdleInterval, NoSessionRecheck, GateRetry, Retry.Count);

        private static TimeSpan? Seconds(int? s) => s is int v ? TimeSpan.FromSeconds(v) : null;
        private static TimeSpan? Minutes(int? m) => m is int v ? TimeSpan.FromMinutes(v) : null;
        private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) => v < min ? min : v > max ? max : v;
    }
}
