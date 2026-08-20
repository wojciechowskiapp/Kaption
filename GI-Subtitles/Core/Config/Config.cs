using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GI_Subtitles.Common;

namespace GI_Subtitles.Core.Config
{
    /// <summary>
    /// Configuration management class
    /// </summary>
    public static class Config
    {
        private static readonly string SettingsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kaption");
        private static readonly string SettingsFile = Path.Combine(SettingsFolder, "Config.json");
        private static readonly Dictionary<string, JsonNode> _settings = new Dictionary<string, JsonNode>();
        private static readonly object _settingsLock = new object();

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

            if (!File.Exists(file))
            {
                if (string.Equals(Path.GetFullPath(file), SettingsFile, StringComparison.OrdinalIgnoreCase))
                    Save();
                return;
            }

            try
            {
                var json = File.ReadAllText(file);
                if (!(JsonNode.Parse(json, documentOptions: ParseOptions) is JsonObject jo))
                    throw new JsonException($"Config file is not a JSON object: {file}");

                if (jo.Count > 0)
                {
                    lock (_settingsLock)
                    {
                        foreach (var prop in jo)
                        {
                            _settings[prop.Key] = prop.Value?.DeepClone();
                        }
                    }
                }
                else
                {
                    Save();
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Error(ex);
            }
        }

        public static void Save()
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
            File.WriteAllText(SettingsFile, jo.ToJsonString(JsonDefaults.Indented));
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
