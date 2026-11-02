using System.Text;
using System.Text.RegularExpressions;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Detection;

namespace GI_Subtitles.Services.Cards
{
    /// <summary>
    /// The build card maker on kaption.one: /pl/moj-build/ for a Polish interface, /my-build/ for any other, with the game
    /// and, when there is one, the player's UID (?game=genshin&amp;uid=735366201). With a UID the page opens straight on
    /// the card; without one it opens on its form, the game already picked.
    /// </summary>
    public static class BuildCardLink
    {
        public const string PolishPath = "/pl/moj-build/";
        public const string EnglishPath = "/my-build/";

        // The site's own rule for Genshin and Star Rail (landing/src/lib/my-build/uid.ts), and what UidReader accepts.
        private static readonly Regex UidShape = new Regex("^[1-9][0-9]{8,9}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>A UID as the reader and the page take it: 9 or 10 ASCII digits, no leading zero.</summary>
        public static bool IsUid(string uid) => uid != null && UidShape.IsMatch(uid);

        /// <summary>The maker's game ("genshin", "starrail", "zzz") for a Kaption game id or alias; null for any other game.</summary>
        public static string CardGame(string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId)) return null;
            return GameRegionProfile.Get(gameId).GameId?.ToLowerInvariant() switch
            {
                "genshin" => "genshin",
                "starrail" => "starrail",
                "zzz" => "zzz",
                _ => null,
            };
        }

        /// <summary>
        /// The maker's URL. The game goes in whenever it is one the maker knows; the UID only when it has a UID's shape.
        /// <paramref name="uiLanguage"/> is the interface language ("pl", "pl-PL", "en-US"...): Polish opens the Polish page.
        /// </summary>
        public static string Url(string cardGame, string uid, string uiLanguage)
        {
            var url = new StringBuilder(CharacterPageLink.Site)
                .Append(CharacterPageLink.IsPolish(uiLanguage) ? PolishPath : EnglishPath);
            char separator = '?';
            if (cardGame is "genshin" or "starrail" or "zzz")
            {
                url.Append(separator).Append("game=").Append(cardGame);
                separator = '&';
            }
            if (IsUid(uid)) url.Append(separator).Append("uid=").Append(uid);
            return url.ToString();
        }
    }
}
