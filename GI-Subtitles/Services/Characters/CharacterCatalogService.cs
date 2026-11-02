using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using GI_Subtitles.Services.Data;
using GI_Subtitles.Services.Security;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Characters
{
    /// <summary>
    /// Lazily loads and caches one <see cref="CharacterCatalog"/> per game from
    /// %APPDATA%\Kaption\&lt;Game&gt;\Characters.gisub. Never throws; a missing or broken file
    /// gives <see cref="CharacterCatalog.Empty"/>.
    /// </summary>
    /// <summary>How the last load of a game's character file went; lets a page tell "no data yet" from "broken".</summary>
    public enum CatalogLoadState
    {
        NotLoaded,
        Missing,
        Loaded,
        Unreadable,
    }

    public static class CharacterCatalogService
    {
        private static readonly ConcurrentDictionary<string, Lazy<Task<CharacterCatalog>>> Cache =
            new ConcurrentDictionary<string, Lazy<Task<CharacterCatalog>>>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, CatalogLoadState> States =
            new ConcurrentDictionary<string, CatalogLoadState>(StringComparer.OrdinalIgnoreCase);

        public static CatalogLoadState StateOf(string game) =>
            game != null && States.TryGetValue(game, out var s) ? s : CatalogLoadState.NotLoaded;

        /// <summary>Raised (on the caller's thread) after a game's data was replaced.</summary>
        public static event Action<string> CatalogChanged;

        public static Task<CharacterCatalog> GetAsync(string game)
        {
            if (string.IsNullOrWhiteSpace(game)) return Task.FromResult(CharacterCatalog.Empty);
            return Cache.GetOrAdd(game, g => new Lazy<Task<CharacterCatalog>>(() => Task.Run(() => LoadFromDisk(g)))).Value;
        }

        public static void Invalidate(string game)
        {
            if (string.IsNullOrWhiteSpace(game)) return;
            Cache.TryRemove(game, out _);
            try { CatalogChanged?.Invoke(game); }
            catch (Exception ex) { Logger.Log.Warn($"CatalogChanged handler failed: {ex.Message}"); }
        }

        private static CharacterCatalog LoadFromDisk(string game)
        {
            try
            {
                string path = GameDataPaths.CharactersJson(game);
                var helper = new FileProtectionHelper(FileProtectionFactory.Create());
                if (!helper.FileExists(path))
                {
                    States[game] = CatalogLoadState.Missing;
                    return CharacterCatalog.Empty;
                }
                using (Stream s = helper.OpenForReading(path))
                {
                    // Config may hold an alias ("Zenless"); the section carries the wire id ("zzz").
                    string expected = GI_Subtitles.Services.Detection.GameRegionProfile.Get(game)?.GameId ?? game;
                    var catalog = CharacterCatalog.Load(s, expected);
                    Logger.Log.Info($"Character data for {game}: {catalog.Characters.Count} characters (v{catalog.GameVersion}).");
                    States[game] = catalog.IsEmpty ? CatalogLoadState.Unreadable : CatalogLoadState.Loaded;
                    return catalog;
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Character data for {game} not loaded: {ex.Message}");
                States[game] = CatalogLoadState.Unreadable;
                return CharacterCatalog.Empty;
            }
        }
    }
}
