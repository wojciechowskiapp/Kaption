using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Today;

namespace GI_Subtitles.Services.Notices
{
    /// <summary>
    /// What one notice of the day is built for, read once before the first wait: the games it speaks about (each with
    /// its server and game day, as the Today card's own request), which of them already had their Today card come up
    /// by itself that day, the selected game, the server choice and the saved Genshin UID. The notice is only sent if
    /// all of it still holds after the last wait, so a server, a game or an account picked while the data was on its
    /// way can never put one player's characters, or one day's books, into the notice of another, a card that came
    /// up meanwhile is not followed by a notice saying the same, and none of it uses up the day.
    /// </summary>
    public sealed record DayNoticeRequest(
        IReadOnlyList<TodayCardRequest> Games,
        IReadOnlyCollection<string> CardsShown,
        GameManifest Selected,
        string ServerId,
        string Uid,
        string DayKey,
        DayOfWeek Weekday,
        DateTimeOffset DayEnds,
        string Lang,
        DateTimeOffset Now)
    {
        /// <summary>
        /// Reads nothing itself: the caller hands over what the settings say at this moment. The day the notice is
        /// remembered by is the selected game's game day (the local date for a game without reset data).
        /// </summary>
        /// <param name="lastPlayed">When Kaption last read a game's stamina: the sign that the player plays it (DayNoticeContent.Games).</param>
        /// <param name="cardShownDay">The game day a game's card last showed by itself (TodayCardSettings.LastShownDay).</param>
        public static DayNoticeRequest Capture(IReadOnlyList<GameManifest> listed, GameManifest selected, string serverId, string uid,
            Func<string, DateTimeOffset?> lastPlayed, Func<GameManifest, string> cardShownDay, string lang, DateTimeOffset now)
        {
            var games = DayNoticeContent.Games(listed, selected?.Id, lastPlayed, now)
                .Select(g => new TodayCardRequest(g, serverId, TodayCardRequest.UidFor(g, uid), TodayCardSettings.DayKey(g, now, serverId), now)).ToList();
            var shown = games.Where(r => CardShown(r, cardShownDay)).Select(r => r.Game.Id).ToHashSet(StringComparer.Ordinal);
            GameResetServer server = selected?.Resets?.Server(serverId);
            DateTimeOffset local = now.ToLocalTime();
            ServerDay? day = server != null ? ServerDay.At(now, selected.Resets, server) : null;
            return new DayNoticeRequest(games, shown, selected, serverId, uid, TodayCardSettings.DayKey(selected, now, serverId),
                day?.Weekday ?? local.DayOfWeek, day?.Reset ?? DayNoticeContent.LocalDayEnds(now), lang, now);
        }

        /// <summary>
        /// Whether a notice built for this request is still the one to send: the same selected game, the same saved
        /// UID, and for every game spoken about the same server, the same game day, and no Today card that came up
        /// by itself since (it said the same and more).
        /// </summary>
        public bool StillCurrent(GameManifest selectedGame, string selectedServer, string savedUid, Func<GameManifest, string> cardShownDay,
            DateTimeOffset now) =>
            string.Equals(selectedGame?.Id, Selected?.Id, StringComparison.Ordinal)
            && string.Equals(savedUid, Uid, StringComparison.Ordinal)
            && string.Equals(TodayCardSettings.DayKey(Selected, now, selectedServer), DayKey, StringComparison.Ordinal)
            && Games.All(r => r.StillCurrent(r.Game, selectedServer, savedUid, now) && CardShown(r, cardShownDay) == CardsShown.Contains(r.Game.Id));

        private static bool CardShown(TodayCardRequest game, Func<GameManifest, string> cardShownDay) =>
            string.Equals(cardShownDay?.Invoke(game.Game), game.DayKey, StringComparison.Ordinal);
    }

    public enum DayNoticeOutcome
    {
        /// <summary>The request held and the notice went out.</summary>
        Delivered,
        /// <summary>The request held, the notice was ready, and the tray or the gate did not take it: try again.</summary>
        Held,
        /// <summary>No game has anything to say today (yet).</summary>
        Nothing,
        /// <summary>The game, the server, the account or the day changed while the notice was being built: not sent, the day not used.</summary>
        Stale,
    }

    /// <summary>Gathers the day's notice from the Today card's own model, and sends it only for the request it was built for.</summary>
    public static class DayNoticeLoader
    {
        /// <summary>
        /// Builds the notice, asks whether its request still holds and, when it does, hands it to
        /// <paramref name="deliver"/>, all in one step: the question comes after the last wait and the delivery runs
        /// right after the answer with no wait between them (the Today card's rule, TodayCardLoader.PrepareAndShowAsync).
        /// </summary>
        /// <param name="deliver">Sends the notice and remembers the day; true when it went out. It must not wait for anything.</param>
        public static async Task<DayNoticeOutcome> PrepareAndDeliverAsync(DayNoticeRequest request,
            Func<DayNoticeRequest, Task<DayNoticeText>> build, Func<DayNoticeRequest, bool> stillCurrent,
            Func<DayNoticeRequest, DayNoticeText, bool> deliver)
        {
            DayNoticeText text = await build(request);
            if (!stillCurrent(request)) return DayNoticeOutcome.Stale;
            if (text == null) return DayNoticeOutcome.Nothing;
            return deliver(request, text) ? DayNoticeOutcome.Delivered : DayNoticeOutcome.Held;
        }

        /// <summary>
        /// The notice for a request: each game's section from its Today card model (TodayCardLoader.BuildAsync, the
        /// request's own server, day and account), so the notice, the card and Start's block say the same. Each
        /// game's request carries the notice's UID (<see cref="DayNoticeRequest.Capture"/>), so the showcase asked for
        /// is that account's whatever is saved by the time the loader reaches it.
        /// </summary>
        public static async Task<DayNoticeText> BuildAsync(DayNoticeRequest request, TodayCardSources sources,
            Func<GameManifest, bool> hasCard, Func<string, string, string> l)
        {
            CultureInfo culture = DayNoticeContent.CultureFor(request.Lang);
            var sections = new List<DayNoticeSection>();
            foreach (TodayCardRequest game in request.Games)
            {
                bool selected = string.Equals(game.Game.Id, request.Selected?.Id, StringComparison.Ordinal);
                // A card that already showed by itself today said the same and more (the request's own snapshot).
                bool shown = request.CardsShown.Contains(game.Game.Id);
                TodayCardModel model = shown ? null : await TodayCardLoader.BuildAsync(game, request.Lang, sources);
                // A click opens the selected game's card, so only its section can promise one.
                sections.Add(DayNoticeContent.Section(model, game.Game, selected && hasCard != null && hasCard(game.Game), l, culture));
            }
            return DayNoticeBuilder.Build(sections, request.Weekday, l, culture);
        }
    }
}
