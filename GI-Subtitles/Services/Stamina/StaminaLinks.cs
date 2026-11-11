using System;
using System.Globalization;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Stamina
{
    /// <summary>
    /// The game's stamina timer on kaption.one (landing/src/data/tools-paths.ts: /tools/stamina-timer/&lt;hub&gt;/,
    /// /pl/narzedzia/licznik-energii/&lt;hub&gt;/). The reading rides in the address's fragment, the part after "#",
    /// which a browser keeps to itself and never sends to a server.
    /// </summary>
    public static class StaminaLinks
    {
        /// <summary>Whether kaption.one has a timer page for the game.</summary>
        public static bool HasTimerPage(GameManifest game) => game?.Stamina != null && !string.IsNullOrWhiteSpace(game.Site?.Hub);

        /// <summary>The timer page in the interface language; null when the site has none for the game.</summary>
        public static string TimerUrl(GameManifest game, string uiLanguage) =>
            HasTimerPage(game)
                ? $"{CharacterPageLink.Site}/{(CharacterPageLink.IsPolish(uiLanguage) ? "pl/narzedzia/licznik-energii" : "tools/stamina-timer")}/{game.Site.Hub.Trim()}/"
                : null;

        /// <summary>
        /// The timer page with the reading in the fragment: <c>#v=143&amp;t=1731200000&amp;n=240</c> (value, the moment
        /// it was read in Unix seconds, seconds to the next point, at least 1), optionally <c>&amp;s=</c>. The page's
        /// side is <c>readingFromFragment</c> in landing/src/lib/stamina.ts.
        /// </summary>
        /// <param name="seenAt">
        /// When the number on screen last agreed with the timer. Later than the reading, it goes along as <c>&amp;s=</c>:
        /// the page then knows the reading is fresh although the timer has run from an older moment.
        /// </param>
        public static string TimerUrl(GameManifest game, string uiLanguage, StaminaReading reading, DateTimeOffset? seenAt = null)
        {
            string page = TimerUrl(game, uiLanguage);
            if (page == null || reading == null) return page;
            long at = reading.At.ToUnixTimeSeconds();
            string seen = seenAt is DateTimeOffset s && s.ToUnixTimeSeconds() > at
                ? string.Create(CultureInfo.InvariantCulture, $"&s={s.ToUnixTimeSeconds()}")
                : string.Empty;
            return string.Create(CultureInfo.InvariantCulture,
                $"{page}#v={reading.Value}&t={at}&n={Math.Max(1, (long)Math.Round(reading.NextInSeconds))}{seen}");
        }
    }
}
