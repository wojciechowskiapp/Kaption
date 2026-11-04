using System;
using System.Collections.Concurrent;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// What the server's file list last said per game and language, so a beta game whose first pack is not out
    /// yet reads "on the way" instead of "download the pack" (there is nothing to download). DictionarySyncService
    /// records every successful listing; the Start card, the start gate and the setup window ask.
    /// </summary>
    public static class PackAvailability
    {
        private static readonly ConcurrentDictionary<string, int> Listed = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Raised (on the recording thread) when a listing changes what <see cref="IsComing"/> answers.</summary>
        public static event Action Changed;

        /// <summary>The server listed <paramref name="files"/> packs for this game and language.</summary>
        public static void Record(string game, string language, int files)
        {
            if (string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(language)) return;
            string key = Key(game, language);
            bool changed = !Listed.TryGetValue(key, out int before) || (before == 0) != (files == 0);
            Listed[key] = Math.Max(0, files);
            if (changed) Changed?.Invoke();
        }

        /// <summary>
        /// A beta game (games/&lt;id&gt;.json) for which the server's last listing had no pack in this language: its
        /// first pack is on the way. False before any listing, offline, or for a live game with nothing listed.
        /// </summary>
        public static bool IsComing(string game, string language) =>
            !string.IsNullOrWhiteSpace(game) && !string.IsNullOrWhiteSpace(language) && GameRegistry.IsBeta(game)
            && Listed.TryGetValue(Key(game, language), out int files) && files == 0;

        /// <summary>Tests: forget every listing.</summary>
        internal static void Reset() => Listed.Clear();

        // "WuWa", "wuwa" and the aliases are one game; languages arrive as "PL" or "pl".
        private static string Key(string game, string language) =>
            (GameRegistry.Current.Find(game)?.Id ?? game.Trim().ToLowerInvariant()) + "|" + language.Trim().ToLowerInvariant();
    }
}
