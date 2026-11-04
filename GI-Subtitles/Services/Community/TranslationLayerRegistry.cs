using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using GI_Subtitles.Services.Data;
using GI_Subtitles.Services.Detection;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Community
{
    /// <summary>
    /// One <see cref="TranslationLayers"/> per game and target language for the whole process, so the overlay, Ctrl+Q,
    /// the fix window and Settings all see the same fixes. Files live next to the packs:
    /// <c>%APPDATA%\Kaption\&lt;Game&gt;\overrides.&lt;lang&gt;.json</c> and <c>community.&lt;lang&gt;.json</c>.
    /// </summary>
    public static class TranslationLayerRegistry
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, TranslationLayers> Cache = new Dictionary<string, TranslationLayers>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The shared instance; its files load in the background the first time it is asked for.</summary>
        public static TranslationLayers For(string game, string language)
        {
            string folder = string.IsNullOrWhiteSpace(game) ? "Genshin" : game.Trim();
            string lang = LangOf(language);
            string key = folder + "|" + lang;
            TranslationLayers layers;
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out layers)) return layers;
                // The same per-game folder name the packs use (GameDataPaths sanitises it), under the test root in tests.
                string dir = GameDataPaths.GameDir(folder);
                if (TestRoot is string root) dir = Path.Combine(root, Path.GetFileName(dir));
                layers = new TranslationLayers(folder, WireGameOf(folder), lang,
                    Path.Combine(dir, $"overrides.{lang}.json"),
                    Path.Combine(dir, $"community.{lang}.json"));
                Cache[key] = layers;
            }
            _ = Task.Run(() =>
            {
                try { layers.EnsureLoaded(); }
                catch (Exception ex) { Logger.Log.Warn($"Translation fixes for {folder}/{lang} not loaded: {ex.Message}"); }
            });
            return layers;
        }

        /// <summary><c>%APPDATA%\Kaption</c>, where the send queue lives too.</summary>
        public static string DataRoot => TestRoot ?? GameDataPaths.Root;

        /// <summary>Tests point fixes and the queue at a temporary folder; set before anything uses the registry.</summary>
        internal static string TestRoot { get; set; }

        /// <summary>Wire id: the registered profile's GameId in lower case ("ZZZ" and its aliases → "zzz").</summary>
        public static string WireGameOf(string game)
        {
            string id = GameRegionProfile.Get(game)?.GameId;
            if (string.IsNullOrWhiteSpace(id)) id = game ?? "";
            return id.Trim().ToLowerInvariant();
        }

        /// <summary>"PL" → "pl"; anything but letters, digits and '-' is dropped, so it is safe in a file name.</summary>
        public static string LangOf(string language)
        {
            var sb = new StringBuilder();
            foreach (char c in (language ?? "").Trim().ToLowerInvariant())
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-') sb.Append(c);
            return sb.Length == 0 ? "pl" : sb.ToString();
        }
    }
}
