using System;
using System.Collections.Generic;
using System.Globalization;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Guess;

namespace GI_Subtitles.Services.Notices
{
    /// <summary>
    /// The notices Kaption sends on its own to bring a player back, in the order two waiting ones go: the lower value
    /// first. Answers to what the player is doing right now (an update is ready, the first-session check, the reader
    /// fell back to the processor) are not notices of this kind and do not pass the gate.
    /// </summary>
    public enum NoticeKind
    {
        /// <summary>"The new story is in Polish now": once per game version.</summary>
        StoryReady = 0,
        /// <summary>Someone passed the player on a guess board: once a day.</summary>
        GuessRank = 1,
        /// <summary>The stamina pool is nearly full: once per fill.</summary>
        StaminaNearlyFull = 2,
        /// <summary>"Kaption has been updated": the tray's one line on a start with Windows, once per release.</summary>
        KaptionNews = 3,
        /// <summary>The notice of the day: once per game day, after anything that waited longer.</summary>
        Today = 4,
    }

    /// <summary>What the gate says to a notice that asks to show now.</summary>
    public enum NoticeGateResult
    {
        Show,
        /// <summary>The kind's own switch is off.</summary>
        Disabled,
        /// <summary>The kind's own once-a-day memory says it already reached the player today.</summary>
        KindShownToday,
        /// <summary>The whole app has sent its notices for today: this one stays on Start.</summary>
        CapReached,
        Translating,
        GameInFront,
        WindowsBusy,
        /// <summary>A kind that goes first is waiting too.</summary>
        Outranked,
        /// <summary>Another notice has just shown; Windows shows one at a time.</summary>
        TooSoon,
    }

    /// <summary>What the screen looks like when a notice asks: subtitles running, a game in front, Windows' own answer.</summary>
    public readonly record struct NoticeScreen(bool Translating, bool GameInFront, int Quns)
    {
        /// <summary>Asks Windows what is in front and whether an app may notify now (the shims of the ranking notice).</summary>
        public static NoticeScreen Read(bool translating) =>
            new(translating, GuessNoticeGate.AnyGameInFront(), GuessNoticeGate.QueryNotificationState());
    }

    /// <summary>Where the gate keeps the day's count and reads its settings; Config in the app, fields in tests.</summary>
    public interface INoticeLedger
    {
        /// <summary>Notices a day for the whole app (the setting, already clamped).</summary>
        int MaxPerDay { get; }
        /// <summary>The pause between two notices.</summary>
        TimeSpan Gap { get; }
        string Day { get; }
        int Count { get; }
        void Save(string day, int count);
    }

    public sealed class ConfigNoticeLedger : INoticeLedger
    {
        public int MaxPerDay => Math.Clamp(Config.Get(NoticeGate.MaxPerDayKey, NoticeGate.DefaultMaxPerDay), NoticeGate.MinMaxPerDay, NoticeGate.MaxMaxPerDay);
        public TimeSpan Gap => TimeSpan.FromSeconds(Math.Clamp(Config.Get(NoticeGate.GapSecondsKey, NoticeGate.DefaultGapSeconds), 0, NoticeGate.MaxGapSeconds));
        public string Day => Config.Get<string>(NoticeGate.DayKey, null);
        public int Count => Math.Max(0, Config.Get(NoticeGate.CountKey, 0));
        public void Save(string day, int count) =>
            Config.SetMany(new Dictionary<string, object> { [NoticeGate.DayKey] = day, [NoticeGate.CountKey] = count });
    }

    /// <summary>
    /// The one path every come-back notice takes to the tray. Each kind keeps its own switch and its own memory (once
    /// a day, once per version, once per fill); on top the gate holds what they share: never while subtitles run,
    /// never with a game in front, only when Windows accepts a notification, at most <see cref="MaxPerDayKey"/> a day
    /// for the whole app, one at a time, and the story notice first when two wait. A notice the gate turns away stays
    /// where it already is: the Start strip, the Start line, the stamina card.
    /// </summary>
    public sealed class NoticeGate
    {
        /// <summary>docs/CONFIG.md "Notices": tray notices a day for the whole app; 0 sends none.</summary>
        public const string MaxPerDayKey = "Notices.MaxPerDay";
        public const int DefaultMaxPerDay = 2, MinMaxPerDay = 0, MaxMaxPerDay = 20;
        /// <summary>Seconds between two notices, so the second does not replace the first on screen.</summary>
        public const string GapSecondsKey = "Notices.GapSeconds";
        public const int DefaultGapSeconds = 45, MaxGapSeconds = 600;
        /// <summary>Written by Kaption: the local day the count belongs to, and the count.</summary>
        public const string DayKey = "Notices.Day";
        public const string CountKey = "Notices.ShownToday";

