// ─────────────────────────────────────────────────────────────────────────────
//  GameDataPaths.cs
//  ---------------------------------------------------------------------------
//  Single source of truth for every file path under %APPDATA%\Kaption\.
//  Before this existed, each subsystem (DictionarySync, DialogGraphDownloader,
//  GameDataUpdateService, SettingsWindow, DictionaryInventoryService) built
//  its own paths with its own conventions — the v2.0 regression where
//  DictionarySync wrote to `paid-dicts\<game>\` but VoiceContentHelper read
//  from `<Game>\` was a direct consequence of that sprawl.
//
//  Canonical layout (v2.0.0+, no backwards-compat needed):
//
//    %APPDATA%\Kaption\
//    ├── <Game>\                      (e.g. "Genshin", "StarRail")
//    │   ├── TextMapEN.json           public, GitHub → GameDataUpdateService
//    │   ├── TextMapEN.meta.json      ETag sidecar
//    │   ├── TextMapPL.gisub          proprietary, R2 → DictionarySyncService
//    │   ├── TextMapDE.json           public mirrored lang, GitHub
//    │   ├── DialogGraph.gisub        public derived, DialogGraphDownloader
//    │   ├── NpcNames.gisub
//    │   ├── QuestInfo.gisub
//    │   ├── HashToDialogs.gisub
//    │   ├── TalkIndex.gisub
//    │   ├── TextMapEN_TextMapPL.v2.gisub    merged matcher source
//    │   └── TextMapEN_TextMapPL.v2.gsmx.gisub  serialized matcher index
//    ├── manifest.json                DictionarySync state tracker
//    ├── Config.json
//    └── crash.log
//
//  Guidance for callers:
//    - Always go through GameDataPaths — never hard-code "Kaption" or
//      "paid-dicts" anywhere else.
//    - Game names passed in come from the UI ("Genshin"), not slugs. We
//      preserve case on disk; callers compare case-insensitively.
//    - Languages are uppercase two-letter codes ("EN", "PL").
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.IO;

namespace GI_Subtitles.Services.Data
{
    /// <summary>
    /// Centralised path resolution for everything Kaption writes under %APPDATA%.
    /// Pure functions — no IO. Safe to call from any thread.
    /// </summary>
    public static class GameDataPaths
    {
        /// <summary><c>%APPDATA%\Kaption</c>. Created on-demand by callers that write.</summary>
        public static string Root { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kaption");

        /// <summary><c>%APPDATA%\Kaption\manifest.json</c> — DictionarySync state.</summary>
        public static string ManifestFile => Path.Combine(Root, "manifest.json");

        /// <summary><c>%APPDATA%\Kaption\crash.log</c> — unbuffered fatal log.</summary>
        public static string CrashLogFile => Path.Combine(Root, "crash.log");

        /// <summary>Per-game directory (e.g. <c>%APPDATA%\Kaption\Genshin</c>).</summary>
        public static string GameDir(string game)
        {
            if (string.IsNullOrWhiteSpace(game))
                throw new ArgumentException("Game name required.", nameof(game));
            return Path.Combine(Root, Sanitise(game));
        }

        /// <summary>
        /// Plaintext public TextMap (GitHub source). Used for the input-language
        /// dictionary and for mirrored output languages (DE/ES/FR/etc.).
        /// </summary>
        public static string TextMapJson(string game, string language) =>
            Path.Combine(GameDir(game), $"TextMap{Sanitise(language)}.json");

        /// <summary>
        /// Encrypted proprietary TextMap (R2 source via DictionarySyncService).
        /// Machine-bound — copying the file to another machine produces garbage.
        /// Currently used for PL (the only unmirrored language).
        /// </summary>
        public static string TextMapGisub(string game, string language) =>
            Path.Combine(GameDir(game), $"TextMap{Sanitise(language)}.gisub");

        /// <summary>ETag / Last-Modified sidecar for conditional refetches.</summary>
        public static string TextMapMetaJson(string game, string language) =>
            Path.Combine(GameDir(game), $"TextMap{Sanitise(language)}.meta.json");

        /// <summary>
        /// Returns the TextMap path for the given language, preferring encrypted
        /// <c>.gisub</c> when present, otherwise falling back to plaintext
        /// <c>.json</c>. Returns null if neither exists.
        /// </summary>
        public static string ResolveTextMap(string game, string language)
        {
            var gisub = TextMapGisub(game, language);
            if (File.Exists(gisub)) return gisub;
            var json = TextMapJson(game, language);
            if (File.Exists(json)) return json;
            return null;
        }

        /// <summary>
        /// True if a TextMap for <paramref name="language"/> exists under
        /// <paramref name="game"/>, encrypted or plaintext.
        /// </summary>
        public static bool HasAnyTextMap(string game, string language)
            => ResolveTextMap(game, language) != null;

        /// <summary>
        /// Scratch directory under the game folder for temporary downloads
        /// (DialogGraph excel JSONs, partial downloads, etc.). Safe to delete
        /// between sessions — nothing here is load-bearing after build completes.
        /// </summary>
        public static string ScratchDir(string game) =>
            Path.Combine(GameDir(game), "cache");

        /// <summary>
        /// Path to the DialogGraph prediction index (unencrypted, logical
        /// path). FileProtectionHelper resolves either this or its
        /// <c>.gisub</c> sibling transparently.
        /// </summary>
        public static string DialogGraphJson(string game) =>
            Path.Combine(GameDir(game), "DialogGraph.json");

        public static string NpcNamesJson(string game) =>
            Path.Combine(GameDir(game), "NpcNames.json");

        public static string QuestInfoJson(string game) =>
            Path.Combine(GameDir(game), "QuestInfo.json");

        public static string HashToDialogsJson(string game) =>
            Path.Combine(GameDir(game), "HashToDialogs.json");

        public static string TalkIndexJson(string game) =>
            Path.Combine(GameDir(game), "TalkIndex.json");

        /// <summary>
        /// Path to the BundleMeta sidecar emitted by GamedataSyncService when
        /// it installs a v2+ bundle. Carries bundle_version and extension.game
        /// so DialogueContextBase can refuse to load a Genshin bundle into an
        /// HSR engine (and vice versa). Missing on v1 installs — tolerated.
        /// </summary>
        public static string BundleMetaJson(string game) =>
            Path.Combine(GameDir(game), "BundleMeta.json");

        /// <summary>Ensure the per-game directory exists; no-op if already present.</summary>
        public static void EnsureGameDir(string game) =>
            Directory.CreateDirectory(GameDir(game));

        /// <summary>Ensure the Kaption root exists; used by callers that write root-level files.</summary>
        public static void EnsureRoot() =>
            Directory.CreateDirectory(Root);

        // ─────────────────────────────────────────────────────────────────
        //  Sanitisation — game / language tokens flow in from UI, config, and
        //  server responses. Strip path separators to stop a bad payload from
        //  writing outside %APPDATA%\Kaption.
        // ─────────────────────────────────────────────────────────────────
        private static string Sanitise(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment)) return "_";
            var chars = segment.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar
                    || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                {
                    chars[i] = '_';
                }
            }
            return new string(chars);
        }
    }
}
