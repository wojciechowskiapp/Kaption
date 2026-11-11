using System.IO;
using System.Text;
using GI_Subtitles.Services.Characters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// A game on site.upcoming "teaser" ships a character not out yet as a record without its kit
    /// (tools/characters/release.cjs): the desktop must load it, and every older section still loads.
    /// </summary>
    [TestClass]
    public class CharacterCatalogTeaserTests
    {
        private const string TeaserJson = @"{
  ""schema"": 1, ""game"": ""starrail"", ""game_version"": ""4.7"",
  ""some_future_key"": { ""anything"": [1, 2, 3] },
  ""stats"": {}, ""sets"": {}, ""weapons"": {},
  ""characters"": [
    { ""id"": ""1001"", ""name"": ""March 7th"", ""rarity"": 4, ""element"": ""Ice"", ""weapon_type"": ""Preservation"",
      ""skills"": [ { ""kind"": ""basic"", ""name"": ""Frigid Cold Arrow"", ""desc"": { ""t"": ""Deals Ice DMG."", ""p"": [] } } ],
      ""build"": null },
    { ""id"": ""1499"", ""slug"": ""soon"", ""name"": ""Soon"", ""icon"": ""SpriteOutput/AvatarIcon/1499.png"", ""rarity"": 5,
      ""element"": ""Quantum"", ""weapon_type"": ""Erudition"", ""release_version"": ""4.8"", ""release_date"": ""2026-12-10"",
      ""release_status"": ""upcoming"", ""teaser"": true }
  ]
}";

        private static Stream S(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

        [TestMethod]
        public void Load_TeaserRecordWithoutKit_LoadsWithEmptyKit()
        {
            var c = CharacterCatalog.Load(S(TeaserJson), "StarRail");

            Assert.AreEqual(2, c.Characters.Count, "the teaser record is not skipped as malformed");
            var soon = c.Find("1499");
            Assert.IsNotNull(soon);
            Assert.AreEqual("Soon", soon.Name);
            Assert.AreEqual("Quantum", soon.Element);
            Assert.AreEqual("Erudition", soon.WeaponType);
            Assert.AreEqual(5, soon.Rarity);
            Assert.AreEqual(0, soon.Skills.Count);
            Assert.IsNull(soon.Build);
            Assert.IsNull(soon.Materials);
            Assert.AreEqual("4.8", soon.ReleaseVersion);
            Assert.AreEqual(1, c.Find("1001").Skills.Count, "the released record keeps its kit");
        }

        [TestMethod]
        public void Overview_OfATeaser_IsNameAndFactsOnly()
        {
            // What the Characters tab and the Ctrl+Q popup draw from a teaser record: no kit to show.
            var c = CharacterCatalog.Load(S(TeaserJson), "StarRail");
            var m = CharacterOverviewBuilder.Build(c.Find("1499"), c, null);

            Assert.AreEqual("Soon", m.EnglishName);
            Assert.AreEqual("Quantum", m.Element);
            Assert.AreEqual(0, m.Skills.Count);
            Assert.AreEqual(0, m.SetGroups.Count);
            Assert.AreEqual(0, m.MainStats.Count);
            Assert.AreEqual(0, m.Weapons.Count);
            Assert.AreEqual(0, m.MaterialsAscension.Count + m.MaterialsSkills.Count);
            Assert.IsFalse(m.HasBuild);
            Assert.AreEqual("", m.Role);
        }

        [TestMethod]
        public void Load_OlderSectionWithoutTeasers_StillLoads()
        {
            var c = CharacterCatalog.Load(S(CharacterCatalogTests.MinimalJson), "StarRail");

            Assert.AreEqual(2, c.Characters.Count);
            Assert.AreEqual(1, c.Find("1001").Skills.Count);
        }
    }
}
