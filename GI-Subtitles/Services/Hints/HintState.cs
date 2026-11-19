using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GI_Subtitles.Services.Hints
{
    /// <summary>How often a teaching tip has shown on this install, and when it last did.</summary>
    public readonly record struct HintTipRecord(int Count, DateTimeOffset? Last);

    /// <summary>
    /// What the hint card remembers across restarts, one small JSON value in Config (<c>Hints.State</c>): the day it
    /// was first kept, how many days the welcome sheet may show and on which it did, which shortcuts the player has
    /// ever used, and each teaching tip's count. A value, never changed in place: every With method gives a new one,
    /// or the same one when nothing changes, so the caller saves only on a real change.
    /// </summary>
    public sealed class HintState
    {
        public const int NewInstallWelcomeDays = 3, ExistingInstallWelcomeDays = 1;
        private const int MaxKeys = 32, MaxTips = 16, MaxTipIdLength = 40, MaxCount = 1000;
        private const string DayFormat = "yyyy-MM-dd";

        /// <summary>The local day this memory began: teaching the fix shortcut waits for a later day.</summary>
        public string Since { get; }
        /// <summary>Days the welcome sheet may show in all: three for a new install, one for a player who already had Kaption.</summary>
        public int WelcomeLimit { get; }
        /// <summary>The local days the welcome sheet showed.</summary>
        public IReadOnlyList<string> WelcomeDays { get; }
        /// <summary>Hotkey ids the player has pressed at least once.</summary>
        public IReadOnlyCollection<int> KeysUsed => _keysUsed;
        public IReadOnlyDictionary<string, HintTipRecord> Tips { get; }

        private readonly SortedSet<int> _keysUsed;

        private HintState(string since, int welcomeLimit, IReadOnlyList<string> welcomeDays, SortedSet<int> keysUsed,
            IReadOnlyDictionary<string, HintTipRecord> tips)
        {
            Since = since;
            WelcomeLimit = welcomeLimit;
            WelcomeDays = welcomeDays;
            _keysUsed = keysUsed;
            Tips = tips;
        }

        /// <summary>
        /// The memory of an install that has none yet. <paramref name="keysAlreadyUsed"/> are shortcuts an older
        /// version left a trace of, so a long-time player is not taught what they already do.
        /// </summary>
        public static HintState Create(string day, bool newInstall, IEnumerable<int> keysAlreadyUsed = null) =>
            new(day, newInstall ? NewInstallWelcomeDays : ExistingInstallWelcomeDays, Array.Empty<string>(),
                new SortedSet<int>(keysAlreadyUsed ?? Enumerable.Empty<int>()), new Dictionary<string, HintTipRecord>());

        public bool KeyUsed(int keyId) => _keysUsed.Contains(keyId);

        public HintTipRecord Tip(string id) => id != null && Tips.TryGetValue(id, out HintTipRecord record) ? record : default;

        public bool WelcomeShownOn(string day)
        {
            foreach (string shown in WelcomeDays)
                if (string.Equals(shown, day, StringComparison.Ordinal)) return true;
            return false;
        }

        public HintState WithWelcomeOn(string day)
        {
            if (WelcomeShownOn(day)) return this;
            var days = new List<string>(WelcomeDays) { day };
            return new HintState(Since, WelcomeLimit, days, _keysUsed, Tips);
        }

        public HintState WithKeyUsed(int keyId)
        {
            if (keyId <= 0 || _keysUsed.Contains(keyId) || _keysUsed.Count >= MaxKeys) return this;
            return new HintState(Since, WelcomeLimit, WelcomeDays, new SortedSet<int>(_keysUsed) { keyId }, Tips);
        }

        public HintState WithTipShown(string id, DateTimeOffset at)
        {
            if (string.IsNullOrEmpty(id)) return this;
            var tips = new Dictionary<string, HintTipRecord>(Tips) { [id] = new HintTipRecord(Tip(id).Count + 1, at) };
            return new HintState(Since, WelcomeLimit, WelcomeDays, _keysUsed, tips);
        }

        public JsonObject ToJson()
        {
            var tips = new JsonObject();
            foreach (var (id, record) in Tips.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                var tip = new JsonObject { ["count"] = record.Count };
                if (record.Last is DateTimeOffset last) tip["last"] = last.ToString("o", CultureInfo.InvariantCulture);
                tips[id] = tip;
            }
            return new JsonObject
            {
                ["since"] = Since,
                ["welcomeLimit"] = WelcomeLimit,
                ["welcomeDays"] = new JsonArray(WelcomeDays.Select(d => (JsonNode)d).ToArray()),
                ["keysUsed"] = new JsonArray(_keysUsed.Select(k => (JsonNode)k).ToArray()),
                ["tips"] = tips,
            };
        }

        /// <summary>
        /// Reads what <see cref="ToJson"/> wrote, forgivingly: a field of the wrong kind is skipped on its own, and a
        /// value that is not an object or has no birth day is no memory at all (null), so the caller starts a new one.
        /// Never throws and never logs: a hand-edited Config.json is not an error.
        /// </summary>
        public static HintState FromJson(JsonNode node)
        {
            if (node is not JsonObject) return null;
            try
            {
                JsonElement root = JsonSerializer.SerializeToElement(node);
                if (!root.TryGetProperty("since", out JsonElement since) || !IsDay(since)) return null;

                int limit = root.TryGetProperty("welcomeLimit", out JsonElement l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out int n)
                    ? Math.Clamp(n, 0, NewInstallWelcomeDays)
                    : ExistingInstallWelcomeDays;

                var days = new List<string>();
                if (root.TryGetProperty("welcomeDays", out JsonElement d) && d.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement day in d.EnumerateArray())
                        if (days.Count < NewInstallWelcomeDays && IsDay(day) && !days.Contains(day.GetString())) days.Add(day.GetString());

                var keys = new SortedSet<int>();
                if (root.TryGetProperty("keysUsed", out JsonElement k) && k.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement key in k.EnumerateArray())
                        if (keys.Count < MaxKeys && key.ValueKind == JsonValueKind.Number && key.TryGetInt32(out int id) && id > 0) keys.Add(id);

                var tips = new Dictionary<string, HintTipRecord>();
                if (root.TryGetProperty("tips", out JsonElement t) && t.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty tip in t.EnumerateObject())
                    {
                        if (tips.Count >= MaxTips || tip.Name.Length is 0 or > MaxTipIdLength || tip.Value.ValueKind != JsonValueKind.Object) continue;
                        if (!tip.Value.TryGetProperty("count", out JsonElement c) || c.ValueKind != JsonValueKind.Number || !c.TryGetInt32(out int count)) continue;
                        DateTimeOffset? last = tip.Value.TryGetProperty("last", out JsonElement at) && at.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset when)
                                ? when
                                : null;
                        tips[tip.Name] = new HintTipRecord(Math.Clamp(count, 0, MaxCount), last);
                    }

                return new HintState(since.GetString(), limit, days, keys, tips);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                return null;
            }
        }

        private static bool IsDay(JsonElement value) =>
            value.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(value.GetString(), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }
}
