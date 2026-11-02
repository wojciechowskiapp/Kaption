using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// Owner (2.5.0): ranked lines read as compact chips in order ("Substaty": A › B › C, the first marked), set counts
    /// read inline ("4 × A lub B"), and a translation shows only when it translates at least one word.
    /// </summary>
    [TestClass]
    public class CharacterSubstatsTests
    {
        [TestMethod]
        public void AGloss_ShowsOnlyWhenItTranslatesAWord()
        {
            Assert.IsTrue(CharacterOverviewViewModel.TranslatesAWord("ATK Percentage", "Procent ATK"));
            Assert.IsTrue(CharacterOverviewViewModel.TranslatesAWord("Elemental Mastery", "Biegłość żywiołów"));
            Assert.IsFalse(CharacterOverviewViewModel.TranslatesAWord("CRIT DMG", "CRIT DMG"));
            Assert.IsFalse(CharacterOverviewViewModel.TranslatesAWord("CRIT DMG", "DMG CRIT"), "the same words in another order");
            Assert.IsFalse(CharacterOverviewViewModel.TranslatesAWord("HP", "hp"));
            Assert.IsFalse(CharacterOverviewViewModel.TranslatesAWord("SPD", ""));
            Assert.AreEqual("", CharacterOverviewViewModel.GlossOf(new[] { "CRIT DMG" }, new[] { "CRIT DMG" }, ""));
            Assert.AreEqual("Szansa na CRIT", CharacterOverviewViewModel.GlossOf(new[] { "CRIT Rate" }, new[] { "Szansa na CRIT" }, ""));
        }

        private static CharacterOverviewModel Model() => new CharacterOverviewModel
        {
            Name = "Chiori", Game = "genshin", ElementKey = "Geo", HasBuild = true, HasOfficialBuild = true,
            SubStats = new[] { "Szansa na CRIT", "CRIT DMG", "Procent DEF" },
            SubStatsEnglish = new[] { "CRIT Rate", "CRIT DMG", "DEF Percentage" },
            SetGroups = new[]
            {
                new OverviewSetGroup { Pieces = 4, Options = new[] { new OverviewSet { Name = "Golden Troupe", EnglishName = "Golden Troupe" },
                                                                       new OverviewSet { Name = "Husk of Opulent Dreams", EnglishName = "Husk of Opulent Dreams" } } },
                new OverviewSetGroup { Pieces = 2, Combine = true, Options = new[] { new OverviewSet { Name = "Husk of Opulent Dreams", EnglishName = "Husk of Opulent Dreams" },
                                                                                      new OverviewSet { Name = "Archaic Petra", EnglishName = "Archaic Petra" } } },
            },
        };

        [TestMethod]
        public void Substats_InRankOrder_FirstMarked_RankKeptForScreenReaders()
        {
            var vm = new CharacterOverviewViewModel(Model());
            CollectionAssert.AreEqual(new[] { "CRIT Rate", "CRIT DMG", "DEF%" }, vm.SubStats.Select(s => s.Name).ToArray(),
                "percent stats use the game's short name (lead's rule)");
            Assert.IsFalse(vm.SubStats[2].HasGloss, "a short name needs no gloss");
            Assert.IsTrue(vm.SubStats[0].IsFirst);
            Assert.IsFalse(vm.SubStats[0].ShowSeparator, "no › before the first");
            Assert.IsTrue(vm.SubStats.Skip(1).All(s => s.ShowSeparator && !s.IsFirst));
            Assert.AreEqual("Szansa na CRIT", vm.SubStats[0].Gloss);
            Assert.IsFalse(vm.SubStats[1].HasGloss, "CRIT DMG reads the same: one line");
            Assert.AreEqual("1. CRIT Rate (Szansa na CRIT)", vm.SubStats[0].AutomationName);
            Assert.AreEqual("2. CRIT DMG", vm.SubStats[1].AutomationName);
        }

        [TestMethod]
        public void SetCounts_ReadInline()
        {
            var vm = new CharacterOverviewViewModel(Model());
            CollectionAssert.AreEqual(new[] { "4 ×", "" }, vm.SetGroups[0].Options.Select(o => o.PiecesLabel).ToArray(), "4 × A lub B");
            CollectionAssert.AreEqual(new[] { "2 ×", "2 ×" }, vm.SetGroups[1].Options.Select(o => o.PiecesLabel).ToArray(), "2 × A + 2 × B");
        }

        [TestMethod]
        public void TheView_HasNoNumerals_AndMarksTheFirstChip()
        {
            RunSta(() =>
            {
                var view = new CharacterOverviewView(Model());
                var host = new Border { Child = view };
                host.Resources.MergedDictionaries.Add(Dictionary("Themes/Palette.Dark.xaml"));
                host.Resources.MergedDictionaries.Add(Dictionary("Resources/CharacterPaletteDark.xaml"));
                host.Resources.MergedDictionaries.Add(Dictionary("Resources/CharacterLayoutPage.xaml"));
                foreach (var d in Strings("pl-PL")) host.Resources.MergedDictionaries.Add(d);
                Layout(host, 900);

                var list = (ItemsControl)view.FindName("SubStatsList");
                Assert.AreEqual("Substaty", AutomationProperties.GetName(list));
                var texts = Descendants<TextBlock>(list).Select(t => t.Text).ToList();
                Assert.IsFalse(texts.Any(t => t == "1" || t == "2" || t == "3"), "no numerals");
                Assert.AreEqual(2, Descendants<TextBlock>(list).Count(t => t.Text == "›" && t.Visibility == Visibility.Visible), "one › between each pair");
                var chips = Descendants<Border>(list).Where(b => b.Name == "Chip").ToList();
                Assert.AreEqual(3, chips.Count);
                Assert.AreEqual(1.5, chips[0].BorderThickness.Left, "the top priority is marked");
                Assert.AreEqual(1.0, chips[1].BorderThickness.Left);
                Assert.AreEqual("1. CRIT Rate (Szansa na CRIT)", AutomationProperties.GetName(chips[0]));
            });
        }
    }
}
