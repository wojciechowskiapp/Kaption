using System;
using System.Linq;
using System.Text.Json.Nodes;
using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Services.Guess
{
    /// <summary>
    /// The ranking notice's Config keys (docs/CONFIG.md, "Guess game notices"). One static class, like FarmCardSettings,
    /// so the Help toggle, the Start strip and the poller read and write the same values.
    /// </summary>
    public static class GuessNotifySettings
    {
        public const string EnabledKey = "GuessNotify.Enabled";
        public const string LastShownDayKey = "GuessNotify.LastShownDay";
        public const string BaselineKey = "GuessNotify.Baseline";
        /// <summary>Debug builds only: a fixture URL in place of the standing endpoint. Never ApiUrl.</summary>
        public const string StandingUrlKey = "GuessNotify.StandingUrl";

        // The read cadence (GuessPollSchedule): unset keys follow the server's `poll`, then the defaults.
        public const string FirstDelaySecondsKey = "GuessNotify.FirstDelaySeconds";
        public const string PollMinutesKey = "GuessNotify.PollMinutes";
        public const string IdlePollMinutesKey = "GuessNotify.IdlePollMinutes";
        public const string NoSessionRecheckMinutesKey = "GuessNotify.NoSessionRecheckMinutes";
        public const string RetryMinutesKey = "GuessNotify.RetryMinutes";
        public const string GateRetrySecondsKey = "GuessNotify.GateRetrySeconds";

        /// <summary>The cadence values pinned in Config; a key that is missing or not a number is no pin.</summary>
        // Read as raw JSON, so a hand-edited value that is not a number is no pin rather than an Error on every read.
        public static GuessPollPins Pins => PinsFrom(key => Config.Has(key) ? Config.Get<JsonNode>(key, null) : null);

        /// <summary>The schedule after reading <paramref name="standing"/> (null before the first read): pins, then its <c>poll</c>.</summary>
        public static GuessPollSchedule Schedule(GuessStanding standing) =>
            GuessPollSchedule.Resolve(Pins, standing?.PollEvery, standing?.PollIdle);

        /// <summary>The pins from raw values (<paramref name="read"/>: key → JSON or null); anything not a number is no pin.</summary>
        public static GuessPollPins PinsFrom(Func<string, JsonNode> read)
        {
            int? Pin(string key) => Number(read(key));
            int[] steps = read(RetryMinutesKey) is JsonArray a ? a.Select(Number).Where(n => n.HasValue).Select(n => n.Value).ToArray() : null;
            return new GuessPollPins(Pin(FirstDelaySecondsKey), Pin(PollMinutesKey), Pin(IdlePollMinutesKey), Pin(NoSessionRecheckMinutesKey),
                steps, Pin(GateRetrySecondsKey));
        }

        private static int? Number(JsonNode n)
        {
            if (n is not JsonValue v) return null;
            if (v.TryGetValue(out int i)) return i;
            // Clamped before the cast: a cast of a double past int's range does not saturate.
            if (v.TryGetValue(out double d) && double.IsFinite(d)) return (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue);
            return null;
        }

        /// <summary>Raised after <see cref="Enabled"/> changes, so every switch and the poller follow.</summary>
        public static event Action EnabledChanged;

        public static bool Enabled
        {
            get => Config.Get(EnabledKey, true);
            set
            {
                if (Config.Get(EnabledKey, true) == value && Config.Has(EnabledKey)) return;
                Config.Set(EnabledKey, value);
                EnabledChanged?.Invoke();
            }
        }

        /// <summary>Local day (yyyy-MM-dd) of the last notice delivered, by balloon or by the Start strip.</summary>
        public static string LastShownDay
        {
            get => Config.Get<string>(LastShownDayKey, null);
            set => Config.Set(LastShownDayKey, value);
        }

        public static GuessBaselines Baselines
        {
            get => GuessBaselines.Parse(Config.Get<string>(BaselineKey, null));
            set => Config.Set(BaselineKey, (value ?? GuessBaselines.Empty).ToJson());
        }

        /// <summary>The fixture URL in a Debug build, else null. Release builds ignore the key.</summary>
        public static string StandingUrlOverride
        {
            get
            {
#if DEBUG
                string url = Config.Get<string>(StandingUrlKey, null)?.Trim();
                return IsAllowedFixtureUrl(url) ? url : null;
#else
                return null;
#endif
            }
        }

        /// <summary>A fixture may be local http or any https address; nothing else is fetched.</summary>
        internal static bool IsAllowedFixtureUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri u)) return false;
            if (u.Scheme == Uri.UriSchemeHttps) return true;
            return u.Scheme == Uri.UriSchemeHttp && u.IsLoopback;
        }

        /// <summary>Today as the daily cap counts it: the local calendar day.</summary>
        public static string Today(DateTime localNow) => localNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }
}
