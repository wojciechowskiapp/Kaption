using System;
using System.Globalization;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// The status bar in words players use: the reader chip says "GPU" or "CPU" (its tooltip names the graphics card by
    /// its product name) and the subtitles chip says whether the subtitles are ready. The model name, the line count and
    /// the driver details belong in the log, the Advanced-mode tooltip and Pomoc › Zaawansowane, never in a chip.
    /// </summary>
    public static class EngineLabels
    {
        /// <summary>"NVIDIA GeForce RTX 5070 Ti (vendor 0x10DE, device 0x2C05, 15997 MB dedicated)" → "NVIDIA GeForce RTX 5070 Ti".</summary>
        public static string ProductName(string adapterDescription)
        {
            if (string.IsNullOrWhiteSpace(adapterDescription)) return null;
            string a = adapterDescription.Trim();
            int details = a.IndexOf(" (vendor", StringComparison.OrdinalIgnoreCase);
            if (details > 0) a = a[..details];
            return a.Trim();
        }

        /// <summary>The reader chip: "GPU" or "CPU". The model name ("PP-OCRv6") means nothing to a player.</summary>
        public static string Chip(bool onGpu, Func<string, string, string> L) =>
            onGpu ? L("shell_Chip_Gpu", "GPU") : L("shell_Chip_Cpu", "CPU");

        /// <summary>
        /// The subtitles chip: ready once the pack is loaded, "on the way" for a beta game without one, missing when
        /// the pack is gone, loading otherwise. No entry count: "Słownik 536 737" told a player nothing.
        /// </summary>
        public static string SubtitlesChip(bool loaded, bool packMissing, bool packComing, Func<string, string, string> L)
        {
            if (loaded) return L("shell_Chip_Subtitles", "Subtitles ready");
            if (packMissing && packComing) return L("shell_Chip_DictComing", "Subtitles on the way");
            if (packMissing) return L("Dash_Status_TranslationDataMissing", "Translation data missing");
            return L("shell_Chip_SubtitlesLoading", "Loading subtitles");
        }

        /// <summary>
        /// The subtitles chip's tooltip: "Genshin Impact · Polski" (the display name, never the raw "StarRail" id). Advanced
        /// mode adds the line and index counts, the only place they are shown besides Pomoc › Zaawansowane.
        /// </summary>
        public static string SubtitlesTip(string gameDisplayName, string language, bool advanced, int lines, int indexEntries, CultureInfo culture,
                                          Func<string, string, string> L)
        {
            string tip = $"{gameDisplayName} · {language}";
            if (!advanced) return tip;
            return tip + "\n" + string.Format(culture, L("shell_Chip_Subtitles_AdvancedTip_Format", "{0:N0} lines · {1:N0} index entries"), lines, indexEntries);
        }
    }
}
