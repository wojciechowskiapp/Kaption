using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Codes
{
    /// <summary>Why the codes card is not on screen; <see cref="None"/> when it is.</summary>
    public enum CodesCardHidden
    {
        None,
        /// <summary>The game has no codes page on kaption.one (its manifest has no news.codes).</summary>
        NoCodesPage,
        /// <summary>Nothing read yet for this game: the first read is under way, or every read so far failed.</summary>
        NotRead,
        /// <summary>The codes worker never checked the game: an empty list would say nothing, so the card says nothing.</summary>
        NeverChecked,
        /// <summary>Checked, and no code is active right now.</summary>
        NoActive,
        /// <summary>The last list read is older than the schedule's StaleAfter (reads keep failing): it may hold dead codes.</summary>
        Stale,
    }

    /// <summary>
    /// One code row: the code as typed, its rewards on one line, the publisher's redeem link (or null), its dates on one
    /// line ("Added 7 November · valid until 12 November, 5:00 PM"), whether it is new, and its "Ends in 7 h" mark (null
    /// unless it ends soon).
    /// </summary>
    public sealed record CodesCardRow(string Code, string Rewards, string RedeemUrl, string Dates = "", bool IsNew = false, string EndsIn = null);

    /// <summary>What the card shows, or why it is hidden.</summary>
    public sealed class CodesCardView
    {
        public static CodesCardView Hidden(CodesCardHidden why) => new() { HiddenBecause = why };

        public CodesCardHidden HiddenBecause { get; init; }
        public bool Visible => HiddenBecause == CodesCardHidden.None;
        /// <summary>The wire id ("genshin").</summary>
        public string Game { get; init; }
        public string GameName { get; init; }
        /// <summary>The first active codes, at most the cap: ending soon first, then the newest.</summary>
        public IReadOnlyList<CodesCardRow> Rows { get; init; } = Array.Empty<CodesCardRow>();
        /// <summary>Every active code right now, the shown ones included.</summary>
        public int ActiveCount { get; init; }
        public int MoreCount => Math.Max(0, ActiveCount - Rows.Count);
        public string AllCodesUrl { get; init; }
        /// <summary>The in-game menu path for games without a redeem page; null otherwise.</summary>
        public IReadOnlyList<string> InGamePath { get; init; }
        public string InGameUnlock { get; init; }
    }

    /// <summary>
    /// Turns a read list into the card (pure, so the tests and the window run the same code). The card exists only
    /// while there is something to redeem: never checked, nothing read, nothing active or a list gone stale all hide it.
    /// </summary>
    public static class CodesCardModel
    {
        /// <summary>The card shows three (ending soon first, then the newest); the rest are one click away on kaption.one.</summary>
        public const int MaxShown = 3;
        /// <summary>Between rewards on a row: "Primogem ×60 · Mora ×10,000".</summary>
        public const string RewardSeparator = " · ";

        public static CodesCardView Build(GameManifest game, RedeemCodeList list, DateTimeOffset? readAt, DateTimeOffset now,
                                          TimeSpan staleAfter, string uiLanguage, CultureInfo culture, int cap = MaxShown,
                                          CodeDateWords words = null, TimeZoneInfo zone = null)
        {
            if (!CodesLinks.HasCodesPage(game)) return CodesCardView.Hidden(CodesCardHidden.NoCodesPage);
            if (list == null || readAt == null || !string.Equals(list.Game, game.Id, StringComparison.Ordinal))
                return CodesCardView.Hidden(CodesCardHidden.NotRead);
            if (!list.Checked) return CodesCardView.Hidden(CodesCardHidden.NeverChecked);
            if (now - readAt.Value > staleAfter) return CodesCardView.Hidden(CodesCardHidden.Stale);
            var active = list.ActiveAt(now);
            if (active.Count == 0) return CodesCardView.Hidden(CodesCardHidden.NoActive);

            culture ??= CultureInfo.CurrentUICulture;
            DateTimeOffset? since = CodeDates.ListStart(list);
            return new CodesCardView
            {
                Game = game.Id,
                GameName = game.Name,
                Rows = CodeDates.Order(active, now).Take(Math.Max(1, cap)).Select(c => new CodesCardRow(c.Code, RewardLine(c.Rewards, culture),
                    CodesLinks.RedeemUrl(game, c.Code), CodeDates.Line(c, now, since, words, culture, zone), CodeDates.IsNew(c, now, since),
                    CodeDates.EndsIn(c, now, words, culture))).ToList(),
                ActiveCount = active.Count,
                AllCodesUrl = CodesLinks.AllCodesUrl(game, uiLanguage),
                InGamePath = CodesLinks.InGamePath(game),
                InGameUnlock = CodesLinks.InGameUnlock(game),
            };
        }

        /// <summary>The card's timer fires this long after a deadline, so the rebuild lands safely past it.</summary>
        public static readonly TimeSpan DeadlineSlack = TimeSpan.FromSeconds(1);
        /// <summary>The longest single wait of the card's timer; a later deadline is simply re-checked then.</summary>
        public static readonly TimeSpan MaxWait = TimeSpan.FromHours(12);

        /// <summary>
        /// When the shown card next changes by itself, with no read: its list goes stale (read time + StaleAfter), an
        /// active code reaches its expiry, or what a code's row says moves with the clock (it stops being new, starts
        /// ending soon, its time left drops a minute), whichever is first. Null when the card is hidden at
        /// <paramref name="now"/>.
        /// </summary>
        public static DateTimeOffset? NextChangeAt(GameManifest game, RedeemCodeList list, DateTimeOffset? readAt, DateTimeOffset now,
                                                   TimeSpan staleAfter, int cap = MaxShown)
        {
            if (!Build(game, list, readAt, now, staleAfter, null, CultureInfo.InvariantCulture).Visible) return null;
            DateTimeOffset next = readAt.Value + staleAfter;
            DateTimeOffset? since = CodeDates.ListStart(list);
            var active = list.ActiveAt(now);
            var shown = new HashSet<string>(CodeDates.Order(active, now).Take(Math.Max(1, cap)).Select(c => c.Code), StringComparer.Ordinal);
            foreach (var code in active)
            {
                // An expiry changes the count and the rows whichever code it is.
                if (code.ExpiresAt is DateTimeOffset at && at < next) next = at;
                if (shown.Contains(code.Code))
                {
                    // A row on screen: its marks and its time left.
                    if (CodeDates.NextChange(code, now, since) is DateTimeOffset moves && moves < next) next = moves;
                }
                else if (code.ExpiresAt is DateTimeOffset end && end - CodeDates.EndsSoonWithin > now && end - CodeDates.EndsSoonWithin < next)
                {
                    // A code past the cap changes nothing on screen until it starts ending soon and moves up into view.
                    next = end - CodeDates.EndsSoonWithin;
                }
            }
            return next;
        }

        /// <summary>The timer's wait for <paramref name="deadline"/>: never negative, the slack added, at most <see cref="MaxWait"/>.</summary>
        public static TimeSpan WaitUntil(DateTimeOffset deadline, DateTimeOffset now)
        {
            TimeSpan wait = deadline - now;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            wait += DeadlineSlack;
            return wait > MaxWait ? MaxWait : wait;
        }

        /// <summary>"Primogem ×60 · Mora ×10,000" (numbers in the interface's culture, names as the game spells them); "" without rewards.</summary>
        public static string RewardLine(IReadOnlyList<CodeReward> rewards, CultureInfo culture) =>
            rewards == null || rewards.Count == 0 ? string.Empty
            : string.Join(RewardSeparator, rewards.Select(r => $"{r.Name} ×{r.Count.ToString("N0", culture)}"));
    }
}
