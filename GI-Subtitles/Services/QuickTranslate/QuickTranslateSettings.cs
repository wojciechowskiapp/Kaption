using System;
using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Services.QuickTranslate
{
    public enum QuickTranslateCloseMode
    {
        Auto,
        Timed,
        Manual
    }

    /// <summary>
    /// User options for the Ctrl+Q popup. Read fresh for every popup, so Settings changes
    /// apply without a restart. Defaults live here, never in Config.json.
    /// </summary>
    public sealed class QuickTranslateSettings
    {
        public const string CloseModeKey = "QuickTranslateCloseMode";
        public const string SecondsKey = "QuickTranslateSeconds";
        public const string FontSizeKey = "QuickTranslateFontSize";

        public const int DefaultSeconds = 8;
        public const int MinSeconds = 2;
        public const int MaxSeconds = 120;
        public const int DefaultFontSize = 16;
        public const int MinFontSize = 12;
        public const int MaxFontSize = 28;

        public QuickTranslateCloseMode CloseMode { get; }
        public int Seconds { get; }
        public int FontSize { get; }

        private QuickTranslateSettings(QuickTranslateCloseMode mode, int seconds, int fontSize)
        {
            CloseMode = mode;
            Seconds = seconds;
            FontSize = fontSize;
        }

        public static QuickTranslateSettings FromValues(string mode, int seconds, int fontSize)
        {
            if (!Enum.TryParse(mode ?? "", ignoreCase: true, out QuickTranslateCloseMode parsed) ||
                !Enum.IsDefined(typeof(QuickTranslateCloseMode), parsed))
            {
                parsed = QuickTranslateCloseMode.Auto;
            }
            return new QuickTranslateSettings(
                parsed,
                Math.Clamp(seconds, MinSeconds, MaxSeconds),
                Math.Clamp(fontSize, MinFontSize, MaxFontSize));
        }

        public static QuickTranslateSettings Load()
        {
            try
            {
                return FromValues(
                    Config.Get(CloseModeKey, nameof(QuickTranslateCloseMode.Auto)),
                    Config.Get(SecondsKey, DefaultSeconds),
                    Config.Get(FontSizeKey, DefaultFontSize));
            }
            catch
            {
                // A hand-edited Config.json with the wrong value type must not break Ctrl+Q.
                return FromValues(null, DefaultSeconds, DefaultFontSize);
            }
        }
    }
}
