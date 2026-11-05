using System;
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
