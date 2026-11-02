using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Owner rule: every term a player has to find in the game's UI reads as the game shows it (English), the
    /// Polish as a smaller gloss; skill names carry the game's type marker wherever they appear outside their card.
    /// </summary>
    [TestClass]
    public class CharacterGameTermsTests
    {
        // Aino-like fixture: Genshin stats, a set, a weapon, a material, three skills and three constellations.
        private const string Json = @"{
  ""schema"": 1, ""game"": ""genshin"", ""game_version"": ""7.1.0"",
  ""stats"": { ""FIGHT_PROP_ELEMENT_MASTERY"": ""Elemental Mastery"", ""FIGHT_PROP_WATER_ADD_HURT"": ""Hydro DMG Bonus"",
               ""FIGHT_PROP_CRITICAL"": ""CRIT Rate"", ""FIGHT_PROP_CHARGE_EFFICIENCY"": ""Energy Recharge"" },
  ""sets"": { ""15040"": { ""name"": ""Silken Moon's Serenade"", ""kind"": ""artifact"", ""bonuses"": [] } },
  ""weapons"": { ""12501"": { ""name"": ""Favonius Greatsword"", ""rarity"": 4, ""type"": ""Claymore"",
                              ""passive_name"": ""Windfall"", ""passive"": { ""t"": ""Generates particles."", ""p"": [] } } },
  ""characters"": [
    { ""id"": ""10000121"", ""name"": ""Aino"", ""rarity"": 4, ""element"": ""Hydro"", ""weapon_type"": ""Claymore"",
      ""skills"": [
        { ""kind"": ""basic"", ""name"": ""Bish-Bash-Bosh Repair"", ""desc"": { ""t"": ""Swings."", ""p"": [] } },
        { ""kind"": ""skill"", ""name"": ""Musecatcher"", ""desc"": { ""t"": ""Throws a device."", ""p"": [] } },
        { ""kind"": ""ultimate"", ""name"": ""Precision Hydronic Cooler"", ""desc"": { ""t"": ""Cools things down."", ""p"": [] } },
        { ""kind"": ""passive"", ""name"": ""Modular Efficiency Protocol"", ""desc"": { ""t"": ""Passive."", ""p"": [] } },
        { ""kind"": ""constellation"", ""name"": ""Debugging Mode"", ""desc"": { ""t"": ""After Musecatcher hits, gain energy."", ""p"": [] } },
        { ""kind"": ""constellation"", ""name"": ""Overclocking"", ""desc"": { ""t"": ""Precision Hydronic Cooler's water balls grow."", ""p"": [] } },
        { ""kind"": ""constellation"", ""name"": ""Firmware Upgrade"", ""desc"": { ""t"": ""Increases the Level of Musecatcher by 3."", ""p"": [] } }
      ],
      ""build"": { ""sets"": [ { ""pieces"": 4, ""ids"": [""15040""] } ],
                   ""main_stats"": [ { ""slot"": ""sands"", ""stats"": [""FIGHT_PROP_ELEMENT_MASTERY"", ""FIGHT_PROP_CHARGE_EFFICIENCY""] },
                                     { ""slot"": ""goblet"", ""stats"": [""FIGHT_PROP_WATER_ADD_HURT""] },
                                     { ""slot"": ""circlet"", ""stats"": [""FIGHT_PROP_CRITICAL""] } ],
                   ""sub_stats"": [""FIGHT_PROP_CHARGE_EFFICIENCY"", ""FIGHT_PROP_CRITICAL""], ""weapons"": [""12501""], ""teammates"": [] },
      ""materials"": { ""ascension"": [ { ""name"": ""Varunada Lazurite Sliver"", ""count"": 1 } ], ""skills"": [] },
      ""leveling"": [2, 1, 0],
      ""ai"": { ""model"": ""m"", ""generated_at"": ""2026-11-01"", ""facts_sha"": ""x"",
                ""pl"": { ""guide"": [ { ""heading"": ""W skrócie"", ""body"": ""Musecatcher zostawia urządzenie, a Precision Hydronic Cooler strzela kulami. Overclocking się opłaca."" } ] },
                ""priority"": { ""sets"": [], ""weapons"": [], ""skills"": [], ""progression"": [5, 6] },
                ""teams"": [ { ""name"": { ""pl"": ""Hydro"" }, ""source"": ""ai"", ""members"": [""10000121"", ""10000032""],
                               ""roles"": { ""10000121"": { ""pl"": ""Wsparcie: Musecatcher co rotację"" } },
                               ""why"": { ""pl"": ""Precision Hydronic Cooler's kule dają reakcje."" } } ] } },
    { ""id"": ""10000032"", ""name"": ""Bennett"", ""rarity"": 4, ""element"": ""Pyro"", ""skills"": [] }
  ]
}";

        private static readonly Dictionary<string, string> Pl = new Dictionary<string, string>
        {
            ["Elemental Mastery"] = "Biegłość żywiołów",
            ["Hydro DMG Bonus"] = "Premia do obrażeń Hydro",
            ["CRIT Rate"] = "Szansa na CRIT",
            ["Energy Recharge"] = "Odnawianie energii",
            ["Silken Moon's Serenade"] = "Serenada Jedwabnego Księżyca",
            ["Favonius Greatsword"] = "Wielki miecz Favoniusa",
            ["Windfall"] = "Pomyślny wiatr",
            ["Varunada Lazurite Sliver"] = "Odłamek Lazurytu Varunady",
            ["Musecatcher"] = "Łapacz muz",
        };

        private sealed class DictLookup : ITranslationLookup
        {
            public bool TryTranslateExact(string english, out string translated) => Pl.TryGetValue(english ?? "", out translated);
        }

        private static CharacterOverviewModel Model()
        {
            var catalog = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Json)), "genshin");
            return CharacterOverviewBuilder.Build(catalog.Find("10000121"), catalog, new DictLookup());
        }

        [TestMethod]
        public void BuildChecklist_GameTermsInEnglish_PolishAsGloss()
        {
            var m = Model();
            var stats = m.Highlights.Single(h => h.LabelKey == "Char_Tip_MainStats");
            Assert.AreEqual("Elemental Mastery · Hydro DMG Bonus · CRIT Rate", stats.Text);
            Assert.AreEqual("Biegłość żywiołów · Premia do obrażeń Hydro · Szansa na CRIT", stats.Gloss);

            var set = m.Highlights.Single(h => h.LabelKey == "Char_Tip_Set");
            Assert.AreEqual("Silken Moon's Serenade", set.Text);
            Assert.AreEqual("Serenada Jedwabnego Księżyca", set.Gloss);

            var weapon = m.Highlights.Single(h => h.LabelKey == "Char_Tip_Weapon");
            Assert.AreEqual("Favonius Greatsword", weapon.Text);
            Assert.AreEqual("Wielki miecz Favoniusa", weapon.Gloss);
        }

        [TestMethod]
        public void BuildDetails_RenderTheEnglishNameFirst()
        {
            var vm = new CharacterOverviewViewModel(Model());

            var sands = vm.MainStats[0];
            Assert.AreEqual("Elemental Mastery", sands.Primary);
            Assert.AreEqual(" / Energy Recharge", sands.Alternatives);
            Assert.AreEqual("Biegłość żywiołów / Odnawianie energii", sands.Gloss);

            Assert.AreEqual("Energy Recharge", vm.SubStats[0].Name);
            Assert.AreEqual("Odnawianie energii", vm.SubStats[0].Gloss);

            var set = vm.SetGroups[0].Options[0];
            Assert.AreEqual("Silken Moon's Serenade", set.Name);
            Assert.AreEqual("Serenada Jedwabnego Księżyca", set.Gloss);

            var weapon = vm.Weapons[0];
            Assert.AreEqual("Favonius Greatsword", weapon.Name);
            Assert.AreEqual(" (Wielki miecz Favoniusa)", weapon.GlossSuffix);
            Assert.AreEqual("Windfall", weapon.PassiveName);
            Assert.AreEqual("Pomyślny wiatr", weapon.PassiveGloss);

            var material = vm.MaterialsAscension[0];
            Assert.AreEqual("Varunada Lazurite Sliver", material.Name);
            Assert.AreEqual("Odłamek Lazurytu Varunady", material.Gloss);

            var tip = vm.Tips.First(t => t.LabelKey == "Char_Tip_Set");
            Assert.AreEqual("Silken Moon's Serenade", tip.Text);
            Assert.IsTrue(tip.HasGloss);
        }

        [TestMethod]
        public void SameInBothLanguages_OrUntranslated_HasNoGloss()
        {
            var model = Model();
            model.MaterialsAscension = new[] { new OverviewMaterial { Name = "Mora", EnglishName = "Mora", Count = 1 } };
            Assert.IsFalse(new CharacterOverviewViewModel(model).MaterialsAscension[0].HasGloss);

            // No pack: English everywhere, nothing to gloss.
            var catalog = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Json)), "genshin");
            var plain = CharacterOverviewBuilder.Build(catalog.Find("10000121"), catalog, null);
            Assert.IsTrue(plain.Highlights.All(h => h.Gloss.Length == 0));
            Assert.IsTrue(new CharacterOverviewViewModel(plain).MainStats.All(s => !s.HasGloss));
        }

        [TestMethod]
        public void SkillNames_OutsideTheirCard_CarryTheTypeMarker()
        {
            var m = Model();
            Assert.AreEqual("Precision Hydronic Cooler (Q) → Musecatcher (E) → Bish-Bash-Bosh Repair (Atak)",
                m.Highlights.Single(h => h.LabelKey == "Char_Tip_Leveling").Text);
            Assert.AreEqual("Overclocking (C2), Firmware Upgrade (C3)", m.Highlights.Single(h => h.LabelKey == "Char_Tip_WorthIt").Text);

            // Free text: the AI summary, team notes and the game's own descriptions.
            StringAssert.Contains(m.Guide[0].Body, "Musecatcher (E) zostawia");
            StringAssert.Contains(m.Guide[0].Body, "Precision Hydronic Cooler (Q) strzela");
            StringAssert.Contains(m.Guide[0].Body, "Overclocking (C2) się");
            StringAssert.Contains(m.Teams[0].Members[0].Role, "Musecatcher (E) co");
            StringAssert.Contains(m.Teams[0].Why, "Precision Hydronic Cooler's kule", "never inside a possessive");
            Assert.AreEqual("Increases the Level of Musecatcher (E) by 3.", m.Skills.Single(s => s.EnglishName == "Firmware Upgrade").Description);

            // The at-a-glance tiles too; the skill cards keep their badge instead.
            var vm = new CharacterOverviewViewModel(m);
            Assert.AreEqual("Precision Hydronic Cooler (Q)", vm.Facts.Single(f => f.LabelKey == "Char_Fact_Signature").Text);
            Assert.AreEqual("Precision Hydronic Cooler (Q)", vm.Facts.Single(f => f.LabelKey == "Char_Fact_LevelFirst").Text);
            Assert.AreEqual("Precision Hydronic Cooler", vm.Skills.Single(s => s.Kind == "ultimate").Title);
        }

        [TestMethod]
        public void Markers_PerGame()
        {
            Assert.AreEqual("(E)", SkillMarkers.For("genshin", "skill"));
            Assert.AreEqual("(Q)", SkillMarkers.For("genshin", "ultimate"));
            Assert.AreEqual("(Atak)", SkillMarkers.For("genshin", "basic"));
            Assert.AreEqual("(C3)", SkillMarkers.For("genshin", "constellation", 3));
            Assert.AreEqual("", SkillMarkers.For("genshin", "passive"));

            Assert.AreEqual("(Skill)", SkillMarkers.For("starrail", "skill"));
            Assert.AreEqual("(Ult)", SkillMarkers.For("starrail", "ultimate"));
            Assert.AreEqual("(Basic)", SkillMarkers.For("starrail", "basic"));
            Assert.AreEqual("(Talent)", SkillMarkers.For("starrail", "talent"));
            Assert.AreEqual("(Technique)", SkillMarkers.For("starrail", "technique"));
            Assert.AreEqual("(E2)", SkillMarkers.For("starrail", "eidolon", 2));

            Assert.AreEqual("(Special)", SkillMarkers.For("zzz", "special"));
            Assert.AreEqual("(Chain)", SkillMarkers.For("zzz", "chain"));
            Assert.AreEqual("(Dodge)", SkillMarkers.For("zzz", "dodge"));
            Assert.AreEqual("(Assist)", SkillMarkers.For("zzz", "assist"));
            Assert.AreEqual("(Basic)", SkillMarkers.For("zzz", "basic"));
            Assert.AreEqual("(M4)", SkillMarkers.For("zzz", "mindscape", 4));
            // ZZZ names that already state their type get no second label.
            Assert.AreEqual("", SkillMarkers.For("zzz", "chain", 0, "Ultimate: Lingering Snow"));
            Assert.AreEqual("", SkillMarkers.For("zzz", "basic", 0, "Basic Attack: Kazahana"));
            Assert.AreEqual("(E)", SkillMarkers.For("genshin", "skill", 0, "Hiyoku: Twin Blades"), "a colon alone is not a type");
        }

        [TestMethod]
        public void Annotate_WholeNamesOnly_Once()
        {
            var refs = new[] { new SkillReference("Musecatcher", "(E)", 1), new SkillReference("Cooler", "(Q)", 2) };
            Assert.AreEqual("Musecatcher (E) i Musecatcher (E).", SkillMarkers.Annotate("Musecatcher i Musecatcher.", refs));
            Assert.AreEqual("Musecatchers", SkillMarkers.Annotate("Musecatchers", refs), "part of a longer word");
            Assert.AreEqual("Musecatcher (E) już", SkillMarkers.Annotate("Musecatcher (E) już", refs), "already marked");
            Assert.AreEqual("Musecatcher itd.", SkillMarkers.Annotate("Musecatcher itd.", refs, self: 1), "a skill's own text");
        }

        [TestMethod]
        public void LinkMarks_ByHostSuffix()
        {
            Assert.AreEqual("game8", LinkHosts.KeyFor("https://game8.co/games/Genshin-Impact/archives/1"));
            Assert.AreEqual("kqm", LinkHosts.KeyFor("https://keqingmains.com/q/chiori-quickguide/"));
            Assert.AreEqual("fandom", LinkHosts.KeyFor("https://genshin-impact.fandom.com/wiki/Chiori"));
            Assert.AreEqual("fandom", LinkHosts.KeyFor("https://zenless-zone-zero.fandom.com/wiki/Ellen_Joe"));
            Assert.AreEqual("hoyolab", LinkHosts.KeyFor("https://wiki.hoyolab.com/pc/genshin/entry/1"));
            Assert.AreEqual("prydwen", LinkHosts.KeyFor("https://www.prydwen.gg/star-rail/characters/kafka"));
            Assert.AreEqual("youtube", LinkHosts.KeyFor("https://youtu.be/abc"));
            Assert.AreEqual("reddit", LinkHosts.KeyFor("https://www.reddit.com/r/Genshin_Impact/"));
            Assert.AreEqual("link", LinkHosts.KeyFor("https://notfandom.com/x"), "whole labels only");
            Assert.AreEqual("link", LinkHosts.KeyFor("https://fandom.com.evil.example/x"));
            Assert.AreEqual("link", LinkHosts.KeyFor("not a url"));
            Assert.AreEqual("K", LinkHosts.MonogramFor("https://keqingmains.com/"));
            Assert.AreEqual("", LinkHosts.MonogramFor("https://www.youtube.com/watch?v=1"), "video gets a drawn glyph");
        }

        [TestMethod]
        public void Links_NeverFetchAnything()
        {
            var json = Json.Replace(@"""leveling"": [2, 1, 0],",
                @"""leveling"": [2, 1, 0], ""links"": [ { ""site"": ""fandom"", ""label"": ""Fandom"", ""url"": ""https://genshin-impact.fandom.com/wiki/Aino"" } ],");
            var catalog = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "genshin");
            var link = CharacterOverviewBuilder.Build(catalog.Find("10000121"), catalog, null).Links.Single();
            Assert.AreEqual("fandom", link.IconKey);
            Assert.AreEqual("F", link.Monogram);
            Assert.IsNull(typeof(OverviewLink).GetProperty("IconUrl"), "no URL to fetch a favicon from");
        }
    }
}
