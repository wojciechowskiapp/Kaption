using System;
using System.Collections.Generic;
using System.Linq;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Stamina;

namespace GI_Subtitles.Services.Today
{
    public enum TodayStaminaState
    {
        /// <summary>Under the cap by the timer: the estimate now and when it fills.</summary>
        Filling,
        /// <summary>At the cap by the timer, from a reading young enough to say when it filled.</summary>
        Full,
        /// <summary>The reading is older than a fill from zero takes: full only as "full since about".</summary>
        FullLongAgo,
    }

    /// <summary>What the reserve pool gained since the main one filled, by its rule; the pool's own number is never read.</summary>
    public sealed record TodayReserve(string Name, int Gained, int Max)
    {
        /// <summary>The rule alone has filled it: it may have been full for a while.</summary>
        public bool AtCap => Gained >= Max;
    }

    /// <summary>
    /// The stamina section: an estimate by the game's rule from one reading, never the present state. The player may
    /// have spent some on a phone since, which is why <see cref="ReadAt"/> always goes with it.
    /// </summary>
    public sealed record TodayStamina(string PoolName, int Value, int Max, TodayStaminaState State, DateTimeOffset FullAt,
        DateTimeOffset ReadAt, TodayReserve Reserve, string TimerUrl);

    /// <summary>The next daily and weekly resets on the player's server.</summary>
    public sealed record TodayResets(DateTimeOffset Daily, DateTimeOffset Weekly, DayOfWeek WeeklyDay, string ServerName);

    /// <summary>A banner or event that ends within the week, with its article on the site.</summary>
    public sealed record TodayPeriod(string Kind, string Title, string Label, DateTimeOffset EndsAt, string Url);

    /// <summary>Something another reader saw on this PC, and when.</summary>
    public sealed record TodaySeen(string ReaderId, string Value, DateTimeOffset SeenAt);

    /// <summary>Everything the section builders read. The caller gathers it; nothing here touches a file or the clock.</summary>
    public sealed record TodayInputs(GameManifest Game, DateTimeOffset Now, string ServerId, bool StaminaOn, StaminaReading Reading,
        DateTimeOffset? SeenAt, IReadOnlyList<WeekPeriod> Periods, IReadOnlyList<AssistStoredReading> Read, FarmCardModel Farm, string UiLanguage);

    /// <summary>
    /// The Today card of one game: sections that fill themselves from what Kaption knows, each absent when it has
    /// nothing to say. Built once per showing; the card over the game and the block on Start lay out the same model.
    /// </summary>
    public sealed class TodayCardModel
    {
        /// <summary>The game's wire id ("starrail") and the GameId its accent and folder use ("StarRail").</summary>
        public string Game { get; init; } = "";
        public string GameId { get; init; } = "";
        public string GameName { get; init; } = "";
        public DateTimeOffset Now { get; init; }
        /// <summary>The weekday of the server's game day; the local one for a game without reset data.</summary>
        public DayOfWeek Weekday { get; init; }
        public TodayStamina Stamina { get; init; }
        public TodayResets Resets { get; init; }
        public IReadOnlyList<TodayPeriod> Ending { get; init; } = Array.Empty<TodayPeriod>();
        /// <summary>Periods past the ones listed that also end within the week.</summary>
        public int MoreEnding { get; init; }
        public IReadOnlyList<TodaySeen> Seen { get; init; } = Array.Empty<TodaySeen>();
        /// <summary>Genshin's "your characters" section (the farm card, content unchanged); null for the other games.</summary>
        public FarmCardModel Farm { get; init; }
        public string FullListUrl { get; init; } = "";

        /// <summary>Something to lay out at all (the card on demand, the block on Start).</summary>
        public bool HasSections => Stamina != null || Resets != null || Ending.Count > 0 || Seen.Count > 0 || Farm != null;

        /// <summary>
        /// Worth showing over the game unasked: more than the reset countdowns, which never change from day to day.
        /// A game with nothing else gets no card that day, silently.
        /// </summary>
        public bool WorthShowing => Stamina != null || Ending.Count > 0 || Seen.Count > 0 || Farm != null;

        /// <summary>Roughly the characters a player reads, for ReadingTime.</summary>
        public int ReadingChars =>
            (Farm?.ReadingChars ?? 40) + (Stamina != null ? 70 : 0) + (Resets != null ? 40 : 0)
            + Ending.Sum(p => p.Title.Length + (p.Label?.Length ?? 0) + 14) + Seen.Sum(s => s.Value.Length + 30);
    }

    /// <summary>The card's sections, one pure builder each, and the model they make together.</summary>
    public static class TodayCardBuilder
    {
        /// <summary>The week "ending soon" looks ahead (landing/src/data/whats-on.ts WEEK_DAYS).</summary>
        public static readonly TimeSpan Week = TimeSpan.FromDays(7);
        public const int MaxEnding = 3;
        public const int MaxSeen = 4;

        /// <summary>A stored time this far ahead of the clock is a clock that moved, not a reading: refused.</summary>
        public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

