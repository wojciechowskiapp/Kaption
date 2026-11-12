using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Stamina;
using GI_Subtitles.Services.Today;

namespace GI_Subtitles.Services.Notices
{
    /// <summary>docs/CONFIG.md, "Notice of the day".</summary>
    public static class DayNoticeSettings
    {
        public const string EnabledKey = "DayNotice.Enabled";
        /// <summary>Written by Kaption: the game day the notice last reached the player.</summary>
        public const string LastDayKey = "DayNotice.LastDay";
        public const string DelaySecondsKey = "DayNotice.DelaySeconds";
        public const string RetrySecondsKey = "DayNotice.RetrySeconds";

        public const int DefaultDelaySeconds = 20, MinDelaySeconds = 5, MaxDelaySeconds = 600;
        public const int DefaultRetrySeconds = 60, MinRetrySeconds = 15, MaxRetrySeconds = 3600;

        /// <summary>Raised after <see cref="Enabled"/> changes, from the thread that changed it.</summary>
        public static event Action Changed;

        public static bool Enabled
        {
            get => Config.Get(EnabledKey, true);
            set
            {
                if (Config.Get(EnabledKey, true) == value && Config.Has(EnabledKey)) return;
                Config.Set(EnabledKey, value);
                Changed?.Invoke();
            }
        }

        public static string LastDay
        {
            get => Config.Get<string>(LastDayKey, null);
            set => Config.Set(LastDayKey, value);
        }

        /// <summary>How long after the start the first look happens: the tray icon is up and a pack sync has had its say.</summary>
        public static TimeSpan Delay =>
            TimeSpan.FromSeconds(Math.Clamp(Config.Get(DelaySecondsKey, DefaultDelaySeconds), MinDelaySeconds, MaxDelaySeconds));

        /// <summary>How often a notice that may not show yet (a game in front, subtitles running) is tried again.</summary>
        public static TimeSpan Retry =>
            TimeSpan.FromSeconds(Math.Clamp(Config.Get(RetrySecondsKey, DefaultRetrySeconds), MinRetrySeconds, MaxRetrySeconds));
    }

    public enum DayNoticeStep
    {
        /// <summary>Switched off, or today's notice already reached the player: nothing until the next game day.</summary>
        Idle,
        /// <summary>This start already had its notice; the next start, or the next game day, may bring this one.</summary>
        StartSpent,
        /// <summary>Another notice goes first, subtitles run, a game is in front or Windows says not now: ask again shortly.</summary>
        Wait,
        Show,
    }

    /// <summary>
    /// One notice on the first start of a game day, and at most one notice per start. Pure: MainWindow asks, shows the
    /// balloon and writes the day only once the balloon really went out.
    /// </summary>
    public static class DayNoticeRule
    {
        /// <param name="lastDay">DayNotice.LastDay.</param>
        /// <param name="dayKey">The game day now (ServerDay.Key).</param>
        /// <param name="startSpentOn">The game day on which this run already gave its one notice (the news card, the tray's news line); null when it has not.</param>
        /// <param name="otherWaits">A notice that goes first is waiting for its turn (the new story).</param>
        /// <param name="mayInterrupt">No subtitles running, no game in front, and Windows accepts a notification.</param>
        public static DayNoticeStep Next(bool enabled, string lastDay, string dayKey, string startSpentOn, bool otherWaits, bool mayInterrupt)
        {
            if (!enabled || string.IsNullOrEmpty(dayKey)) return DayNoticeStep.Idle;
            // Only a later game day than the last one told: the keys are dates (yyyy-MM-dd), so they order as text.
            // A clock set back does not bring an old day's notice again, nor the days after it a second time.
            if (!string.IsNullOrEmpty(lastDay) && string.CompareOrdinal(dayKey, lastDay) <= 0) return DayNoticeStep.Idle;
            // The same for the day this run gave its notice on: a clock set back is not a new day.
            if (!string.IsNullOrEmpty(startSpentOn) && string.CompareOrdinal(dayKey, startSpentOn) <= 0) return DayNoticeStep.StartSpent;
            return otherWaits || !mayInterrupt ? DayNoticeStep.Wait : DayNoticeStep.Show;
        }
    }

    /// <summary>One game's part of the notice: sentences, the most important first.</summary>
    public sealed record DayNoticeSection(string Game, string GameName, IReadOnlyList<string> Lines, bool HasCard);

    /// <summary>The notice as the tray shows it.</summary>
    public sealed record DayNoticeText(string Title, string Body, bool OpensCard);

