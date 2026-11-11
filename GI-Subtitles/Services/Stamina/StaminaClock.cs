using System;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Stamina
{
    /// <summary>
    /// What was read and when: the pool's value at <see cref="At"/> and how long until its next point
    /// (<see cref="NextInSeconds"/>: a whole interval right after a point was seen arriving, half of one when the
    /// moment within the interval is unknown).
    /// </summary>
    public sealed record StaminaReading(int Value, DateTimeOffset At, double NextInSeconds);

    /// <summary>
    /// The stamina timer's maths, the same rules as the site's timer (landing/src/lib/stamina.ts): whole points only,
    /// regeneration stops at the pool's max, a value over max stays as it is.
    /// </summary>
    public static class StaminaClock
    {
        private static DateTimeOffset FirstTick(StaminaReading r) => r.At + TimeSpan.FromSeconds(r.NextInSeconds);

        /// <summary>The pool at <paramref name="t"/>.</summary>
        public static int ValueAt(GameStaminaPool pool, StaminaReading r, DateTimeOffset t)
        {
            if (r.Value >= pool.Max || t < FirstTick(r)) return r.Value;
            long points = 1 + (long)Math.Floor((t - FirstTick(r)).TotalSeconds / pool.RegenSeconds);
            return (int)Math.Min(pool.Max, r.Value + points);
        }

        /// <summary>
        /// When regeneration brings the pool to <paramref name="target"/>: the reading's own moment when it is there
        /// already, null when regeneration never gets there (over max).
        /// </summary>
        public static DateTimeOffset? ReachAt(GameStaminaPool pool, StaminaReading r, int target)
        {
            if (target <= r.Value) return r.At;
            if (target > pool.Max) return null;
            return FirstTick(r) + TimeSpan.FromSeconds((double)(target - r.Value - 1) * pool.RegenSeconds);
        }

        /// <summary>When the pool is full: the reading's moment when it is at or over max.</summary>
        public static DateTimeOffset FullAt(GameStaminaPool pool, StaminaReading r) => ReachAt(pool, r, pool.Max) ?? r.At;

        /// <summary>Seconds until the next point at <paramref name="t"/>; null at or over max.</summary>
        public static double? NextInAt(GameStaminaPool pool, StaminaReading r, DateTimeOffset t)
        {
            if (ValueAt(pool, r, t) >= pool.Max) return null;
            DateTimeOffset first = FirstTick(r);
            if (t < first) return (first - t).TotalSeconds;
            double into = (t - first).TotalSeconds % pool.RegenSeconds;
            return pool.RegenSeconds - into;
        }

        /// <summary>The wait for a point when nothing is known about the moment within the interval: half of it.</summary>
        public static double UnknownPhase(GameStaminaPool pool) => pool.RegenSeconds / 2.0;
    }
}
