using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Community;

namespace GI_Subtitles.Core.Config
{
    /// <summary>
    /// Configuration management class
    /// </summary>
    public static class Config
    {
        // KAPTION_CONFIG_DIR is for tests: they point the settings at a temporary folder, never a real profile.
        // The E2E mode's folder wins over it, so an inherited override cannot pull the suite's app elsewhere.
        private static readonly string SettingsFolder =
            GI_Subtitles.Common.E2EMode.IsActive ? Services.Data.GameDataPaths.Root
            : Environment.GetEnvironmentVariable("KAPTION_CONFIG_DIR") is string dir && dir.Length > 0
                ? dir
                : Services.Data.GameDataPaths.Root;
        private static readonly string SettingsFile = Path.Combine(SettingsFolder, "Config.json");

        /// <summary>The settings file this process reads and writes (tests check what lands there).</summary>
        internal static string SettingsFilePath => SettingsFile;
        private static readonly Dictionary<string, JsonNode> _settings = new Dictionary<string, JsonNode>();
        private static readonly object _settingsLock = new object();
        private static readonly object _saveLock = new object();

        private static readonly JsonDocumentOptions ParseOptions = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>
        /// Config.json is hand-editable, so a numeric setting may legitimately
        /// arrive quoted; <see cref="JsonNumberHandling.AllowReadingFromString"/>
        /// keeps those files loading.
        /// </summary>
        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions(JsonDefaults.Options)
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        static Config()
        {
            Load("Config.json");
            Load(SettingsFile);
        }

        private static void Load(string file)
        {
            if (!Directory.Exists(SettingsFolder))
                Directory.CreateDirectory(SettingsFolder);

            bool isSettingsFile = string.Equals(Path.GetFullPath(file), SettingsFile, StringComparison.OrdinalIgnoreCase);
            switch (ReadSettings(file, isSettingsFile, _settings, _settingsLock))
            {
                case LoadOutcome.Missing:
                case LoadOutcome.Empty:
                case LoadOutcome.SetAside:
                case LoadOutcome.Unrestored:
                    if (isSettingsFile) Save();
                    break;
            }
        }

        /// <summary>What <see cref="ReadSettings"/> did with a settings file.</summary>
        internal enum LoadOutcome { Loaded, Missing, Empty, SetAside, Broken, Unrestored }

        /// <summary>
        /// Reads <paramref name="file"/> into <paramref name="into"/>. For the settings file, never destroys the last
        /// copy of the player's settings: a save that did not finish is restored first (one that cannot be read is
        /// <see cref="LoadOutcome.Unrestored"/>: settings start from defaults and that file is kept, see
        /// <see cref="AtomicFile.RestoreInterruptedSave"/>), and a file that does not parse is set aside as
        /// <c>Config.json.bad</c> before anything writes a new one.
        /// </summary>
        internal static LoadOutcome ReadSettings(string file, bool isSettingsFile, Dictionary<string, JsonNode> into, object gate)
        {
            // A save whose swap failed leaves its new copy in a temp file and no Config.json: take that copy back.
            if (isSettingsFile && !File.Exists(file)
                && AtomicFile.RestoreInterruptedSave(file) == AtomicFile.RestoreResult.Unreadable)
                return LoadOutcome.Unrestored;
            if (!File.Exists(file))
                return LoadOutcome.Missing;

            string json = null;
            try
            {
                json = File.ReadAllText(file);
                if (!(JsonNode.Parse(json, documentOptions: ParseOptions) is JsonObject jo))
                    throw new JsonException($"Config file is not a JSON object: {file}");
                if (jo.Count == 0)
                    return LoadOutcome.Empty;
                lock (gate)
                {
                    foreach (var prop in jo)
                    {
                        into[prop.Key] = prop.Value?.DeepClone();
                    }
                }
                return LoadOutcome.Loaded;
            }
            catch (Exception ex)
            {
                Logger.Log.Error(ex);
                // A partial file still holds most settings: keep it for the player (or support) instead of letting the
                // next save write over it. An empty one holds nothing worth keeping.
                if (isSettingsFile && !string.IsNullOrWhiteSpace(json))
                {
                    if (AtomicFile.SetAside(file))
                    {
                        Logger.Log.Warn($"{Path.GetFileName(file)} did not read and was kept as {Path.GetFileName(file)}.bad; settings start from defaults.");
                        return LoadOutcome.SetAside;
                    }
                    // Held so it cannot be moved: keep what was read instead. Nothing is saved over it at load (the swap
                    // would fail while it is held); a later save may replace it, the copy stays.
                    if (AtomicFile.KeepCopyAside(file, json) is string copy)
                        Logger.Log.Warn($"{Path.GetFileName(file)} did not read and a copy was kept as {Path.GetFileName(copy)}; settings start from defaults.");
                }
                return LoadOutcome.Broken;
            }
        }

