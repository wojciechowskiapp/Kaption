using System;
using System.Globalization;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Today
{
    /// <summary>A Genshin server of the farm card (Config FarmCardRegion); its offset is the manifest's.</summary>
    public enum FarmServer
    {
        Europe,
        America,
        Asia,
    }

    /// <summary>
    /// A game server's day: it turns over at the daily reset, in server time. The C# twin of serverDay in
    /// landing/src/data/today.ts; both run the day cases in tools/fixtures/talent-farm-cases.json. The servers'
    /// offsets, the reset hour and the weekly day are data: the game manifest's <c>resets</c> block.
    /// </summary>
    public readonly record struct ServerDay(DayOfWeek Weekday, DateTimeOffset Start, DateTimeOffset Reset)
    {
        /// <summary>Genshin's rules, for the farm card's <see cref="FarmServer"/> calls.</summary>
        private static GameResets Farm => GameRegistry.Current.Find(FarmCardSettings.GameId)?.Resets;

        /// <summary>The hour of Genshin's daily reset, server time.</summary>
        public static int ResetHour => Farm?.DailyHour ?? 0;

        /// <summary>The server's date of this game day ("2026-11-04"), the key for "once per server day".</summary>
        public string Key(FarmServer server) => Key(OffsetOf(server), ResetHour);

        public string Key(int offsetHours, int resetHour) =>
            Start.ToOffset(TimeSpan.FromHours(offsetHours)).AddHours(-resetHour).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public static int OffsetOf(FarmServer server) => Farm?.Server(Id(server))?.UtcOffset ?? 0;

        /// <summary>"europe" / "america" / "asia" (Config FarmCardRegion); anything else is Europe.</summary>
        public static FarmServer Parse(string value) => value?.Trim().ToLowerInvariant() switch
        {
            "america" => FarmServer.America,
            "asia" => FarmServer.Asia,
            _ => FarmServer.Europe,
        };

        public static string Id(FarmServer server) => server.ToString().ToLowerInvariant();

        public static ServerDay At(DateTimeOffset now, FarmServer server) => At(now, OffsetOf(server), ResetHour);

        public static ServerDay At(DateTimeOffset now, int offsetHours) => At(now, offsetHours, ResetHour);

        /// <summary>The game day at <paramref name="now"/> on a server <paramref name="offsetHours"/> from UTC that resets at <paramref name="resetHour"/>.</summary>
        public static ServerDay At(DateTimeOffset now, int offsetHours, int resetHour)
        {
            DateTime shifted = now.UtcDateTime.AddHours(offsetHours - resetHour);
            var start = new DateTimeOffset(shifted.Date.AddHours(resetHour - offsetHours), TimeSpan.Zero);
            return new ServerDay(shifted.DayOfWeek, start, start.AddDays(1));
        }

        /// <summary>The game day on a manifest's server.</summary>
        public static ServerDay At(DateTimeOffset now, GameResets resets, GameResetServer server) =>
            At(now, server?.UtcOffset ?? 0, resets?.DailyHour ?? 0);

        /// <summary>
        /// The next weekly reset after <paramref name="now"/>: the start of the next game day whose weekday is
        /// <paramref name="weeklyDay"/> (0 Sunday). On that day before the reset hour it is today's reset.
        /// </summary>
        public static DateTimeOffset NextWeekly(DateTimeOffset now, int offsetHours, int resetHour, int weeklyDay)
        {
            ServerDay day = At(now, offsetHours, resetHour);
            int ahead = (((weeklyDay - (int)day.Weekday) % 7) + 7) % 7;
            return day.Start.AddDays(ahead == 0 ? 7 : ahead);
        }
    }
}
