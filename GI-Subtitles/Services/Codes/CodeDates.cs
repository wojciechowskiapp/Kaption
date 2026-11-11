using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GI_Subtitles.Views.Shell;

namespace GI_Subtitles.Services.Codes
{
    /// <summary>
    /// The words of a code's dates, as format strings from the interface's resources (<c>codes_Added</c> …):
    /// <c>{0}</c> is the date or the time left. <see cref="Days"/> is a "one|few|many" plural (see <see cref="Plural"/>).
    /// </summary>
    public sealed record CodeDateWords(string Added, string Listed, string Until, string NoEnd, string EndsIn, string Days)
    {
        /// <summary>The English words, for callers without resources (tests, a missing dictionary).</summary>
        public static readonly CodeDateWords English = new("Added {0}", "On the list since {0}", "valid until {0}",
            "no end date announced", "Ends in {0}", "{0} day|{0} days|{0} days");
    }

    /// <summary>
    /// When a code came onto the list and until when it works, as the card says it. The rules are the site's
    /// (landing/src/lib/codes-view.ts): new for two days unless the list's first check found it, ending soon inside two
    /// days of a known end, ending-soon codes first and then the newest. Pure: the clock, the zone and the words come in.
    /// </summary>
    public static class CodeDates
    {
        /// <summary>A code first seen this recently is new.</summary>
        public static readonly TimeSpan FreshFor = TimeSpan.FromHours(48);
        /// <summary>A code whose end is this close is ending soon.</summary>
        public static readonly TimeSpan EndsSoonWithin = TimeSpan.FromHours(48);
        /// <summary>Codes first seen this close to the list's start came with its first check.</summary>
        public static readonly TimeSpan FirstCheckWindow = TimeSpan.FromHours(1);

        /// <summary>
        /// When the game's list began: the server's <c>listedSince</c>, else (an older server) the oldest first-seen time
        /// among the codes at hand. Null when neither is known.
        /// </summary>
        public static DateTimeOffset? ListStart(RedeemCodeList list)
        {
            if (list == null) return null;
            if (list.ListedSince.HasValue) return list.ListedSince;
            var seen = list.Active.Where(c => c.FirstSeen.HasValue).Select(c => c.FirstSeen.Value).ToList();
            return seen.Count > 0 ? seen.Min() : null;
        }

        /// <summary>A code the list's first check found: it was out before the list, so its first-seen day is not its release day.</summary>
        public static bool FromFirstCheck(RedeemCode code, DateTimeOffset? since) =>
            since.HasValue && code?.FirstSeen is DateTimeOffset seen && seen - since.Value < FirstCheckWindow;

        public static bool IsNew(RedeemCode code, DateTimeOffset now, DateTimeOffset? since) =>
            code?.FirstSeen is DateTimeOffset seen && seen <= now && now - seen < FreshFor && !FromFirstCheck(code, since);

        public static bool EndsSoon(RedeemCode code, DateTimeOffset now) =>
            code?.ExpiresAt is DateTimeOffset end && end > now && end - now <= EndsSoonWithin;

        /// <summary>Ending-soon codes first (the soonest end first), then the newest first, the code as the tie-break.</summary>
        public static IReadOnlyList<RedeemCode> Order(IEnumerable<RedeemCode> active, DateTimeOffset now) =>
            (active ?? Enumerable.Empty<RedeemCode>())
                .OrderBy(c => EndsSoon(c, now) ? 0 : 1)
                .ThenBy(c => EndsSoon(c, now) ? c.ExpiresAt.Value.UtcTicks : -(c.FirstSeen ?? DateTimeOffset.MinValue).UtcTicks)
                .ThenBy(c => c.Code, StringComparer.Ordinal)
                .ToList();

        /// <summary>"7 November" / "7 listopada" in <paramref name="zone"/>; the year only when it is not <paramref name="now"/>'s.</summary>
        public static string Day(DateTimeOffset at, DateTimeOffset now, CultureInfo culture, TimeZoneInfo zone)
        {
            DateTimeOffset local = TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local);
            bool sameYear = local.Year == TimeZoneInfo.ConvertTime(now, zone ?? TimeZoneInfo.Local).Year;
            return local.ToString(sameYear ? "d MMMM" : "d MMMM yyyy", culture);
        }

