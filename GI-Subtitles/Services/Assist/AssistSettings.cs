using System;
using System.Linq;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Stamina;

namespace GI_Subtitles.Services.Assist
{
    /// <summary>
    /// The in-game helper's Config keys (docs/CONFIG.md, "In-game helper"). Every reader has its own switch and is
    /// off until the player turns it on. The stamina reader keeps the key it shipped with (<c>Stamina.Enabled</c>),
    /// so a switch flipped before this page existed stays flipped.
    /// </summary>
    public static class AssistSettings
    {
        public const string StaminaReader = "stamina";
        public const string ReaderKeyPrefix = "Assist.Reader.";
        public const string SaveSamplesKey = "Assist.SaveSamples";
        public const string SampleLimitKey = "Assist.SampleLimit";
        public const string SampleMegabytesKey = "Assist.SampleMegabytes";

        public const int DefaultSampleLimit = 200, MinSampleLimit = 10, MaxSampleLimit = 5000;
        public const int DefaultSampleMegabytes = 20, MinSampleMegabytes = 1, MaxSampleMegabytes = 500;

        /// <summary>Raised after a reader's switch or the samples switch changes, from the thread that changed it.</summary>
        public static event Action Changed;

        static AssistSettings()
        {
            StaminaSettings.Changed += () => Changed?.Invoke();
        }

        /// <summary>The player's consent to this reader. Off until they switch it on.</summary>
        public static bool IsOn(string readerId)
        {
            if (string.IsNullOrEmpty(readerId)) return false;
            return readerId == StaminaReader ? StaminaSettings.Enabled : Config.Get(ReaderKeyPrefix + readerId, false);
        }

        public static void SetOn(string readerId, bool on)
        {
            if (string.IsNullOrEmpty(readerId) || IsOn(readerId) == on) return;
            if (readerId == StaminaReader)
            {
                StaminaSettings.Enabled = on; // raises Changed through the forward above
                return;
            }
            Config.Set(ReaderKeyPrefix + readerId, on);
            Changed?.Invoke();
        }

        /// <summary>Whether any reader of any listed game is on: off means the capture tick pays one comparison.</summary>
        public static bool AnyOn(GameRegistry games) =>
            games != null && games.Listed.Any(g => g.Desktop?.Assist?.Readers != null && g.Desktop.Assist.Readers.Keys.Any(IsOn));

        /// <summary>
        /// Diagnostics: keep the small picture of every read, every refusal and every near miss of a screen anchor
        /// (<see cref="AssistSampleSaver"/>). Never a whole frame.
        /// </summary>
        /// <remarks>
        /// <c>Stamina.SaveSamples</c>, the key the stamina reader shipped with, counts as on too, and switching this
        /// off clears it: the checkbox always shows what is really happening.
        /// </remarks>
        public static bool SaveSamples
        {
            get => Config.Get(SaveSamplesKey, false) || Config.Get(StaminaSettings.SaveSamplesKey, false);
            set
            {
                if (SaveSamples == value) return;
                // One key from here on: the old one is taken out either way.
                Config.SetMany(new System.Collections.Generic.KeyValuePair<string, object>[]
                {
                    new(SaveSamplesKey, value), new(StaminaSettings.SaveSamplesKey, null),
                });
                Changed?.Invoke();
            }
        }

        /// <summary>The most sample pictures kept per folder; the oldest go first.</summary>
        public static int SampleLimit => Math.Clamp(Config.Get(SampleLimitKey, DefaultSampleLimit), MinSampleLimit, MaxSampleLimit);

        /// <summary>The most a sample folder may hold, in bytes.</summary>
        public static long SampleBytes =>
            Math.Clamp(Config.Get(SampleMegabytesKey, DefaultSampleMegabytes), MinSampleMegabytes, MaxSampleMegabytes) * 1024L * 1024L;
    }
}
