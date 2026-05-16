using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
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
        private static readonly Dictionary<string, JToken> _settings = new Dictionary<string, JToken>();
        private static readonly object _settingsLock = new object();

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
                var jo = JObject.Parse(json);
                if (jo.Count > 0)
                {
                    lock (_settingsLock)
                    {
                        foreach (var prop in jo.Properties())
                        {
                            _settings[prop.Name] = prop.Value;
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
            JObject jo;
            lock (_settingsLock)
            {
                jo = new JObject();
                foreach (var kv in _settings)
                {
                    jo[kv.Key] = kv.Value;
                }
            }
            File.WriteAllText(SettingsFile, jo.ToString(Formatting.Indented));
        }

        public static T Get<T>(string key, T defaultValue = default)
        {
            lock (_settingsLock)
            {
                if (_settings.TryGetValue(key, out var token))
                {
                    try { return token.ToObject<T>(); }
                    catch (Exception ex) { Logger.Log.Error($"Config.Get<{typeof(T).Name}>(\"{key}\") failed: {ex.Message}"); }
                }
            }
            return defaultValue;
        }

        public static void Set<T>(string key, T value)
        {
            lock (_settingsLock)
            {
                _settings[key] = JToken.FromObject(value);
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
                if (_settings.TryGetValue("Pad", out var token))
                {
                    try
                    {
                        if (token.Type == JTokenType.Array)
                        {
                            var padArray = token.ToObject<int[]>();
                            if (padArray != null && padArray.Length > 0)
                            {
                                return padArray[0];
                            }
                        }
                        else
                        {
                            return token.ToObject<int>();
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
                if (_settings.TryGetValue("Pad", out var token))
                {
                    try
                    {
                        if (token.Type == JTokenType.Array)
                        {
                            var padArray = token.ToObject<int[]>();
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
