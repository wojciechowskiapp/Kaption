using System.Linq;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>The single-page character view: reading order, at-a-glance facts, names, ladder, teams.</summary>
    [TestClass]
    public class CharacterPageViewModelTests
    {
        private static OverviewSkill Skill(string kind, string english, string polish = null) => new OverviewSkill
        {
            Kind = kind,
            KindKey = "Char_Kind_" + kind,
            EnglishName = english,
            Name = polish ?? english,
            Description = english + " text.",
        };

        private static CharacterOverviewModel Full() => new CharacterOverviewModel
        {
            Name = "Chiori",
            Game = "genshin",
            ElementKey = "Geo",
            Role = "Off-field\nGeo damage",
            Skills = new[]
            {
                Skill("basic", "Weaving Blade"),
                Skill("skill", "Fluttering Hasode", "Powiewająca Hasode"),
                Skill("ultimate", "Hiyoku: Twin Blades", "Hiyoku: bliźniacze ostrza"),
                Skill("constellation", "Six Paths of Sage Silkcraft"),
                Skill("constellation", "In Five Colors Dyed"),
                Skill("constellation", "Four Brocade Embellishments"),
            },
            Leveling = new[] { "Fluttering Hasode", "Hiyoku: Twin Blades" },
            LevelingIsAi = true,
            Highlights = new[] { new OverviewHighlight { LabelKey = "Char_Tip_Set", Text = "Golden Troupe" } },
            Teams = new[]
            {
                new OverviewTeam
                {
                    Name = "Podwójne Geo", Source = "official", Why = "Druga lalka.",
                    Members = new[] { new OverviewTeamMember { Name = "Chiori", Role = "DPS" }, new OverviewTeamMember { Name = "Navia" } },
                },
                new OverviewTeam { Name = "Meta", Source = "meta-tool", Members = new[] { new OverviewTeamMember { Name = "Chiori" }, new OverviewTeamMember { Name = "Zhongli" } } },
            },
            TeammateCards = new[] { new OverviewTeammate { Name = "Zhongli" } },
            MaterialsSkills = new[] { new OverviewMaterial { Name = "Mora", Count = 1 } },
            Guide = new[] { new OverviewGuideSection { Heading = "W skrócie", Body = "Pierwszy akapit.\n\nDrugi.\n- punkt" } },
            Links = new[] { new OverviewLink { Label = "Game8", Url = "https://game8.co/x" } },
            AmbiencePath = @"C:\nowhere\ambience.jpg",
            AmbienceAspect = 2.0,
        };

        [TestMethod]
        public void Sections_FollowTheReadingOrder()
        {
            var vm = new CharacterOverviewViewModel(Full());
            CollectionAssert.AreEqual(
                new[] { "Glance", "Build", "Skills", "Ranks", "Teams", "Materials", "Links" },
                vm.Sections.Select(s => s.Key).ToArray(), "no guide beyond the summary, so no Guide link");
            Assert.AreEqual("Char_Tab_constellation", vm.Sections.Single(s => s.Key == "Ranks").LabelKey);
        }

        [TestMethod]
        public void Glance_Summary_Ambience_AndFacts()
        {
            var vm = new CharacterOverviewViewModel(Full());
            CollectionAssert.AreEqual(new[] { "Pierwszy akapit.", "Drugi.", "punkt" }, vm.Summary.Select(b => b.Text).ToArray());
            Assert.IsTrue(vm.Summary[2].IsBullet);
            Assert.IsTrue(vm.HasAmbience);
            Assert.AreEqual(OverviewImageSizes.Ambience / 2.0, vm.AmbienceHeight, 1e-9, "space is reserved from the data's aspect");

            CollectionAssert.AreEqual(new[] { "Char_Fact_Role", "Char_Fact_Signature", "Char_Fact_LevelFirst" },
                vm.Facts.Select(f => f.LabelKey).ToArray());
            Assert.AreEqual("Off-field: Geo damage", vm.Facts[0].Text, "game text is flattened into one line");
            Assert.AreEqual("Hiyoku: Twin Blades", vm.Facts[1].Text, "the ultimate, by its English name");
            Assert.IsTrue(vm.Facts[1].HasSkill);
            Assert.AreEqual("Fluttering Hasode", vm.Facts[2].Text);
            Assert.IsTrue(vm.Facts[2].IsAi);
        }

        [TestMethod]
        public void Glance_FallsBackToPlaystyle_WithoutAGuide()
        {
            var m = Full();
            m.Guide = new OverviewGuideSection[0];
            m.Playstyle = new[] { "Wciśnij E.", "Zmień postać." };
            var vm = new CharacterOverviewViewModel(m);
            Assert.IsTrue(vm.Summary.All(b => b.IsBullet));
            Assert.AreEqual(2, vm.Summary.Count);
            Assert.IsFalse(vm.HasGuide);
        }

        [TestMethod]
        public void Skills_ShowTheEnglishName_WithTheTranslationUnderIt()
        {
            var vm = new CharacterOverviewViewModel(Full());
            Assert.AreEqual("Weaving Blade", vm.Skills[0].Title);
            Assert.AreEqual("", vm.Skills[0].Subtitle, "no second line when the pack has nothing different");
            Assert.AreEqual("Fluttering Hasode", vm.Skills[1].Title);
            Assert.AreEqual("Powiewająca Hasode", vm.Skills[1].Subtitle);
            Assert.IsNotNull(vm.Skills[1].Glyph, "a kind glyph stands in until there is an icon");
        }

        [TestMethod]
        public void Ladder_KnowsItsEnds()
        {
            var vm = new CharacterOverviewViewModel(Full());
            CollectionAssert.AreEqual(new[] { "C1", "C2", "C3" }, vm.Progression.Select(p => p.Numeral).ToArray());
            Assert.IsTrue(vm.Progression[0].IsFirst);
            Assert.IsFalse(vm.Progression[1].IsFirst || vm.Progression[1].IsLast);
            Assert.IsTrue(vm.Progression[2].IsLast);
            Assert.AreEqual("Six Paths of Sage Silkcraft", vm.Progression[0].Title);
        }

        [TestMethod]
        public void Teams_LabelTheirSource_AndHideTheFallback()
        {
            var vm = new CharacterOverviewViewModel(Full());
            Assert.IsTrue(vm.Teams[0].IsOfficial);
            Assert.IsTrue(vm.Teams[1].IsOtherSource);
            Assert.AreEqual("meta-tool", vm.Teams[1].Source);
            Assert.AreEqual("DPS", vm.Teams[0].Members[0].Role);
            Assert.IsFalse(vm.ShowTeammates, "the game's teammates show only when there are no comps");

            var m = Full();
            m.Teams = new OverviewTeam[0];
            Assert.IsTrue(new CharacterOverviewViewModel(m).ShowTeammates);
        }

        [TestMethod]
        public void GuideBlock_SplitsParagraphsAndBullets()
        {
            var blocks = GuideBlock.Parse("One\nline.\n\n- first\n• second\nAfter.");
            CollectionAssert.AreEqual(new[] { "One line.", "first", "second", "After." }, blocks.Select(b => b.Text).ToArray());
            CollectionAssert.AreEqual(new[] { false, true, true, false }, blocks.Select(b => b.IsBullet).ToArray());
            Assert.AreEqual(0, GuideBlock.Parse(null).Count);
        }

        [TestMethod]
        public void SearchFold_IgnoresCaseAndPolishMarks()
        {
            Assert.AreEqual("lowca zolw", CharactersPage.FoldForSearch("  Łowca Żółw "));
        }
    }
}
