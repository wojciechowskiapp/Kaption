using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Goal stats (the updater's ai.stat_goals) and the rank a team assumes per member (teams[].ranks): tolerant loading,
    /// the builder, the value format in both interface languages, the view model.
    /// </summary>
    [TestClass]
    public class CharacterStatGoalsTests
    {
        internal const string Chiori = "10000094", Navia = "10000091", Zhongli = "10000030", Gorou = "10000055";

        private const string Dash = "\u2013";   // en dash between the bounds
        private const string Thin = "\u202F";   // narrow no-break space between Polish thousands

        /// <summary>
        /// The four goals the render shows (stats by catalog key, as the bundle writes them): a % range, an open %, an
        /// open flat range in thousands, a % range.
        /// </summary>
        internal const string ChioriGoals = @"[
  { ""stat"": ""FIGHT_PROP_CRITICAL"", ""min"": 60, ""max"": 70, ""unit"": ""%"", ""open"": false,
    ""note"": { ""en"": ""Raise it before CRIT DMG."", ""pl"": ""Podnieś ją przed CRIT DMG."" } },
  { ""stat"": ""FIGHT_PROP_CRITICAL_HURT"", ""min"": 120, ""max"": null, ""unit"": ""%"", ""open"": true },
  { ""stat"": ""FIGHT_PROP_DEFENSE"", ""min"": 2500, ""max"": 3000, ""unit"": """", ""open"": true,
    ""note"": { ""en"": ""Fluttering Hasode scales with DEF and ATK."", ""pl"": ""Fluttering Hasode liczy obrażenia z DEF i ATK."" } },
  { ""stat"": ""FIGHT_PROP_CHARGE_EFFICIENCY"", ""min"": 100, ""max"": 130, ""unit"": ""%"", ""open"": false }
]";

        internal static string Character(string id, string name, string element, string extra = "") =>
            @"{ ""id"": """ + id + @""", ""name"": """ + name + @""", ""rarity"": 5, ""element"": """ + element + @""", ""weapon_type"": ""Sword""" + extra + " }";

        /// <summary>A Genshin section: Chiori with goals and two teams (ranks on one), Navia whose team assumes Chiori at C1.</summary>
        internal static string GenshinJson(string goals = ChioriGoals, string game = "genshin") => @"{
  ""schema"": 1, ""game"": """ + game + @""", ""game_version"": ""7.2.0"",
  ""stats"": { ""FIGHT_PROP_CRITICAL"": ""CRIT Rate"", ""FIGHT_PROP_CRITICAL_HURT"": ""CRIT DMG"", ""FIGHT_PROP_DEFENSE"": ""DEF"",
               ""FIGHT_PROP_CHARGE_EFFICIENCY"": ""Energy Recharge"", ""FIGHT_PROP_ATTACK_PERCENT"": ""ATK Percentage"",
               ""FIGHT_PROP_ELEMENT_MASTERY"": ""Elemental Mastery"", ""FIGHT_PROP_HEAL_ADD"": ""Healing Bonus"", ""FIGHT_PROP_BLANK"": """" },
  ""stats_short"": { ""FIGHT_PROP_ATTACK_PERCENT"": ""ATK%"" },
  ""characters"": [
    " + Character(Chiori, "Chiori", "Geo", @", ""release_date"": ""2024-06-25"",
      ""skills"": [ { ""kind"": ""skill"", ""name"": ""Fluttering Hasode"" } ],
      ""ai"": { ""model"": ""m"", ""stat_goals"": " + goals + @",
        ""teams"": [
          { ""name"": { ""en"": ""Double Geo"", ""pl"": ""Podwójne Geo"" }, ""source"": ""official"",
            ""members"": [""" + Chiori + @""", """ + Navia + @""", """ + Zhongli + @"""] },
          { ""name"": { ""en"": ""Mono Geo"", ""pl"": ""Czyste Geo"" }, ""source"": ""ai"",
            ""members"": [""" + Chiori + @""", """ + Navia + @""", """ + Zhongli + @""", """ + Gorou + @"""],
            ""roles"": [ { ""id"": """ + Gorou + @""", ""role"": { ""en"": ""Buffer"", ""pl"": ""Wzmocnienia"" } } ],
            ""why"": { ""en"": ""Navia works at C0; Gorou at C6 adds Geo CRIT DMG."", ""pl"": ""Navii wystarczy C0, a Gorou z C6 dokłada Geo CRIT DMG."" },
            ""ranks"": { """ + Navia + @""": 0, """ + Gorou + @""": 6 } } ] }") + @",
    " + Character(Navia, "Navia", "Geo", @", ""release_date"": ""2024-05-14"", ""skills"": [],
      ""ai"": { ""teams"": [ { ""name"": { ""en"": ""Crystallize"", ""pl"": ""Krystalizacja"" }, ""source"": ""ai"",
        ""members"": [""" + Navia + @""", """ + Chiori + @""", """ + Zhongli + @"""],
        ""roles"": [ { ""id"": """ + Chiori + @""", ""role"": { ""en"": ""Off-field DPS"", ""pl"": ""DPS z drugiej linii"" } } ],
        ""why"": { ""en"": ""Chiori at C1 needs no second Geo."", ""pl"": ""Chiori z C1 nie potrzebuje drugiej postaci Geo."" },
        ""ranks"": { """ + Chiori + @""": 1 } } ] } ") + @",
    " + Character(Zhongli, "Zhongli", "Geo", @", ""skills"": []") + @",
    " + Character(Gorou, "Gorou", "Geo", @", ""skills"": []") + @"
  ]
}";

        internal static CharacterCatalog Load(string json, string game = "genshin") =>
            CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), game);

        internal static CharacterCatalog Genshin(string goals = ChioriGoals) => Load(GenshinJson(goals));

        // ── loading ─────────────────────────────────────────────────────────

        [TestMethod]
        public void Load_ReadsEveryGoalField_InDataOrder()
        {
            var goals = Genshin().Find(Chiori).Ai.StatGoals;

            CollectionAssert.AreEqual(new[] { "FIGHT_PROP_CRITICAL", "FIGHT_PROP_CRITICAL_HURT", "FIGHT_PROP_DEFENSE", "FIGHT_PROP_CHARGE_EFFICIENCY" },
                goals.Select(g => g.Stat).ToArray(), "the catalog keys, in the data's order");
            Assert.AreEqual(60.0, goals[0].Min);
            Assert.AreEqual(70.0, goals[0].Max);
            Assert.AreEqual("%", goals[0].Unit);
            Assert.IsFalse(goals[0].Open);
            Assert.AreEqual("Raise it before CRIT DMG.", goals[0].Note.En);
            Assert.AreEqual("Podnieś ją przed CRIT DMG.", goals[0].Note.Pl);
            Assert.IsNull(goals[1].Max, "max: null is no upper bound");
            Assert.IsTrue(goals[1].Open);
            Assert.IsTrue(goals[1].Note.IsEmpty, "no note");
            Assert.AreEqual("", goals[2].Unit);
            Assert.AreEqual(2500.0, goals[2].Min);
        }

        [TestMethod]
        public void Load_BadGoalsAreSkipped_TheRestKept()
        {
            const string em = "FIGHT_PROP_ELEMENT_MASTERY";
            string goals = @"[
  ""not an object"", 7, null,
  { ""min"": 1 },
  { ""stat"": ""   "", ""min"": 1 },
  { ""stat"": true, ""min"": 1 },
  { ""stat"": """ + em + @""", ""min"": ""200"" },
  { ""stat"": """ + em + @""", ""min"": -5 },
  { ""stat"": """ + em + @""", ""min"": 300, ""max"": 200 },
  { ""stat"": """ + em + @""", ""min"": null, ""max"": null, ""open"": true },
  { ""stat"": """ + em + @""" },
  { ""stat"": """ + em + @""", ""min"": 100, ""unit"": ""pts"" },
  { ""stat"": """ + em + @""", ""min"": 100, ""unit"": 1 },
  { ""stat"": """ + em + @""", ""min"": 100, ""open"": ""yes"", ""note"": ""Plain text counts as English."" },
  { ""stat"": """ + em + @""", ""min"": 999 },
  { ""stat"": ""FIGHT_PROP_CRITICAL"", ""max"": 75, ""unit"": "" % "" },
  { ""stat"": 31402, ""min"": 300 },
  { ""stat"": """ + new string('x', 65) + @""", ""min"": 1 }
]";
            var kept = Genshin(goals).Find(Chiori).Ai.StatGoals;

            Assert.AreEqual(3, kept.Count, string.Join(", ", kept.Select(g => g.Stat)));
            Assert.AreEqual(em, kept[0].Stat);
            Assert.AreEqual(100.0, kept[0].Min, "the first goal for a key wins");
            Assert.IsFalse(kept[0].Open, "only JSON true opens a goal");
            Assert.AreEqual("Plain text counts as English.", kept[0].Note.En);
            Assert.AreEqual("", kept[0].Note.Pl);
            Assert.AreEqual("FIGHT_PROP_CRITICAL", kept[1].Stat);
            Assert.IsNull(kept[1].Min);
            Assert.AreEqual(75.0, kept[1].Max);
            Assert.AreEqual("%", kept[1].Unit, "the unit is trimmed");
            Assert.AreEqual("31402", kept[2].Stat, "a numeric key reads as its digits (ZZZ keys are numbers as text)");
        }

        [TestMethod]
        public void Load_KeepsAtMostSixGoals()
        {
            string goals = "[" + string.Join(", ", Enumerable.Range(1, 9).Select(i => @"{ ""stat"": ""STAT_" + i + @""", ""min"": " + i + " }")) + "]";
            var kept = Genshin(goals).Find(Chiori).Ai.StatGoals;

            Assert.AreEqual(CharacterCatalog.MaxStatGoals, kept.Count);
            Assert.AreEqual("STAT_6", kept.Last().Stat);
        }

        [TestMethod]
        public void Load_OldBundleWithoutGoals_LoadsUnchanged()
        {
            var march = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(CharacterCatalogTests.MinimalJson)), "starrail").Find("1001");
            Assert.IsNotNull(march);
            Assert.IsNull(march.Ai, "no ai block at all");
            Assert.AreEqual(1, march.Build.Sets.Count, "the official build is untouched");

            foreach (string notAList in new[] { "null", "{}", @"""CRIT Rate 70%""", "42" })
            {
                var chiori = Genshin(notAList).Find(Chiori);
                Assert.AreEqual(0, chiori.Ai.StatGoals.Count, notAList);
                Assert.AreEqual(2, chiori.Ai.Teams.Count, "the rest of the ai block is untouched: " + notAList);
            }

            string json = GenshinJson().Replace(@"""stat_goals"": " + ChioriGoals + ",", "");
            Assert.AreEqual(0, Load(json).Find(Chiori).Ai.StatGoals.Count, "the key absent");
        }

        [TestMethod]
        public void Load_AiBlockWithOnlyGoals_IsKept()
        {
            string json = CharacterCatalogTests.MinimalJson.Replace(@"""role"": null, ""skills"": [], ""build"": null }",
                @"""role"": null, ""skills"": [], ""build"": null,
                  ""ai"": { ""stat_goals"": [ { ""stat"": ""SpeedDelta"", ""min"": 134, ""open"": true } ] } }");
            var dan = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "starrail").Find("1002");

            Assert.IsNotNull(dan.Ai, "an ai block holding only goals is not dropped as empty");
            Assert.AreEqual("SpeedDelta", dan.Ai.StatGoals.Single().Stat);
        }

        [TestMethod]
        public void Load_TeamRanks_MembersAndWholeNumbersZeroToSixOnly()
        {
            string json = GenshinJson().Replace(@"""ranks"": { """ + Navia + @""": 0, """ + Gorou + @""": 6 }",
                @"""ranks"": { """ + Navia + @""": 0, """ + Gorou + @""": 6, """ + Zhongli + @""": 3, """ + Chiori + @""": -1,
                               ""10000002"": 2, ""extra"": 1 }");
            Assert.AreNotEqual(GenshinJson(), json, "the fixture took the extra pairs");
            var team = Load(json).Find(Chiori).Ai.Teams.Single(t => t.Source == "ai");

            CollectionAssert.AreEquivalent(new[] { Navia, Gorou, Zhongli }, team.Ranks.Keys.ToArray(), "out of range or not a member: dropped");
            Assert.AreEqual(0, team.Ranks[Navia], "0 is a rank (works at C0), not a missing one");
            Assert.AreEqual(6, team.Ranks[Gorou]);
            Assert.AreEqual(3, team.Ranks[Zhongli]);

            foreach (string odd in new[] { "7", "1.5", @"""2""", "true", "null" })
            {
                string oddJson = GenshinJson().Replace(@"""ranks"": { """ + Navia + @""": 0,", @"""ranks"": { """ + Navia + @""": " + odd + ",");
                Assert.AreNotEqual(GenshinJson(), oddJson, odd);
                var oddTeam = Load(oddJson).Find(Chiori).Ai.Teams.Single(t => t.Source == "ai");
                Assert.IsFalse(oddTeam.Ranks.ContainsKey(Navia), odd);
                Assert.AreEqual(6, oddTeam.Ranks[Gorou], "one bad pair never costs the others: " + odd);
            }

            var official = Genshin().Find(Chiori).Ai.Teams.Single(t => t.Source == "official");
            Assert.AreEqual(0, official.Ranks.Count, "no ranks key: empty");
            string notAnObject = GenshinJson().Replace(@"""ranks"": { """ + Navia + @""": 0, """ + Gorou + @""": 6 }", @"""ranks"": [1, 2]");
            Assert.AreEqual(0, Load(notAnObject).Find(Chiori).Ai.Teams.Single(t => t.Source == "ai").Ranks.Count);
        }

        // ── the value, as the site writes it ────────────────────────────────

        [TestMethod]
        public void Format_English_TheSitesExamples()
        {
            Assert.AreEqual("2,500" + Dash + "3,000+", StatGoalFormat.Value(2500, 3000, "", true, "en"));
            Assert.AreEqual("200%+", StatGoalFormat.Value(200, null, "%", true, "en"));
            Assert.AreEqual("100" + Dash + "130%", StatGoalFormat.Value(100, 130, "%", false, "en"));
        }

        [TestMethod]
        public void Format_Polish_TheSitesExamples()
        {
            Assert.AreEqual("2" + Thin + "500" + Dash + "3" + Thin + "000+", StatGoalFormat.Value(2500, 3000, "", true, "pl"));
            Assert.AreEqual("200%+", StatGoalFormat.Value(200, null, "%", true, "pl"));
            Assert.AreEqual("100" + Dash + "130%", StatGoalFormat.Value(100, 130, "%", false, "pl"));
        }

        [TestMethod]
        public void Format_SeparatorsAndDecimals_PerLanguage()
        {
            Assert.AreEqual("134.4", StatGoalFormat.Number(134.4, "en"));
            Assert.AreEqual("134,4", StatGoalFormat.Number(134.4, "pl"));
            Assert.AreEqual("1.25", StatGoalFormat.Number(1.25, "en-US"));
            Assert.AreEqual("1,25", StatGoalFormat.Number(1.25, "pl-PL"));
            Assert.AreEqual("12,345.5", StatGoalFormat.Number(12345.5, "en"));
            Assert.AreEqual("12" + Thin + "345,5", StatGoalFormat.Number(12345.5, "pl"));
            Assert.AreEqual("1,000,000", StatGoalFormat.Number(1000000, "en"));
            Assert.AreEqual("1" + Thin + "000" + Thin + "000", StatGoalFormat.Number(1000000, "PL"));
            Assert.AreEqual("999", StatGoalFormat.Number(999, "pl"));
            Assert.AreEqual("0", StatGoalFormat.Number(0, "en"));
            Assert.AreEqual("70", StatGoalFormat.Number(70.0, "pl"), "no trailing zeros");
            Assert.AreEqual("0.33", StatGoalFormat.Number(1.0 / 3, "en"), "two decimals at most");
            // The site's own rounding (goals.ts): cents = round(n * 100) half away from zero, then floor and remainder.
            Assert.AreEqual("0.13", StatGoalFormat.Number(0.125, "en"), "half away from zero");
            Assert.AreEqual("2.68", StatGoalFormat.Number(2.675, "en"), "2.675 * 100 is 267.5 in binary64, as in JS");
            Assert.AreEqual("1", StatGoalFormat.Number(1.005, "en"), "1.005 * 100 is 100.4999..., as in JS");
            Assert.AreEqual("1.05", StatGoalFormat.Number(1.05, "en"), "a leading zero in the fraction stays");
            Assert.AreEqual("1.5", StatGoalFormat.Number(1.5, "en"));
            Assert.AreEqual("1,5", StatGoalFormat.Number(1.5, "pl"));
            Assert.AreEqual("2,500.5", StatGoalFormat.Number(2500.5, "en"));
            Assert.AreEqual("2" + Thin + "500,5", StatGoalFormat.Number(2500.5, "pl"));
            Assert.AreEqual("100,000", StatGoalFormat.Number(99999.999, "en"), "rounding carries into the whole part");
            Assert.AreEqual("", StatGoalFormat.Number(double.NaN, "en"));
            Assert.AreEqual("2,500", StatGoalFormat.Number(2500, null), "no language: English");
            // The contract's own cases: ZZZ Energy Regen 1.2, Star Rail Energy Regeneration Rate 19.4%, large flat numbers.
            Assert.AreEqual("1.2+", StatGoalFormat.Value(1.2, null, "", true, "en"));
            Assert.AreEqual("1,2+", StatGoalFormat.Value(1.2, null, "", true, "pl"));
            Assert.AreEqual("19.4%", StatGoalFormat.Value(19.4, 19.4, "%", false, "en"));
            Assert.AreEqual("19,4%", StatGoalFormat.Value(19.4, 19.4, "%", false, "pl"));
            Assert.AreEqual("40,000+", StatGoalFormat.Value(40000, null, "", true, "en"));
            Assert.AreEqual("40" + Thin + "000+", StatGoalFormat.Value(40000, null, "", true, "pl"));
        }

        [TestMethod]
        public void Format_TheMachinesCultureNeverLeaksIn()
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                foreach (string culture in new[] { "de-DE", "fr-FR", "pl-PL", "ar-SA" })
                {
                    CultureInfo.CurrentCulture = new CultureInfo(culture);
                    Assert.AreEqual("2,500" + Dash + "3,000+", StatGoalFormat.Value(2500, 3000, "", true, "en"), culture);
                    Assert.AreEqual("2" + Thin + "500" + Dash + "3" + Thin + "000+", StatGoalFormat.Value(2500, 3000, "", true, "pl"), culture);
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        [TestMethod]
        public void Format_Shapes_EqualBoundsUpperBoundOnlyAndNothing()
        {
            Assert.AreEqual("130%", StatGoalFormat.Value(130, 130, "%", false, "en"), "equal bounds read as one value");
            Assert.AreEqual("160+", StatGoalFormat.Value(160, 160, "", true, "en"));
            Assert.AreEqual("70" + Dash + "80%", StatGoalFormat.Value(70, 80, "%", false, "en"));
            Assert.AreEqual("120%", StatGoalFormat.Value(120, 120, "%", false, "pl"), "120 to 120 is one value");
            Assert.AreEqual("\u2264" + "130%", StatGoalFormat.Value(null, 130, "%", false, "en"), "only an upper bound");
            Assert.AreEqual("\u2264" + "1" + Thin + "200", StatGoalFormat.Value(null, 1200, "", true, "pl"), "an upper bound is never open");
            Assert.AreEqual("", StatGoalFormat.Value(null, null, "%", true, "en"));
            Assert.AreEqual("", StatGoalFormat.Value(null, "en"));
        }

        [TestMethod]
        public void Format_NeverSpellsOutMoreIsFine()
        {
            foreach (string language in new[] { "en", "pl" })
            {
                string value = StatGoalFormat.Value(2500, 3000, "", true, language);
                StringAssert.EndsWith(value, "+");
                Assert.IsFalse(value.Any(char.IsLetter), $"{language}: digits and signs only, never \"or above\": {value}");
                Assert.IsFalse(value.Contains('-'), "an en dash, not a hyphen");
                Assert.IsFalse(value.Contains(' ') || value.Contains('\u00A0'), "no plain or wide no-break space");
            }
        }

        // ── builder ─────────────────────────────────────────────────────────

        [TestMethod]
        public void Builder_ShowsEachKeysLabel_InDataOrder()
        {
            var catalog = Genshin();
            var model = CharacterOverviewBuilder.Build(catalog.Find(Chiori), catalog, null);

            Assert.IsTrue(model.HasStatGoals);
            CollectionAssert.AreEqual(new[] { "CRIT Rate", "CRIT DMG", "DEF", "Energy Recharge" }, model.StatGoals.Select(g => g.Stat).ToArray(),
                "each key reads as the stat catalog labels it, in English");
            var def = model.StatGoals[2];
            Assert.AreEqual(2500.0, def.Min);
            Assert.AreEqual(3000.0, def.Max);
            Assert.IsTrue(def.Open);
            Assert.AreEqual("2,500" + Dash + "3,000+", def.ValueFor("en"));
            Assert.AreEqual("2" + Thin + "500" + Dash + "3" + Thin + "000+", def.ValueFor("pl"));
            Assert.AreEqual("Fluttering Hasode (E) scales with DEF and ATK.", def.Note.En, "skill names in a note get their marker, like other AI text");
            Assert.AreEqual("Fluttering Hasode (E) liczy obrażenia z DEF i ATK.", def.Note.Pl);
        }

        [TestMethod]
        public void Builder_PercentStatsByShortName_KeysTheCatalogLacksAreSkipped()
        {
            string goals = @"[
  { ""stat"": ""FIGHT_PROP_ATTACK_PERCENT"", ""min"": 40, ""unit"": ""%"" },
  { ""stat"": ""CRIT Rate"", ""min"": 50, ""unit"": ""%"" },
  { ""stat"": ""FIGHT_PROP_NOPE"", ""min"": 1 },
  { ""stat"": ""FIGHT_PROP_BLANK"", ""min"": 1 },
  { ""stat"": ""FIGHT_PROP_HEAL_ADD"", ""min"": 20, ""unit"": ""%"", ""note"": { ""en"": ""<color=#FFD780FF>Optional</color>."" } },
  { ""stat"": ""FIGHT_PROP_ELEMENT_MASTERY"", ""min"": 200, ""max"": 300 }
]";
            var catalog = Genshin(goals);
            var model = CharacterOverviewBuilder.Build(catalog.Find(Chiori), catalog, null);

            CollectionAssert.AreEqual(new[] { "ATK%", "Healing Bonus", "Elemental Mastery" }, model.StatGoals.Select(g => g.Stat).ToArray(),
                "a label instead of a key, an unknown key and a key without a label are skipped");
            Assert.AreEqual("ATK%", catalog.StatsShort["FIGHT_PROP_ATTACK_PERCENT"], "the section's stats_short");
            Assert.AreEqual("Optional.", model.StatGoals[1].Note.En, "markup stripped");
            Assert.AreEqual("", model.StatGoals[1].Note.Pl);

            // Without stats_short the label as the catalog writes it, exactly as the site shows it.
            var older = Load(GenshinJson(goals).Replace(@"""stats_short"": { ""FIGHT_PROP_ATTACK_PERCENT"": ""ATK%"" },", ""));
            Assert.AreEqual(0, older.StatsShort.Count);
            Assert.AreEqual("ATK Percentage", CharacterOverviewBuilder.Build(older.Find(Chiori), older, null).StatGoals[0].Stat);

            // A short name that is not one ("ATK% <b>") is ignored, and so is one for a key the catalog lacks.
            var odd = Load(GenshinJson(goals).Replace(@"""stats_short"": { ""FIGHT_PROP_ATTACK_PERCENT"": ""ATK%"" },",
                @"""stats_short"": { ""FIGHT_PROP_ATTACK_PERCENT"": ""ATK% <b>"", ""FIGHT_PROP_NOPE"": ""NOPE%"" },"));
            Assert.AreEqual(0, odd.StatsShort.Count);
        }

        [TestMethod]
        public void Builder_StatsShortNamesWhatTheFallbackCannot()
        {
            // ZZZ writes its percent stats "Percent ATK": only the section's stats_short makes that "ATK%".
            string json = @"{ ""schema"": 1, ""game"": ""zzz"", ""game_version"": ""3.3"",
  ""stats"": { ""12102"": ""Percent ATK"", ""12103"": ""ATK"", ""31402"": ""Anomaly Mastery"" },
  ""stats_short"": { ""12102"": ""ATK%"" },
  ""characters"": [ " + Character("1191", "Ellen", "Ice", @", ""skills"": [],
    ""ai"": { ""stat_goals"": [ { ""stat"": ""12102"", ""min"": 30, ""unit"": ""%"", ""open"": true },
                                { ""stat"": 31402, ""min"": 120, ""max"": 150 } ] }") + @" ] }";
            var zzz = Load(json, "zzz");
            var model = CharacterOverviewBuilder.Build(zzz.Find("1191"), zzz, null);
            CollectionAssert.AreEqual(new[] { "ATK%", "Anomaly Mastery" }, model.StatGoals.Select(g => g.Stat).ToArray(),
                "a numeric key reads as its digits and resolves like a string one");
            Assert.AreEqual("30%+", model.StatGoals[0].ValueFor("pl"));

            var older = Load(json.Replace(@"""stats_short"": { ""12102"": ""ATK%"" },", ""), "zzz");
            Assert.AreEqual(0, older.StatsShort.Count);
            Assert.AreEqual("Percent ATK", CharacterOverviewBuilder.Build(older.Find("1191"), older, null).StatGoals[0].Stat,
                "without stats_short the label as the catalog writes it");

            var trailing = Load(json.Replace(@"""12102"": ""ATK%"" }", @"""12102"": ""ATK%\n"" }"), "zzz");
            Assert.AreEqual(0, trailing.StatsShort.Count, "a short name ends at its % (the site's rule)");
        }

        [TestMethod]
        public void Load_NegativeZeroReadsAsZero()
        {
            var goal = Genshin(@"[ { ""stat"": ""FIGHT_PROP_ELEMENT_MASTERY"", ""min"": -0, ""max"": 100 } ]").Find(Chiori).Ai.StatGoals.Single();
            Assert.AreEqual("0" + Dash + "100", StatGoalFormat.Value(goal, "en"));
            Assert.AreEqual("0", StatGoalFormat.Number(-0.0, "pl"));
        }

        [TestMethod]
        public void Builder_GoalsShowWithOrWithoutABuild()
        {
            string json = CharacterCatalogTests.MinimalJson
                .Replace(@"""role"": { ""t"": ""Shields allies"", ""p"": [] },",
                    @"""role"": { ""t"": ""Shields allies"", ""p"": [] },
                      ""ai"": { ""stat_goals"": [ { ""stat"": ""CriticalChanceBase"", ""min"": 30, ""unit"": ""%"", ""open"": true } ] },")
                .Replace(@"""role"": null, ""skills"": [], ""build"": null }",
                    @"""role"": null, ""skills"": [], ""build"": null,
                      ""ai"": { ""stat_goals"": [ { ""stat"": ""SpeedDelta"", ""min"": 134, ""max"": 145 } ] } }");
            var catalog = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "starrail");

            var march = CharacterOverviewBuilder.Build(catalog.Find("1001"), catalog, null);
            Assert.IsTrue(march.HasOfficialBuild);
            Assert.AreEqual("30%+", march.StatGoals.Single().ValueFor("pl"));

            var dan = CharacterOverviewBuilder.Build(catalog.Find("1002"), catalog, null);
            Assert.IsFalse(dan.HasBuild);
            Assert.AreEqual("SPD", dan.StatGoals.Single().Stat);
            Assert.AreEqual("134" + Dash + "145", dan.StatGoals.Single().ValueFor("en"));
        }

        [TestMethod]
        public void Builder_NoGoalsInTheData_NoGoals()
        {
            var old = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(CharacterCatalogTests.MinimalJson)), "starrail");
            Assert.IsFalse(CharacterOverviewBuilder.Build(old.Find("1001"), old, null).HasStatGoals);
            var genshin = Genshin();
            Assert.IsFalse(CharacterOverviewBuilder.Build(genshin.Find(Navia), genshin, null).HasStatGoals, "an ai block without goals");
        }

        [TestMethod]
        public void Builder_TeamRanks_WithTheGamesLetter()
        {
            var catalog = Genshin();
            var model = CharacterOverviewBuilder.Build(catalog.Find(Chiori), catalog, null);

            var mono = model.Teams.Single(t => t.Source == "ai");
            var byName = mono.Members.ToDictionary(m => m.Name);
            Assert.AreEqual(0, byName["Navia"].Rank);
            Assert.AreEqual("C0", byName["Navia"].RankLabel, "0 shows: the team works at C0");
            Assert.AreEqual("C6", byName["Gorou"].RankLabel);
            Assert.IsNull(byName["Chiori"].Rank);
            Assert.AreEqual("", byName["Zhongli"].RankLabel, "no rank: no badge");
            Assert.IsTrue(model.Teams.Single(t => t.Source == "official").Members.All(m => m.RankLabel == "" && m.Rank == null));

            foreach (var (game, letter) in new[] { ("starrail", "E"), ("zzz", "M") })
            {
                var other = Load(GenshinJson(game: game), game);
                var member = CharacterOverviewBuilder.Build(other.Find(Chiori), other, null).Teams.Single(t => t.Source == "ai")
                    .Members.Single(m => m.Name == "Gorou");
                Assert.AreEqual(letter + "6", member.RankLabel, game);
            }
        }

        [TestMethod]
        public void Builder_TeamHost_CarriesThisCharactersRankInTheHostsTeam()
        {
            var catalog = Genshin();
            var model = CharacterOverviewBuilder.Build(catalog.Find(Chiori), catalog, null);

            var host = model.TeamHosts.Single(h => h.HostId == Navia);
            Assert.AreEqual(1, host.Rank);
            Assert.AreEqual("C1", host.RankLabel);
            Assert.AreEqual("DPS z drugiej linii", host.Role);
            Assert.IsNull(CharacterOverviewBuilder.Build(catalog.Find(Zhongli), catalog, null).TeamHosts.Single(h => h.HostId == Navia).Rank,
                "Zhongli has no rank in Navia's team");
        }

        [TestMethod]
        public void RankLabel_LettersPerGame_AndOutOfRange()
        {
            Assert.AreEqual("C1", SkillMarkers.RankLabel("genshin", 1));
            Assert.AreEqual("E0", SkillMarkers.RankLabel("StarRail", 0));
            Assert.AreEqual("M6", SkillMarkers.RankLabel("ZZZ", 6));
            Assert.AreEqual("", SkillMarkers.RankLabel("genshin", 7));
            Assert.AreEqual("", SkillMarkers.RankLabel("genshin", -1));
            Assert.AreEqual("", SkillMarkers.RankLabel("", 1), "a game without a rank letter shows no badge");
            Assert.AreEqual("", SkillMarkers.RankLabel(null, 1));
        }

        // ── view model ──────────────────────────────────────────────────────

        private static CharacterOverviewViewModel ChioriPage()
        {
            var catalog = Genshin();
            return new CharacterOverviewViewModel(CharacterOverviewBuilder.Build(catalog.Find(Chiori), catalog, null));
        }

        [TestMethod]
        public void ViewModel_GoalRows_FollowTheInterfaceLanguage()
        {
            var vm = ChioriPage();
            Assert.IsTrue(vm.HasStatGoals);
            // Polish until the view reads the strings in use (Char_SiteLocale).
            var def = vm.StatGoals[2];
            Assert.AreEqual("DEF", def.Stat);
            Assert.AreEqual("2" + Thin + "500" + Dash + "3" + Thin + "000+", def.Value);
            Assert.AreEqual("Fluttering Hasode (E) liczy obrażenia z DEF i ATK.", def.Note);
            Assert.IsTrue(vm.StatGoals[0].IsFirst);
            Assert.IsFalse(vm.StatGoals[1].IsFirst);
            Assert.IsFalse(vm.StatGoals[1].HasNote);

            var changed = new List<string>();
            vm.PropertyChanged += (s, e) => changed.Add(e.PropertyName);
            vm.SetSiteLanguage("en");
            CollectionAssert.Contains(changed, nameof(CharacterOverviewViewModel.StatGoals));
            Assert.AreEqual("2,500" + Dash + "3,000+", vm.StatGoals[2].Value);
            Assert.AreEqual("Fluttering Hasode (E) scales with DEF and ATK.", vm.StatGoals[2].Note);
            Assert.AreEqual("CRIT Rate: 60" + Dash + "70%. Raise it before CRIT DMG.", vm.StatGoals[0].AutomationName);
            Assert.AreEqual("CRIT DMG: 120%+", vm.StatGoals[1].AutomationName);
        }

        [TestMethod]
        public void Note_InterfaceLanguageFirst_TheOtherAsTheFallback()
        {
            var both = new AiLocalized { En = "en", Pl = "pl" };
            Assert.AreEqual("pl", both.PickFor("pl"));
            Assert.AreEqual("pl", both.PickFor("pl-PL"));
            Assert.AreEqual("en", both.PickFor("en"));
            Assert.AreEqual("en", both.PickFor(null));
            Assert.AreEqual("en", new AiLocalized { En = "en" }.PickFor("pl"));
            Assert.AreEqual("pl", new AiLocalized { Pl = "pl" }.PickFor("en"));
            Assert.AreEqual("", AiLocalized.Empty.PickFor("pl"));
        }

        [TestMethod]
        public void ViewModel_BuildSection_ShowsForGoalsAlone()
        {
            var vm = new CharacterOverviewViewModel(new CharacterOverviewModel
            {
                Name = "Dan Heng", Game = "starrail",
                StatGoals = new[] { new OverviewStatGoal { Stat = "SPD", Min = 134, Open = true } },
            });
            Assert.IsFalse(vm.HasBuild);
            Assert.IsTrue(vm.HasBuildSection);
            CollectionAssert.Contains(vm.Sections.Select(s => s.Key).ToList(), "Build");
            Assert.AreEqual("134+", vm.StatGoals.Single().Value);

            var none = new CharacterOverviewViewModel(new CharacterOverviewModel { Name = "Dan Heng", Game = "starrail" });
            Assert.IsFalse(none.HasStatGoals);
            Assert.IsFalse(none.HasBuildSection, "no goals, no build, no tips: no build section");
        }

        [TestMethod]
        public void ViewModel_AGoalWithNothingToWrite_IsLeftOut()
        {
            var vm = new CharacterOverviewViewModel(new CharacterOverviewModel
            {
                Name = "Skirk", Game = "genshin",
                StatGoals = new[]
                {
                    new OverviewStatGoal { Stat = "CRIT Rate" },
                    new OverviewStatGoal { Stat = "", Min = 5 },
                    null,
                    new OverviewStatGoal { Stat = "Energy Recharge", Min = 120, Unit = "%" },
                },
            });
            Assert.AreEqual("Energy Recharge", vm.StatGoals.Single().Stat);
            Assert.IsTrue(vm.StatGoals[0].IsFirst, "the first row shown has no rule above it");
        }

        [TestMethod]
        public void ViewModel_RankBadges_OnMembersAndOnTheHostRow()
        {
            var vm = ChioriPage();
            var mono = vm.Teams.Single(t => t.IsAi);
            Assert.AreEqual("C0", mono.Members.Single(m => m.Name == "Navia").RankLabel);
            Assert.IsTrue(mono.Members.Single(m => m.Name == "Gorou").HasRank);
            Assert.IsFalse(mono.Members.Single(m => m.Name == "Zhongli").HasRank);
            Assert.IsTrue(vm.Teams.Single(t => t.IsOfficial).Members.All(m => !m.HasRank), "the game's own squads carry none");

            var host = vm.TeamHosts.Single();
            Assert.AreEqual("Navia", host.HostName);
            Assert.AreEqual("C1", host.RankLabel);
            Assert.IsTrue(host.HasRoleLine);
            Assert.AreEqual("Navia, C1", host.AutomationName, "screen readers hear the rank with the host");
            Assert.AreEqual("B", new TeamHostRow(new OverviewTeamHost { HostName = "B" }).AutomationName);
            Assert.IsTrue(new TeamHostRow(new OverviewTeamHost { HostName = "A", RankLabel = "C2" }).HasRoleLine, "a rank alone still gets the line");
            Assert.IsFalse(new TeamHostRow(new OverviewTeamHost { HostName = "B" }).HasRoleLine);
        }
    }
}