        /// <summary>"12 November, 5:00 PM" / "12 listopada, 17:00": the day and the culture's own short time.</summary>
        public static string DayTime(DateTimeOffset at, DateTimeOffset now, CultureInfo culture, TimeZoneInfo zone) =>
            $"{Day(at, now, culture, zone)}, {TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local).ToString("t", culture)}";

        /// <summary>
        /// A time left: "2 days 4 h", "7 h", "8 min". Whole hours (rounded down: never more time than there is) until the
        /// last hour, then minutes rounded up, so it never reads 0 before the end. The card redraws only when this
        /// text changes: hourly, then every minute of the last hour.
        /// </summary>
        public static string Countdown(TimeSpan left, CodeDateWords words, CultureInfo culture)
        {
            if (left < TimeSpan.FromHours(1)) return $"{Math.Max(1, (long)Math.Ceiling(left.TotalMinutes))} min";
            long hours = (long)Math.Floor(left.TotalHours);
            long d = hours / 24, h = hours % 24;
            if (d == 0) return $"{h} h";
            string days = Plural.Format((words ?? CodeDateWords.English).Days, d, culture);
            return h > 0 ? $"{days} {h} h" : days;
        }

        /// <summary>The mark of a code ending soon ("Ends in 7 h 14 min"); null when it is not.</summary>
        public static string EndsIn(RedeemCode code, DateTimeOffset now, CodeDateWords words, CultureInfo culture) =>
            EndsSoon(code, now)
                ? string.Format(culture, (words ?? CodeDateWords.English).EndsIn, Countdown(code.ExpiresAt.Value - now, words, culture))
                : null;

        /// <summary>
        /// One line of dates: "Added 7 November · valid until 12 November, 5:00 PM", or with no end announced
        /// "On the list since 4 November · no end date announced". Never a made-up end date.
        /// </summary>
        public static string Line(RedeemCode code, DateTimeOffset now, DateTimeOffset? since, CodeDateWords words, CultureInfo culture,
                                  TimeZoneInfo zone = null)
        {
            if (code == null) return string.Empty;
            words ??= CodeDateWords.English;
            culture ??= CultureInfo.CurrentUICulture;
            var parts = new List<string>(2);
            if (code.FirstSeen is DateTimeOffset seen)
                parts.Add(string.Format(culture, FromFirstCheck(code, since) ? words.Listed : words.Added, Day(seen, now, culture, zone)));
            parts.Add(code.ExpiresAt is DateTimeOffset end
                ? string.Format(culture, words.Until, DayTime(end, now, culture, zone))
                : words.NoEnd);
            return string.Join(CodesCardModel.RewardSeparator, parts);
        }

        /// <summary>
        /// When what the card says about <paramref name="code"/> next changes with the clock alone: it stops being new,
        /// starts ending soon, or (while ending soon) its time left reads differently: at the next whole hour, and each
        /// minute of the last hour. Null when nothing will.
        /// </summary>
        public static DateTimeOffset? NextChange(RedeemCode code, DateTimeOffset now, DateTimeOffset? since)
        {
            if (code == null) return null;
            DateTimeOffset? next = null;
            void Take(DateTimeOffset at) { if (at > now && (next == null || at < next)) next = at; }
            if (IsNew(code, now, since)) Take(code.FirstSeen.Value + FreshFor);
            // A clock set back leaves a first-seen time ahead of it: the code turns new when the clock gets there.
            else if (code.FirstSeen is DateTimeOffset seen && seen > now && !FromFirstCheck(code, since)) Take(seen);
            if (code.ExpiresAt is DateTimeOffset end && end > now)
            {
                if (EndsSoon(code, now))
                {
                    // The moment Countdown reads differently. Hours are rounded down, so "5 h" turns to "4 h" just past
                    // the whole hour (one tick: with exactly 5 h left the change is due at once, not in an hour). Minutes are
                    // rounded up, so "20 min" holds until exactly 19 are left.
                    TimeSpan left = end - now;
                    long wait;
                    if (left >= TimeSpan.FromHours(1)) wait = left.Ticks % TimeSpan.TicksPerHour + 1;
                    else
                    {
                        long into = left.Ticks % TimeSpan.TicksPerMinute;
                        wait = into == 0 ? TimeSpan.TicksPerMinute : into;
                    }
                    Take(now + TimeSpan.FromTicks(wait));
                }
                else Take(end - EndsSoonWithin);
            }
            return next;
        }
    }
}
