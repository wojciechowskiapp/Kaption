using System;
using System.Globalization;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Stamina
{
    /// <summary>A tray notice: title and body.</summary>
    public sealed record StaminaNoticeText(string Title, string Body);

    /// <summary>
    /// The words for the stamina timer, shared by the Start card and the tray notice. The pool's name is the game's own
    /// English one (the manifest's) in every interface language. <c>l</c> looks a string up by key with a fallback.
    /// </summary>
    public static class StaminaText
    {
        /// <summary>A length of time in whole minutes, rounded up: "7 h 36 min", "1 d 2 h", "5 min".</summary>
        public static string Duration(TimeSpan span, Func<string, string, string> l)
        {
            long total = Math.Max(0, (long)Math.Ceiling(span.TotalMinutes));
            long d = total / 1440, h = total % 1440 / 60, m = total % 60;
            string day = l("Stamina_Unit_Day", "d"), hour = l("Stamina_Unit_Hour", "h"), min = l("Stamina_Unit_Minute", "min");
            if (d > 0) return h > 0 ? $"{d} {day} {h} {hour}" : $"{d} {day}";
            if (h > 0) return m > 0 ? $"{h} {hour} {m} {min}" : $"{h} {hour}";
            return $"{m} {min}";
        }

        /// <summary>A moment as the sentence needs it, in the player's own time zone: "at 21:40", "tomorrow at 06:10".</summary>
        public static string Moment(DateTimeOffset moment, DateTimeOffset now, Func<string, string, string> l, CultureInfo culture)
        {
            DateTime local = moment.ToLocalTime().DateTime, today = now.ToLocalTime().Date;
            string clock = local.ToString("t", culture);
            int days = (local.Date - today).Days;
            return days switch
            {
                0 => string.Format(culture, l("Stamina_At", "at {0}"), clock),
                1 => string.Format(culture, l("Stamina_Tomorrow", "tomorrow at {0}"), clock),
                -1 => string.Format(culture, l("Stamina_Yesterday", "yesterday at {0}"), clock),
                _ => string.Format(culture, l("Stamina_OnDay", "on {0} at {1}"), local.ToString("m", culture), clock),
            };
        }

        /// <summary>The notice for a pool that is nearly full, or full by the time it could be shown.</summary>
        public static StaminaNoticeText Notice(GameStamina stamina, StaminaReading reading, DateTimeOffset now,
            Func<string, string, string> l, CultureInfo culture)
        {
            int value = StaminaClock.ValueAt(stamina, reading, now);
            DateTimeOffset full = StaminaClock.FullAt(stamina, reading);
            if (value >= stamina.Max)
            {
                // A game with a reserve pool loses nothing at the cap: the reserve fills instead, slower.
                string body = string.IsNullOrWhiteSpace(stamina.Reserve?.Name)
                    ? string.Format(culture, l("Stamina_Notice_Full_Body", "{0}/{1}. It filled {2}; anything more is lost until you spend some."),
                        value, stamina.Max, Moment(full, now, l, culture))
                    : string.Format(culture, l("Stamina_Notice_Full_Body_Reserve", "{0}/{1}. It filled {2}; what comes next goes to {3}, slower, until that is full too."),
                        value, stamina.Max, Moment(full, now, l, culture), stamina.Reserve.Name);
                return new StaminaNoticeText(string.Format(culture, l("Stamina_Notice_Full_Title", "{0} is full"), stamina.Name), body);
            }
            return new StaminaNoticeText(
                string.Format(culture, l("Stamina_Notice_Almost_Title", "{0} is almost full"), stamina.Name),
                string.Format(culture, l("Stamina_Notice_Almost_Body", "About {0}/{1} now, full {2}."),
                    value, stamina.Max, Moment(full, now, l, culture)));
        }
    }
}
