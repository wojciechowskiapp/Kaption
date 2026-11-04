using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Xml.Linq;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Wuthering Waves' `characters` section as tools/characters/wuwa.cjs writes it: asset-path icon names under one
    /// base, its own skill kinds and the Resonance Chain as `sequence` (S1-S6), Sonata sets with 5-, 3- and 1-piece
    /// bonuses, nation and faction (ignored by the app), and the release fields.
    /// </summary>
    [TestClass]
    public class CharacterWuwaTests
    {
        private const string Base = "https://static.nanoka.cc/assets/ww/UIResources/";

        internal static readonly string Json = @"{
  ""schema"": 1, ""game"": ""wuwa"", ""game_version"": ""3.7"", ""game_live_version"": ""3.7"",
  ""icon_base"": { ""character"": """ + Base + @""", ""weapon"": """ + Base + @""", ""set"": """ + Base + @""",
                   ""material"": """ + Base + @""", ""splash"": """ + Base + @""", ""skill"": """ + Base + @""" },
  ""icon_ext"": { ""character"": "".webp"", ""weapon"": "".webp"", ""set"": "".webp"", ""material"": "".webp"", ""splash"": "".webp"", ""skill"": "".webp"" },
  ""stats"": {},
  ""sets"": {
    ""4"": { ""name"": ""Sierra Gale"", ""icon"": ""Common/Image/IconElementAttri/T_IconElementAttriWind"", ""kind"": ""sonata"",
             ""bonuses"": [ { ""pieces"": 2, ""text"": { ""t"": ""Aero DMG + 10%."", ""p"": [] } },
                            { ""pieces"": 5, ""text"": { ""t"": ""Aero DMG + 30% for 15s after releasing Intro Skill."", ""p"": [] } } ] },
    ""19"": { ""name"": ""Dream of the Lost"", ""kind"": ""sonata"",
              ""bonuses"": [ { ""pieces"": 3, ""text"": { ""t"": ""Holding 0 Resonance Energy increases Crit. Rate by 20%."", ""p"": [] } } ] }
  },
  ""weapons"": {
    ""21010016"": { ""name"": ""Verdant Summit"", ""icon"": ""Common/Image/IconWeapon/T_IconWeapon21010016_UI"", ""rarity"": 5,
                    ""type"": ""Broadblade"", ""passive_name"": ""Prelude"", ""passive"": { ""t"": ""Crit. DMG +24%."", ""p"": [] } }
  },
  ""characters"": [
    { ""id"": ""1404"", ""name"": ""Jiyan"", ""slug"": ""jiyan"", ""icon"": ""Common/Image/IconRoleHead256/T_IconRoleHead256_11_UI"",
      ""splash"": ""Common/Image/IconRolePile/T_IconRole_Pile_jiyan_UI"", ""rarity"": 5, ""element"": ""Aero"", ""weapon_type"": ""Broadblade"",
      ""role"": { ""t"": ""Main Damage Dealer · Heavy Attack DMG · Traction"", ""p"": [] }, ""nation"": ""Huanglong"", ""faction"": ""Jinzhou"",
      ""release_version"": ""1.0"",
      ""skills"": [
        { ""kind"": ""basic"", ""name"": ""Lone Lance"", ""icon"": ""Common/Atlas/SkillIcon/SkillIconJiyan/SP_IconJiyanA1"",
          ""short"": { ""t"": ""Basic Attack\nDeal Aero DMG."", ""p"": [] }, ""desc"": { ""t"": ""Basic Attack\nPerform up to 5 consecutive attacks, dealing Aero DMG."", ""p"": [] } },
        { ""kind"": ""skill"", ""name"": ""Windqueller"", ""short"": null, ""desc"": { ""t"": ""Dash forward a certain distance, dealing Aero DMG."", ""p"": [] } },
        { ""kind"": ""forte"", ""name"": ""Qingloong at War"", ""short"": null, ""desc"": { ""t"": ""Consume 30 Resolve to empower Windqueller."", ""p"": [] } },
        { ""kind"": ""liberation"", ""name"": ""Emerald Storm - Prelude"", ""short"": null, ""desc"": { ""t"": ""Enter Qingloong Mode."", ""p"": [] } },
        { ""kind"": ""intro"", ""name"": ""Tactical Strike"", ""short"": null, ""desc"": { ""t"": ""Pierce the target in mid-air."", ""p"": [] } },
        { ""kind"": ""outro"", ""name"": ""Discipline"", ""short"": null, ""desc"": { ""t"": ""Summon a lance for the incoming Resonator."", ""p"": [] } },
        { ""kind"": ""tune_break"", ""name"": ""Tune Break: Broadblade"", ""short"": null, ""desc"": { ""t"": ""Cast Tune Break on the target."", ""p"": [] } },
        { ""kind"": ""passive"", ""name"": ""Heavenly Balance"", ""short"": null, ""desc"": { ""t"": ""ATK +10% for 15s."", ""p"": [] } },
        { ""kind"": ""sequence"", ""name"": ""Benevolence"", ""icon"": ""Common/Image/IconDevice/T_IconDevice_JiyanM1_UI"", ""short"": null, ""desc"": { ""t"": ""Windqueller can be used 1 more time."", ""p"": [] } },
        { ""kind"": ""sequence"", ""name"": ""Versatility"", ""short"": null, ""desc"": { ""t"": ""ATK +28%."", ""p"": [] } },
        { ""kind"": ""sequence"", ""name"": ""Spectation"", ""short"": null, ""desc"": { ""t"": ""Crit. Rate +16%."", ""p"": [] } },
        { ""kind"": ""sequence"", ""name"": ""Prudence"", ""short"": null, ""desc"": { ""t"": ""Heavy Attack DMG Bonus +25%."", ""p"": [] } },
        { ""kind"": ""sequence"", ""name"": ""Resolution"", ""short"": null, ""desc"": { ""t"": ""Discipline gains 120% more."", ""p"": [] } },
        { ""kind"": ""sequence"", ""name"": ""Fortitude"", ""short"": null, ""desc"": { ""t"": ""Momentum stacks up to 2 times."", ""p"": [] } }
      ],
      ""build"": { ""sets"": [ { ""pieces"": 5, ""ids"": [""4""] }, { ""pieces"": 3, ""ids"": [""19""] } ],
                   ""main_stats"": [], ""sub_stats"": [], ""weapons"": [""21010016""], ""teammates"": [] },
      ""materials"": { ""ascension"": [ { ""name"": ""Shell Credit"", ""count"": 170000, ""icon"": ""Common/Image/IconA/T_IconA_hsb_UI"" } ], ""skills"": [] } }
  ]
}";

        private static CharacterCatalog Catalog(string expected = "WuWa") =>
            CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Json)), expected);

        [TestMethod]
        public void Catalog_LoadsTheWuwaSection_ForTheProfileIdAndTheWireId()
        {
            var c = Catalog();
            Assert.IsFalse(c.IsEmpty, "the profile id WuWa matches the section's wire id wuwa");
            Assert.IsFalse(Catalog("wuwa").IsEmpty);
            var jiyan = c.Find("1404");
            Assert.AreEqual("Jiyan", jiyan.Name);
            Assert.AreEqual(5, jiyan.Rarity);
            Assert.AreEqual("1.0", jiyan.ReleaseVersion);
            Assert.AreEqual("Main Damage Dealer · Heavy Attack DMG · Traction", jiyan.Role.Template);
            CollectionAssert.AreEqual(
                new[] { "basic", "skill", "forte", "liberation", "intro", "outro", "tune_break", "passive", "sequence", "sequence", "sequence", "sequence", "sequence", "sequence" },
                jiyan.Skills.Select(s => s.Kind).ToArray());
            CollectionAssert.AreEqual(new[] { 5, 3 }, jiyan.Build.Sets.Select(s => s.Pieces).ToArray());
            Assert.AreEqual("sonata", c.Sets["4"].Kind);
            Assert.AreEqual("Prelude", c.Weapons["21010016"].PassiveName);
        }

        [TestMethod]
        public void Icons_AssetPathNames_BuildNanokaUrls()
        {
            var c = Catalog();
            var jiyan = c.Find("1404");
            Assert.AreEqual(Base + "Common/Image/IconRoleHead256/T_IconRoleHead256_11_UI.webp", c.IconUrl(IconKind.Character, jiyan.Icon));
            Assert.AreEqual(Base + "Common/Image/IconRolePile/T_IconRole_Pile_jiyan_UI.webp", c.IconUrl(IconKind.Splash, jiyan.Splash));
            Assert.AreEqual(Base + "Common/Atlas/SkillIcon/SkillIconJiyan/SP_IconJiyanA1.webp", c.IconUrl(IconKind.Skill, jiyan.Skills[0].Icon));
            Assert.AreEqual(Base + "Common/Image/IconElementAttri/T_IconElementAttriWind.webp", c.IconUrl(IconKind.Set, c.Sets["4"].Icon));
            Assert.AreEqual(Base + "Common/Image/IconA/T_IconA_hsb_UI.webp", c.IconUrl(IconKind.Material, jiyan.Materials.Ascension[0].Icon));
            Assert.IsNull(c.IconUrl(IconKind.Character, "Common/../secret"), "a path that climbs out is refused");
        }

        [TestMethod]
        public void Overview_ResonanceChain_ReadsS1ToS6_UnderItsOwnTab()
        {
            var c = Catalog();
            var model = CharacterOverviewBuilder.Build(c.Find("1404"), c, null);
            var vm = new CharacterOverviewViewModel(model);

            CollectionAssert.AreEqual(new[] { "S1", "S2", "S3", "S4", "S5", "S6" }, vm.Progression.Select(p => p.Numeral).ToArray());
            Assert.AreEqual("Char_Tab_sequence", vm.ProgressionKey);
            Assert.AreEqual(8, vm.Skills.Count, "the chain nodes are not skills");
            CollectionAssert.AreEqual(
                new[] { "Char_Kind_basic", "Char_Kind_skill", "Char_Kind_forte", "Char_Kind_liberation", "Char_Kind_intro", "Char_Kind_outro", "Char_Kind_tune_break", "Char_Kind_passive" },
                model.Skills.Where(s => s.Kind != "sequence").Select(s => s.KindKey).ToArray());
            Assert.AreEqual("★★★★★", model.RarityText, "Wuthering Waves counts stars");
            Assert.AreEqual("top", CharacterOverviewViewModel.Tier("wuwa", 5));
            Assert.AreEqual("high", CharacterOverviewViewModel.Tier("wuwa", 4));
            var signature = vm.Facts.Single(f => f.LabelKey == "Char_Fact_Signature");
            Assert.AreEqual("Emerald Storm - Prelude (Liberation)", signature.Text, "the Liberation is the signature, like an ultimate");
        }

        [TestMethod]
        public void Overview_WithoutAGameId_InfersWuwaFromItsKinds()
        {
            var model = new CharacterOverviewModel
            {
                Name = "Jiyan", EnglishName = "Jiyan", ElementKey = "Aero", Element = "Aero", Rarity = 5,
                Skills = new[]
                {
                    new OverviewSkill { Kind = "forte", KindKey = "Char_Kind_forte", Name = "Qingloong at War" },
                    new OverviewSkill { Kind = "sequence", KindKey = "Char_Kind_sequence", Name = "Benevolence" },
                },
            };
            var vm = new CharacterOverviewViewModel(model);
            Assert.AreEqual("wuwa", vm.Game);
            Assert.AreEqual("S1", vm.Progression[0].Numeral);
        }

        [TestMethod]
        public void Markers_GameShorthands_AndChainNumbers()
        {
            Assert.AreEqual("(S2)", SkillMarkers.For("wuwa", "sequence", 2));
            Assert.AreEqual("(Liberation)", SkillMarkers.For("wuwa", "liberation", 0, "Emerald Storm - Prelude"));
            Assert.AreEqual("(Forte)", SkillMarkers.For("wuwa", "forte"));
            Assert.AreEqual("(Intro)", SkillMarkers.For("wuwa", "intro"));
            Assert.AreEqual("(Outro)", SkillMarkers.For("wuwa", "outro"));
            Assert.AreEqual("(Tune Break)", SkillMarkers.For("wuwa", "tune_break", 0, "Spectral Analysis"));
            Assert.AreEqual("", SkillMarkers.For("wuwa", "tune_break", 0, "Tune Break: Broadblade"), "the name already says it");
            Assert.AreEqual("", SkillMarkers.For("wuwa", "basic", 0, "Heavy Attack: Detonate"));
            Assert.AreEqual("S", SkillMarkers.RankLetter("wuwa"));
            Assert.AreEqual("S0", SkillMarkers.RankLabel("WuWa", 0));

            var c = Catalog();
            var jiyan = c.Find("1404");
            var markers = SkillMarkers.ForRecord("wuwa", jiyan.Skills, "");
            Assert.AreEqual("(S1)", markers[8]);
            Assert.AreEqual("(S6)", markers[13]);
        }

        [TestMethod]
        public void Chips_LiberationIsTheBurst_ChainNumbersAreQuiet()
        {
            var pieces = SkillChipText.Split("Cast Emerald Storm - Prelude (Liberation), then Benevolence (S1) and Qingloong at War (Forte).");
            CollectionAssert.AreEqual(new[] { "Liberation", "S1", "Forte" }, pieces.Where(p => p.Marker != null).Select(p => p.Marker).ToArray());
            Assert.AreEqual(SkillChipKind.Burst, SkillChipText.KindOf("Liberation"));
            Assert.AreEqual(SkillChipKind.Other, SkillChipText.KindOf("S1"));
            Assert.AreEqual(SkillChipKind.Other, SkillChipText.KindOf("Tune Break"));
        }

        [TestMethod]
        public void PageLink_WuwaPagesLiveUnderTheGamesHubSlug()
        {
            Assert.AreEqual("wuthering-waves", CharacterPageLink.GameSlug("wuwa"));
            Assert.AreEqual("wuthering-waves", CharacterPageLink.GameSlug("WuWa"), "the profile id too");
            var c = Catalog();
            Assert.AreEqual("https://kaption.one/pl/postacie/wuthering-waves/jiyan/", CharacterPageLink.For(c.Find("1404"), c, "pl"));
            Assert.AreEqual("https://kaption.one/characters/wuthering-waves/jiyan/", CharacterPageLink.For(c.Find("1404"), c, "en"));
        }

        [TestMethod]
        public void Palette_EveryWuwaAttribute_HasItsOwnHue()
        {
            var neutral = ElementPalette.For("wuwa", "");
            var hues = new[] { "Glacio", "Fusion", "Electro", "Aero", "Spectro", "Havoc" }.Select(e => ElementPalette.For("wuwa", e)).ToList();
            Assert.IsTrue(hues.All(h => h != neutral), "no attribute falls back to grey");
            Assert.AreEqual(hues.Count, hues.Distinct().Count(), "six different colours");
        }

        [TestMethod]
        public void Strings_EveryWuwaKind_HasALabelInEnglishAndPolish()
        {
            string root = RenderTestKit.RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            string[] keys =
            {
                "Char_Kind_liberation", "Char_Kind_forte", "Char_Kind_intro", "Char_Kind_outro", "Char_Kind_tune_break",
                "Char_Kind_sequence", "Char_Tab_sequence",
            };
            foreach (string lang in new[] { "en-US", "pl-PL" })
            {
                var doc = XDocument.Load(Path.Combine(root, "GI-Subtitles", "Resources", $"Strings.{lang}.xaml"));
                var present = doc.Root.Elements().ToDictionary(e => (string)e.Attribute(x + "Key") ?? "", e => e.Value, StringComparer.Ordinal);
                foreach (string key in keys)
                    Assert.IsTrue(present.TryGetValue(key, out string v) && v.Trim().Length > 0, $"{lang}: {key}");
                Assert.AreEqual("Resonance Chain", present["Char_Tab_sequence"], $"{lang}: the game's own name for the tab");
            }
        }
    }
}
