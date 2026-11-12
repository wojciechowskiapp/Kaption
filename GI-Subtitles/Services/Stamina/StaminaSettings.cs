using System;
using System.Globalization;
using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Services.Stamina
{
    /// <summary>
    /// The stamina reader's Config keys (docs/CONFIG.md, "Stamina from the screen"). Everything here stays in
    /// Config.json on this PC: the feature has no server side.
    /// </summary>
    public static class StaminaSettings
    {
        public const string EnabledKey = "Stamina.Enabled";
        public const string NoticeKey = "Stamina.Notice";
        public const string NoticeLeadMinutesKey = "Stamina.NoticeLeadMinutes";
        public const string GateRetrySecondsKey = "Stamina.GateRetrySeconds";
        public const string SaveSamplesKey = "Stamina.SaveSamples";
        public const string SampleLimitKey = "Stamina.SampleLimit";
        private const string ReadingKeyPrefix = "Stamina.Reading.";
        private const string ArmedKeyPrefix = "Stamina.NoticeArmed.";
        private const string SeenKeyPrefix = "Stamina.Seen.";

        public const int DefaultNoticeLeadMinutes = 30, MinNoticeLeadMinutes = 0, MaxNoticeLeadMinutes = 600;
        public const int DefaultGateRetrySeconds = 60, MinGateRetrySeconds = 15, MaxGateRetrySeconds = 3600;
        public const int DefaultSampleLimit = 200, MinSampleLimit = 10, MaxSampleLimit = 5000;

        /// <summary>Raised after <see cref="Enabled"/> or <see cref="Notice"/> changes, from the thread that changed it.</summary>
        public static event Action Changed;

        /// <summary>The player's consent to read the number off the game screen. Off until they switch it on.</summary>
        public static bool Enabled
        {
            get => Config.Get(EnabledKey, false);
            set
            {
                if (Config.Get(EnabledKey, false) == value) return;
                Config.Set(EnabledKey, value);
                Changed?.Invoke();
            }
        }

        /// <summary>Whether a tray notice says when the pool is nearly full.</summary>
        public static bool Notice
        {
            get => Config.Get(NoticeKey, true);
            set
            {
                if (Config.Get(NoticeKey, true) == value && Config.Has(NoticeKey)) return;
                Config.Set(NoticeKey, value);
                Changed?.Invoke();
            }
        }

        /// <summary>How long before the pool fills the notice is due.</summary>
        public static TimeSpan NoticeLead =>
            TimeSpan.FromMinutes(Math.Clamp(Config.Get(NoticeLeadMinutesKey, DefaultNoticeLeadMinutes), MinNoticeLeadMinutes, MaxNoticeLeadMinutes));

        /// <summary>How often a notice that may not show yet (a game in front, subtitles running) is tried again.</summary>
        public static TimeSpan GateRetry =>
            TimeSpan.FromSeconds(Math.Clamp(Config.Get(GateRetrySecondsKey, DefaultGateRetrySeconds), MinGateRetrySeconds, MaxGateRetrySeconds));

        /// <summary>
        /// Testing aid: keep a small picture of every number read (and of misses) in the game's data folder. On with
        /// its own key or with the in-game helper's Diagnostics switch.
        /// </summary>
        public static bool SaveSamples => Assist.AssistSettings.SaveSamples;

        /// <summary>The most sample pictures kept per game; the oldest go first.</summary>
        public static int SampleLimit => Math.Clamp(Config.Get(SampleLimitKey, DefaultSampleLimit), MinSampleLimit, MaxSampleLimit);

        /// <summary>The reading the timer runs from, per game (wire id); null when there is none or it does not parse.</summary>
        public static StaminaReading LoadReading(string wireId) =>
            string.IsNullOrEmpty(wireId) ? null : ParseReading(Config.Get<string>(ReadingKeyPrefix + wireId, null));

        public static void SaveReading(string wireId, StaminaReading reading)
        {
            if (string.IsNullOrEmpty(wireId) || reading == null) return;
            Config.Set(ReadingKeyPrefix + wireId, FormatReading(reading));
        }

        /// <summary>
        /// When the number on screen last agreed with the timer, per game; null when never. Kept apart from the reading
        /// so that seeing a full pool again does not move the moment it filled.
        /// </summary>
        public static DateTimeOffset? LoadSeen(string wireId)
        {
            if (string.IsNullOrEmpty(wireId)) return null;
            long ms = Config.Get(SeenKeyPrefix + wireId, 0L);
            if (ms <= 0) return null;
            try { return DateTimeOffset.FromUnixTimeMilliseconds(ms); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        public static void SaveSeen(string wireId, DateTimeOffset at)
        {
            if (!string.IsNullOrEmpty(wireId)) Config.Set(SeenKeyPrefix + wireId, at.ToUnixTimeMilliseconds());
        }

        /// <summary>Whether a notice is still owed for the current fill (see <see cref="StaminaNotice"/>).</summary>
        public static bool LoadArmed(string wireId) => !string.IsNullOrEmpty(wireId) && Config.Get(ArmedKeyPrefix + wireId, false);

        public static void SaveArmed(string wireId, bool armed)
        {
            if (string.IsNullOrEmpty(wireId) || Config.Get(ArmedKeyPrefix + wireId, false) == armed) return;
            Config.Set(ArmedKeyPrefix + wireId, armed);
        }

        /// <summary>
        /// An accepted reading with what belongs to it, in one write of Config.json: the reading, when it was seen and,
        /// unless <paramref name="armed"/> is null (a one-point correction keeps what is owed), whether a notice is
        /// owed for this fill. Written apart, a crash between the two could leave a new fill with no notice owed.
        /// </summary>
        public static void SaveAccepted(string wireId, StaminaReading reading, bool? armed, DateTimeOffset seen)
        {
            if (string.IsNullOrEmpty(wireId) || reading == null) return;
            var values = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, object>>
            {
                new(ReadingKeyPrefix + wireId, FormatReading(reading)),
                new(SeenKeyPrefix + wireId, seen.ToUnixTimeMilliseconds()),
            };
            if (armed is bool owed) values.Add(new(ArmedKeyPrefix + wireId, owed));
            Config.SetMany(values);
        }

        /// <summary>Forgets a game's reading, when it was seen and the notice owed for it ("delete what was read").</summary>
        public static void Forget(string wireId)
        {
            if (string.IsNullOrEmpty(wireId)) return;
            Config.SetMany(new System.Collections.Generic.KeyValuePair<string, object>[]
            {
                new(ReadingKeyPrefix + wireId, null), new(SeenKeyPrefix + wireId, null), new(ArmedKeyPrefix + wireId, null),
            });
            Changed?.Invoke();
        }

        /// <summary>"value|unix milliseconds|seconds to the next point", invariant culture.</summary>
        public static string FormatReading(StaminaReading r) => string.Create(CultureInfo.InvariantCulture,
            $"{r.Value}|{r.At.ToUnixTimeMilliseconds()}|{r.NextInSeconds:0.###}");

        public static StaminaReading ParseReading(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string[] parts = text.Split('|');
            if (parts.Length != 3) return null;
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value > 99999) return null;
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ms)) return null;
            if (!double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double nextIn)
                || !double.IsFinite(nextIn) || nextIn < 0 || nextIn > 86400) return null;
            try { return new StaminaReading(value, DateTimeOffset.FromUnixTimeMilliseconds(ms), nextIn); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
    }
}
