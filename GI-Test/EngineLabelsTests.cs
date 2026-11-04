using System.Globalization;
using System.Text.RegularExpressions;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>ux-06: the status bar's chips say what a player needs (GPU or CPU, subtitles ready or loading), nothing more.</summary>
    [TestClass]
    public class EngineLabelsTests
    {
        private static string Fallback(string key, string fallback) => fallback;

        [TestMethod]
        public void ReaderChip_IsTheDeviceOnly()
        {
            Assert.AreEqual("GPU", EngineLabels.Chip(true, Fallback));
            Assert.AreEqual("CPU", EngineLabels.Chip(false, Fallback));
        }

        [TestMethod]
        public void SubtitlesChip_SaysReadyLoadingComingOrMissing_NeverACount()
        {
            Assert.AreEqual("Subtitles ready", EngineLabels.SubtitlesChip(loaded: true, packMissing: false, packComing: false, Fallback));
            Assert.AreEqual("Loading subtitles", EngineLabels.SubtitlesChip(loaded: false, packMissing: false, packComing: false, Fallback));
            Assert.AreEqual("Subtitles on the way", EngineLabels.SubtitlesChip(loaded: false, packMissing: true, packComing: true, Fallback));
            Assert.AreEqual("Translation data missing", EngineLabels.SubtitlesChip(loaded: false, packMissing: true, packComing: false, Fallback));
            Assert.AreEqual("Subtitles ready", EngineLabels.SubtitlesChip(loaded: true, packMissing: true, packComing: true, Fallback), "loaded wins");

            foreach (bool loaded in new[] { true, false })
            foreach (bool missing in new[] { true, false })
            foreach (bool coming in new[] { true, false })
            foreach (bool gpu in new[] { true, false })
            {
                string chips = EngineLabels.SubtitlesChip(loaded, missing, coming, Fallback) + " " + EngineLabels.Chip(gpu, Fallback);
                Assert.IsFalse(Regex.IsMatch(chips, @"\bOCR\b|\d"), chips);
            }
        }

        [TestMethod]
        public void SubtitlesChip_ReadsTheNewKeys()
        {
            string L(string key, string fallback) => key switch
            {
                "shell_Chip_Subtitles" => "Napisy gotowe",
                "shell_Chip_SubtitlesLoading" => "Wczytuję napisy",
                _ => fallback,
            };
            Assert.AreEqual("Napisy gotowe", EngineLabels.SubtitlesChip(true, false, false, L));
            Assert.AreEqual("Wczytuję napisy", EngineLabels.SubtitlesChip(false, false, false, L));
        }

        [TestMethod]
        public void SubtitlesTip_NamesTheGame_AndKeepsTheCountsForAdvancedMode()
        {
            var culture = CultureInfo.GetCultureInfo("en-US");
            Assert.AreEqual("Honkai: Star Rail · Polski", EngineLabels.SubtitlesTip("Honkai: Star Rail", "Polski", advanced: false, 536_737, 540_002, culture, Fallback));
            string advanced = EngineLabels.SubtitlesTip("Genshin Impact", "Polski", advanced: true, 536_737, 540_002, culture, Fallback);
            StringAssert.Contains(advanced, "536,737 lines");
            StringAssert.Contains(advanced, "540,002 index entries");

            // The counts line is localised like the rest of the tooltip.
            string L(string key, string fallback) => key == "shell_Chip_Subtitles_AdvancedTip_Format" ? "{0:N0} kwestii · {1:N0} wpisów w indeksie" : fallback;
            string polish = EngineLabels.SubtitlesTip("Genshin Impact", "Polski", advanced: true, 536_737, 540_002, CultureInfo.GetCultureInfo("pl-PL"), L);
            StringAssert.Contains(polish, "kwestii");
            Assert.IsFalse(polish.Contains("lines"), polish);
        }

        [TestMethod]
        public void ProductName_DropsTheDriverDetails()
        {
            Assert.AreEqual("NVIDIA GeForce RTX 5070 Ti",
                EngineLabels.ProductName("NVIDIA GeForce RTX 5070 Ti (vendor 0x10DE, device 0x2C05, 15997 MB dedicated)"));
            Assert.IsNull(EngineLabels.ProductName("  "));
        }
    }
}
