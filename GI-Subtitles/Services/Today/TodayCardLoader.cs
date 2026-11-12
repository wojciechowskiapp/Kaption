using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Stamina;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Today
{
    /// <summary>
    /// What one card is built for: the game, the server and its game day, and the saved UID where the game's card
    /// reads a showcase, all read once before the first wait. Every section is built from this snapshot, so a server
    /// or an account the player picks while data is on its way cannot give a card with one server's books and
    /// another server's day, or one account's characters under another account's UID.
    /// </summary>
    /// <param name="Uid">The account the card's characters are read for (<see cref="UidFor"/>); null when none is saved or the game has no such section.</param>
    public sealed record TodayCardRequest(GameManifest Game, string ServerId, string Uid, string DayKey, DateTimeOffset Now)
    {
        /// <summary>Reads the selected server and the saved UID once and keys the request to that server's game day.</summary>
        public static TodayCardRequest Capture(GameManifest game, DateTimeOffset now)
        {
            string server = TodayCardSettings.ServerId;
            return new TodayCardRequest(game, server, UidFor(game, FarmCardSettings.Uid), TodayCardSettings.DayKey(game, now, server), now);
        }

        /// <summary>
        /// The saved UID as this game's card reads it: only the game with the showcase section has one, so saving or
        /// removing a UID never makes another game's card stale.
        /// </summary>
        public static string UidFor(GameManifest game, string savedUid) =>
            game != null && FarmCardSettings.IsGame(game.GameId) ? savedUid : null;

        /// <summary>
        /// Whether a card built for this request is still the one to show: the same game, the same server (as the
        /// game resolves it, so "no choice yet" and its first server are one), the same game day and, where the card
        /// reads a showcase, the same saved UID.
        /// </summary>
        public bool StillCurrent(GameManifest selectedGame, string selectedServer, string savedUid, DateTimeOffset now) =>
            string.Equals(selectedGame?.Id, Game?.Id, StringComparison.Ordinal)
            && string.Equals(Resolved(selectedServer), Resolved(ServerId), StringComparison.OrdinalIgnoreCase)
            && string.Equals(UidFor(Game, savedUid), Uid, StringComparison.Ordinal)
            && string.Equals(TodayCardSettings.DayKey(Game, now, selectedServer), DayKey, StringComparison.Ordinal);

        // A game without reset data has no server to tell apart.
        private string Resolved(string serverId) => Game?.Resets?.Server(serverId)?.Id ?? "";
    }

    /// <summary>Where the card's data comes from: the app's files and kaption.one, or a test's own answers.</summary>
    public sealed record TodayCardSources(
        Func<ServerDay, Task<TodaySchedule>> Schedule,
        Func<string, ServerDay, FarmServer, Task<ShowcaseRead>> Showcase,
        Func<Task<CharacterCatalog>> Catalog,
        Func<DateTime, Task<TodayWeek>> Week,
        Func<string, IReadOnlyList<AssistStoredReading>> Seen)
    {
        /// <summary>The app's own reads: each kept on disk and asked for at most once per game day or six hours.</summary>
        public static TodayCardSources Live { get; } = new(
            FarmTodayData.ScheduleAsync,
            FarmTodayData.ShowcaseAsync,
            () => CharacterCatalogService.GetAsync(FarmCardSettings.GameId),
            TodayWeekData.GetAsync,
            game => AssistStore.Default.Readings(game));
    }

    /// <summary>
    /// What came of getting a card ready: the request it was made for, what was prepared, and whether that request
    /// still held once everything had loaded. One that no longer holds is never shown.
    /// </summary>
    public sealed record TodayCardAttempt<T>(TodayCardRequest Request, T Prepared, bool Current, int Builds);

    /// <summary>
    /// Gathers what the card's sections read and builds the model. Config, two small files and at most two requests
    /// to kaption.one a day; the capture loop is never asked for anything.
    /// </summary>
    public static class TodayCardLoader
    {
        /// <summary>
        /// Gets a card ready, asks whether its request still holds and, when it does, shows it, all in one step:
        /// the question comes after the last wait, whatever waits <paramref name="prepare"/> has (the data, the
        /// icons, the screen), and <paramref name="show"/> runs right after the answer with no wait between them.
        /// A caller that showed the card after awaiting this method would resume as a new dispatcher operation, and a
        /// server or UID picked in between would slip past the answer. A stale card is prepared again from a fresh
        /// request while <paramref name="rebuilds"/> allow; stale after the last try, nothing is shown and
        /// <see cref="TodayCardAttempt{T}.Current"/> is false.
        /// </summary>
        /// <param name="rebuilds">0 for the card that shows by itself (the watcher comes back), 1 for one the player asked for.</param>
        /// <param name="show">Shows what was prepared. It must not wait for anything.</param>
        public static async Task<TodayCardAttempt<T>> PrepareAndShowAsync<T>(TodayCardRequest first,
            Func<TodayCardRequest, Task<T>> prepare, Func<TodayCardRequest, bool> stillCurrent, Func<TodayCardRequest> recapture, int rebuilds,
            Action<TodayCardRequest, T> show)
        {
            TodayCardRequest request = first;
            for (int build = 1; ; build++)
            {
                T prepared = await prepare(request);
                if (stillCurrent(request))
                {
                    show(request, prepared);
                    return new TodayCardAttempt<T>(request, prepared, true, build);
                }
                if (build > rebuilds) return new TodayCardAttempt<T>(request, prepared, false, build);
                request = recapture();
            }
        }

        public static async Task<TodayCardModel> BuildAsync(TodayCardRequest request, string lang, TodayCardSources sources)
        {
            GameManifest game = request?.Game;
            if (game == null) return null;
            DateTimeOffset now = request.Now;
            FarmCardModel farm = null;
            if (FarmCardSettings.IsGame(game.GameId))
            {
                // The request's server, never the setting read again: it may change at any wait below.
                FarmServer region = ServerDay.Parse(request.ServerId);
                var day = ServerDay.At(now, region);
                var schedule = await sources.Schedule(day);
                if (schedule != null)
                {
                    // The request's account too: the saved UID can change during the schedule's wait above, and one
                    // switched away and back would pass the host's check with the account in between on the card.
                    string uid = request.Uid;
                    var read = await sources.Showcase(uid, day, region);
                    var catalog = await sources.Catalog();
                    farm = FarmCardBuilder.Build(schedule, day, now, uid != null, read.Roster, catalog, lang, read.UnknownUid);
                }
                else
                {
                    Logger.Log.Info("Today card: no domain schedule yet, so no books and characters.");
                }
            }
            TodayWeek week = await sources.Week(now.UtcDateTime);
            IReadOnlyList<AssistStoredReading> seen = Array.Empty<AssistStoredReading>();
            try { seen = sources.Seen(game.GameId) ?? seen; }
            catch (Exception ex) { Logger.Log.Warn($"Today card: what was read on this PC is not available: {ex.Message}"); }
            return TodayCardBuilder.Build(new TodayInputs(game, now, request.ServerId, StaminaSettings.Enabled,
                StaminaSettings.LoadReading(game.Id), StaminaSettings.LoadSeen(game.Id), week?.For(lang), seen, farm, lang));
        }
    }
}
