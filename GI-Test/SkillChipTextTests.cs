using System.Linq;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>Skill-type markers become chips after the name, the same three looks in every game.</summary>
    [TestClass]
    public class SkillChipTextTests
    {
        [TestMethod]
        [DataRow("Q", SkillChipKind.Burst)]
        [DataRow("Ult", SkillChipKind.Burst)]
        [DataRow("Chain", SkillChipKind.Burst)]
        [DataRow("E", SkillChipKind.Skill)]
        [DataRow("Skill", SkillChipKind.Skill)]
        [DataRow("Special", SkillChipKind.Skill)]
        [DataRow("Atak", SkillChipKind.Normal)]
        [DataRow("Basic", SkillChipKind.Normal)]
        [DataRow("Talent", SkillChipKind.Other)]
        [DataRow("Dodge", SkillChipKind.Other)]
        [DataRow("C3", SkillChipKind.Other)]
        public void GamesShareThreeKinds(string marker, SkillChipKind kind)
        {
            Assert.AreEqual(kind, SkillChipText.KindOf(marker));
        }

        [TestMethod]
        public void MarkersSplitOutOfTheText()
        {
            var pieces = SkillChipText.Split("Level Kamisato Art: Hyouka (E) first, then Soumetsu (Q).");
            CollectionAssert.AreEqual(new[] { "Level Kamisato Art: Hyouka", null, " first, then Soumetsu", null, "." },
                pieces.Select(p => p.Text).ToArray());
            CollectionAssert.AreEqual(new[] { null, "E", null, "Q", null }, pieces.Select(p => p.Marker).ToArray());
            Assert.AreEqual(1, SkillChipText.Split("No marker here (Polish text)").Count, "only real markers become chips");
            Assert.AreEqual(0, SkillChipText.Split(null).Count);
        }

        [TestMethod]
        public void TheTextBlockKeepsItsAutomationName()
        {
            RunSta(() =>
            {
                var block = new TextBlock();
                SkillChipText.SetText(block, "Normal Attack: Kamisato Art (Atak)");
                Assert.AreEqual("Normal Attack: Kamisato Art (Atak)", AutomationProperties.GetName(block));
                Assert.AreEqual(1, block.Inlines.OfType<InlineUIContainer>().Count(), "one chip");
                var chip = (Border)block.Inlines.OfType<InlineUIContainer>().Single().Child;
                Assert.AreEqual(6, chip.CornerRadius.TopLeft);
                StringAssert.StartsWith(((TextBlock)chip.Child).Text, "A", "upper case");
            });
        }
    }
}