        public static TodayCardModel Build(TodayInputs input)
        {
            GameManifest game = input.Game;
            GameResetServer server = game?.Resets?.Server(input.ServerId);
            var ending = Ending(input.Periods, game?.Id, input.Now);
            return new TodayCardModel
            {
                Game = game?.Id ?? "",
                GameId = game?.GameId ?? "",
                GameName = game?.Name ?? "",
                Now = input.Now,
                Weekday = server != null ? ServerDay.At(input.Now, game.Resets, server).Weekday : input.Now.ToLocalTime().DayOfWeek,
                Stamina = input.StaminaOn ? Stamina(game, input.Reading, input.SeenAt, input.Now, input.UiLanguage) : null,
                Resets = Resets(game?.Resets, input.ServerId, input.Now),
                Ending = ending.Rows,
                MoreEnding = ending.More,
                Seen = Seen(input.Read, input.Now),
                Farm = input.Farm,
                FullListUrl = CharacterPageLink.Site + (CharacterPageLink.IsPolish(input.UiLanguage) ? FarmCardBuilder.PolishPath : FarmCardBuilder.EnglishPath),
            };
        }

        /// <summary>
        /// The stamina estimate from the last reading and the manifest's rule. Null without a reading, for a game
        /// without stamina, and for a reading dated after <paramref name="now"/> (refused, not guessed).
        /// </summary>
        public static TodayStamina Stamina(GameManifest game, StaminaReading reading, DateTimeOffset? seenAt, DateTimeOffset now, string uiLanguage)
        {
            GameStamina pool = game?.Stamina;
            if (pool == null || reading == null || pool.Max <= 0 || pool.RegenSeconds <= 0) return null;
            if (reading.At > now + ClockSkew) return null;

            int value = StaminaClock.ValueAt(pool, reading, now);
            DateTimeOffset full = StaminaClock.FullAt(pool, reading);
            // The number on screen last agreed with the timer at seenAt: the read is that fresh.
            DateTimeOffset read = seenAt is DateTimeOffset seen && seen > reading.At && seen <= now + ClockSkew ? seen : reading.At;
            TimeSpan fromZero = TimeSpan.FromSeconds((double)pool.Max * pool.RegenSeconds);
            TodayStaminaState state = value < pool.Max ? TodayStaminaState.Filling
                : now - read >= fromZero ? TodayStaminaState.FullLongAgo
                : TodayStaminaState.Full;

            TodayReserve reserve = null;
            GameStaminaPool second = pool.Reserve;
            if (value >= pool.Max && second != null && second.Max > 0 && second.RegenSeconds > 0 && !string.IsNullOrWhiteSpace(second.Name) && now > full)
            {
                long gained = (long)Math.Floor((now - full).TotalSeconds / second.RegenSeconds);
                if (gained > 0) reserve = new TodayReserve(second.Name, (int)Math.Min(second.Max, gained), second.Max);
            }
            return new TodayStamina(pool.Name, value, pool.Max, state, full, read, reserve, StaminaLinks.TimerUrl(game, uiLanguage, reading, seenAt));
        }

        /// <summary>The next daily and weekly resets on the chosen server; null for a game without reset data.</summary>
        public static TodayResets Resets(GameResets resets, string serverId, DateTimeOffset now)
        {
            GameResetServer server = resets?.Server(serverId);
            if (server == null || resets.DailyHour is < 0 or > 23 || resets.WeeklyDay is < 0 or > 6) return null;
            ServerDay day = ServerDay.At(now, resets, server);
            return new TodayResets(day.Reset, ServerDay.NextWeekly(now, server.UtcOffset, resets.DailyHour, resets.WeeklyDay),
                (DayOfWeek)resets.WeeklyDay, server.Name ?? "");
        }

        /// <summary>
        /// What ends within the week in this game, soonest first: periods that have started and whose end is still
        /// ahead. A period whose article is not a page of the site is left out.
        /// </summary>
        public static (IReadOnlyList<TodayPeriod> Rows, int More) Ending(IReadOnlyList<WeekPeriod> periods, string game, DateTimeOffset now, int max = MaxEnding)
        {
            if (periods == null || string.IsNullOrEmpty(game)) return (Array.Empty<TodayPeriod>(), 0);
            var rows = periods
                .Where(p => p != null && string.Equals(p.Game, game, StringComparison.OrdinalIgnoreCase))
                .Where(p => (p.StartsAt == null || p.StartsAt <= now) && p.EndsAt > now && p.EndsAt <= now + Week)
                .Select(p => new TodayPeriod(p.Kind, p.Title, p.Label, p.EndsAt, CharacterPageLink.Site + p.Path))
                .Where(p => CharacterPageLink.IsSitePage(p.Url))
                .OrderBy(p => p.EndsAt).ThenBy(p => p.Title, StringComparer.Ordinal)
                .ToList();
            return (rows.Take(Math.Max(0, max)).ToList(), Math.Max(0, rows.Count - Math.Max(0, max)));
        }

        /// <summary>
        /// What the other readers saw on this PC, newest first, each with its time. The stamina reader is not here: it
        /// has a rule and a section of its own. Nothing seen means no section.
        /// </summary>
        public static IReadOnlyList<TodaySeen> Seen(IReadOnlyList<AssistStoredReading> readings, DateTimeOffset now, int max = MaxSeen)
        {
            if (readings == null) return Array.Empty<TodaySeen>();
            return readings
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Value) && !string.IsNullOrWhiteSpace(r.ReaderId))
                .Where(r => !string.Equals(r.ReaderId, AssistSettings.StaminaReader, StringComparison.Ordinal))
                .Where(r => r.SeenUtc <= now + ClockSkew)
                .OrderByDescending(r => r.SeenUtc)
                .Take(Math.Max(0, max))
                .Select(r => new TodaySeen(r.ReaderId, r.Value.Trim(), r.SeenUtc))
                .ToList();
        }
    }
}
