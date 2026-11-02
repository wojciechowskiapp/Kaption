using System;
using System.Collections.Generic;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// Allows the gamedata-bundle self-heal once per game per process, and only for a bundle
    /// that failed HMAC. A second failure after re-downloading must surface, not loop.
    /// </summary>
    public sealed class GamedataRecoveryGate
    {
        private readonly HashSet<string> _attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        /// <summary>True exactly once per game for <see cref="DialoguePrepareFailure.StaleMachineBoundFile"/>;
        /// any other outcome returns false without using up the attempt.</summary>
        public bool TryBegin(string game, DialoguePrepareFailure failure)
        {
            if (failure != DialoguePrepareFailure.StaleMachineBoundFile || string.IsNullOrWhiteSpace(game))
                return false;

            lock (_lock)
            {
                return _attempted.Add(game.Trim());
            }
        }
    }
}
