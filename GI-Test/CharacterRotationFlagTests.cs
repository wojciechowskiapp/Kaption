using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Rotation flags (session 54 round 4): a held skill, an optional input, a choice of two inputs, a step's note and a
    /// rotation's covers, from loading to the chips. Words and orders are the site's (landing rotations.ts).
    /// </summary>
    [TestClass]
    public class CharacterRotationFlagTests
    {
        private const string Varka = CharacterRotationTests.Varka, Durin = CharacterRotationTests.Durin,
            Prune = CharacterRotationTests.Prune, Nicole = CharacterRotationTests.Nicole, Chiori = CharacterRotationTests.Chiori;

        /// <summary>
        /// Varka's team with every flag in the shapes the updater writes: a held skill with a count, optional inputs and a
        /// whole optional turn, a choice of two icons and one of a skill and a text input, a turn's notes. Not game facts.
        /// </summary>
        internal const string VarkaFlagRotations = """
            [
              { "variant": null, "covers": true,
                "steps": [ { "member": "10000123", "actions": [ { "a": "E", "n": 2, "hold": true }, { "a": "Q", "opt": true } ], "note": "Omit if C6" },
                           { "member": "10000132", "actions": [ { "a": "E", "alt": "Q" }, { "a": "D", "opt": true } ] },
                           { "member": "10000131", "actions": [ { "a": "E", "opt": true }, { "a": "Q", "opt": true } ] },
                           { "member": "10000128", "actions": [ { "a": "E", "hold": true }, { "a": "N", "alt": "CA" }, { "a": "combo" } ],
                             "note": "Hold the Skill only when the Burst is ready" } ],
                "combo": { "10000128": "Skill > N5 > Skill / Charged Attack > N5 > Skill / Charged Attack" } }
            ]
            """;

        /// <summary>Chiori's Geo team, whose skill icons the app's cache holds: held icons, a pair of icons, an optional disc; Xilonen sits out.</summary>
        internal const string GeoFlagRotations = """
            [
              { "covers": false,
                "steps": [ { "member": "10000038", "actions": [ { "a": "E", "hold": true } ], "note": "Hold only on the first rotation" },
                           { "member": "10000094", "actions": [ { "a": "E", "alt": "Q" } ] },
                           { "member": "10000057", "actions": [ { "a": "Q", "opt": true }, { "a": "E", "n": 2, "hold": true }, { "a": "combo" } ] } ],
                "combo": { "10000057": "N2 > CA > N2 > CA > Arataki Kesagiri Final Slash" } }
            ]
            """;

        internal static readonly RotationWords Polish = new RotationWords("hold", "opcjonalnie", "do wyboru");

        internal static CharacterCatalog Flags() =>
            CharacterRotationTests.Load(CharacterRotationTests.GenshinJson(VarkaFlagRotations, GeoFlagRotations));

        private static IReadOnlyList<TeamRotation> Loaded(string rotations) =>
            CharacterRotationTests.Genshin(rotations).Find(Varka).Ai.Teams.Single().Rotations;

        private static CharacterOverviewModel Page(string id = Varka)
        {
            var catalog = Flags();
            return CharacterOverviewBuilder.Build(catalog.Find(id), catalog, null);
        }

        private static CharacterOverviewModel VarkaPage(string rotations)
        {
            var catalog = CharacterRotationTests.Genshin(rotations);
            return CharacterOverviewBuilder.Build(catalog.Find(Varka), catalog, null);
        }

        private const string EQ = "[ { \"a\": \"E\" }, { \"a\": \"Q\" } ]";

        private static string Step(string member, string actions, string extra = "") =>
            "{ \"member\": \"" + member + "\", \"actions\": " + actions + extra + " }";

        /// <summary>One rotation of one Durin turn; <paramref name="rotationExtra"/> goes before "steps", <paramref name="stepExtra"/> after "actions".</summary>
        private static string One(string actions, string rotationExtra = "", string stepExtra = "") =>
            "{ " + rotationExtra + "\"steps\": [ " + Step(Durin, actions, stepExtra) + " ] }";

        // ── loading ─────────────────────────────────────────────────────────

        [TestMethod]
        public void Load_ReadsTheFlags_TheNotesAndCovers()
        {
            var r = Loaded(VarkaFlagRotations).Single();
            Assert.AreEqual(true, r.Covers);

            var durin = r.Steps[0];
            Assert.AreEqual("Omit if C6", durin.Note);
            Assert.IsTrue(durin.Actions[0].Hold);
            Assert.AreEqual(2, durin.Actions[0].Count, "flags combine with n");
            Assert.IsFalse(durin.Actions[0].Optional);
            Assert.AreEqual("", durin.Actions[0].Alt);
            Assert.IsTrue(durin.Actions[1].Optional);
            Assert.IsFalse(durin.Actions[1].Hold);

            Assert.AreEqual("Q", r.Steps[1].Actions[0].Alt, "\"E/Q\" is E with alt Q");
            Assert.AreEqual("", r.Steps[1].Note, "no note");
            Assert.IsTrue(r.Steps[2].Actions.All(a => a.Optional), "a whole optional turn");
            Assert.AreEqual("CA", r.Steps[3].Actions[1].Alt);
            Assert.AreEqual("Hold the Skill only when the Burst is ready", r.Steps[3].Note);

            Assert.AreEqual(false, Flags().Find(Chiori).Ai.Teams.Single().Rotations.Single().Covers, "covers: false still loads");
        }

        [TestMethod]
        public void Load_FlagsReadTrueFalseNullOrAbsent_OldDataUnchanged()
        {
            string rotations = "[ " + One("[ { \"a\": \"E\", \"hold\": false, \"opt\": null, \"alt\": null }, { \"a\": \"Q\", \"hold\": null, \"opt\": false } ]",
                "\"covers\": null, ", ", \"note\": null") + " ]";
            var r = Loaded(rotations).Single();
            Assert.IsNull(r.Covers, "covers: null says nothing");
            Assert.IsTrue(r.Steps[0].Actions.All(a => !a.Hold && !a.Optional && a.Alt == ""), "false and null are no flag");
            Assert.AreEqual("", r.Steps[0].Note);

            // The round 3 data (no flags anywhere) reads as before.
            var old = Loaded(CharacterRotationTests.VarkaRotations);
            Assert.AreEqual(2, old.Count);
            Assert.IsTrue(old.All(x => x.Covers == null && x.Steps.All(s => s.Note == "" && s.Actions.All(a => !a.Hold && !a.Optional && a.Alt == ""))));
        }

        [TestMethod]
        public void Load_EveryFlagCombines()
        {
            var a = Loaded("[ " + One("[ { \"a\": \"E\", \"n\": 2, \"hold\": true, \"opt\": true, \"alt\": \"Q\" } ]") + " ]")
                .Single().Steps.Single().Actions.Single();
            Assert.AreEqual(("E", 2, true, true, "Q"), (a.Action, a.Count, a.Hold, a.Optional, a.Alt));
            Assert.AreEqual("Q", Loaded("[ " + One("[ { \"a\": \"E\", \"alt\": \" Q \" } ]") + " ]").Single().Steps.Single().Actions.Single().Alt,
                "trimmed, as a is");
        }

        [TestMethod]
        public void Load_AnUnknownFlagValue_SkipsThatRotationAlone()
        {
            string good = "{ \"variant\": \"Prune\", \"steps\": [ " + Step(Prune, "[ { \"a\": \"E\", \"hold\": true } ]") + " ] }";
            var bad = new Dictionary<string, string>
            {
                ["hold as text"] = One("[ { \"a\": \"E\", \"hold\": \"yes\" } ]"),
                ["hold as \"true\""] = One("[ { \"a\": \"E\", \"hold\": \"true\" } ]"),
                ["hold as 1"] = One("[ { \"a\": \"E\", \"hold\": 1 } ]"),
                ["hold as an object"] = One("[ { \"a\": \"E\", \"hold\": {} } ]"),
                ["opt as text"] = One("[ { \"a\": \"Q\", \"opt\": \"optional\" } ]"),
                ["opt as 0"] = One("[ { \"a\": \"Q\", \"opt\": 0 } ]"),
                ["opt as a list"] = One("[ { \"a\": \"Q\", \"opt\": [] } ]"),
                ["a choice outside the grammar"] = One("[ { \"a\": \"E\", \"alt\": \"J\" } ]"),
                ["a lower-case choice"] = One("[ { \"a\": \"E\", \"alt\": \"q\" } ]"),
                ["a choice of the same input"] = One("[ { \"a\": \"E\", \"alt\": \"E\" } ]"),
                ["a choice that is not text"] = One("[ { \"a\": \"E\", \"alt\": 1 } ]"),
                ["an empty choice"] = One("[ { \"a\": \"E\", \"alt\": \"\" } ]"),
                ["a choice of two"] = One("[ { \"a\": \"E\", \"alt\": [ \"Q\", \"N\" ] } ]"),
                ["a held skill written as an input"] = One("[ { \"a\": \"hE\" } ]"),
                ["covers as text"] = One(EQ, "\"covers\": \"yes\", "),
                ["covers as 1"] = One(EQ, "\"covers\": 1, "),
                ["a note that is not text"] = One(EQ, "", ", \"note\": 42"),
                ["a note as an object"] = One(EQ, "", ", \"note\": { \"en\": \"Omit if C6\" }"),
                ["a note past the cap"] = One(EQ, "", ", \"note\": \"" + new string('x', CharacterCatalog.MaxStepNoteLength + 1) + "\""),
            };
            foreach (var (why, rotation) in bad)
            {
                var kept = Loaded("[ " + rotation + ", " + good + " ]");
                Assert.AreEqual(1, kept.Count, why);
                Assert.AreEqual("Prune", kept[0].Variant, why);
                Assert.IsTrue(kept[0].Steps.Single().Actions.Single().Hold, why);
            }
        }

        [TestMethod]
        public void Load_NotesAreOneLine_BlankIsNone_TheCapHolds()
        {
            Assert.AreEqual("Omit if C6", Loaded("[ " + One(EQ, "", ", \"note\": \"  Omit if \\n C6 \"") + " ]").Single().Steps.Single().Note);
            var blank = Loaded("[ " + One(EQ, "", ", \"note\": \" \\n \"") + " ]");
            Assert.AreEqual(1, blank.Count, "a blank note keeps its rotation");
            Assert.AreEqual("", blank.Single().Steps.Single().Note);

            Assert.AreEqual(80, CharacterCatalog.MaxStepNoteLength, "the contract's cap, as on the site");
            string atCap = new string('x', CharacterCatalog.MaxStepNoteLength);
            Assert.AreEqual(atCap, Loaded("[ " + One(EQ, "", ", \"note\": \"" + atCap + "\"") + " ]").Single().Steps.Single().Note);
        }

        // ── builder ─────────────────────────────────────────────────────────

        [TestMethod]
        public void Builder_CarriesTheFlags_TheChoicesSkillAndIcon_AndTheNotes()
        {
            var steps = Page().Teams.Single().Rotations.Single().Steps;

            var durinE = steps[0].Actions[0];
            Assert.IsTrue(durinE.Hold);
            Assert.AreEqual("Elemental Skill: Binary Form: Convergence and Division", durinE.Name);
            Assert.AreEqual("Elemental Skill: Binary Form: Convergence and Division ×2 (hold)", durinE.Description, "the site's wording");
            Assert.AreEqual("Elemental Burst: Principle of Purity: As the Light Shifts (optional)", steps[0].Actions[1].Description);
            Assert.AreEqual("Omit if C6", steps[0].Note);

            var prune = steps[1].Actions[0];
            Assert.AreEqual("Q", prune.Alt);
            Assert.AreEqual("The Bell Tolls! The Hunt Is On!", prune.AltSkillName);
            Assert.AreEqual("Elemental Burst: The Bell Tolls! The Hunt Is On!", prune.AltName);
            Assert.AreEqual("https://enka.network/ui/Skill_S_Prune_01.png", prune.IconUrl);
            Assert.AreEqual("https://enka.network/ui/Skill_E_Prune_01.png", prune.AltIconUrl);
            Assert.AreEqual("Elemental Skill: Ring-A-Ding-Ding! Hexhunter Chime / Elemental Burst: The Bell Tolls! The Hunt Is On! (either)", prune.Description);
            Assert.AreEqual("Dash (optional)", steps[1].Actions[1].Description);
            Assert.AreEqual("", steps[1].Note);

            var varka = steps[3].Actions;
            Assert.AreEqual("Elemental Skill: Windbound Execution (hold)", varka[0].Description);
            Assert.AreEqual("Normal Attack: Favonius Bladework: Dancing Radiance / Charged Attack (either)", varka[1].Description);
            Assert.AreEqual("https://enka.network/ui/Skill_A_01.png", varka[1].IconUrl);
            Assert.IsNull(varka[1].AltIconUrl, "a charged attack has no icon");
            Assert.AreEqual("Combo *", varka[2].Description);
            Assert.AreEqual("Hold the Skill only when the Burst is ready", steps[3].Note);
        }

        [TestMethod]
        public void Builder_TheNotesMarkupIsStripped()
        {
            string rotation = "[ " + One(EQ, "", ", \"note\": \"<color=#FFD780FF>Omit</color> if C6\"") + " ]";
            Assert.AreEqual("Omit if C6", VarkaPage(rotation).Teams.Single().Rotations.Single().Steps.Single().Note);
        }

        [TestMethod]
        public void Builder_AComboOfferedAsAChoice_KeepsItsFootnote()
        {
            string rotation = "[ { \"steps\": [ " + Step(Varka, "[ { \"a\": \"Q\", \"alt\": \"combo\" } ]") + " ], " +
                              "\"combo\": { \"" + Varka + "\": \"Skill > N5\" } } ]";
            var r = VarkaPage(rotation).Teams.Single().Rotations.Single();
            var chip = r.Steps.Single().Actions.Single();

            Assert.AreEqual("*", chip.Marker);
            Assert.AreEqual("Elemental Burst: Northwind Avatar / Combo * (either)", chip.Description);
            Assert.AreEqual("Skill > N5", r.Combos.Single().Text);
            var row = new RotationChipRow(chip);
            Assert.IsTrue(row.IsText, "a combo has no icon, so the choice is text");
            Assert.AreEqual("Q / Combo*", row.Text);
        }

        [TestMethod]
        public void Builder_CoversIsNeverShown()
        {
            static string Rotation(string covers) => "[ { " + covers + "\"steps\": [ " + Step(Durin, EQ) + ", " + Step(Prune, EQ) + " ] } ]";
            static string Strip(string rotations) => new CharacterOverviewViewModel(VarkaPage(rotations)).Teams.Single().Rotations.Single().Text;

            string shown = Strip(Rotation(""));
            Assert.AreEqual("Durin: E, Q > Prune: E, Q", shown);
            Assert.AreEqual(shown, Strip(Rotation("\"covers\": false, ")), "a member sitting out changes nothing on the card");
            Assert.AreEqual(shown, Strip(Rotation("\"covers\": true, ")));
        }

        [TestMethod]
        public void Builder_Zzz_EveryFlagReadsAsText()
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
                        "rotations": [ { "steps": [ { "member": "1141", "actions": [ { "a": "E", "hold": true }, { "a": "Q", "opt": true } ] },
                                                    { "member": "1191", "actions": [ { "a": "E", "n": 2, "alt": "Q" }, { "a": "N", "n": 3, "hold": true } ],
                                                      "note": "Skip the Ultimate without full Decibels" } ] } ] } ] } },
                    { "id": "1141", "name": "Lycaon", "icon": "IconRoleCircle18", "element": "Ice", "skills": [] }
                  ] }
                """;
            var catalog = CharacterRotationTests.Load(json, "zzz");
            var model = CharacterOverviewBuilder.Build(catalog.Find("1191"), catalog, null);
            var steps = model.Teams.Single().Rotations.Single().Steps;
            CollectionAssert.AreEqual(new[] { "Special Attack (hold)", "Ultimate (optional)" }, steps[0].Actions.Select(a => a.Description).ToArray());
            CollectionAssert.AreEqual(new[] { "Special Attack: EX Special Attack: Sharknami / Ultimate: Endless Winter ×2 (either)", "Basic Attack ×3 (hold)" },
                steps[1].Actions.Select(a => a.Description).ToArray());

            var rows = new CharacterOverviewViewModel(model).Teams.Single().Rotations.Single().Steps;
            var chips = rows.SelectMany(s => s.Chips).ToList();
            Assert.IsTrue(chips.All(c => c.IsText && !c.HasHoldTag), "no icons: the words are in the text");
            CollectionAssert.AreEqual(new[] { "E", "Q", "E / Q", "N3" }, chips.Select(c => c.Label).ToArray(), "an N alone still folds its count");
            CollectionAssert.AreEqual(new[] { " hold", "", "", " hold" }, chips.Select(c => c.InlineHold).ToArray());
            CollectionAssert.AreEqual(new[] { "E hold", "(Q)", "E / Q ×2", "N3 hold" }, chips.Select(c => c.Text).ToArray());
            Assert.AreEqual("Skip the Ultimate without full Decibels", rows[1].Note);
        }

        // ── notation (identical on the site) ────────────────────────────────

        [TestMethod]
        public void Notation_ChoiceHoldAndOptional_AsTheSite()
        {
            // A choice reads "E / Q" and keeps its count apart, so an N never folds it in.
            Assert.AreEqual("E / Q", RotationNotation.Label("E", 1, false, "Q"));
            Assert.AreEqual("N / CA", RotationNotation.Label("N", 3, false, "CA"));
            Assert.AreEqual("×3", RotationNotation.Count("N", 3, false, "CA"));
            Assert.AreEqual("N5", RotationNotation.Label("N", 5, false, ""), "alone, an icon-less N still folds");
            Assert.AreEqual("", RotationNotation.Count("N", 5, false, ""));
            Assert.AreEqual("Q / Combo", RotationNotation.Label("Q", 1, false, "combo"));

            Assert.IsTrue(RotationNotation.HasIconSlot("E", true, "Q", true), "both inputs have icons: a pair");
            Assert.IsFalse(RotationNotation.HasIconSlot("E", true, "Q", false), "one icon missing: text");
            Assert.IsFalse(RotationNotation.HasIconSlot("E", true, "CA", true), "a charged attack never draws an icon");
            Assert.IsTrue(RotationNotation.HasIconSlot("N", true, "", false), "no choice: the input alone decides");

            Assert.AreEqual("E hold", RotationNotation.Text("E", 1, false, hold: true));
            Assert.AreEqual("E hold ×2", RotationNotation.Text("E", 2, false, hold: true), "the site's order: text, hold, count");
            Assert.AreEqual("E hold ×2", RotationNotation.Text("E", 2, true, hold: true), "the plain text names the hold on icon chips too");
            Assert.AreEqual("N / CA ×3", RotationNotation.Text("N", 3, false, alt: "CA"));
            Assert.AreEqual("(Q)", RotationNotation.Text("Q", 1, true, optional: true), "KQM's own brackets");
            Assert.AreEqual("(Combo ×2)*", RotationNotation.Text("combo", 2, false, "*", optional: true));
            Assert.AreEqual("Q / Combo*", RotationNotation.Text("Q", 1, false, "*", alt: "combo"));
            Assert.AreEqual(("H", "hold"), (RotationNotation.HoldTag, RotationNotation.HoldWord));
        }

        [TestMethod]
        public void Notation_TheTooltip_AsTheSitesChipLabel()
        {
            var en = RotationWords.English;
            static string D(string name, string alt, int n, string marker, bool hold, bool opt, RotationWords words) =>
                RotationNotation.Describe(name, alt, n, marker, RotationNotation.Flags(!string.IsNullOrEmpty(alt), hold, opt, words));

            // Word for word the landing's own expectations (tools/test-build-landing-characters.cjs, round 4).
            Assert.AreEqual("Elemental Skill: Binding Flame ×2 (hold)", D("Elemental Skill: Binding Flame", "", 2, "", true, false, en));
            Assert.AreEqual("Elemental Burst: Blazing Dawn (optional)", D("Elemental Burst: Blazing Dawn", "", 1, "", false, true, en));
            Assert.AreEqual("Elemental Skill: Binding Flame / Elemental Burst: Blazing Dawn (either)",
                D("Elemental Skill: Binding Flame", "Elemental Burst: Blazing Dawn", 1, "", false, false, en));
            Assert.AreEqual("Normal Attack: Radiant Sword / Elemental Skill: Binding Flame ×3 (either)",
                D("Normal Attack: Radiant Sword", "Elemental Skill: Binding Flame", 3, "", false, false, en));
            Assert.AreEqual("Elemental Skill: Windward Charge / Elemental Burst ×2 (either, optional)",
                D("Elemental Skill: Windward Charge", "Elemental Burst", 2, "", false, true, en));
            Assert.AreEqual("Normal Attack: Gale Rush ×5 (optional)", D("Normal Attack: Gale Rush", "", 5, "", false, true, en));
            Assert.AreEqual("Elemental Burst / Combo * (either)", D("Elemental Burst", "Combo", 1, "*", false, false, en));
            Assert.AreEqual("Elemental Skill: Windward Charge / Elemental Burst ×2 (do wyboru, hold, opcjonalnie)",
                D("Elemental Skill: Windward Charge", "Elemental Burst", 2, "", true, true, Polish), "either, hold, optional");
            Assert.AreEqual("Elemental Skill: Binding Flame ×2", D("Elemental Skill: Binding Flame", "", 2, "", false, false, en), "no flags: as before");
            Assert.AreEqual("Elemental Skill: Binding Flame (hold)", D("Elemental Skill: Binding Flame", "", 1, "", true, false, null), "no words: English");
        }

        [TestMethod]
        public void Strings_TheFlagWords_AsTheSite()
        {
            string[] keys = { "Char_Rotation_Hold", "Char_Rotation_Optional", "Char_Rotation_Either" };
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            string[] Words(string culture) =>
                keys.Select(k => XDocument.Load(Path.Combine(RenderTestKit.RepoRoot(), "GI-Subtitles", "Resources", $"Strings.{culture}.xaml"))
                    .Root.Elements().Single(e => (string)e.Attribute(x + "Key") == k).Value).ToArray();

            // The landing's rotationFlags: "hold" is the game's own word in both languages.
            CollectionAssert.AreEqual(new[] { "hold", "optional", "either" }, Words("en-US"));
            CollectionAssert.AreEqual(new[] { "hold", "opcjonalnie", "do wyboru" }, Words("pl-PL"));
            Assert.AreEqual(RotationWords.English, new RotationWords(Words("en-US")[0], Words("en-US")[1], Words("en-US")[2]));
            Assert.AreEqual(Polish, new RotationWords(Words("pl-PL")[0], Words("pl-PL")[1], Words("pl-PL")[2]));
        }

        // ── chips ───────────────────────────────────────────────────────────

        private static OverviewRotationAction Input(string a, int n = 1, bool hold = false, bool opt = false, string alt = "",
                                                    bool icon = false, bool altIcon = false, string name = "") =>
            new OverviewRotationAction
            {
                Action = a, Count = n, Hold = hold, Optional = opt, Alt = alt, Name = name,
                IconPath = icon ? @"C:\icons\" + a + ".png" : null, AltIconPath = altIcon ? @"C:\icons\" + alt + ".png" : null,
            };

        [TestMethod]
        public void Chips_AHeldSkill_TagsItsIcon_OrSaysHoldAfterItsText()
        {
            var icon = new RotationChipRow(Input("E", 2, hold: true, icon: true));
            Assert.IsTrue(icon.IsDisc);
            Assert.IsTrue(icon.HasHoldTag, "the H on the bottom-right corner");
            Assert.AreEqual("H", icon.HoldTag);
            Assert.IsTrue(icon.HasBadge, "the count keeps the top-right one");
            Assert.AreEqual("", icon.InlineHold);
            Assert.AreEqual("E hold ×2", icon.Text);

            var text = new RotationChipRow(Input("E", 2, hold: true));
            Assert.IsTrue(text.IsText, "no icon in the bundle");
            Assert.IsFalse(text.HasHoldTag);
            Assert.AreEqual(" hold", text.InlineHold);
            Assert.AreEqual(" ×2", text.InlineCount);
            Assert.AreEqual("E hold ×2", text.Text);

            var plain = new RotationChipRow(Input("E", icon: true));
            Assert.IsFalse(plain.IsHeld || plain.HasHoldTag || plain.IsOptional || plain.IsChoice || plain.IsPair);
            Assert.AreEqual("", plain.InlineHold);
        }

        [TestMethod]
        public void Chips_AnOptionalInput_IsDashedInEitherForm()
        {
            var disc = new RotationChipRow(Input("Q", opt: true, icon: true, name: "Elemental Burst: Northwind Avatar"));
            Assert.IsTrue(disc.IsOptional && disc.IsDisc);
            Assert.AreEqual("Q", disc.Label, "the outline says it, not the letter");
            Assert.AreEqual("(Q)", disc.Text);
            Assert.AreEqual("Elemental Burst: Northwind Avatar (optional)", disc.Description);

            var text = new RotationChipRow(Input("D", opt: true, name: "Dash"));
            Assert.IsTrue(text.IsOptional && text.IsText);
            Assert.AreEqual("D", text.Label);
            Assert.AreEqual("Dash (optional)", text.Description);
            Assert.AreEqual("Dash (opcjonalnie)", new RotationChipRow(Input("D", opt: true, name: "Dash"), Polish).Description);
        }

        [TestMethod]
        public void Chips_AChoice_IsAPairOnlyWhenBothInputsHaveIcons()
        {
            var pair = new RotationChipRow(Input("E", alt: "Q", icon: true, altIcon: true));
            Assert.IsTrue(pair.IsPair);
            Assert.IsFalse(pair.IsDisc);
            Assert.AreEqual(("E", "Q"), (pair.Letter, pair.AltLetter));
            Assert.AreEqual(@"C:\icons\E.png", pair.IconPath);
            Assert.AreEqual(@"C:\icons\Q.png", pair.AltIconPath);
            Assert.AreEqual("E / Q", pair.Text);

            var named = new RotationChipRow(new OverviewRotationAction { Action = "E", Alt = "Q", IconUrl = "https://x.example/e.png", AltIconUrl = "https://x.example/q.png" });
            Assert.IsTrue(named.IsPair, "named but not on disk yet: the pair shows its letters");
            Assert.IsNull(named.IconPath);
            Assert.IsNull(named.AltIconPath);

            var mixed = new RotationChipRow(Input("N", alt: "CA", icon: true));
            Assert.IsTrue(mixed.IsText, "a charged attack has no icon: text");
            Assert.AreEqual("N / CA", mixed.Label);
            Assert.IsNull(mixed.IconPath, "text chips never draw an icon");

            Assert.IsTrue(new RotationChipRow(Input("E", alt: "Q", icon: true)).IsText, "the second icon is missing: text");

            var counted = new RotationChipRow(Input("N", 3, alt: "CA"));
            Assert.AreEqual("N / CA", counted.Label, "a choice never folds the count into N");
            Assert.AreEqual(" ×3", counted.InlineCount);
            Assert.AreEqual("N / CA ×3", counted.Text);

            var badged = new RotationChipRow(Input("N", 3, alt: "E", icon: true, altIcon: true));
            Assert.IsTrue(badged.IsPair && badged.HasBadge);
            Assert.AreEqual("×3", badged.Count);
        }

        [TestMethod]
        public void Chips_TheTooltipFollowsTheInterfaceWords_EnglishIsTheBuilders()
        {
            var actions = Page().Teams.Single().Rotations.Single().Steps.SelectMany(s => s.Actions).ToList();
            foreach (var a in actions)
                Assert.AreEqual(a.Description, new RotationChipRow(a).Description, a.Action + " " + a.Alt);

            CollectionAssert.AreEqual(new[]
            {
                "Elemental Skill: Binary Form: Convergence and Division ×2 (hold)",
                "Elemental Burst: Principle of Purity: As the Light Shifts (opcjonalnie)",
                "Elemental Skill: Ring-A-Ding-Ding! Hexhunter Chime / Elemental Burst: The Bell Tolls! The Hunt Is On! (do wyboru)",
                "Dash (opcjonalnie)",
                "Elemental Skill: Revelation: Uncreated Light (opcjonalnie)",
                "Elemental Burst: Revelation: Ladder of Divine Ascent (opcjonalnie)",
                "Elemental Skill: Windbound Execution (hold)",
                "Normal Attack: Favonius Bladework: Dancing Radiance / Charged Attack (do wyboru)",
                "Combo *",
            }, actions.Select(a => new RotationChipRow(a, Polish).Description).ToArray(), "game terms English, the flag words Polish");
        }

        // ── view model ──────────────────────────────────────────────────────

        private static RotationChipRow Chip(CharacterOverviewViewModel vm, int step, int chip) =>
            vm.Teams.Single().Rotations.Single().Steps[step].Chips[chip];

        [TestMethod]
        public void ViewModel_TheInterfaceWords_RebuildTheCards_OnlyWhenTheyChange()
        {
            var vm = new CharacterOverviewViewModel(Page());
            var changed = new List<string>();
            vm.PropertyChanged += (s, e) => changed.Add(e.PropertyName);
            Assert.AreEqual("Elemental Burst: Principle of Purity: As the Light Shifts (optional)", Chip(vm, 0, 1).Description, "English until the view says otherwise");

            vm.SetRotationWords(Polish);
            CollectionAssert.AreEqual(new[] { "Teams" }, changed);
            Assert.AreEqual("Elemental Burst: Principle of Purity: As the Light Shifts (opcjonalnie)", Chip(vm, 0, 1).Description);

            changed.Clear();
            vm.SetRotationWords(new RotationWords("hold", "opcjonalnie", "do wyboru"));
            vm.SetRotationWords(null);
            Assert.AreEqual(0, changed.Count, "the same words, or none: nothing rebuilt");

            var plain = new CharacterOverviewViewModel(VarkaPage("[]"));
            var before = plain.Teams;
            plain.SetRotationWords(Polish);
            Assert.AreSame(before, plain.Teams, "no rotations: the cards stay");
        }

        [TestMethod]
        public void ViewModel_TheStrip_ReadsTheFlagsAndNotes()
        {
            var rotation = new CharacterOverviewViewModel(Page()).Teams.Single().Rotations.Single();
            Assert.AreEqual("Durin: E hold ×2, (Q) (Omit if C6) > Prune: E / Q, (D) > Nicole: (E), (Q) > " +
                            "Varka: E hold, N / CA, Combo* (Hold the Skill only when the Burst is ready)", rotation.Text);
            CollectionAssert.AreEqual(new[] { true, false, false, true }, rotation.Steps.Select(s => s.HasNote).ToArray());
            Assert.AreEqual("Omit if C6", rotation.Steps[0].Note);

            var chips = rotation.Steps.SelectMany(s => s.Chips).ToList();
            CollectionAssert.AreEqual(new[] { true, false, false, false, false, false, true, false, false }, chips.Select(c => c.HasHoldTag).ToArray());
            CollectionAssert.AreEqual(new[] { false, true, false, true, true, true, false, false, false }, chips.Select(c => c.IsOptional).ToArray());
            CollectionAssert.AreEqual(new[] { false, false, true, false, false, false, false, false, false }, chips.Select(c => c.IsPair).ToArray());
            Assert.IsTrue(chips[7].IsText && chips[7].IsChoice, "N / CA: the charged attack has no icon");
            CollectionAssert.AreEqual(new[] { "* " + CharacterRotationTests.VarkaCombo }, rotation.Footnotes.ToArray());
        }

        // ── icons ───────────────────────────────────────────────────────────

        [TestMethod]
        public async Task Icons_AChoicesSecondIcon_IsFetchedOnceAndApplied()
        {
            string dir = Path.Combine(Path.GetTempPath(), "kaption-rotation-flag-icons-" + Guid.NewGuid().ToString("N"));
            try
            {
                var handler = new CharacterRotationTests.Handler();
                var cache = new CharacterImageCache(dir, new HttpClient(handler));
                var model = Page();
                var prune = model.Teams.Single().Rotations.Single().Steps[1].Actions[0];

                Assert.IsTrue(await CharacterIcons.FetchMissingAsync(model, cache));
                Assert.IsTrue(File.Exists(prune.AltIconPath), "the choice's second icon");
                CollectionAssert.Contains(handler.Urls, "https://enka.network/ui/Skill_E_Prune_01.png");
                Assert.AreEqual(handler.Urls.Distinct().Count(), handler.Urls.Count, "each URL once");

                var again = Page();
                CharacterIcons.ApplyCached(again, cache);
                var applied = again.Teams.Single().Rotations.Single().Steps[1].Actions[0];
                Assert.AreEqual(prune.AltIconPath, applied.AltIconPath);
                var row = new RotationChipRow(applied);
                Assert.IsTrue(row.IsPair);
                Assert.AreEqual(prune.AltIconPath, row.AltIconPath);
                Assert.IsFalse(await CharacterIcons.FetchMissingAsync(again, cache), "nothing new to fetch");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