    /// <summary>Which games the notice speaks about, and what each has to say today. Pure.</summary>
    public static class DayNoticeContent
    {
        /// <summary>A game other than the selected one counts while its stamina was read this recently: the player plays it with Kaption.</summary>
        public static readonly TimeSpan RecentlyPlayed = TimeSpan.FromDays(14);

        public const int MaxSeries = 3;
        public const int MaxNamesPerSeries = 3;

        /// <summary>
        /// The culture the notice's weekday and clock times are written in: the app's own language, not Windows'.
        /// An English Kaption on a Polish Windows says "Tuesday".
        /// </summary>
        public static CultureInfo CultureFor(string uiLanguage) =>
            CultureInfo.GetCultureInfo(string.Equals(uiLanguage, "pl-PL", StringComparison.OrdinalIgnoreCase) ? "pl-PL" : "en-US");

        /// <summary>
        /// The games the player has: the selected one, and any other whose stamina Kaption read lately. In the
        /// registry's order, the selected game first.
        /// </summary>
        public static IReadOnlyList<GameManifest> Games(IReadOnlyList<GameManifest> listed, string selectedWireId,
            Func<string, DateTimeOffset?> lastRead, DateTimeOffset now)
        {
            var games = new List<GameManifest>();
            foreach (GameManifest g in listed ?? Array.Empty<GameManifest>())
            {
                if (g == null) continue;
                bool selected = string.Equals(g.Id, selectedWireId, StringComparison.OrdinalIgnoreCase);
                DateTimeOffset? read = selected ? null : lastRead?.Invoke(g.Id);
                if (selected) games.Insert(0, g);
                else if (read is DateTimeOffset at && at <= now && now - at <= RecentlyPlayed) games.Add(g);
            }
            return games;
        }

        /// <summary>
        /// Genshin's sentence about today's talent books, from the card's own model: for the player's characters when
        /// the showcase says who needs them, the open series otherwise. Null when the card has nothing for today.
        /// </summary>
        public static string Books(FarmCardModel farm, Func<string, string, string> l, CultureInfo culture)
        {
            if (farm == null) return null;
            if (farm.Player == FarmPlayerState.Rows && farm.Rows.Count > 0)
            {
                var parts = new List<string>();
                foreach (FarmBook book in farm.Books.Where(b => b.Mine > 0).Take(MaxSeries))
                {
                    var names = farm.Rows.Where(r => r.Steps.Any(s => string.Equals(s.Series, book.Series, StringComparison.Ordinal)))
                        .Select(r => r.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
                    if (names.Count == 0) continue;
                    string shown = string.Join(", ", names.Take(MaxNamesPerSeries));
                    if (names.Count > MaxNamesPerSeries) shown += " +" + (names.Count - MaxNamesPerSeries).ToString(culture);
                    parts.Add(string.Format(culture, l("DayNotice_Books_Series_Format", "{0} ({1})"), book.Series, shown));
                }
                if (parts.Count > 0)
                {
                    string line = string.Format(culture, l("DayNotice_Books_Mine", "Books for your characters: {0}."), string.Join(", ", parts));
                    return farm.MoreRows > 0
                        ? line + " " + string.Format(culture, l("DayNotice_Books_More", "Plus {0} more."), farm.MoreRows)
                        : line;
                }
            }
            if (farm.IsSunday) return l("DayNotice_Books_Sunday", "Sunday: every talent book Domain is open.");
            var series = farm.Books.Select(b => b.Series).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList();
            if (series.Count == 0) return null;
            string open = string.Format(culture, l("DayNotice_Books_Open", "Talent books open today: {0}."), string.Join(", ", series));
            return farm.Player == FarmPlayerState.NoUid
                ? open + " " + l("DayNotice_Books_AddUid", "Save your UID in Kaption to see who they are for.")
                : open;
        }

        /// <summary>
        /// A game's stamina sentence from the Today card's own estimate, when it matters today: the pool is at its
        /// cap by the timer, or reaches it before the game day ends. Null otherwise.
        /// </summary>
        public static string Stamina(TodayStamina stamina, DateTimeOffset now, DateTimeOffset dayEnds,
            Func<string, string, string> l, CultureInfo culture)
        {
            if (stamina == null) return null;
            switch (stamina.State)
            {
                case TodayStaminaState.Full:
                    return string.Format(culture, l("DayNotice_Stamina_Full", "{0} is at the cap; it filled {1}."), stamina.PoolName,
                        StaminaText.Moment(stamina.FullAt, now, l, culture));
                case TodayStaminaState.FullLongAgo:
                    // The reading is older than a fill from zero: the card says so too, without a moment it cannot know.
                    return string.Format(culture, l("DayNotice_Stamina_FullLongAgo", "{0} has been at the cap for a while."), stamina.PoolName);
                default:
                    if (stamina.FullAt > dayEnds) return null;
                    return string.Format(culture, l("DayNotice_Stamina_FillsToday", "{0}: about {1}/{2}, at the cap {3}."),
                        stamina.PoolName, stamina.Value, stamina.Max, StaminaText.Moment(stamina.FullAt, now, l, culture));
            }
        }

        /// <summary>
        /// What ends before the game day does (a banner or an event on its last day), from the card's "ending soon"
        /// rows. Null when nothing does.
        /// </summary>
        public static string EndsToday(IReadOnlyList<TodayPeriod> ending, DateTimeOffset now, DateTimeOffset dayEnds,
            Func<string, string, string> l, CultureInfo culture)
        {
            var today = (ending ?? Array.Empty<TodayPeriod>())
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Title) && p.EndsAt > now && p.EndsAt <= dayEnds)
                .Select(p => p.Title.Trim().TrimEnd('.')).Distinct(StringComparer.Ordinal).ToList();
            if (today.Count == 0) return null;
            string names = string.Join(", ", today.Take(MaxEndingToday));
            if (today.Count > MaxEndingToday) names += " +" + (today.Count - MaxEndingToday).ToString(culture);
            return string.Format(culture, l("DayNotice_Ends_Today", "Last day: {0}."), names);
        }

