using System;
using System.Collections.Generic;
using GI_Subtitles.Core.Settings;

namespace GI_Test
{
    /// <summary>
    /// <see cref="ISettingsStore"/> in memory, for view model tests: what was written, in order, with the exact
    /// type, so a test can pin the shape Config.json gets (a rolled-back build reads the same keys).
    /// </summary>
    internal sealed class MemorySettingsStore : ISettingsStore
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);
        public List<(string Key, object Value)> Writes { get; } = new();

        public MemorySettingsStore With(string key, object value)
        {
            Values[key] = value;
            return this;
        }

        public T Get<T>(string key, T fallback)
        {
            if (!Values.TryGetValue(key, out object value) || value == null) return fallback;
            // Config.Get logs and falls back on a value of the wrong type; a test that wants a throw stores an exception.
            if (value is Exception ex) throw ex;
            return value is T t ? t : fallback;
        }

        public void Set<T>(string key, T value)
        {
            Values[key] = value;
            Writes.Add((key, value));
        }
    }
}
