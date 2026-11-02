using System.Linq;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class CharacterOverviewViewModelTipsTests
    {
        private static CharacterOverviewModel Model(string game) => new CharacterOverviewModel
        {
            Name = "Kafka",
            Game = game,
            Highlights = new[]
            {
                new OverviewHighlight { LabelKey = "Char_Tip_Set", Text = "Prisoner in Deep Confinement", IconUrl = "https://img.example/s.png" },
                new OverviewHighlight { LabelKey = "Char_Tip_Weapon", Text = "Patience Is All You Need", IconUrl = "https://img.example/w.png" },
                new OverviewHighlight { LabelKey = "Char_Tip_Leveling", Text = "Skill → Ultimate", IsAi = true },
            },
            MaterialsAscension = new[] { new OverviewMaterial { Name = "Credit", Count = 308000 } },
            Links = new[] { new OverviewLink { Site = "prydwen", Label = "Prydwen", Url = "https://www.prydwen.gg/x" } },
            Guide = new[]
            {
                new OverviewGuideSection { Heading = "Overview", Body = "A." },
                new OverviewGuideSection { Heading = "Build", Body = "B." },
                new OverviewGuideSection { Heading = "Teams", Body = "C." },
            },
            PageUrlPl = "https://kaption.one/pl/postacie/honkai-star-rail/kafka/",
            PageUrlEn = "https://kaption.one/characters/honkai-star-rail/kafka/",
        };

        [TestMethod]
        public void WeaponLabel_FollowsTheGame()
        {
            Assert.AreEqual("Char_Tip_Weapon_starrail", new CharacterOverviewViewModel(Model("starrail")).Tips[1].LabelKey);
            Assert.AreEqual("Char_Tip_Weapon_zzz", new CharacterOverviewViewModel(Model("zzz")).Tips[1].LabelKey);
            Assert.AreEqual("Char_Tip_Set", new CharacterOverviewViewModel(Model("genshin")).Tips[0].LabelKey);
        }

        [TestMethod]
        public void IconSlots_OnlyForSetsAndWeapons()
        {
            var vm = new CharacterOverviewViewModel(Model("starrail"));
            Assert.IsTrue(vm.Tips[0].HasIconSlot);
            Assert.IsTrue(vm.Tips[1].HasIconSlot);
            Assert.IsFalse(vm.Tips[2].HasIconSlot);
            Assert.IsTrue(vm.Tips[2].IsAi);
        }

        [TestMethod]
        public void Materials_Guide_Links_AndPage()
        {
            var vm = new CharacterOverviewViewModel(Model("starrail"));
            Assert.IsTrue(vm.HasTips);
            Assert.IsTrue(vm.HasMaterials);
            StringAssert.StartsWith(vm.MaterialsAscension[0].CountText, "×");
            Assert.IsTrue(vm.MaterialsAscension[0].CountText.Length >= "×308000".Length, vm.MaterialsAscension[0].CountText);
            Assert.AreEqual("A.", vm.Summary.Single().Text, "the first section is the summary at a glance");
            Assert.AreEqual(2, vm.Guide.Count);
            Assert.AreEqual("Build", vm.Guide[0].Heading);
            Assert.IsTrue(vm.Guide[0].IsExpanded, "first of the rest open");
            Assert.IsFalse(vm.Guide[1].IsExpanded);
            Assert.AreEqual("Prydwen", vm.Links.Single().Label);
            Assert.IsTrue(vm.HasPageUrl);
        }

        [TestMethod]
        public void NothingToShow_HidesTheTab()
        {
            var vm = new CharacterOverviewViewModel(new CharacterOverviewModel { Name = "Anby", Game = "zzz" });
            Assert.IsFalse(vm.HasTips);
            Assert.IsFalse(vm.HasGuide);
            Assert.IsFalse(vm.HasMaterials);
        }
    }
}
