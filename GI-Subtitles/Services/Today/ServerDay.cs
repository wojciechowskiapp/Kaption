using System;
using System.Globalization;

namespace GI_Subtitles.Services.Today
{
    /// <summary>A HoYoverse server region: fixed UTC offset, no daylight saving (landing/src/data/today.ts SERVERS).</summary>
    public enum FarmServer
    {
        Europe,
        America,
        Asia,
    }

    /// <summary>
    /// The game servers' day: domains turn over at the 04:00 server reset. The C# twin of serverDay in
    /// landing/src/data/today.ts; both run the day cases in tools/fixtures/talent-farm-cases.json.
    /// </summary>
    public readonly record struct ServerDay(DayOfWeek Weekday, DateTimeOffset Start, DateTimeOffset Reset)
    {
        public const int ResetHour = 4;

        /// <summary>The server's date of this game day ("2026-11-04"), the key for "once per server day".</summary>
        public string Key(FarmServer server) =>
            Start.ToOffset(TimeSpan.FromHours(OffsetOf(server))).AddHours(-ResetHour).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public static int OffsetOf(FarmServer server) => server switch
        {
            FarmServer.America => -5,
            FarmServer.Asia => 8,
            _ => 1,
        };

        /// <summary>"europe" / "america" / "asia" (Config FarmCardRegion); anything else is Europe.</summary>
        public static FarmServer Parse(string value) => value?.Trim().ToLowerInvariant() switch
        {
            "america" => FarmServer.America,
            "asia" => FarmServer.Asia,
            _ => FarmServer.Europe,
        };

        public static string Id(FarmServer server) => server.ToString().ToLowerInvariant();

        public static ServerDay At(DateTimeOffset now, FarmServer server) => At(now, OffsetOf(server));

        public static ServerDay At(DateTimeOffset now, int offsetHours)
        {
            DateTime shifted = now.UtcDateTime.AddHours(offsetHours - ResetHour);
            var start = new DateTimeOffset(shifted.Date.AddHours(ResetHour - offsetHours), TimeSpan.Zero);
            return new ServerDay(shifted.DayOfWeek, start, start.AddDays(1));
        }
    }
}