        /// <summary>Titles named in "last day"; the rest is a count, and the card has them.</summary>
        public const int MaxEndingToday = 2;

        /// <summary>The end of the local day at <paramref name="now"/>: the day's end for a game without reset data.</summary>
        public static DateTimeOffset LocalDayEnds(DateTimeOffset now) => LocalDayEnds(now, TimeZoneInfo.Local);

        /// <summary>
        /// The next midnight in <paramref name="zone"/>, with the offset that holds at that midnight: on the day the
        /// clocks change, today's offset would put it an hour off. Where the clocks go back across midnight it
        /// comes twice; the day ends at the first, when the date turns.
        /// </summary>
        public static DateTimeOffset LocalDayEnds(DateTimeOffset now, TimeZoneInfo zone)
        {
            DateTime midnight = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1);
            // The first of two is the one still on daylight time: the larger offset.
            TimeSpan offset = zone.IsAmbiguousTime(midnight) ? zone.GetAmbiguousTimeOffsets(midnight).Max() : zone.GetUtcOffset(midnight);
            return new DateTimeOffset(midnight, offset).ToUniversalTime();
        }

        /// <summary>
        /// What a click on a delivered notice opens. The card of the day is the selected game's, so it opens only when
        /// the notice promised a card and that game is still the selected one; else Start, where the Today block is.
        /// </summary>
        public static bool ClickOpensCard(bool noticeOpensCard, string noticeGame, string selectedGame) =>
            noticeOpensCard && !string.IsNullOrEmpty(noticeGame) && string.Equals(noticeGame, selectedGame, StringComparison.Ordinal);

        /// <summary>
        /// One game's part of the notice, from its Today card model: the talent books (Genshin), a pool at its cap,
        /// what is on its last day. A null model (no data, or the game's card already showed today) says nothing.
        /// </summary>
        /// <param name="opensCard">A click on the notice opens this game's card.</param>
        public static DayNoticeSection Section(TodayCardModel model, GameManifest game, bool opensCard,
            Func<string, string, string> l, CultureInfo culture)
        {
            var lines = new List<string>();
            if (model != null)
            {
                DateTimeOffset dayEnds = model.Resets?.Daily ?? LocalDayEnds(model.Now);
                string books = Books(model.Farm, l, culture);
                if (books != null) lines.Add(books);
                string pool = Stamina(model.Stamina, model.Now, dayEnds, l, culture);
                if (pool != null) lines.Add(pool);
                string ends = EndsToday(model.Ending, model.Now, dayEnds, l, culture);
                if (ends != null) lines.Add(ends);
            }
            return new DayNoticeSection(game?.Id ?? "", game?.Name ?? "", lines, opensCard);
        }
    }

    /// <summary>Puts the sections into one tray notice that fits Windows' limits. Pure.</summary>
    public static class DayNoticeBuilder
    {
        /// <summary>NOTIFYICONDATA: szInfoTitle holds 63 characters and szInfo 255.</summary>
        public const int MaxTitle = 63, MaxBody = 255;

        /// <summary>Null when no game has anything to say today.</summary>
        public static DayNoticeText Build(IReadOnlyList<DayNoticeSection> sections, DayOfWeek weekday, Func<string, string, string> l, CultureInfo culture)
        {
            var said = (sections ?? Array.Empty<DayNoticeSection>())
                .Where(s => s != null && s.Lines != null)
                .Select(s => s with { Lines = s.Lines.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList() })
                .Where(s => s.Lines.Count > 0).ToList();
            if (said.Count == 0) return null;
            culture ??= CultureInfo.CurrentUICulture;
            string day = culture.DateTimeFormat.GetDayName(weekday);
            bool one = said.Count == 1;
            string title = one
                ? string.Format(culture, l("DayNotice_Title_Game", "{0} today, {1}"), said[0].GameName, day)
                : string.Format(culture, l("DayNotice_Title_Games", "Today in your games, {0}"), day);

            // Whole sentences only, the first of each game before any game's second: what does not fit is on the card.
            var lines = said.Select(_ => new List<string>()).ToList();
            int depth = said.Max(s => s.Lines.Count);
            for (int round = 0; round < depth; round++)
            for (int g = 0; g < said.Count; g++)
            {
                if (round >= said[g].Lines.Count) continue;
                lines[g].Add(said[g].Lines[round]);
                if (Compose(said, lines, one).Length > MaxBody) lines[g].RemoveAt(lines[g].Count - 1);
            }
            string body = Compose(said, lines, one);
            // One sentence alone longer than the limit: cut it, visibly, rather than say nothing.
            if (body.Length == 0)
            {
                lines = said.Select((s, i) => i == 0 ? s.Lines.Take(1).ToList() : new List<string>()).ToList();
                body = Clip(Compose(said, lines, one), MaxBody);
            }
            // A click opens a card only when the notice really speaks about a game that has one.
            bool opensCard = said.Where((s, g) => lines[g].Count > 0).Any(s => s.HasCard);
            return new DayNoticeText(Clip(title, MaxTitle), body, opensCard);
        }

        private static string Compose(IReadOnlyList<DayNoticeSection> said, IReadOnlyList<List<string>> lines, bool one)
        {
            var parts = new List<string>();
            for (int g = 0; g < said.Count; g++)
            {
                if (lines[g].Count == 0) continue;
                string text = string.Join(" ", lines[g]);
                parts.Add(one ? text : said[g].GameName + ": " + text);
            }
            return string.Join("\n", parts);
        }

        private static string Clip(string text, int max) =>
            text.Length <= max ? text : text.Substring(0, max - 1).TrimEnd() + "…";
    }

    /// <summary>What a start may show on its own, one thing at most.</summary>
    public enum StartNoticeKind
    {
        None,
        /// <summary>The window opened on Start with the news card: that is this start's notice.</summary>
        NewsCard,
        /// <summary>Started in the tray with news waiting: one tray line that says so.</summary>
        NewsTray,
        /// <summary>The notice of the day.</summary>
        Day,
    }

    /// <summary>
    /// At most one notice per start. News about Kaption goes first, on the first start after an update; the notice of
    /// the day takes every other start. A start in the tray only ever uses the tray. Pure.
    /// </summary>
    public static class StartNoticePlanner
    {
        /// <summary>What to do with the tray's news line while it still waits for its turn.</summary>
        public enum NewsTrayStep
        {
            Send,
            /// <summary>The player opened the window meanwhile and the card was on screen: it was this start's notice.</summary>
            CardSaidIt,
            /// <summary>Answered or closed meanwhile: nothing left to announce.</summary>
            NothingLeft,
        }

        /// <param name="cardPresented">The news card was really on screen in this run.</param>
        /// <param name="rowsLeft">Rows still waiting on the card.</param>
        public static NewsTrayStep NewsTray(bool cardPresented, int rowsLeft) =>
            cardPresented ? NewsTrayStep.CardSaidIt
            : rowsLeft <= 0 ? NewsTrayStep.NothingLeft
            : NewsTrayStep.Send;

        /// <param name="newsWaiting">The news card has rows for this player.</param>
        /// <param name="windowShown">The app window opened at this start (false for a start with Windows).</param>
        /// <param name="trayToldNews">The tray already said, at an earlier start, that this release has news.</param>
        public static StartNoticeKind Pick(bool newsWaiting, bool windowShown, bool trayToldNews) =>
            !newsWaiting ? StartNoticeKind.Day
            : windowShown ? StartNoticeKind.NewsCard
            : trayToldNews ? StartNoticeKind.Day
            : StartNoticeKind.NewsTray;
    }
}