        public static void Save()
        {
            // One save at a time, its snapshot taken inside: the file always ends with the newest settings, and two
            // threads never fight over the file (in place, the second one threw "being used by another process").
            lock (_saveLock)
            {
                JsonObject jo;
                lock (_settingsLock)
                {
                    jo = new JsonObject();
                    foreach (var kv in _settings)
                    {
                        jo[kv.Key] = kv.Value?.DeepClone();
                    }
                }
                // Never in place: a save cut by a crash, the updater's forced stop or a power loss leaves the old file or
                // the new one, whole (a temp file flushed to disk, then swapped in).
                AtomicFile.WriteAllText(SettingsFile, jo.ToJsonString(JsonDefaults.Indented));
            }
        }

        public static T Get<T>(string key, T defaultValue = default)
        {
            lock (_settingsLock)
            {
                if (_settings.TryGetValue(key, out var token) && token != null)
                {
                    try { return token.Deserialize<T>(ReadOptions); }
                    catch (Exception ex) { Logger.Log.Error($"Config.Get<{typeof(T).Name}>(\"{key}\") failed: {ex.Message}"); }
                }
            }
            return defaultValue;
        }

        public static void Set<T>(string key, T value)
        {
            lock (_settingsLock)
            {
                _settings[key] = JsonSerializer.SerializeToNode(value, JsonDefaults.Options);
            }
            Save();
        }

        /// <summary>
        /// Sets several keys and writes the file once, so values that belong together (a reading and what is owed
        /// for it) are never on disk one without the other. A null value removes its key.
        /// </summary>
        public static void SetMany(IEnumerable<KeyValuePair<string, object>> values)
        {
            if (values == null) return;
            lock (_settingsLock)
            {
                foreach (var kv in values)
                {
                    if (kv.Value == null) _settings.Remove(kv.Key);
                    else _settings[kv.Key] = JsonSerializer.SerializeToNode(kv.Value, kv.Value.GetType(), JsonDefaults.Options);
                }
            }
            Save();
        }

        /// <summary>
        /// True when <paramref name="key"/> is explicitly present in Config.json.
        /// Use this to distinguish "user has not touched this setting" (fall back
        /// to per-game profile defaults) from "user has explicitly pinned a
        /// value" (honour it regardless of profile recommendation).
        /// </summary>
        public static bool Has(string key)
        {
            lock (_settingsLock)
            {
                return _settings.ContainsKey(key);
            }
        }

        /// <summary>
        /// Remove a key from Config. Used by one-shot migrations that want to
        /// revert an install to the "never touched this setting" state so the
        /// game-profile default can take over. No-op when the key is absent.
        /// </summary>
        public static void Remove(string key)
        {
            bool removed;
            lock (_settingsLock)
            {
                removed = _settings.Remove(key);
            }
            if (removed) Save();
        }

        public static int GetPad(int defaultValue = 0)
        {
            lock (_settingsLock)
            {
                if (_settings.TryGetValue("Pad", out var token) && token != null)
                {
                    try
                    {
                        if (token is JsonArray)
                        {
                            var padArray = token.Deserialize<int[]>(ReadOptions);
                            if (padArray != null && padArray.Length > 0)
                            {
                                return padArray[0];
                            }
                        }
                        else
                        {
                            return token.Deserialize<int>(ReadOptions);
                        }
                    }
                    catch (Exception ex) { Logger.Log.Error($"Config.GetPad failed: {ex.Message}"); }
                }
            }
            return defaultValue;
        }

        public static int GetPadHorizontal(int defaultValue = 0)
        {
            lock (_settingsLock)
            {
                if (_settings.TryGetValue("Pad", out var token) && token != null)
                {
                    try
                    {
                        if (token is JsonArray)
                        {
                            var padArray = token.Deserialize<int[]>(ReadOptions);
                            if (padArray != null && padArray.Length > 1)
                            {
                                return padArray[1];
                            }
                        }
                    }
                    catch (Exception ex) { Logger.Log.Error($"Config.GetPadHorizontal failed: {ex.Message}"); }
                }
            }
            return defaultValue;
        }
    }
}
