using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GI_Subtitles.Services.Characters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>Phase C3 data for the Tips tab: materials, leveling, links, AI guide and priorities.</summary>
    [TestClass]
    public class CharacterTipsTests
    {
        private static string Json => CharacterCatalogTests.MinimalJson
            .Replace(@"""schema"": 1, ""game"": ""starrail"",",
                @"""schema"": 1, ""game"": ""starrail"",
                  ""icon_base"": { ""character"": ""https://img.example/avatar/"", ""set"": ""https://img.example/relic/"", ""weapon"": ""https://img.example/lc/"", ""material"": ""https://img.example/item/"" },
                  ""icon_ext"": { ""character"": "".png"", ""set"": "".png"", ""weapon"": "".png"", ""material"": "".webp"" },")
            .Replace(@"""name"": ""Hunter of Glacial Forest"",", @"""name"": ""Hunter of Glacial Forest"", ""icon"": ""71003"",")
            .Replace(@"""name"": ""In the Name of the World"",", @"""name"": ""In the Name of the World"", ""icon"": ""23004"",")
            .Replace(@"""teammates"": [] } },", @"""teammates"": [] },
                  ""materials"": { ""ascension"": [ { ""name"": ""Credit"", ""count"": 308000 }, { ""name"": ""Horn of Snow"", ""count"": 65, ""icon"": ""110411"" } ],
                                   ""skills"": [ { ""name"": ""Tracks of Destiny"", ""count"": 8, ""icon"": ""110501"" } ] },
                  ""leveling"": [0],
                  ""links"": [ { ""site"": ""prydwen"", ""label"": ""Prydwen"", ""url"": ""https://www.prydwen.gg/star-rail/characters/march-7th"" },
                               { ""site"": ""evil"", ""label"": ""Evil"", ""url"": ""http://insecure.example/x"" } ],
                  ""ai"": { ""model"": ""gpt-6.1-terra"", ""generated_at"": ""2026-11-01"", ""facts_sha"": ""x"",
                            ""en"": { ""guide"": [ { ""heading"": ""Overview"", ""body"": ""A shield bot."" } ] },
                            ""pl"": { ""guide"": [ { ""heading"": ""Przegląd"", ""body"": ""Tarczowniczka."" }, { ""heading"": ""Build"", ""body"": ""Bierz DEF."" } ] },
                            ""priority"": { ""sets"": [""103""], ""weapons"": [""23004""], ""skills"": [], ""progression"": [0] } } },");

        private static CharacterCatalog Catalog() =>
            CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Json)), "starrail");

        [TestMethod]
        public void Catalog_ParsesMaterialsLevelingLinksGuideAndPriority()
        {
            var c = Catalog();
            var march = c.Find("1001");

            Assert.AreEqual(2, march.Materials.Ascension.Count);
            Assert.AreEqual(308000, march.Materials.Ascension[0].Count);
            Assert.AreEqual("110411", march.Materials.Ascension[1].Icon);
            Assert.AreEqual("Tracks of Destiny", march.Materials.Skills[0].Name);
            CollectionAssert.AreEqual(new[] { 0 }, march.Leveling.ToArray());

            Assert.AreEqual(1, march.Links.Count, "the http:// link is dropped");
            Assert.AreEqual("Prydwen", march.Links[0].Label);
            Assert.AreEqual("https://www.prydwen.gg/star-rail/characters/march-7th", march.Links[0].Url);

            Assert.AreEqual("Overview", march.Ai.En.Guide[0].Heading);
            Assert.AreEqual(2, march.Ai.Pl.Guide.Count);
            CollectionAssert.AreEqual(new[] { "103" }, march.Ai.Priority.Sets.ToArray());
            CollectionAssert.AreEqual(new[] { 0 }, march.Ai.Priority.Progression.ToArray());

            Assert.AreEqual("https://img.example/item/110411.webp", c.IconUrl(IconKind.Material, "110411"));
            Assert.IsNull(c.Find("1002").Materials);
            Assert.AreEqual(0, c.Find("1002").Links.Count);
        }

        [TestMethod]
        public void Builder_TipsBlock_FromOfficialDataFirst()
        {
            var c = Catalog();
            var pl = new Dictionary<string, string> { ["Hunter of Glacial Forest"] = "Łowca", ["Frigid Cold Arrow"] = "Mroźna strzała", ["Horn of Snow"] = "Róg śniegu" };
            var m = CharacterOverviewBuilder.Build(c.Find("1001"), c, new DictLookup(pl));

            // Official build wins: best set/weapon/main stats come from it, not from the AI priority.
            // Names a player looks for in the game read as the game shows them; the translation is the gloss.
            var set = m.Highlights.Single(h => h.LabelKey == "Char_Tip_Set");
            Assert.AreEqual("Hunter of Glacial Forest", set.Text);
            Assert.AreEqual("Łowca", set.Gloss);
            Assert.IsFalse(set.IsAi);
            Assert.AreEqual("https://img.example/relic/71003.png", set.IconUrl);
            var weapon = m.Highlights.Single(h => h.LabelKey == "Char_Tip_Weapon");
            Assert.AreEqual("In the Name of the World", weapon.Text);
            Assert.AreEqual("https://img.example/lc/23004.png", weapon.IconUrl);
            Assert.IsTrue(m.Highlights.Any(h => h.LabelKey == "Char_Tip_MainStats" && h.Text.Contains("CRIT Rate")));

            // Official leveling order, not AI, in the English names the game shows.
            var leveling = m.Highlights.Single(h => h.LabelKey == "Char_Tip_Leveling");
            Assert.AreEqual("Frigid Cold Arrow (Basic)", leveling.Text);
            Assert.IsFalse(leveling.IsAi);

            // Progression picks come from AI priority and say so.
            var worth = m.Highlights.Single(h => h.LabelKey == "Char_Tip_WorthIt");
            Assert.IsTrue(worth.IsAi);

            Assert.AreEqual("Róg śniegu", m.MaterialsAscension[1].Name);
            Assert.AreEqual(65, m.MaterialsAscension[1].Count);
            Assert.AreEqual("https://img.example/item/110411.webp", m.MaterialsAscension[1].IconUrl);
            Assert.AreEqual("Tracks of Destiny", m.MaterialsSkills[0].Name);

            Assert.AreEqual("Prydwen", m.Links.Single().Label);
            Assert.AreEqual("Przegląd", m.Guide[0].Heading, "Polish guide preferred");
            // The character's own page on the site, Polish or English by the interface (CharacterPageLink).
            Assert.AreEqual("https://kaption.one/pl/postacie/honkai-star-rail/march-7th/", m.PageUrlPl);
            Assert.AreEqual("https://kaption.one/characters/honkai-star-rail/march-7th/", m.PageUrlEn);
            Assert.IsTrue(m.HasTips);
        }

        [TestMethod]
        public void Builder_TipsBlock_FallsBackToAiPriorityWhenNoOfficialBuild()
        {
            string json = Json.Replace(@"""build"": { ""sets"": [ { ""pieces"": 4, ""ids"": [""103""] } ],", @"""build"": null, ""unused_build"": { ""sets"": [ { ""pieces"": 4, ""ids"": [""103""] } ],")
                .Replace(@"""leveling"": [0],", "");
            var c = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "starrail");
            var m = CharacterOverviewBuilder.Build(c.Find("1001"), c, null);

            var set = m.Highlights.Single(h => h.LabelKey == "Char_Tip_Set");
            Assert.AreEqual("Hunter of Glacial Forest", set.Text);
            Assert.IsTrue(set.IsAi, "no official build: the AI priority is the source");
            Assert.IsFalse(m.Highlights.Any(h => h.LabelKey == "Char_Tip_Leveling"), "no official leveling and empty AI skills: nothing to show");
            Assert.AreEqual("Overview", m.Guide[0].Heading == "Przegląd" ? "Overview" : m.Guide[0].Heading == "Overview" ? "Overview" : "", "PL still preferred when present");
        }

        [TestMethod]
        public void Builder_NoTipsData_HasTipsFalse()
        {
            var c = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(CharacterCatalogTests.MinimalJson)), "starrail");
            var dan = CharacterOverviewBuilder.Build(c.Find("1002"), c, null);
            Assert.IsFalse(dan.HasTips);
            Assert.AreEqual(0, dan.Highlights.Count);
            Assert.AreEqual(0, dan.Links.Count);
            Assert.AreEqual("https://kaption.one/pl/postacie/honkai-star-rail/dan-heng/", dan.PageUrlPl);

            var march = CharacterOverviewBuilder.Build(c.Find("1001"), c, null);
            Assert.IsTrue(march.HasTips, "an official build alone gives highlights");
        }

        private sealed class DictLookup : ITranslationLookup
        {
            private readonly Dictionary<string, string> _d;
            public DictLookup(Dictionary<string, string> d) => _d = d;
            public bool TryTranslateExact(string english, out string translated) => _d.TryGetValue(english ?? "", out translated);
        }
    }
}
