using System;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Stamina
{
    /// <summary>What to do about the "nearly full" notice right now.</summary>
    public enum StaminaNoticeStep
    {
        /// <summary>No notice is owed (none armed, it is switched off, or there is no reading).</summary>
        Idle,
        /// <summary>Owed, not due yet: look again at <see cref="StaminaNoticeDecision.At"/>.</summary>
        Sleep,
        /// <summary>Due, but subtitles run, a game is in front or Windows says not now: try again shortly.</summary>
        Wait,
        Show,
    }

    public readonly record struct StaminaNoticeDecision(StaminaNoticeStep Step, DateTimeOffset? At);

    /// <summary>
    /// One notice per fill. A reading with more than the lead time left arms it; it shows once when the lead time is
    /// reached, and stays quiet until a later reading arms it again (the player spent some). A reading taken when the
    /// pool was already that close does not arm it: the player just saw the number in the game. Pure.
    /// </summary>
    public static class StaminaNotice
    {
        /// <summary>Whether a fresh reading owes the player a notice later.</summary>
        public static bool Arms(GameStaminaPool pool, StaminaReading reading, TimeSpan lead) =>
            pool != null && reading != null && reading.Value < pool.Max && StaminaClock.FullAt(pool, reading) - reading.At > lead;

        /// <summary>When the notice is due: the lead time before the pool fills.</summary>
        public static DateTimeOffset DueAt(GameStaminaPool pool, StaminaReading reading, TimeSpan lead) =>
            StaminaClock.FullAt(pool, reading) - lead;

        /// <param name="mayInterrupt">No subtitles running, no game in front, and Windows accepts a notification.</param>
        public static StaminaNoticeDecision Next(GameStaminaPool pool, StaminaReading reading, bool armed, bool enabled, TimeSpan lead,
            DateTimeOffset now, bool mayInterrupt)
        {
            if (!enabled || !armed || pool == null || reading == null) return new StaminaNoticeDecision(StaminaNoticeStep.Idle, null);
            DateTimeOffset due = DueAt(pool, reading, lead);
            if (now < due) return new StaminaNoticeDecision(StaminaNoticeStep.Sleep, due);
            return new StaminaNoticeDecision(mayInterrupt ? StaminaNoticeStep.Show : StaminaNoticeStep.Wait, null);
        }
    }
}
