using System.Collections.Generic;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Detection;

namespace GI_Subtitles.Services.Cards
{
    /// <summary>
    /// The last UID read for each game, so the next build card opens with it at once. Config.json keys
    /// <c>BuildCard.Uid.Genshin</c>, <c>BuildCard.Uid.StarRail</c> and <c>BuildCard.Uid.ZZZ</c> (docs/CONFIG.md); the
    /// "Zapomnij UID" action under Start › Zaawansowane forgets them all. A value that is not a UID (an edited file) is
    /// ignored, never sent.
    /// </summary>
    public static class BuildCardMemory
    {
        public const string KeyPrefix = "BuildCard.Uid.";

        /// <summary>The Config key for a game (its profile id, aliases folded); null for a game the maker has no cards for.</summary>
        public static string KeyFor(string gameId) =>
            BuildCardLink.CardGame(gameId) == null ? null : KeyPrefix + GameRegionProfile.Get(gameId).GameId;

        /// <summary>The UID remembered for the game, or null.</summary>
        public static string Recall(string gameId)
        {
            string key = KeyFor(gameId);
            if (key == null || !Config.Has(key)) return null;
            string uid = Config.Get<string>(key, null)?.Trim();
            return BuildCardLink.IsUid(uid) ? uid : null;
        }

        /// <summary>Keeps <paramref name="uid"/> for the game; false (and nothing written) when either is not valid.</summary>
        public static bool Remember(string gameId, string uid)
        {
            string key = KeyFor(gameId);
            if (key == null || !BuildCardLink.IsUid(uid)) return false;
            if (Config.Get<string>(key, null) != uid) Config.Set(key, uid);
            return true;
        }

        /// <summary>Every game's remembered UID, in the app's game order.</summary>
        public static IReadOnlyList<(string GameId, string Uid)> All()
        {
            var all = new List<(string, string)>();
            foreach (var profile in GameRegionProfile.RegisteredProfiles)
                if (Recall(profile.GameId) is string uid) all.Add((profile.GameId, uid));
            return all;
        }

        /// <summary>Forgets the UID of every game; returns how many keys there were.</summary>
        public static int ForgetAll()
        {
            int forgotten = 0;
            foreach (var profile in GameRegionProfile.RegisteredProfiles)
            {
                string key = KeyFor(profile.GameId);
                if (key == null || !Config.Has(key)) continue;
                Config.Remove(key);
                forgotten++;
            }
            return forgotten;
        }
    }
}
