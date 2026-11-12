using System;
using System.Collections.Generic;
using System.Linq;
using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Services.Notices.WhatsNew
{
    /// <summary>What "New in Kaption" remembers; Config in the app, fields in tests.</summary>
    public interface IWhatsNewMemory
    {
        bool Enabled { get; set; }
        /// <summary>The newest release whose card this profile was shown (or that needed none).</summary>
        Version SeenThrough { get; set; }
        /// <summary>What the player said about a feature; null when it was never asked or answered.</summary>
        WhatsNewAnswer? AnswerOf(string feature);
        void Answer(string feature, WhatsNewAnswer answer);
        /// <summary>The level the tray was told about on a start without a window.</summary>
        Version TrayTold { get; set; }
    }

    /// <summary>
    /// docs/CONFIG.md, "New in Kaption". An answer is one value written once (the feature and what was said), so a stop
    /// at any moment leaves either the whole answer or none of it.
    /// </summary>
    public sealed class ConfigWhatsNewMemory : IWhatsNewMemory
    {
        public const string EnabledKey = "WhatsNew.Enabled";
        public const string SeenThroughKey = "WhatsNew.SeenThrough";
        public const string AnswersKey = "WhatsNew.Answers";
        public const string TrayToldKey = "WhatsNew.TrayTold";
        /// <summary>Features remembered; far above what the app has, so nothing is ever forgotten in practice.</summary>
        public const int Cap = 500;

        public static ConfigWhatsNewMemory Default { get; } = new();

        /// <summary>Raised after <see cref="Enabled"/> changes, from the thread that changed it.</summary>
        public static event Action EnabledChanged;

        public bool Enabled
        {
            get => Config.Get(EnabledKey, true);
            set
            {
                if (Config.Get(EnabledKey, true) == value && Config.Has(EnabledKey)) return;
                Config.Set(EnabledKey, value);
                EnabledChanged?.Invoke();
            }
        }

        public Version SeenThrough
        {
            get => ParseVersion(Config.Get<string>(SeenThroughKey, null));
            set { if (value != null) Config.Set(SeenThroughKey, value.ToString()); }
        }

        public Version TrayTold
        {
            get => ParseVersion(Config.Get<string>(TrayToldKey, null));
            set { if (value != null) Config.Set(TrayToldKey, value.ToString()); }
        }

        public WhatsNewAnswer? AnswerOf(string feature) => Find(Config.Get<string[]>(AnswersKey, null), feature);

        public void Answer(string feature, WhatsNewAnswer answer)
        {
            if (string.IsNullOrWhiteSpace(feature)) return;
            Config.Set(AnswersKey, With(Config.Get<string[]>(AnswersKey, null), feature, answer));
        }

        /// <summary>"stamina-notice=on": the last answer for a feature wins; anything unreadable is no answer.</summary>
        public static WhatsNewAnswer? Find(IEnumerable<string> answers, string feature)
        {
            if (string.IsNullOrWhiteSpace(feature)) return null;
            WhatsNewAnswer? found = null;
            foreach (string item in answers ?? Array.Empty<string>())
            {
                string[] parts = (item ?? "").Split('=');
                if (parts.Length != 2 || !string.Equals(parts[0], feature, StringComparison.Ordinal)) continue;
                found = parts[1] switch
                {
                    "on" => WhatsNewAnswer.On,
                    "later" => WhatsNewAnswer.Later,
                    "never" => WhatsNewAnswer.Never,
                    _ => found,
                };
            }
            return found;
        }

        /// <summary>The list with the feature's answer replaced, newest last, the oldest dropped past the cap.</summary>
        public static string[] With(IEnumerable<string> answers, string feature, WhatsNewAnswer answer)
        {
            string word = answer switch { WhatsNewAnswer.On => "on", WhatsNewAnswer.Later => "later", _ => "never" };
            var next = (answers ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrEmpty(x) && !x.StartsWith(feature + "=", StringComparison.Ordinal))
                .Append(feature + "=" + word).ToList();
            return (next.Count > Cap ? next.Skip(next.Count - Cap) : next).ToArray();
        }

        public static Version ParseVersion(string text) => Version.TryParse(text?.Trim(), out Version v) ? v : null;
    }
}
