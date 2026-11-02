using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class CharacterOverviewViewModelTests
    {
        private static OverviewSkill Skill(string kind, string name, string desc = "", string shortText = "", string ai = "") =>
            new OverviewSkill { Kind = kind, KindKey = "Char_Kind_" + kind, Name = name, Description = desc, Short = shortText, AiSummary = ai };

        private static CharacterOverviewModel Model(params OverviewSkill[] skills) => new CharacterOverviewModel
        {
            Name = "Skirk", EnglishName = "Skirk", ElementKey = "Cryo", Element = "Cryo", Rarity = 5, RarityText = "★★★★★",
            Skills = skills,
        };

        [TestMethod]
        public void Progression_SplitFromSkills_WithNumerals()
        {
            var vm = new CharacterOverviewViewModel(Model(
                Skill("basic", "Havoc: Sunder"),
                Skill("constellation", "Far to Fall", "a"),
                Skill("constellation", "Into the Abyss", "b")));

            Assert.AreEqual(1, vm.Skills.Count);
            CollectionAssert.AreEqual(new[] { "C1", "C2" }, vm.Progression.Select(p => p.Numeral).ToArray());
            Assert.AreEqual("Char_Tab_constellation", vm.ProgressionKey);
            Assert.AreEqual("genshin", vm.Game);
        }

        [TestMethod]
        public void EidolonsAndMindscapes_UseTheirLetters()
        {
            var hsr = new CharacterOverviewViewModel(Model(Skill("technique", "t"), Skill("eidolon", "Da Capo")));
            var zzz = new CharacterOverviewViewModel(Model(Skill("dodge", "d"), Skill("mindscape", "Glacial Omen")));

            Assert.AreEqual("E1", hsr.Progression[0].Numeral);
            Assert.AreEqual("starrail", hsr.Game);
            Assert.AreEqual("M1", zzz.Progression[0].Numeral);
            Assert.AreEqual("zzz", zzz.Game);
        }

        [TestMethod]
        public void KindTag_OnlyOnFirstRowOfARun()
        {
            var vm = new CharacterOverviewViewModel(Model(
                Skill("basic", "a"), Skill("passive", "b"), Skill("passive", "c"), Skill("basic", "d")));

            CollectionAssert.AreEqual(new[] { true, true, false, true }, vm.Skills.Select(s => s.ShowKind).ToArray());
        }

        [TestMethod]
        public void Preview_PrefersAiThenShortThenDescription()
        {
            var vm = new CharacterOverviewViewModel(Model(
                Skill("basic", "a", desc: "Full.", shortText: "Short.", ai: "AI line."),
                Skill("skill", "b", desc: "Full.", shortText: "Short."),
                Skill("ultimate", "c", desc: "Normal Attack\nPerforms up to 5 attacks.")));

            Assert.AreEqual("AI line.", vm.Skills[0].Preview);
            Assert.IsTrue(vm.Skills[0].PreviewIsAi);
            Assert.AreEqual("Short.", vm.Skills[1].Preview);
            Assert.AreEqual("Normal Attack: Performs up to 5 attacks.", vm.Skills[2].Preview);
            Assert.IsTrue(vm.Skills[2].IsTeaser);
            Assert.IsTrue(vm.HasAiSummaries);
        }

        [TestMethod]
        public void Expanding_ATeaser_HidesThePreview_ButKeepsASummary()
        {
            var vm = new CharacterOverviewViewModel(Model(
                Skill("basic", "a", desc: "Full text."),
                Skill("skill", "b", desc: "Full text.", shortText: "Short.")));

            vm.Skills[0].IsExpanded = true;
            vm.Skills[1].IsExpanded = true;

            Assert.IsFalse(vm.Skills[0].ShowPreview, "the full text replaces its own first line");
            Assert.IsTrue(vm.Skills[0].ShowDescription);
            Assert.IsTrue(vm.Skills[1].ShowPreview, "a separate summary stays above the full text");
        }

        [TestMethod]
        public void EverySkillWithText_CanExpand()
        {
            var vm = new CharacterOverviewViewModel(Model(Skill("passive", "a", desc: "Short line."), Skill("passive", "b")));

            Assert.IsTrue(vm.Skills[0].CanExpand, "a one-line teaser can still be cut off at narrow widths");
            Assert.IsFalse(vm.Skills[1].CanExpand);
        }

        [TestMethod]
        public void Flatten_JoinsHeadingsWithAColon()
        {
            Assert.AreEqual("Tap: Dashes forward. Hold: Charges.",
                CharacterOverviewViewModel.Flatten("Tap\nDashes forward.\n\nHold\nCharges."));
            Assert.AreEqual("", CharacterOverviewViewModel.Flatten("  \n "));
        }

        [TestMethod]
        public void SetGroup_PiecesAndConnectors()
        {
            var one = new OverviewSet { Name = "A", Bonuses = new[] { "2: ATK +18%", "4: Long text" } };
            var two = new OverviewSet { Name = "B" };
            var pick = SetGroupRow.From(new OverviewSetGroup { Pieces = 4, Options = new[] { one, two } });
            var pair = SetGroupRow.From(new OverviewSetGroup { Pieces = 2, Combine = true, Options = new[] { one, two } });

            Assert.AreEqual("4", pick.PiecesText);
            Assert.IsTrue(pick.Options[1].ShowOr);
            Assert.AreEqual("2+2", pair.PiecesText);
            Assert.IsTrue(pair.Options[1].ShowPlus);
            Assert.AreEqual("2", pick.Options[0].Bonuses[0].Pieces);
            Assert.AreEqual("ATK +18%", pick.Options[0].Bonuses[0].Text);
        }

        [TestMethod]
        public void Rarity_StarsOrRankTile()
        {
            var stars = new CharacterOverviewViewModel(Model());
            var rank = new CharacterOverviewViewModel(new CharacterOverviewModel
            {
                Name = "Ellen", RarityText = "S", Rarity = 4, Skills = new[] { Skill("dodge", "d") },
            });

            Assert.IsTrue(stars.ShowStars);
            Assert.AreEqual("top", stars.RarityTier);
            Assert.IsTrue(rank.IsRank);
            Assert.AreEqual("top", rank.RarityTier, "ZZZ rank S is stored as 4");
            Assert.AreEqual("high", CharacterOverviewViewModel.Tier("genshin", 4));
        }

        [TestMethod]
        public void ElementPalette_KnownUnknownAndZzzPhysical()
        {
            Assert.AreNotEqual(ElementPalette.For("genshin", "Pyro"), ElementPalette.For("genshin", "Nonsense"));
            Assert.AreEqual(ElementPalette.For("genshin", "Nonsense"), ElementPalette.For("starrail", ""));
            Assert.AreNotEqual(ElementPalette.For("starrail", "Physical"), ElementPalette.For("zzz", "Physical"));
            Assert.IsTrue(ElementPalette.Order("Pyro") < ElementPalette.Order("Geo"));
            Assert.AreEqual(int.MaxValue, ElementPalette.Order("Nonsense"));
        }

        [TestMethod]
        public void InkOn_PicksTheReadableColour()
        {
            Assert.AreEqual(Colors.White, ElementPalette.InkOn(Color.FromRgb(0x20, 0x20, 0x60)));
            Assert.AreNotEqual(Colors.White, ElementPalette.InkOn(Color.FromRgb(0xF0, 0xE0, 0x80)));
        }

        [TestMethod]
        public void SlotIcons_KnownSlotsDifferFromTheFallback()
        {
            var fallback = SlotIcons.For("nope");
            foreach (string slot in new[] { "sands", "goblet", "circlet", "body", "feet", "sphere", "rope", "disc4", "disc6" })
                Assert.AreNotSame(fallback, SlotIcons.For(slot), slot);
            Assert.AreSame(SlotIcons.For("disc4"), SlotIcons.For("disc5"));
        }

        [TestMethod]
        public void IconImages_NullForAnythingButAReadableLocalImage()
        {
            string dir = Path.Combine(Path.GetTempPath(), "kaption-icon-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string png = Path.Combine(dir, "ok.png");
                var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 4 * 4], 16);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var fs = File.Create(png)) encoder.Save(fs);
                string junk = Path.Combine(dir, "junk.png");
                File.WriteAllText(junk, "not an image");

                Assert.IsNotNull(IconImages.Load(png, 24));
                Assert.IsTrue(IconImages.Load(png, 24).IsFrozen);
                Assert.IsNull(IconImages.Load(junk, 24));
                Assert.IsNull(IconImages.Load(Path.Combine(dir, "missing.png"), 24));
                Assert.IsNull(IconImages.Load(@"\\server\share\icon.png", 24), "never a network path");
                Assert.IsNull(IconImages.Load("relative.png", 24));
                Assert.IsNull(IconImages.Load(null, 24));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [TestMethod]
        public void LocFallback_ReadableTail()
        {
            Assert.AreEqual("Dash", Loc.Fallback("Char_Kind_dash"));
            Assert.AreEqual("", Loc.Fallback(null));
        }
    }
}