        /// <summary>
        /// How long a notice that asked and was told to wait keeps its place in the order. Its owner asks again every
        /// minute or so; one that stopped asking (closed, expired, switched off) no longer holds the others back.
        /// </summary>
        public static readonly TimeSpan WaitingFor = TimeSpan.FromMinutes(5);

        private readonly INoticeLedger _ledger;
        private readonly object _lock = new();
        private readonly Dictionary<NoticeKind, DateTimeOffset> _waiting = new();
        private DateTimeOffset? _lastShown;

        public NoticeGate(INoticeLedger ledger) => _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

        /// <summary>The app's gate, on Config.</summary>
        public static NoticeGate Default { get; } = new(new ConfigNoticeLedger());

        /// <summary>The local day a count belongs to.</summary>
        public static string DayOf(DateTimeOffset now) => now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>The decision itself, pure: the first reason that holds, in this order.</summary>
        public static NoticeGateResult Check(bool kindEnabled, bool kindShownToday, int shownToday, int maxPerDay, NoticeScreen screen,
            bool higherWaits, bool tooSoon)
        {
            if (!kindEnabled) return NoticeGateResult.Disabled;
            if (kindShownToday) return NoticeGateResult.KindShownToday;
            if (shownToday >= maxPerDay) return NoticeGateResult.CapReached;
            if (screen.Translating) return NoticeGateResult.Translating;
            if (screen.GameInFront) return NoticeGateResult.GameInFront;
            if (screen.Quns != GuessNoticeGate.QunsAcceptsNotifications) return NoticeGateResult.WindowsBusy;
            if (higherWaits) return NoticeGateResult.Outranked;
            if (tooSoon) return NoticeGateResult.TooSoon;
            return NoticeGateResult.Show;
        }

        /// <summary>True for an answer that means "ask again later today".</summary>
        public static bool IsWait(NoticeGateResult r) =>
            r is NoticeGateResult.Translating or NoticeGateResult.GameInFront or NoticeGateResult.WindowsBusy
                or NoticeGateResult.Outranked or NoticeGateResult.TooSoon;

        /// <summary>
        /// A notice of <paramref name="kind"/> asks to show now. <see cref="NoticeGateResult.Show"/> means show it and
        /// call <see cref="Shown"/>. A wait answer keeps the kind's place in the order until it asks again. The screen
        /// is only looked at when the answer depends on it.
        /// </summary>
        /// <param name="kindShownToday">The kind's own once-a-day memory; false for a kind without one.</param>
        public NoticeGateResult Ask(NoticeKind kind, bool kindEnabled, bool kindShownToday, DateTimeOffset now, Func<NoticeScreen> screen)
        {
            lock (_lock)
            {
                int shown = ShownTodayLocked(now);
                int max = _ledger.MaxPerDay;
                // Without a screen to look at the answer is one of the three that need none.
                bool needsScreen = kindEnabled && !kindShownToday && shown < max;
                NoticeScreen s = needsScreen && screen != null ? screen() : new NoticeScreen(false, false, GuessNoticeGate.QunsAcceptsNotifications);
                bool tooSoon = _lastShown is DateTimeOffset last && now >= last && now - last < _ledger.Gap;
                var result = Check(kindEnabled, kindShownToday, shown, max, s, HigherWaitsLocked(kind, now), tooSoon);
                if (IsWait(result)) _waiting[kind] = now;
                else _waiting.Remove(kind);
                return result;
            }
        }

        /// <summary>The notice was shown: it counts toward today, and the next one waits out the gap.</summary>
        public void Shown(NoticeKind kind, DateTimeOffset now)
        {
            lock (_lock)
            {
                _ledger.Save(DayOf(now), ShownTodayLocked(now) + 1);
                _lastShown = now;
                _waiting.Remove(kind);
            }
        }

        /// <summary>The kind has nothing waiting any more (closed, expired, switched off).</summary>
        public void Withdraw(NoticeKind kind)
        {
            lock (_lock) _waiting.Remove(kind);
        }

        /// <summary>Notices shown on the local day of <paramref name="now"/>.</summary>
        public int ShownToday(DateTimeOffset now)
        {
            lock (_lock) return ShownTodayLocked(now);
        }

        /// <summary>True while <paramref name="kind"/> holds a place in the order.</summary>
        public bool IsWaiting(NoticeKind kind, DateTimeOffset now)
        {
            lock (_lock) return _waiting.TryGetValue(kind, out var at) && StillWaits(at, now);
        }

        private int ShownTodayLocked(DateTimeOffset now) =>
            string.Equals(_ledger.Day, DayOf(now), StringComparison.Ordinal) ? _ledger.Count : 0;

        private bool HigherWaitsLocked(NoticeKind kind, DateTimeOffset now)
        {
            foreach (var (other, at) in _waiting)
                if (other < kind && StillWaits(at, now)) return true;
            return false;
        }

        private static bool StillWaits(DateTimeOffset askedAt, DateTimeOffset now) => now >= askedAt && now - askedAt < WaitingFor;
    }
}
