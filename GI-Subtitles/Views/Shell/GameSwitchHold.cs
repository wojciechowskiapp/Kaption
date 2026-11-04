using System;
using GI_Subtitles.Services.Detection;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// A game switch writes Config["Game"] at once, but the reader, the dialogue data and the matcher stay the ones this
    /// process loaded until the restart. "Later" on the restart prompt used to leave auto-start watching for the new game
    /// and translating it with the old game's data; while a switch waits for the restart, auto-start holds and the Start
    /// card offers the restart instead.
    /// </summary>
    public static class GameSwitchHold
    {
        /// <summary>The saved game is not the one this session loaded (aliases and case count as the same game).</summary>
        public static bool AwaitsRestart(string loadedGame, string savedGame) =>
            !string.Equals(Canonical(loadedGame), Canonical(savedGame), StringComparison.OrdinalIgnoreCase);

        /// <summary>Auto-start runs with the user's setting on and no switch waiting for the restart.</summary>
        public static bool AutoStartEnabled(bool settingOn, bool switchPending) => settingOn && !switchPending;

        private static string Canonical(string game)
        {
            string id = string.IsNullOrWhiteSpace(game) ? "Genshin" : game.Trim();
            return GameRegionProfile.Get(id).GameId ?? id;
        }
    }
}
