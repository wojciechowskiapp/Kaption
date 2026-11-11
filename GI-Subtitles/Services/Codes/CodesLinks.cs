using System;
using System.Collections.Generic;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Codes
{
    /// <summary>
    /// Where the codes card points, all from the game's manifest. The site's rule (landing/src/data/news.ts
    /// codesGames, redeemUrl): a game has a codes page when its news.codes is "hoyolab" with a redeem address, or
    /// "ingame" with the menu path. Polish interface: /pl/kody/&lt;hub&gt;/, any other: /codes/&lt;hub&gt;/.
    /// </summary>
    public static class CodesLinks
    {
        public static bool IsHoyolab(GameManifest game) =>
            game?.News?.Codes is { Kind: GameCodes.KindHoyolab } c && IsRedeemTemplate(c.RedeemUrl);

        public static bool IsInGame(GameManifest game) =>
            game?.News?.Codes is { Kind: GameCodes.KindInGame, RedeemHow.Path.Length: > 0 };

        /// <summary>Whether kaption.one has a codes page for the game (so the card can exist at all).</summary>
        public static bool HasCodesPage(GameManifest game) =>
            !string.IsNullOrWhiteSpace(game?.Site?.Hub) && (IsHoyolab(game) || IsInGame(game));

        /// <summary>The game's codes page in the interface language; null when the site has none.</summary>
        public static string AllCodesUrl(GameManifest game, string uiLanguage) =>
            HasCodesPage(game)
                ? $"{CharacterPageLink.Site}/{(CharacterPageLink.IsPolish(uiLanguage) ? "pl/kody" : "codes")}/{game.Site.Hub.Trim()}/"
                : null;

        /// <summary>The publisher's redeem page with the code filled in (HoYoLAB games); null for any other game.</summary>
        public static string RedeemUrl(GameManifest game, string code) =>
            IsHoyolab(game) && !string.IsNullOrEmpty(code)
                ? game.News.Codes.RedeemUrl.Replace(GameCodes.CodePlaceholder, Uri.EscapeDataString(code), StringComparison.Ordinal)
                : null;

        /// <summary>The in-game menu path to the code field ("Terminal", "Settings", …); null for a game redeemed on a page.</summary>
        public static IReadOnlyList<string> InGamePath(GameManifest game) => IsInGame(game) ? game.News.Codes.RedeemHow.Path : null;

        /// <summary>When the in-game code field unlocks ("Union Level 2"); null when the manifest says nothing.</summary>
        public static string InGameUnlock(GameManifest game) =>
            IsInGame(game) && !string.IsNullOrWhiteSpace(game.News.Codes.RedeemHow.Unlock) ? game.News.Codes.RedeemHow.Unlock.Trim() : null;

        // games/game.schema.json: ^https://[^\s]*\{code\}[^\s]*$
        private static bool IsRedeemTemplate(string url) =>
            url != null && url.StartsWith("https://", StringComparison.Ordinal)
            && url.Contains(GameCodes.CodePlaceholder, StringComparison.Ordinal) && !url.AsSpan().ContainsAny(" \t\r\n");
    }
}
