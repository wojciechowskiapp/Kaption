using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class CharacterOverviewBuilderTests
    {
        /// <summary>Exact-key dictionary standing in for the Polish pack.</summary>
        private sealed class DictLookup : ITranslationLookup
        {
            private readonly Dictionary<string, string> _d;
            public DictLookup(Dictionary<string, string> d) => _d = d;
            public bool TryTranslateExact(string english, out string translated) =>
                _d.TryGetValue(english ?? "", out translated);
        }

        private static readonly Dictionary<string, string> Pl = new Dictionary<string, string>
        {
            ["Ice"] = "Lód",
            ["Preservation"] = "Zachowanie",
            ["Shields allies"] = "Osłania sojuszników",
            ["Frigid Cold Arrow"] = "Mroźna strzała",
            ["Deals minor Ice DMG to one enemy."] = "Zadaje niewielkie obrażenia Lodowe jednemu wrogowi.",
            ["Deals Ice DMG equal to 1[i]% of March 7th's ATK."] = "Zadaje obrażenia Lodowe równe 1[i]% ATK March 7th.",
            ["Hunter of Glacial Forest"] = "Łowca z Lodowego Lasu",
            ["Increases Ice DMG by 1[i]%."] = "Zwiększa obrażenia Lodowe o 1[i]%.",
            ["CRIT Rate"] = "Szansa na kryt.",
            ["SPD"] = "SZB",
            ["In the Name of the World"] = "W imię świata",
            ["DMG +1[i]%"] = "Obrażenia +1[i]%",
        };

        private static CharacterCatalog Catalog() =>
            CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(CharacterCatalogTests.MinimalJson)), "starrail");

        [TestMethod]
        public void TranslatesNamesAndFillsParams()
        {
            var c = Catalog();
            var m = CharacterOverviewBuilder.Build(c.Find("1001"), c, new DictLookup(Pl));

            Assert.AreEqual("Lód", m.Element);
            Assert.AreEqual("Zachowanie", m.WeaponType);
            Assert.AreEqual("Osłania sojuszników", m.Role);
            Assert.AreEqual("Mroźna strzała", m.Skills[0].Name);
            Assert.AreEqual("Zadaje niewielkie obrażenia Lodowe jednemu wrogowi.", m.Skills[0].Short);
            Assert.AreEqual("Zadaje obrażenia Lodowe równe 100% ATK March 7th.", m.Skills[0].Description);
            Assert.AreEqual("Char_Kind_basic", m.Skills[0].KindKey);

            var group = m.SetGroups.Single();
            Assert.AreEqual(4, group.Pieces);
            Assert.AreEqual("Łowca z Lodowego Lasu", group.Options[0].Name);
            StringAssert.Contains(group.Options[0].Bonuses[0], "Zwiększa obrażenia Lodowe o 10%.");

            Assert.AreEqual("Char_Slot_body", m.MainStats[0].SlotKey);
            CollectionAssert.AreEqual(new[] { "Szansa na kryt." }, m.MainStats[0].Stats.ToArray());
            CollectionAssert.AreEqual(new[] { "SZB" }, m.SubStats.ToArray());
            Assert.AreEqual("W imię świata", m.Weapons[0].Name);
            Assert.AreEqual("Obrażenia +24%", m.Weapons[0].Passive);
            Assert.IsTrue(m.HasOfficialBuild);
        }

        [TestMethod]
        public void ElementKey_StaysEnglishWhenElementIsTranslated()
        {
            var c = Catalog();
            var m = CharacterOverviewBuilder.Build(c.Find("1001"), c, new DictLookup(Pl));

            Assert.AreEqual("Lód", m.Element);
            Assert.AreEqual("Ice", m.ElementKey, "colours and the element filter key off the English label");
        }

        [TestMethod]
        public void FallsBackToEnglish()
        {
            var c = Catalog();
            var m = CharacterOverviewBuilder.Build(c.Find("1001"), c, null);

            Assert.AreEqual("Ice", m.Element);
            Assert.AreEqual("Deals Ice DMG equal to 100% of March 7th's ATK.", m.Skills[0].Description);
            Assert.AreEqual("CRIT Rate", m.MainStats[0].Stats[0]);
        }

        [TestMethod]
        public void UnknownSetIdSkipped()
        {
            string json = CharacterCatalogTests.MinimalJson.Replace(@"""ids"": [""103""]", @"""ids"": [""999"", ""103""]");
            var c = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "starrail");
            var m = CharacterOverviewBuilder.Build(c.Find("1001"), c, null);

            Assert.AreEqual(1, m.SetGroups[0].Options.Count);
        }

        [TestMethod]
        public void UnknownStatKey_ShownRaw()
        {
            string json = CharacterCatalogTests.MinimalJson.Replace(@"""sub_stats"": [""SpeedDelta""]", @"""sub_stats"": [""MysteryStat""]");
            var c = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "starrail");
            Assert.AreEqual("MysteryStat", CharacterOverviewBuilder.Build(c.Find("1001"), c, null).SubStats[0]);
        }

        [TestMethod]
        public void NullBuild_HasOfficialBuildFalse()
        {
            var c = Catalog();
            var m = CharacterOverviewBuilder.Build(c.Find("1002"), c, null);

            Assert.IsFalse(m.HasOfficialBuild);
            Assert.AreEqual(0, m.SetGroups.Count);
            Assert.AreEqual("", m.Role);
        }

        [TestMethod]
        public void CombineEntry_IsOneGroupWithAllSets()
        {
            string json = CharacterCatalogTests.MinimalJson
                .Replace(@"""sets"": [ { ""pieces"": 4, ""ids"": [""103""] } ]",
                         @"""sets"": [ { ""pieces"": 2, ""ids"": [""103"", ""103""], ""combine"": true } ]");
            var c = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "starrail");
            var g = CharacterOverviewBuilder.Build(c.Find("1001"), c, null).SetGroups.Single();

            Assert.IsTrue(g.Combine);
            Assert.AreEqual(2, g.Options.Count);
            Assert.IsTrue(c.Find("1001").Build.Sets[0].Combine);
        }

        [TestMethod]
        public void ProtagonistTitle_KeepsEnglishName_WithPolishGloss()
        {
            string json = CharacterCatalogTests.MinimalJson.Replace(@"""name"": ""Dan Heng""", @"""name"": ""Traveler (Hydro)""");
            var c = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "starrail");
            var lookup = new DictLookup(new Dictionary<string, string> { ["Traveler"] = "Podróżnik", ["Hydro"] = "Hydro" });

            var model = CharacterOverviewBuilder.Build(c.Find("1002"), c, lookup);
            Assert.AreEqual("Traveler (Hydro)", model.Name);
            Assert.AreEqual("Podróżnik", model.NameGloss);
        }

        [TestMethod]
        public void RarityText_StarsOrZzzRank()
        {
            Assert.AreEqual("★★★★★", CharacterOverviewBuilder.RarityText("genshin", 5));
            Assert.AreEqual("S", CharacterOverviewBuilder.RarityText("zzz", 4));
            Assert.AreEqual("A", CharacterOverviewBuilder.RarityText("ZZZ", 3));
            Assert.AreEqual("B", CharacterOverviewBuilder.RarityText("zzz", 2));
            Assert.AreEqual("", CharacterOverviewBuilder.RarityText("starrail", 0));
        }

        [TestMethod]
        public void MarkupStripped()
        {
            string json = CharacterCatalogTests.MinimalJson.Replace(
                @"""t"": ""Deals minor Ice DMG to one enemy.""", @"""t"": ""Has a <u>base chance</u> to <i>Freeze</i>.""");
            var c = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "starrail");
            var lookup = new DictLookup(new Dictionary<string, string> { ["Has a <u>base chance</u> to <i>Freeze</i>."] = "Ma <u>bazową szansę</u> na <i>zamrożenie</i>." });

            Assert.AreEqual("Ma bazową szansę na zamrożenie.", CharacterOverviewBuilder.Build(c.Find("1001"), c, lookup).Skills[0].Short);
            Assert.AreEqual("Has a base chance to Freeze.", CharacterOverviewBuilder.Build(c.Find("1001"), c, null).Skills[0].Short);
        }

        // Dan Heng (1002) has no official build; March 7th (1001) has one.
        private static CharacterCatalog WithAi() => CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(
            CharacterCatalogTests.MinimalJson
                .Replace(@"""role"": null, ""skills"": [], ""build"": null }",
                    @"""role"": null, ""skills"": [ { ""kind"": ""basic"", ""name"": ""Cloudlancer Art"" } ], ""build"": null,
                      ""ai"": { ""model"": ""gpt-6.1-terra"", ""generated_at"": ""2026-11-01"", ""facts_sha"": ""x"",
                        ""en"": { ""skills"": { ""0"": ""Quick wind jabs."" }, ""playstyle"": [""Hit weak points.""], ""build_note"": ""Speed first."" },
                        ""pl"": { ""skills"": { ""0"": ""Szybkie pchnięcia wiatrem."" }, ""playstyle"": [""Celuj w słabości.""], ""build_note"": ""Najpierw szybkość."" },
                        ""build"": { ""sets"": [ { ""pieces"": 4, ""ids"": [""103""] } ], ""main_stats"": [ { ""slot"": ""body"", ""stats"": [""CriticalChanceBase""] } ],
                                     ""sub_stats"": [""SpeedDelta""], ""weapons"": [""23004""], ""teammates"": [] } } }")
                .Replace(@"""role"": { ""t"": ""Shields allies"", ""p"": [] },",
                    @"""role"": { ""t"": ""Shields allies"", ""p"": [] },
                      ""ai"": { ""pl"": { ""skills"": {}, ""playstyle"": [""Osłaniaj drużynę.""] },
                               ""build"": { ""sets"": [ { ""pieces"": 4, ""ids"": [""103""] } ] } },"))), "starrail");

        [TestMethod]
        public void Ai_PolishPreferred_AndBuildFillsGapOnly()
        {
            var c = WithAi();
            var dan = CharacterOverviewBuilder.Build(c.Find("1002"), c, null);

            Assert.AreEqual("Szybkie pchnięcia wiatrem.", dan.Skills[0].AiSummary);
            CollectionAssert.AreEqual(new[] { "Celuj w słabości." }, dan.Playstyle.ToArray());
            Assert.IsTrue(dan.HasBuild);
            Assert.IsTrue(dan.BuildIsAi);
            Assert.IsFalse(dan.HasOfficialBuild);
            Assert.AreEqual("Najpierw szybkość.", dan.BuildNote);
            Assert.AreEqual("Hunter of Glacial Forest", dan.SetGroups[0].Options[0].Name);

            var march = CharacterOverviewBuilder.Build(c.Find("1001"), c, null);
            Assert.IsTrue(march.HasOfficialBuild);
            Assert.IsFalse(march.BuildIsAi, "an official build always wins over an AI one");
            CollectionAssert.AreEqual(new[] { "Osłaniaj drużynę." }, march.Playstyle.ToArray());
        }

        [TestMethod]
        public void Ai_EnglishFallback_WhenPolishMissing()
        {
            string json = CharacterCatalogTests.MinimalJson.Replace(@"""role"": null, ""skills"": [], ""build"": null }",
                @"""role"": null, ""skills"": [ { ""kind"": ""basic"", ""name"": ""Cloudlancer Art"" } ], ""build"": null,
                  ""ai"": { ""en"": { ""skills"": { ""0"": ""Quick wind jabs."", ""9"": ""out of range"" }, ""playstyle"": [""Hit weak points.""] } } }");
            var c = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "starrail");
            var dan = CharacterOverviewBuilder.Build(c.Find("1002"), c, null);

            Assert.AreEqual("Quick wind jabs.", dan.Skills[0].AiSummary);
            CollectionAssert.AreEqual(new[] { "Hit weak points." }, dan.Playstyle.ToArray());
            Assert.IsFalse(dan.HasBuild);
        }

        [TestMethod]
        public void Ai_Absent_NothingAiShown()
        {
            var c = Catalog();
            var m = CharacterOverviewBuilder.Build(c.Find("1001"), c, null);
            Assert.AreEqual(0, m.Playstyle.Count);
            Assert.IsTrue(m.Skills.All(s => s.AiSummary == ""));
            Assert.IsFalse(m.BuildIsAi);
            Assert.IsFalse(m.HasAi);
        }

        [TestMethod]
        public void Game_TypedBonuses_AndTeammateCards()
        {
            string json = CharacterCatalogTests.MinimalJson
                .Replace(@"""teammates"": []", @"""teammates"": [""1002"", ""nope""]")
                .Replace(@"""id"": ""1002"", ""name"": ""Dan Heng"",", @"""id"": ""1002"", ""name"": ""Dan Heng"", ""icon"": ""1002"",")
                .Replace(@"""schema"": 1, ""game"": ""starrail"",",
                    @"""schema"": 1, ""game"": ""starrail"", ""icon_base"": { ""character"": ""https://img.example/avatar/"" }, ""icon_ext"": { ""character"": "".png"" },");
            var c = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "starrail");
            var m = CharacterOverviewBuilder.Build(c.Find("1001"), c, new DictLookup(Pl));

            Assert.AreEqual("starrail", m.Game);
            var bonus = m.SetGroups[0].Options[0].BonusItems.Single();
            Assert.AreEqual(2, bonus.Pieces);
            Assert.AreEqual("Zwiększa obrażenia Lodowe o 10%.", bonus.Text);
            var mate = m.TeammateCards.Single();
            Assert.AreEqual("1002", mate.Id);
            Assert.AreEqual("Dan Heng", mate.Name);
            Assert.AreEqual("https://img.example/avatar/1002.png", mate.IconUrl);
            CollectionAssert.AreEqual(new[] { "Dan Heng" }, m.Teammates.ToArray());
        }

        // HSR Character Enhancement / ZZZ Potential: two switchable kits in one record.
        private static CharacterCatalog WithVariants() => CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(
            CharacterCatalogTests.MinimalJson
                .Replace(@"""id"": ""1002"", ""name"": ""Dan Heng"",", @"""id"": ""1002"", ""name"": ""Dan Heng"", ""default_variant"": ""enhanced"",")
                .Replace(@"""role"": null, ""skills"": [], ""build"": null }",
                    @"""role"": null, ""build"": null,
                      ""skills"": [ { ""kind"": ""basic"", ""name"": ""Cloudlancer Art: North Wind"" },
                                    { ""kind"": ""skill"", ""name"": ""Cloudlancer Art: Torrent"", ""variant"": ""original"" },
                                    { ""kind"": ""skill"", ""name"": ""Cloudlancer Art: Torrent (Enhanced)"", ""variant"": ""enhanced"" },
                                    { ""kind"": ""eidolon"", ""name"": ""E1 original"", ""variant"": ""original"" },
                                    { ""kind"": ""eidolon"", ""name"": ""E1 enhanced"", ""variant"": ""enhanced"" } ],
                      ""ai"": { ""en"": { ""skills"": { ""2"": ""Big torrent."", ""1"": ""Small torrent."" } } } }"))), "starrail");

        [TestMethod]
        public void Variants_DefaultKitOnly_AiLinesFollowOriginalIndices()
        {
            var c = WithVariants();
            var dan = CharacterOverviewBuilder.Build(c.Find("1002"), c, null);

            CollectionAssert.AreEqual(
                new[] { "Cloudlancer Art: North Wind", "Cloudlancer Art: Torrent (Enhanced)", "E1 enhanced" },
                dan.Skills.Select(s => s.Name).ToArray());
            Assert.AreEqual("Big torrent.", dan.Skills[1].AiSummary, "AI keys index the full skills[] list");
            Assert.AreEqual("enhanced", dan.Variant);
            CollectionAssert.AreEqual(new[] { "original", "enhanced" }, dan.Variants.ToArray());
        }

        [TestMethod]
        public void Variants_ExplicitChoice_AndNoVariantsMeansEverything()
        {
            var c = WithVariants();
            var original = CharacterOverviewBuilder.Build(c.Find("1002"), c, null, variant: "original");
            CollectionAssert.AreEqual(
                new[] { "Cloudlancer Art: North Wind", "Cloudlancer Art: Torrent", "E1 original" },
                original.Skills.Select(s => s.Name).ToArray());
            Assert.AreEqual("Small torrent.", original.Skills[1].AiSummary);

            var march = CharacterOverviewBuilder.Build(c.Find("1001"), c, null);
            Assert.AreEqual("", march.Variant);
            Assert.AreEqual(0, march.Variants.Count);
            Assert.AreEqual(1, march.Skills.Count);
        }

        [TestMethod]
        public void NullRecord_Null()
        {
            Assert.IsNull(CharacterOverviewBuilder.Build(null, Catalog(), null));
        }

        [TestMethod]
        public void PlainText_ContainsNameAndSkills()
        {
            var c = Catalog();
            string text = CharacterOverviewBuilder.Build(c.Find("1001"), c, new DictLookup(Pl)).ToPlainText();

            StringAssert.Contains(text, "March 7th");
            StringAssert.Contains(text, "Mroźna strzała");
            StringAssert.Contains(text, "Łowca z Lodowego Lasu");
        }

        [TestMethod]
        public void MatcherLookup_RejectsFuzzy()
        {
            var matcher = new OptimizedMatcher(new Dictionary<string, string> { ["Frigid Cold Arrow"] = "Mroźna strzała" }, "EN");
            var lookup = new MatcherTranslationLookup(() => matcher);

            Assert.IsTrue(lookup.TryTranslateExact("Frigid Cold Arrow", out string pl));
            Assert.AreEqual("Mroźna strzała", pl);
            Assert.IsFalse(lookup.TryTranslateExact("Frigid Cold Arrows", out _));
        }

        [TestMethod]
        public void MatcherLookup_NameIgnoresPunctuatedDialogueLines()
        {
            // Dialogue lines that are just a name with punctuation normalize like the name itself.
            var dict = new Dictionary<string, string>
            {
                ["...Aino."] = "...Aino.", ["Aino"] = "Aino-PL",
                ["Eula?"] = "Eula?", ["Eula"] = "Eula-PL",
                ["Escoffier!"] = "Escoffier!",
                ["Ice"] = "Lód",
            };
            var matcher = new OptimizedMatcher(dict, "EN");
            var lookup = new MatcherTranslationLookup(() => matcher);

            Assert.IsTrue(lookup.TryTranslateExact("Aino", out string aino));
            Assert.AreEqual("Aino-PL", aino);
            Assert.IsTrue(lookup.TryTranslateExact("Eula", out string eula));
            Assert.AreEqual("Eula-PL", eula);
            Assert.IsFalse(lookup.TryTranslateExact("Escoffier", out _), "only a punctuated line exists");
            Assert.IsTrue(lookup.TryTranslateExact("Ice", out string ice));
            Assert.AreEqual("Lód", ice);
        }

        [TestMethod]
        public void MatcherLookup_NullMatcher_False()
        {
            var lookup = new MatcherTranslationLookup(() => null);
            Assert.IsFalse(lookup.TryTranslateExact("anything", out _));
        }

        [TestMethod]
        public void MatcherLookup_NewMatcherClearsCache()
        {
            OptimizedMatcher current = new OptimizedMatcher(new Dictionary<string, string> { ["Ice"] = "Lód" }, "EN");
            var lookup = new MatcherTranslationLookup(() => current);
            lookup.TryTranslateExact("Ice", out _);

            current = new OptimizedMatcher(new Dictionary<string, string> { ["Ice"] = "Lodowy" }, "EN");
            Assert.IsTrue(lookup.TryTranslateExact("Ice", out string pl));
            Assert.AreEqual("Lodowy", pl);
        }
    }
}
