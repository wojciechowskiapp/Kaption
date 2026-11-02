using System;

namespace GI_Subtitles.Services.QuickTranslate
{
    /// <summary>How long a quick-translate popup stays up. Null means "until the player closes it".</summary>
    public static class ReadingTime
    {
        public const double BaseSeconds = 2;
        public const double CharsPerSecond = 15;
        public const double MinAutoSeconds = 4;
        public const double MaxAutoSeconds = 90;

        public static TimeSpan? Compute(int chars, QuickTranslateSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            switch (settings.CloseMode)
            {
                case QuickTranslateCloseMode.Manual:
                    return null;
                case QuickTranslateCloseMode.Timed:
                    return TimeSpan.FromSeconds(settings.Seconds);
                default:
                    double s = BaseSeconds + Math.Max(0, chars) / CharsPerSecond;
                    return TimeSpan.FromSeconds(Math.Clamp(s, MinAutoSeconds, MaxAutoSeconds));
            }
        }
    }
}
