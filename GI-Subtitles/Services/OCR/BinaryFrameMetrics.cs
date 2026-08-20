using System;

namespace GI_Subtitles.Services.OCR
{
    public static class BinaryFrameMetrics
    {
        private const int MinimumReferencePixels = 8192;
        private const int ForegroundScale = 5;

        /// <summary>
        /// Normalises a binary-frame difference to the amount of visible text,
        /// not to the full crop. Large 4K/ultrawide padding therefore cannot
        /// dilute the same changed glyphs below the trigger threshold. The 5x
        /// scale and absolute floor keep tiny antialias/noise changes stable.
        /// </summary>
        public static double NormalizedChangeRatio(
            int changedPixels,
            int currentForegroundPixels,
            int previousForegroundPixels,
            int totalPixels)
        {
            if (changedPixels <= 0 || totalPixels <= 0)
                return 0;

            long foreground = Math.Max(currentForegroundPixels, previousForegroundPixels);
            long reference = Math.Max(
                Math.Min(totalPixels, MinimumReferencePixels),
                foreground * ForegroundScale);
            reference = Math.Min(totalPixels, Math.Max(1, reference));
            return changedPixels / (double)reference;
        }
    }
}
