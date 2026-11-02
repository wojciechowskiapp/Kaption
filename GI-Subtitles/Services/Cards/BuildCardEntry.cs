using System;
using System.Threading.Tasks;

namespace GI_Subtitles.Services.Cards
{
    /// <summary>Where the build card's UID came from.</summary>
    public enum UidSource { None, Remembered, Screen }

    /// <summary>The page to open and the UID in it (null when the page has to ask for one).</summary>
    /// <param name="ReadOutcome">
    /// How the read off the screen ended; null when there was none (a remembered UID, or a game the maker has no cards for).
    /// </param>
    public readonly record struct BuildCardTarget(string Url, string Uid, UidSource Source, UidReadOutcome? ReadOutcome = null);

    /// <summary>
    /// "Karta buildu" (Start's Skróty tile, the Characters header): the UID remembered for the game when there is one,
    /// else one read off the game screen and remembered from then on, else none; then the maker's URL.
    /// </summary>
    public static class BuildCardEntry
    {
        /// <param name="gameId">The selected game (Config "Game").</param>
        /// <param name="uiLanguage">The interface language; Polish opens the Polish page.</param>
        /// <param name="readFromScreen">Reads the UID off the screen; called only when nothing is remembered.</param>
        public static async Task<BuildCardTarget> ResolveAsync(string gameId, string uiLanguage, Func<Task<UidReadResult>> readFromScreen)
        {
            string game = BuildCardLink.CardGame(gameId);
            string uid = BuildCardMemory.Recall(gameId);
            var source = uid != null ? UidSource.Remembered : UidSource.None;
            UidReadOutcome? outcome = null;
            if (uid == null && game != null && readFromScreen != null)
            {
                UidReadResult read = await readFromScreen();
                outcome = read.Outcome;
                if (BuildCardMemory.Remember(gameId, read.Uid))
                {
                    uid = read.Uid;
                    source = UidSource.Screen;
                }
            }
            return new BuildCardTarget(BuildCardLink.Url(game, uid, uiLanguage), uid, source, outcome);
        }
    }
}
