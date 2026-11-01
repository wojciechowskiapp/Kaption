// ─────────────────────────────────────────────────────────────────────────────
//  GamedataPairRequestTests.cs
//  ---------------------------------------------------------------------------
//  GamedataSync asks for the bundle paired with the translation pack that
//  DictionarySync installed (/api/license/gamedata?game=X&pair=<id>), so a
//  release promoted between the two startup syncs can't hand the client a
//  mismatched pair. Without a known pack the request must stay exactly what
//  older builds sent. Design: .plan/in-progress/AUTO-TRANSLATION-UPDATER.md §6.10.
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class GamedataPairRequestTests
    {
        [TestMethod]
        public void Url_WithoutPair_IsUnchanged()
        {
            Assert.AreEqual("/api/license/gamedata?game=starrail",
                KaptionApiClient.BuildGamedataListUrl("starrail", null));
            Assert.AreEqual("/api/license/gamedata?game=starrail",
                KaptionApiClient.BuildGamedataListUrl("starrail", "  "));
        }

        [TestMethod]
        public void Url_WithPair_AppendsEscapedId()
        {
            Assert.AreEqual("/api/license/gamedata?game=zzz&pair=rel_zzz_3.3.0-r1",
                KaptionApiClient.BuildGamedataListUrl("zzz", "rel_zzz_3.3.0-r1"));
            Assert.AreEqual("/api/license/gamedata?game=genshin&pair=a%26b%3Dc",
                KaptionApiClient.BuildGamedataListUrl("genshin", "a&b=c"));
        }

        private static Dictionary<string, DictionarySyncService.ManifestEntry> Manifest() =>
            new Dictionary<string, DictionarySyncService.ManifestEntry>
            {
                ["starrail/pl"] = new DictionarySyncService.ManifestEntry
                {
                    Game = "starrail", Language = "pl", Version = "4.6.0-r2", FileVersionId = "rel_starrail_460_r2",
                },
                ["zzz/pl"] = new DictionarySyncService.ManifestEntry
                {
                    Game = "zzz", Language = "pl", Version = "3.1", FileVersionId = "",
                },
            };

        [TestMethod]
        public void InstalledPack_IsFoundForConfigTagCasing()
        {
            // Config tags are "StarRail"/"ZZZ"; manifest keys come from lower-case server ids.
            Assert.AreEqual("rel_starrail_460_r2",
                DictionarySyncService.FindFileVersionId(Manifest(), "StarRail", "PL"));
        }

        [TestMethod]
        public void NoInstalledPack_MeansNoPair()
        {
            Assert.IsNull(DictionarySyncService.FindFileVersionId(Manifest(), "Genshin", "pl"), "no entry");
            Assert.IsNull(DictionarySyncService.FindFileVersionId(Manifest(), "ZZZ", "pl"), "blank id");
            Assert.IsNull(DictionarySyncService.FindFileVersionId(Manifest(), "StarRail", "it"), "other language");
            Assert.IsNull(DictionarySyncService.FindFileVersionId(null, "StarRail", "pl"));
            Assert.IsNull(DictionarySyncService.FindFileVersionId(Manifest(), "", "pl"));
        }
    }
}
