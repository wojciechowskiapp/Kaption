using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Stamina;

namespace GI_Subtitles.Services.Today
{
    /// <summary>A server in a picker: its id and its name in the interface language.</summary>
    public sealed record TodayServerChoice(string Id, string Name);

    /// <summary>
    /// The Today card's settings beside the farm card's (docs/CONFIG.md, "Today card"). One switch
    /// (<see cref="FarmCardSettings.EnabledKey"/>) and one server choice (<see cref="FarmCardSettings.RegionKey"/>) for
    /// every game; "shown today" is kept per game, so switching games still shows the other game's card.
    /// </summary>
    public static class TodayCardSettings
    {
        /// <summary>Written by Kaption: the game day a game's card last showed by itself (TodayCard.LastShownDay.&lt;wire id&gt;).</summary>
        public const string LastShownDayPrefix = "TodayCard.LastShownDay.";

        /// <summary>The selected game's manifest; null for a game this build does not list.</summary>
        public static GameManifest ActiveGame() => GameRegistry.Current.Find(Config.Get<string>("Game", "Genshin"));

        /// <summary>The server id the player chose ("europe"); a game without that server uses its first.</summary>
        public static string ServerId => Config.Get<string>(FarmCardSettings.RegionKey, null);

        /// <summary>Saves the server choice; an id that is not a server's shape is not written.</summary>
        public static bool SetServer(string id)
        {
            id = id?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(id) || id.Length > 16 || !id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))) return false;
            Config.Set(FarmCardSettings.RegionKey, id);
            return true;
        }

        /// <summary>
        /// The game's servers for a picker, named in the interface language where the app has the name
        /// (FarmBlock_Region_Europe ...), else by the manifest's English name. Empty for a game without reset data.
        /// </summary>
        public static IReadOnlyList<TodayServerChoice> Servers(GameManifest game, Func<string, string, string> l)
        {
            var servers = game?.Resets?.Servers;
            if (servers == null) return Array.Empty<TodayServerChoice>();
            return servers.Where(s => !string.IsNullOrWhiteSpace(s?.Id)).Select(s =>
            {
                string key = "FarmBlock_Region_" + char.ToUpperInvariant(s.Id[0]) + s.Id.Substring(1).ToLowerInvariant();
                return new TodayServerChoice(s.Id, l?.Invoke(key, s.Name ?? s.Id) ?? s.Name ?? s.Id);
            }).ToList();
        }

        /// <summary>
        /// Whether this game can have a card at all: it has stamina the app can read, reset data, or it is the game
        /// with the weekday rotation. What ends soon comes from the site and can add to any of them.
        /// </summary>
        public static bool HasCard(GameManifest game) =>
            game != null && game.IsListed && (game.Resets != null || StaminaCardModel.Supports(game) || FarmCardSettings.IsGame(game.GameId));

        /// <summary>
        /// The key of "today" for the once-a-day showing: the server's game day where the game has reset data, the
        /// local date otherwise.
        /// </summary>
        public static string DayKey(GameManifest game, DateTimeOffset now, string serverId)
        {
            GameResetServer server = game?.Resets?.Server(serverId);
            return server != null
                ? ServerDay.At(now, game.Resets, server).Key(server.UtcOffset, game.Resets.DailyHour)
                : now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        /// <summary>The day this game's card last showed by itself. Genshin's falls back to the farm card's old key.</summary>
        public static string LastShownDay(GameManifest game)
        {
            if (game == null) return null;
            string own = Config.Get<string>(LastShownDayPrefix + game.Id, null);
            return own ?? (FarmCardSettings.IsGame(game.GameId) ? FarmCardSettings.LastShownDay : null);
        }

        public static void SetLastShownDay(GameManifest game, string day)
        {
            if (game == null) return;
            // An empty day (never shown) is written as such, not removed: Genshin would fall back to the old key.
            Config.Set(LastShownDayPrefix + game.Id, day ?? "");
        }
    }
}
