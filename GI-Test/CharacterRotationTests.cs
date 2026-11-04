using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Team rotations (the updater's teams[].rotations, session 54 round 3): tolerant loading, the builder (roster check,
    /// E and Q as the member's skill and icon, combo footnotes), the notation shared with the site, the chip rows and the
    /// icon fill. Ids, names and skills are the game's own (7.1 data).
    /// </summary>
    [TestClass]
    public class CharacterRotationTests
    {
        internal const string Varka = "10000128", Durin = "10000123", Prune = "10000132", Nicole = "10000131";
        internal const string Chiori = "10000094", Itto = "10000057", Albedo = "10000038", Xilonen = "10000103";

        internal const string VarkaCombo = "Skill > N5 > Skill / Charged Attack > N5 > Skill / Charged Attack";

        /// <summary>
        /// The owner's example, KQM's "Sample Rotation (Prune): Durin 2[E]Q > Prune EQ > Nicole EQ > Varka E combo", then a
        /// second rotation without a variant that uses every text chip (N2, CA twice, D, P).
        /// </summary>
        internal const string VarkaRotations = """
            [
              { "variant": "Prune",
                "steps": [ { "member": "10000123", "actions": [ { "a": "E", "n": 2 }, { "a": "Q" } ] },
                           { "member": "10000132", "actions": [ { "a": "E" }, { "a": "Q" } ] },
                           { "member": "10000131", "actions": [ { "a": "E" }, { "a": "Q" } ] },
                           { "member": "10000128", "actions": [ { "a": "E" }, { "a": "combo" } ] } ],
                "combo": { "10000128": "Skill > N5 > Skill / Charged Attack > N5 > Skill / Charged Attack" },
                "source": "https://keqingmains.com/q/varka-quickguide/" },
              { "variant": null,
                "steps": [ { "member": "10000131", "actions": [ { "a": "E" }, { "a": "Q" } ] },
                           { "member": "10000123", "actions": [ { "a": "Q" }, { "a": "E" } ] },
                           { "member": "10000128", "actions": [ { "a": "E" }, { "a": "N", "n": 2 }, { "a": "CA", "n": 2 }, { "a": "D" },
                                                                { "a": "P" }, { "a": "combo" } ] } ],
                "combo": { "10000128": "Skill > N5 > Skill / Charged Attack > N5 > Skill / Charged Attack" } }
            ]
            """;

        /// <summary>Chiori's Geo team, whose four members' skill icons the app's cache usually holds (the render's icon sample).</summary>
        internal const string GeoRotations = """
            [
              { "steps": [ { "member": "10000038", "actions": [ { "a": "E" } ] },
                           { "member": "10000094", "actions": [ { "a": "E" }, { "a": "Q" } ] },
                           { "member": "10000103", "actions": [ { "a": "E" }, { "a": "N", "n": 2 }, { "a": "Q" } ] },
                           { "member": "10000057", "actions": [ { "a": "Q" }, { "a": "E" }, { "a": "combo" } ] } ],
                "combo": { "10000057": "N2 > CA > N2 > CA > Arataki Kesagiri Final Slash" } },
              { "steps": [ { "member": "10000094", "actions": [ { "a": "E", "n": 2 } ] },
                           { "member": "10000103", "actions": [ { "a": "E" }, { "a": "Q" } ] },
                           { "member": "10000057", "actions": [ { "a": "E" }, { "a": "Q" }, { "a": "N", "n": 2 }, { "a": "CA", "n": 3 } ] } ] }
            ]
            """;

        private static JsonObject Character(string id, string name, string sprite, string element, string weapon, int rarity,
                                            string basic, string skill, string burst) => new JsonObject
        {
            ["id"] = id, ["name"] = name, ["icon"] = "UI_AvatarIcon_" + sprite, ["element"] = element,
            ["weapon_type"] = weapon, ["rarity"] = rarity,
            ["skills"] = new JsonArray(
                new JsonObject { ["kind"] = "basic", ["name"] = basic, ["icon"] = "Skill_A_01" },
                new JsonObject { ["kind"] = "skill", ["name"] = skill, ["icon"] = "Skill_S_" + sprite + "_01" },
                new JsonObject { ["kind"] = "ultimate", ["name"] = burst, ["icon"] = "Skill_E_" + sprite + "_01" }),
        };

        internal static JsonObject Team(string en, string pl, string[] members, string rotations, string whyEn = null, string whyPl = null)
        {
            var team = new JsonObject
            {
                ["name"] = new JsonObject { ["en"] = en, ["pl"] = pl },
                ["source"] = "ai",
                ["members"] = new JsonArray(members.Select(m => (JsonNode)m).ToArray()),
            };
            if (whyEn != null) team["why"] = new JsonObject { ["en"] = whyEn, ["pl"] = whyPl };
            if (rotations != null) team["rotations"] = JsonNode.Parse(rotations);
            return team;
        }

        /// <summary>Varka's team with <paramref name="varkaRotations"/>, Chiori's Geo team with <paramref name="geoRotations"/>.</summary>
        internal static string GenshinJson(string varkaRotations = VarkaRotations, string geoRotations = GeoRotations)
        {
            var varka = Character(Varka, "Varka", "Varka", "Anemo", "Claymore", 5,
                "Favonius Bladework: Dancing Radiance", "Windbound Execution", "Northwind Avatar");
            varka["ai"] = new JsonObject
            {
                ["model"] = "m",
                ["teams"] = new JsonArray(Team("Hexerei Swirl", "Hexerei ze Swirl", new[] { Varka, Durin, Prune, Nicole }, varkaRotations,
                    "Durin and Nicole feed Pyro to Varka's Swirls; Prune keeps the Anemo buffs up.",
                    "Durin i Nicole dokładają Pyro do Swirl Varki, a Prune podtrzymuje buffy Anemo.")),
            };
            var chiori = Character(Chiori, "Chiori", "Chiori", "Geo", "Sword", 5, "Weaving Blade", "Fluttering Hasode", "Hiyoku: Twin Blades");
            chiori["ai"] = new JsonObject
            {
                ["model"] = "m",
                ["teams"] = new JsonArray(Team("Mono Geo", "Czyste Geo", new[] { Chiori, Itto, Albedo, Xilonen }, geoRotations,
                    "Three Geo allies power Itto's charged slashes; Xilonen heals and lowers resistances.",
                    "Trzy postacie Geo napędzają ciężkie cięcia Itto, a Xilonen leczy i obniża odporności.")),
            };
            var root = new JsonObject
            {
                ["schema"] = 1, ["game"] = "genshin", ["game_version"] = "7.2.0",
                ["icon_base"] = new JsonObject { ["character"] = "https://enka.network/ui/", ["skill"] = "https://enka.network/ui/" },
                ["icon_ext"] = new JsonObject { ["character"] = ".png", ["skill"] = ".png" },
                ["characters"] = new JsonArray(
                    varka,
                    Character(Durin, "Durin", "Durin", "Pyro", "Sword", 5,
                        "Radiant Wingslash", "Binary Form: Convergence and Division", "Principle of Purity: As the Light Shifts"),
                    Character(Prune, "Prune", "Prune", "Anemo", "Catalyst", 4,
                        "Badaboom! Hexbuster Hammer", "Ring-A-Ding-Ding! Hexhunter Chime", "The Bell Tolls! The Hunt Is On!"),
                    Character(Nicole, "Nicole", "Nicole", "Pyro", "Catalyst", 5,
                        "Allegoria", "Revelation: Uncreated Light", "Revelation: Ladder of Divine Ascent"),
                    chiori,
                    Character(Itto, "Arataki Itto", "Itto", "Geo", "Claymore", 5,
                        "Fight Club Legend", "Masatsu Zetsugi: Akaushi Burst!", "Royal Descent: Behold, Itto the Evil!"),
                    Character(Albedo, "Albedo", "Albedo", "Geo", "Sword", 5,
                        "Favonius Bladework - Weiss", "Abiogenesis: Solar Isotoma", "Rite of Progeniture: Tectonic Tide"),
                    Character(Xilonen, "Xilonen", "Xilonen", "Geo", "Sword", 5, "Ehecatl's Roar", "Yohual's Scratch", "Ocelotlicue Point!")),
            };
            return root.ToJsonString();
        }

        internal static CharacterCatalog Load(string json, string game = "genshin") =>
            CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), game);

        internal static CharacterCatalog Genshin(string varkaRotations = VarkaRotations) => Load(GenshinJson(varkaRotations));

        private static IReadOnlyList<TeamRotation> VarkaTeamRotations(string rotations) =>
            Genshin(rotations).Find(Varka).Ai.Teams.Single().Rotations;

        private static CharacterOverviewModel VarkaPage(string rotations = VarkaRotations)
        {
            var catalog = Genshin(rotations);
            return CharacterOverviewBuilder.Build(catalog.Find(Varka), catalog, null);
        }

        private static string Step(string member, string actions) => "{ \"member\": \"" + member + "\", \"actions\": " + actions + " }";

        private const string EQ = "[ { \"a\": \"E\" }, { \"a\": \"Q\" } ]";

        // ── loading ─────────────────────────────────────────────────────────

        [TestMethod]
        public void Load_ReadsTheRotation_AsTheContractWritesIt()
        {
            var rotations = VarkaTeamRotations(VarkaRotations);

            Assert.AreEqual(2, rotations.Count);
            var prune = rotations[0];
            Assert.AreEqual("Prune", prune.Variant);
            CollectionAssert.AreEqual(new[] { Durin, Prune, Nicole, Varka }, prune.Steps.Select(s => s.Member).ToArray());
            CollectionAssert.AreEqual(new[] { "E", "Q" }, prune.Steps[0].Actions.Select(a => a.Action).ToArray());
            Assert.AreEqual(2, prune.Steps[0].Actions[0].Count, "n: 2");
            Assert.AreEqual(1, prune.Steps[0].Actions[1].Count, "no n is one");
            CollectionAssert.AreEqual(new[] { "E", "combo" }, prune.Steps[3].Actions.Select(a => a.Action).ToArray());
            Assert.AreEqual(VarkaCombo, prune.Combo[Varka]);

            var second = rotations[1];
            Assert.AreEqual("", second.Variant, "variant: null is no variant");
            var varka = second.Steps[2].Actions;
            CollectionAssert.AreEqual(new[] { "E", "N", "CA", "D", "P", "combo" }, varka.Select(a => a.Action).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2, 2, 1, 1, 1 }, varka.Select(a => a.Count).ToArray());
        }

        [TestMethod]
        public void Load_BadRotationsAreSkipped_TheRestKept()
        {
            string good = "{ \"variant\": \"Prune\", \"steps\": [ " + Step(Durin, EQ) + ", " + Step(Prune, EQ) + " ] }";
            string rotations = "[ " + string.Join(", ",
                "\"not an object\"", "7", "null",
                "{ }",
                "{ \"steps\": [] }",
                "{ \"steps\": {} }",
                "{ \"steps\": [ \"Durin\" ] }",
                "{ \"steps\": [ " + Step("10000999", EQ) + " ] }",                                    // not a member of the team
                "{ \"steps\": [ { \"actions\": " + EQ + " } ] }",                                        // no member
                "{ \"steps\": [ " + Step(Durin, "[]") + " ] }",                                          // no actions
                "{ \"steps\": [ " + Step(Durin, "[ { \"a\": \"e\" } ]") + " ] }",                       // tokens are exact
                "{ \"steps\": [ " + Step(Durin, "[ { \"a\": \"Hold E\" } ]") + " ] }",                  // outside the grammar
                "{ \"steps\": [ " + Step(Durin, "[ { \"a\": \"Combo\" } ]") + " ] }",
                "{ \"steps\": [ " + Step(Durin, "[ \"E\" ]") + " ] }",
                "{ \"steps\": [ " + Step(Durin, "[ { \"a\": \"E\", \"n\": 0 } ]") + " ] }",
                "{ \"steps\": [ " + Step(Durin, "[ { \"a\": \"E\", \"n\": 1.5 } ]") + " ] }",
                "{ \"steps\": [ " + Step(Durin, "[ { \"a\": \"E\", \"n\": \"2\" } ]") + " ] }",
                "{ \"steps\": [ " + Step(Durin, "[ { \"a\": \"N\", \"n\": 21 } ]") + " ] }",            // over the cap
                "{ \"variant\": 3, \"steps\": [ " + Step(Durin, EQ) + " ] }",
                "{ \"variant\": \"" + new string('x', 65) + "\", \"steps\": [ " + Step(Durin, EQ) + " ] }",
                "{ \"steps\": [ " + Step(Durin, EQ) + ", " + Step("Varka", EQ) + " ] }",                // a name, not an id: the whole rotation goes
                good) + " ]";

            var kept = VarkaTeamRotations(rotations);

            Assert.AreEqual(1, kept.Count, "only the good rotation survives");
            Assert.AreEqual("Prune", kept[0].Variant);
            var team = Genshin(rotations).Find(Varka).Ai.Teams.Single();
            Assert.AreEqual(4, team.Members.Count, "the team itself is untouched");
            Assert.AreEqual("Hexerei ze Swirl", team.Name.Pl);
        }

        [TestMethod]
        public void Load_TheCapsHoldExactlyAtTheirLimit()
        {
            string Actions(int n) => "[ " + string.Join(", ", Enumerable.Repeat("{ \"a\": \"E\" }", n)) + " ]";
            string Rotation(int steps, int actions, int count) => "{ \"steps\": [ " +
                string.Join(", ", Enumerable.Repeat(Step(Durin, Actions(actions).Replace("\"E\" }", "\"E\", \"n\": " + count + " }")), steps)) + " ] }";

            var atLimit = VarkaTeamRotations("[ " + Rotation(CharacterCatalog.MaxRotationSteps, CharacterCatalog.MaxStepActions, CharacterCatalog.MaxActionCount) + " ]");
            Assert.AreEqual(1, atLimit.Count);
            Assert.AreEqual(CharacterCatalog.MaxRotationSteps, atLimit[0].Steps.Count);

            Assert.AreEqual(0, VarkaTeamRotations("[ " + Rotation(CharacterCatalog.MaxRotationSteps + 1, 1, 1) + " ]").Count, "too many steps");
            Assert.AreEqual(0, VarkaTeamRotations("[ " + Rotation(1, CharacterCatalog.MaxStepActions + 1, 1) + " ]").Count, "too many actions");
            Assert.AreEqual(0, VarkaTeamRotations("[ " + Rotation(1, 1, CharacterCatalog.MaxActionCount + 1) + " ]").Count, "too many repeats");
        }

        [TestMethod]
        public void Load_KeepsAtMostTwoRotations_TheFirstGoodOnes()
        {
            string R(string variant) => "{ \"variant\": \"" + variant + "\", \"steps\": [ " + Step(Durin, EQ) + " ] }";
            var kept = VarkaTeamRotations("[ " + R("A") + ", { \"steps\": [] }, " + R("B") + ", " + R("C") + " ]");
            CollectionAssert.AreEqual(new[] { "A", "B" }, kept.Select(r => r.Variant).ToArray());
        }

        [TestMethod]
        public void Load_OldBundleWithoutRotations_LoadsUnchanged()
        {
            foreach (string rotations in new[] { null, "null", "{}", "\"Durin E > Varka Q\"", "[]" })
            {
                var catalog = rotations == null ? Load(GenshinJson(null, null)) : Load(GenshinJson(rotations, rotations));
                var team = catalog.Find(Varka).Ai.Teams.Single();
                Assert.AreEqual(0, team.Rotations.Count, rotations ?? "absent");
                Assert.AreEqual(4, team.Members.Count);
                Assert.AreEqual("Durin and Nicole feed Pyro to Varka's Swirls; Prune keeps the Anemo buffs up.", team.Why.En);

                var model = CharacterOverviewBuilder.Build(catalog.Find(Varka), catalog, null);
                Assert.AreEqual(0, model.Teams.Single().Rotations.Count);
                Assert.IsFalse(new CharacterOverviewViewModel(model).Teams.Single().HasRotations, "no strip without data");
            }
        }

        [TestMethod]
        public void Load_ComboPairs_MembersWithTextOnly()
        {
            string rotation = "[ { \"steps\": [ " + Step(Varka, "[ { \"a\": \"combo\" } ]") + ", " + Step(Durin, "[ { \"a\": \"combo\" } ]") + " ], " +
                              "\"combo\": { \"" + Varka + "\": \"  Skill  >  N5 \", \"" + Durin + "\": \"\", \"10000999\": \"Not in the team\", " +
                              "\"" + Prune + "\": 7, \"" + Nicole + "\": \"" + new string('x', CharacterCatalog.MaxComboLength + 1) + "\" } } ]";
            var kept = VarkaTeamRotations(rotation).Single();

            Assert.AreEqual(1, kept.Combo.Count, string.Join(", ", kept.Combo.Keys));
            Assert.AreEqual("Skill > N5", kept.Combo[Varka], "whitespace runs collapse");
            Assert.AreEqual(2, kept.Steps.Count, "a bad combo drops alone, never the rotation");
        }

        [TestMethod]
        public void Load_AStepMemberGivenAsANumber_IsTolerated()
        {
            string rotation = "[ { \"steps\": [ { \"member\": " + Durin + ", \"actions\": " + EQ + " } ] } ]";
            Assert.AreEqual(Durin, VarkaTeamRotations(rotation).Single().Steps.Single().Member);
        }

        [TestMethod]
        public void Load_NullCountIsOne_OneIsAccepted()
        {
            string rotation = "[ { \"steps\": [ " + Step(Durin, "[ { \"a\": \"E\", \"n\": null }, { \"a\": \"Q\", \"n\": 1 } ]") + " ] } ]";
            CollectionAssert.AreEqual(new[] { 1, 1 }, VarkaTeamRotations(rotation).Single().Steps.Single().Actions.Select(a => a.Count).ToArray());
        }

        // ── builder ─────────────────────────────────────────────────────────

        [TestMethod]
        public void Builder_StepsCarryNamesPortraitsAndTheMembersSkills()
        {
            var rotation = VarkaPage().Teams.Single().Rotations[0];

            Assert.AreEqual("Prune", rotation.Variant);
            CollectionAssert.AreEqual(new[] { "Durin", "Prune", "Nicole", "Varka" }, rotation.Steps.Select(s => s.MemberName).ToArray());
            Assert.AreEqual("https://enka.network/ui/UI_AvatarIcon_Durin.png", rotation.Steps[0].IconUrl, "the card's portrait");
            Assert.IsNull(rotation.Steps[0].IconPath, "paths come from the cache, never the builder");
            CollectionAssert.AreEqual(new[] { false, false, false, true }, rotation.Steps.Select(s => s.IsSelf).ToArray(), "Varka's own page");

            var durinE = rotation.Steps[0].Actions[0];
            Assert.AreEqual("Binary Form: Convergence and Division", durinE.SkillName, "the game's name");
            Assert.AreEqual("https://enka.network/ui/Skill_S_Durin_01.png", durinE.IconUrl);
            Assert.AreEqual(2, durinE.Count);
            Assert.AreEqual("Elemental Skill: Binary Form: Convergence and Division ×2", durinE.Description, "the site's wording");
            var durinQ = rotation.Steps[0].Actions[1];
            Assert.AreEqual("https://enka.network/ui/Skill_E_Durin_01.png", durinQ.IconUrl);
            Assert.AreEqual("Elemental Burst: Principle of Purity: As the Light Shifts", durinQ.Description);

            var combo = rotation.Steps[3].Actions[1];
            Assert.AreEqual("combo", combo.Action);
            Assert.AreEqual("*", combo.Marker, "the footnote spells it out");
            Assert.AreEqual("Combo *", combo.Description);
            Assert.AreEqual("", combo.SkillName);
            Assert.IsNull(combo.IconUrl);
            Assert.AreEqual("*", rotation.Combos.Single().Marker);
            Assert.AreEqual(VarkaCombo, rotation.Combos.Single().Text);
        }

        [TestMethod]
        public void Builder_NDrawsTheNormalAttack_TextInputsCarryNoSkill()
        {
            var varka = VarkaPage().Teams.Single().Rotations[1].Steps[2].Actions;

            Assert.AreEqual("Favonius Bladework: Dancing Radiance", varka[1].SkillName, "N stands for the normal attack");
            Assert.AreEqual("https://enka.network/ui/Skill_A_01.png", varka[1].IconUrl);
            Assert.AreEqual("Normal Attack: Favonius Bladework: Dancing Radiance ×2", varka[1].Description);
            foreach (var a in varka.Where(a => a.Action is "CA" or "P" or "D" or "combo"))
            {
                Assert.AreEqual("", a.SkillName, a.Action);
                Assert.IsNull(a.IconUrl, a.Action);
            }
            CollectionAssert.AreEqual(new[] { "Elemental Skill: Windbound Execution", "Charged Attack ×2", "Dash", "Plunging Attack", "Combo *" },
                varka.Where(a => a.Action != "N").Select(a => a.Description).ToArray());
        }

        [TestMethod]
        public void Builder_FootnoteMarkers_PerMemberInStepOrder()
        {
            string two = "[ { \"steps\": [ " + Step(Durin, "[ { \"a\": \"combo\" } ]") + ", " + Step(Varka, "[ { \"a\": \"E\" }, { \"a\": \"combo\" } ]") +
                         ", " + Step(Durin, "[ { \"a\": \"combo\" } ]") + ", " + Step(Prune, "[ { \"a\": \"combo\" } ]") + " ], " +
                         "\"combo\": { \"" + Varka + "\": \"Skill > N5\", \"" + Durin + "\": \"N2 > CA\", \"" + Nicole + "\": \"No combo input\", " +
                         "\"" + Prune + "\": \"<color=#FFD780FF> </color>\" } } ]";
            var rotation = VarkaPage(two).Teams.Single().Rotations.Single();

            CollectionAssert.AreEqual(new[] { "*", "**" }, rotation.Combos.Select(c => c.Marker).ToArray());
            CollectionAssert.AreEqual(new[] { "Durin", "Varka" }, rotation.Combos.Select(c => c.MemberName).ToArray(), "step order, once per member");
            CollectionAssert.AreEqual(new[] { "N2 > CA", "Skill > N5" }, rotation.Combos.Select(c => c.Text).ToArray());
            var markers = rotation.Steps.SelectMany(s => s.Actions).Where(a => a.Action == "combo").Select(a => a.Marker).ToArray();
            CollectionAssert.AreEqual(new[] { "*", "**", "*", "" }, markers, "Durin twice with one note; Prune's note is empty once markup goes");

            string none = "[ { \"steps\": [ " + Step(Varka, "[ { \"a\": \"combo\" } ]") + " ] } ]";
            var bare = VarkaPage(none).Teams.Single().Rotations.Single();
            Assert.AreEqual(0, bare.Combos.Count, "a combo the page does not spell out has no footnote");
            Assert.AreEqual("", bare.Steps.Single().Actions.Single().Marker);
        }

        [TestMethod]
        public void Builder_AMemberOffTheRoster_LeavesTheRotationOut()
        {
            // The team lists a member the bundle has no record of: its card drops the member, the strip drops the rotation.
            var root = JsonNode.Parse(GenshinJson()).AsObject();
            var team = root["characters"][0]["ai"]["teams"][0].AsObject();
            team["members"].AsArray().Add("10000999");
            team["rotations"] = JsonNode.Parse("[ { \"steps\": [ " + Step(Durin, EQ) + ", " + Step("10000999", EQ) + " ] }, " +
                                               "{ \"variant\": \"Prune\", \"steps\": [ " + Step(Prune, EQ) + " ] } ]");
            var catalog = Load(root.ToJsonString());
            Assert.AreEqual(2, catalog.Find(Varka).Ai.Teams.Single().Rotations.Count, "both load: the id is a member of the team");

            var model = CharacterOverviewBuilder.Build(catalog.Find(Varka), catalog, null);
            Assert.AreEqual("Prune", model.Teams.Single().Rotations.Single().Variant);
        }

        [TestMethod]
        public void Builder_StarRail_TakesTheDefaultKit()
        {
            const string kafka = "1005", swan = "1307";
            string json = """
                { "schema": 1, "game": "starrail",
                  "icon_base": { "character": "https://img.example/avatar/", "skill": "https://img.example/skill/" },
                  "icon_ext": { "character": ".png", "skill": ".png" },
                  "characters": [
                    { "id": "1005", "name": "Kafka", "icon": "1005", "element": "Lightning", "default_variant": "enhanced",
                      "skills": [ { "kind": "skill", "variant": "original", "name": "Caressing Moonlight", "icon": "1005_skill" },
                                  { "kind": "skill", "variant": "enhanced", "name": "Caressing Moonlight", "icon": "1005_skill_enhanced" },
                                  { "kind": "ultimate", "variant": "original", "name": "Twilight Trill", "icon": "1005_ultimate" },
                                  { "kind": "ultimate", "variant": "enhanced", "name": "Twilight Trill", "icon": "1005_ultimate_enhanced" } ],
                      "ai": { "teams": [ { "name": "DoT", "source": "ai", "members": [ "1005", "1307" ],
                        "rotations": [ { "steps": [ { "member": "1307", "actions": [ { "a": "E" }, { "a": "N" } ] },
                                                    { "member": "1005", "actions": [ { "a": "Q" }, { "a": "E" } ] } ] } ] } ] } },
                    { "id": "1307", "name": "Black Swan", "icon": "1307", "element": "Wind",
                      "skills": [ { "kind": "basic", "name": "Percipience, Shrouded Divinity", "icon": "1307_basic" },
                                  { "kind": "skill", "name": "Decadence, False Twilight", "icon": "1307_skill" } ] }
                  ] }
                """;
            var catalog = Load(json, "starrail");
            var steps = CharacterOverviewBuilder.Build(catalog.Find(kafka), catalog, null).Teams.Single().Rotations.Single().Steps;

            Assert.AreEqual("Skill: Decadence, False Twilight", steps[0].Actions[0].Description, "Star Rail's own words");
            Assert.AreEqual("https://img.example/skill/1307_skill.png", steps[0].Actions[0].IconUrl);
            Assert.AreEqual("Basic ATK: Percipience, Shrouded Divinity", steps[0].Actions[1].Description);
            Assert.AreEqual("https://img.example/skill/1005_ultimate_enhanced.png", steps[1].Actions[0].IconUrl, "the default kit");
            Assert.AreEqual("Ultimate: Twilight Trill", steps[1].Actions[0].Description);
            Assert.AreEqual("https://img.example/skill/1005_skill_enhanced.png", steps[1].Actions[1].IconUrl);
            Assert.AreEqual("https://img.example/avatar/" + swan + ".png", steps[0].IconUrl);
            Assert.IsTrue(steps[1].IsSelf);
        }

        [TestMethod]
        public void Builder_Zzz_TextChips_TheChainNamedUltimateIsQ()
        {
            string json = """
                { "schema": 1, "game": "zzz",
                  "icon_base": { "character": "https://enka.network/ui/zzz/" }, "icon_ext": { "character": ".png" },
                  "characters": [
                    { "id": "1191", "name": "Ellen", "icon": "IconRoleCircle21", "element": "Ice",
                      "skills": [ { "kind": "special", "name": "EX Special Attack: Sharknami" },
                                  { "kind": "chain", "name": "Chain Attack: Avalanche" },
                                  { "kind": "chain", "name": "Ultimate: Endless Winter" } ],
                      "ai": { "teams": [ { "name": "Ice", "source": "ai", "members": [ "1191", "1141" ],
                        "rotations": [ { "steps": [ { "member": "1141", "actions": [ { "a": "E" }, { "a": "Q" } ] },
                                                    { "member": "1191", "actions": [ { "a": "Q" }, { "a": "E", "n": 2 }, { "a": "N", "n": 3 }, { "a": "D" } ] } ] } ] } ] } },
                    { "id": "1141", "name": "Lycaon", "icon": "IconRoleCircle18", "element": "Ice", "skills": [] }
                  ] }
                """;
            var catalog = Load(json, "zzz");
            var model = CharacterOverviewBuilder.Build(catalog.Find("1191"), catalog, null);
            var steps = model.Teams.Single().Rotations.Single().Steps;

            Assert.IsTrue(steps.SelectMany(s => s.Actions).All(a => a.IconUrl == null), "ZZZ names no skill icons");
            Assert.AreEqual("https://enka.network/ui/zzz/IconRoleCircle18.png", steps[0].IconUrl, "portraits still show");
            CollectionAssert.AreEqual(new[] { "Special Attack", "Ultimate" }, steps[0].Actions.Select(a => a.Description).ToArray());
            CollectionAssert.AreEqual(
                new[] { "Ultimate: Endless Winter", "Special Attack: EX Special Attack: Sharknami ×2", "Basic Attack ×3", "Dodge" },
                steps[1].Actions.Select(a => a.Description).ToArray(), "Q is the chain named Ultimate, not the Chain Attack");

            var chips = new CharacterOverviewViewModel(model).Teams.Single().Rotations.Single().Steps.SelectMany(s => s.Chips).ToList();
            Assert.IsTrue(chips.All(c => c.IsText), "every ZZZ input is a text chip");
            CollectionAssert.AreEqual(new[] { "E", "Q", "Q", "E ×2", "N3", "D" }, chips.Select(c => c.Text).ToArray());
        }

        [TestMethod]
        public void Builder_VariantShowsAsWritten_MarkupStripped()
        {
            string rotation = "[ { \"variant\": \"<color=#FFD780FF>Prune</color>\", \"steps\": [ " + Step(Prune, EQ) + " ] } ]";
            Assert.AreEqual("Prune", VarkaPage(rotation).Teams.Single().Rotations.Single().Variant);
        }

        // ── notation (identical on the site) and chips ──────────────────────

        [TestMethod]
        public void Notation_IconChipsBadgeTheirCount_TextChipsKeepIt()
        {
            Assert.IsTrue(new[] { "E", "Q", "N" }.All(a => RotationNotation.HasIconSlot(a, hasIcon: true)));
            Assert.IsFalse(new[] { "E", "Q", "N" }.Any(a => RotationNotation.HasIconSlot(a, hasIcon: false)), "no icon in the bundle: text");
            Assert.IsFalse(new[] { "CA", "P", "D", "combo" }.Any(a => RotationNotation.HasIconSlot(a, hasIcon: true)));

            Assert.AreEqual("N2", RotationNotation.Label("N", 2, hasIcon: false), "N folds its count without an icon");
            Assert.AreEqual("N", RotationNotation.Label("N", 1, hasIcon: false), "one hit reads N");
            Assert.AreEqual("N", RotationNotation.Label("N", 5, hasIcon: true), "with an icon the count is the badge");
            Assert.AreEqual("", RotationNotation.Count("N", 5, hasIcon: false), "never next to an N5");
            Assert.AreEqual("×5", RotationNotation.Count("N", 5, hasIcon: true));
            Assert.AreEqual("Combo", RotationNotation.Label("combo", 1, hasIcon: false));
            foreach (string token in new[] { "E", "Q", "CA", "P", "D" })
            {
                Assert.AreEqual(token, RotationNotation.Label(token, 3, hasIcon: false), token);
                Assert.AreEqual("×3", RotationNotation.Count(token, 3, hasIcon: false), token);
                Assert.AreEqual("", RotationNotation.Count(token, 1, hasIcon: true), token);
            }
            Assert.AreEqual("E ×2", RotationNotation.Text("E", 2, hasIcon: true));
            Assert.AreEqual("CA ×2", RotationNotation.Text("CA", 2, hasIcon: false));
            Assert.AreEqual("N2", RotationNotation.Text("N", 2, hasIcon: false));
            Assert.AreEqual("Combo*", RotationNotation.Text("combo", 1, hasIcon: false, "*"));
            Assert.AreEqual("Combo ×2**", RotationNotation.Text("combo", 2, hasIcon: false, "**"), "the count, then the marker, as the site");
            Assert.AreEqual('×', RotationNotation.Times, "the multiplication sign, not the letter x");
            Assert.AreEqual("*", RotationNotation.Marker(0));
            Assert.AreEqual("**", RotationNotation.Marker(1));

            Assert.IsTrue(RotationNotation.Actions.All(RotationNotation.IsAction));
            Assert.IsFalse(new[] { "e", "q", "Combo", "C", "NA", "", " E", null }.Any(RotationNotation.IsAction));
        }

        [TestMethod]
        public void Notation_TermsAndDescriptions_AsTheSite()
        {
            Assert.AreEqual("Elemental Skill", RotationNotation.Term("E", "genshin"));
            Assert.AreEqual("Skill", RotationNotation.Term("E", "starrail"));
            Assert.AreEqual("Special Attack", RotationNotation.Term("E", "zzz"));
            Assert.AreEqual("Basic ATK", RotationNotation.Term("N", "starrail"));
            Assert.AreEqual("Dodge", RotationNotation.Term("D", "zzz"));
            Assert.AreEqual("Dash", RotationNotation.Term("D", "genshin"));
            Assert.AreEqual("Charged Attack", RotationNotation.Term("CA", null));

            Assert.AreEqual("Elemental Skill: Passion Overload", RotationNotation.Describe("E", 1, "genshin", "Passion Overload", ""));
            Assert.AreEqual("Ultimate: Lingering Snow", RotationNotation.Describe("Q", 1, "zzz", "Ultimate: Lingering Snow", ""),
                "a name that starts with the term stays as it is");
            Assert.AreEqual("Charged Attack ×3", RotationNotation.Describe("CA", 3, "genshin", "", ""));
            Assert.AreEqual("Combo **", RotationNotation.Describe("combo", 1, "genshin", "", "**"));
        }

        [TestMethod]
        public void Notation_PolishZe_AsTheSitesPlWith()
        {
            foreach (string name in new[] { "Skirk", "Sparkle", "Szczepan", "Zwierzak", "Śnieżka" })
                Assert.IsTrue(RotationNotation.TakesPolishZe(name), name);
            foreach (string name in new[] { "Prune", "Shenhe", "Zhongli", "Sucrose", "Szymon", "Xilonen", "Sethos", "", null })
                Assert.IsFalse(RotationNotation.TakesPolishZe(name), name ?? "null");
        }

        [TestMethod]
        public void Chips_IconDiscsAndTextChips()
        {
            var e = new RotationChipRow(new OverviewRotationAction
            {
                Action = "E", Count = 2, SkillName = "Binary Form: Convergence and Division", IconUrl = "https://enka.network/ui/Skill_S_Durin_01.png",
                IconPath = @"C:\icons\durin-e.png", Description = "Elemental Skill: Binary Form: Convergence and Division ×2",
            });
            Assert.IsTrue(e.IsIcon);
            Assert.AreEqual("E", e.Label, "the letter, drawn until the icon is on disk");
            Assert.AreEqual("×2", e.Count);
            Assert.IsTrue(e.HasBadge, "a repeat on an icon chip is the corner badge");
            Assert.AreEqual("", e.InlineCount);
            Assert.AreEqual(@"C:\icons\durin-e.png", e.IconPath);
            Assert.AreEqual("E ×2", e.Text);
            Assert.AreEqual("Elemental Skill: Binary Form: Convergence and Division ×2", e.Description);

            var waiting = new RotationChipRow(new OverviewRotationAction { Action = "Q", IconUrl = "https://enka.network/ui/Skill_E_Durin_01.png" });
            Assert.IsTrue(waiting.IsIcon, "the icon is named but not on disk yet: the disc shows its letter");
            Assert.IsNull(waiting.IconPath);
            Assert.AreEqual("Q", waiting.Description, "no description in the data: the chip's text");

            var n = new RotationChipRow(new OverviewRotationAction { Action = "N", Count = 2 });
            Assert.IsTrue(n.IsText);
            Assert.AreEqual("N2", n.Label);
            Assert.IsFalse(n.HasBadge);
            Assert.AreEqual("", n.InlineCount, "the count is in the label");

            var normal = new RotationChipRow(new OverviewRotationAction { Action = "N", Count = 2, IconPath = @"C:\icons\normal.png" });
            Assert.IsTrue(normal.IsIcon, "a normal-attack icon on disk draws N");
            Assert.AreEqual("N", normal.Label);
            Assert.IsTrue(normal.HasBadge);

            var ca = new RotationChipRow(new OverviewRotationAction { Action = "CA", Count = 3, IconPath = @"C:\icons\ignored.png" });
            Assert.IsTrue(ca.IsText);
            Assert.IsNull(ca.IconPath, "text chips never draw an icon");
            Assert.AreEqual(" ×3", ca.InlineCount);
            Assert.AreEqual("CA ×3", ca.Text);

            var combo = new RotationChipRow(new OverviewRotationAction { Action = "combo", Marker = "*", Description = "Combo *" });
            Assert.AreEqual("Combo", combo.Label);
            Assert.AreEqual("*", combo.Marker);
            Assert.AreEqual("Combo*", combo.Text);
            Assert.AreEqual("Combo *", combo.Description);
        }

        [TestMethod]
        public void ViewModel_TheStripReadsAsTheGuideWroteIt()
        {
            var card = new CharacterOverviewViewModel(VarkaPage()).Teams.Single();
            Assert.IsTrue(card.HasRotations);
            Assert.AreEqual(2, card.Rotations.Count);

            var prune = card.Rotations[0];
            Assert.IsTrue(prune.HasVariant);
            Assert.AreEqual("Prune", prune.Variant);
            Assert.IsFalse(prune.VariantTakesZe, "z Prune");
            Assert.AreEqual("Durin: E ×2, Q > Prune: E, Q > Nicole: E, Q > Varka: E, Combo*", prune.Text);
            CollectionAssert.AreEqual(new[] { false, true, true, true }, prune.Steps.Select(s => s.ShowSeparator).ToArray(), "a > between turns");
            CollectionAssert.AreEqual(new[] { false, false, false, true }, prune.Steps.Select(s => s.IsSelf).ToArray());
            Assert.IsFalse(prune.Steps.Any(s => s.ShowName), "every member has a portrait");
            CollectionAssert.AreEqual(new[] { "* " + VarkaCombo }, prune.Footnotes.ToArray());
            Assert.AreEqual("D", prune.Steps[0].Initial);

            var second = card.Rotations[1];
            Assert.IsFalse(second.HasVariant);
            Assert.AreEqual("Nicole: E, Q > Durin: Q, E > Varka: E, N ×2, CA ×2, D, P, Combo*", second.Text);
            var varka = second.Steps[2].Chips;
            CollectionAssert.AreEqual(new[] { true, true, false, false, false, false }, varka.Select(c => c.IsIcon).ToArray());
            CollectionAssert.AreEqual(new[] { false, true, false, false, false, false }, varka.Select(c => c.HasBadge).ToArray());
            CollectionAssert.AreEqual(new[] { "E", "N", "CA", "D", "P", "Combo" }, varka.Select(c => c.Label).ToArray());
        }

        [TestMethod]
        public void ViewModel_FootnotesCarryTheirMarkers_NamesShowWithoutAPortrait()
        {
            var row = new RotationRow(new OverviewRotation
            {
                Variant = "Skirk",
                Steps = new[]
                {
                    new OverviewRotationStep { MemberName = "Varka", IconUrl = "https://enka.network/ui/UI_AvatarIcon_Varka.png",
                        Actions = new[] { new OverviewRotationAction { Action = "combo", Marker = "*" } } },
                    new OverviewRotationStep { MemberName = "Durin", Actions = new[] { new OverviewRotationAction { Action = "combo", Marker = "**" } } },
                },
                Combos = new[]
                {
                    new OverviewRotationCombo { Marker = "*", MemberName = "Varka", Text = "Skill > N5" },
                    new OverviewRotationCombo { Marker = "**", MemberName = "Durin", Text = "N2 > CA" },
                },
            });
            CollectionAssert.AreEqual(new[] { "* Skill > N5", "** N2 > CA" }, row.Footnotes.ToArray());
            Assert.IsTrue(row.VariantTakesZe, "ze Skirk");
            Assert.IsFalse(row.Steps[0].ShowName);
            Assert.IsTrue(row.Steps[1].ShowName, "no portrait in the bundle: the name follows the initial");
            Assert.IsFalse(new RotationRow(new OverviewRotation { Steps = new[] { new OverviewRotationStep() } }).HasFootnotes, "no combos, no footnotes");
        }

        [TestMethod]
        public void ViewModel_EmptyOrMissingRotations_ShowNothing()
        {
            var card = new TeamCardRow(new OverviewTeam
            {
                Name = "Team",
                Rotations = new[] { null, new OverviewRotation(), new OverviewRotation { Steps = null } },
            });
            Assert.IsFalse(card.HasRotations);
            Assert.IsFalse(new TeamCardRow(new OverviewTeam { Name = "Team", Rotations = null }).HasRotations);
        }

        // ── icons ───────────────────────────────────────────────────────────

        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        private sealed class Handler : HttpMessageHandler
        {
            public readonly List<string> Urls = new List<string>();
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            {
                lock (Urls) Urls.Add(r.RequestUri.AbsoluteUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Png) { Headers = { { "Content-Type", "image/png" } } },
                });
            }
        }

        [TestMethod]
        public async Task Icons_RotationPortraitsAndSkills_AreFetchedOnceAndApplied()
        {
            string dir = Path.Combine(Path.GetTempPath(), "kaption-rotation-icons-" + Guid.NewGuid().ToString("N"));
            try
            {
                var handler = new Handler();
                var cache = new CharacterImageCache(dir, new HttpClient(handler));
                var model = VarkaPage();
                var steps = model.Teams.SelectMany(t => t.Rotations).SelectMany(r => r.Steps).ToList();

                Assert.IsTrue(await CharacterIcons.FetchMissingAsync(model, cache));
                Assert.IsTrue(steps.All(s => File.Exists(s.IconPath)), "every turn's portrait");
                Assert.IsTrue(steps.SelectMany(s => s.Actions).Where(a => a.IconUrl != null).All(a => File.Exists(a.IconPath)), "every E and Q icon");
                Assert.AreEqual(handler.Urls.Distinct().Count(), handler.Urls.Count, "a URL used twice is downloaded once");
                CollectionAssert.Contains(handler.Urls, "https://enka.network/ui/Skill_S_Nicole_01.png");

                var again = VarkaPage();
                CharacterIcons.ApplyCached(again, cache);
                var first = again.Teams.Single().Rotations[0].Steps[0];
                Assert.AreEqual(steps[0].IconPath, first.IconPath);
                Assert.IsNotNull(first.Actions[0].IconPath);
                Assert.IsFalse(await CharacterIcons.FetchMissingAsync(again, cache), "nothing new to fetch");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
