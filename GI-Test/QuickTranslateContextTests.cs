using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.QuickTranslate;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>The Ctrl+Q popup's one view: the recognised character's strip, the inline page, and Esc.</summary>
    [TestClass]
    public class QuickTranslateContextTests
    {
        private const string Json = @"{
  ""schema"": 1, ""game"": ""genshin"", ""game_version"": ""7.1.0"",
  ""characters"": [
    { ""id"": ""10000094"", ""name"": ""Chiori"", ""rarity"": 5, ""element"": ""Geo"", ""weapon_type"": ""Sword"",
      ""skills"": [
        { ""kind"": ""basic"", ""name"": ""Weaving Blade"", ""desc"": { ""t"": ""Up to 4 strikes."", ""p"": [] } },
        { ""kind"": ""skill"", ""name"": ""Fluttering Hasode"", ""desc"": { ""t"": ""Dashes nimbly forward."", ""p"": [] } },
        { ""kind"": ""ultimate"", ""name"": ""Hiyoku: Twin Blades"", ""desc"": { ""t"": ""Twin blades leave their sheaths."", ""p"": [] } },
        { ""kind"": ""passive"", ""name"": ""Tailor-Made"", ""desc"": { ""t"": ""Gain different effects."", ""p"": [] } },
        { ""kind"": ""constellation"", ""name"": ""Six Paths of Sage Silkcraft"", ""desc"": { ""t"": ""Tamoto's AoE is increased."", ""p"": [] } }
      ] }
  ]
}";

        private static (CharacterOverviewModel Model, CharacterRecord Record) Chiori()
        {
            var catalog = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Json)), "genshin");
            var record = catalog.Find("10000094");
            return (CharacterOverviewBuilder.Build(record, catalog, null), record);
        }

        private static QuickTranslateContext Context(params string[] lines)
        {
            var (model, record) = Chiori();
            return new QuickTranslateContext(model, record, lines);
        }

        [TestMethod]
        public void Strip_ShowsTheCharacterAndTheRecognisedSkill()
        {
            var ctx = Context("Fluttering Hasode", "Dashes nimbly forward with silken steps.", "Hold to dash further.");
            Assert.AreEqual("Chiori", ctx.Name);
            Assert.AreEqual("5★ Geo", ctx.Facts);
            Assert.IsTrue(ctx.HasSkill);
            Assert.AreEqual("Fluttering Hasode (E)", ctx.SkillTitle);
            Assert.AreEqual("Skills", ctx.SectionToOpen, "the page opens where the skill is");
        }

        [TestMethod]
        public void Strip_RanksOpenTheLadder_AndTitlesMatchAfterAKindPrefix()
        {
            Assert.AreEqual("Ranks", Context("Six Paths of Sage Silkcraft").SectionToOpen);
            Assert.AreEqual("Hiyoku: Twin Blades (Q)", Context("Twin Blades").SkillTitle, "the part after \"Hiyoku:\" names it too");
            Assert.AreEqual("Tailor-Made", Context("TAILOR-MADE").SkillTitle, "a passive has no marker; case and punctuation fold");
        }

        [TestMethod]
        public void NoSkillOnScreen_StripWithoutASkill()
        {
            var ctx = Context("Chiori", "Some unrelated line.");
            Assert.IsFalse(ctx.HasSkill);
            Assert.AreEqual("", ctx.SkillTitle);
            Assert.IsNull(ctx.SectionToOpen);
        }

        [TestMethod]
        public void Lines_FromTheCard_AreMarkedWithTheirType()
        {
            var ctx = Context("Fluttering Hasode");
            var segments = new[]
            {
                new QuickTranslateSegment("Fluttering Hasode", "k0", "Powiewająca Hasode", true),
                new QuickTranslateSegment("Dashes nimbly forward.", "k1", "Doskakuje.", true),
                new QuickTranslateSegment("Six Paths of Sage Silkcraft", "k2", "Sześć Ścieżek Tkactwa Mędrca", true),
                new QuickTranslateSegment("Tailor-Made", "k3", "Szyte na miarę", true),
            };
            var marks = ctx.MarksFor(segments);
            Assert.IsTrue(marks[0].FromCard);
            Assert.AreEqual("E", marks[0].Badge);
            Assert.IsFalse(marks[1].FromCard, "a description is not a title");
            Assert.AreEqual("C1", marks[2].Badge);
            Assert.IsTrue(marks[3].FromCard);
            Assert.AreEqual("", marks[3].Badge, "from the card, no type to show");
        }

        [TestMethod]
        public void OpenAndBack_AndEscapeClosesThePageFirst()
        {
            var ctx = Context("Fluttering Hasode");
            var changed = new List<string>();
            ((INotifyPropertyChanged)ctx).PropertyChanged += (s, e) => changed.Add(e.PropertyName);

            Assert.IsTrue(ctx.IsCollapsed);
            ctx.Expand();
            Assert.IsTrue(ctx.IsExpanded);
            CollectionAssert.Contains(changed, nameof(QuickTranslateContext.IsExpanded));

            Assert.AreEqual(PopupEscape.Collapse, ctx.Escape(), "Esc on the page goes back to the translation");
            Assert.IsTrue(ctx.IsCollapsed);
            Assert.AreEqual(PopupEscape.Close, ctx.Escape(), "Esc on the translation closes");

            ctx.Expand();
            ctx.Collapse();
            Assert.IsFalse(ctx.IsExpanded);
        }

        [TestMethod]
        public void SkillIndexFor_WholeTitlesOnly()
        {
            var (_, record) = Chiori();
            Assert.AreEqual(1, CharacterDetector.SkillIndexFor(record, "Fluttering Hasode"));
            Assert.AreEqual(2, CharacterDetector.SkillIndexFor(record, "Hiyoku: Twin Blades"));
            Assert.AreEqual(-1, CharacterDetector.SkillIndexFor(record, "Fluttering Hasode deals damage"), "a sentence is not a title");
            Assert.AreEqual(-1, CharacterDetector.SkillIndexFor(null, "Fluttering Hasode"));
        }

        [TestMethod]
        public void Wuwa_AResonanceChainNodeOpensTheRanks_ASkillOpensTheSkills()
        {
            var catalog = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(CharacterWuwaTests.Json)), "WuWa");
            var record = catalog.Find("1404");
            var model = CharacterOverviewBuilder.Build(record, catalog, null);

            var node = new QuickTranslateContext(model, record, new[] { "Versatility" });
            Assert.AreEqual("Ranks", node.SectionToOpen, "a Resonance Chain node sits on the ladder");
            Assert.AreEqual("Versatility (S2)", node.SkillTitle);
            Assert.AreEqual("Skills", new QuickTranslateContext(model, record, new[] { "Windqueller" }).SectionToOpen);

            Assert.AreEqual("S", SkillMarkers.RankLetterOfKind("Sequence"), "one rank table for markers, the ladder and the popup");
            Assert.IsTrue(new[] { "constellation", "eidolon", "mindscape", "sequence" }.All(SkillMarkers.IsRankKind));
            Assert.IsFalse(SkillMarkers.IsRankKind("forte") || SkillMarkers.IsRankKind(null));
        }
    }
}
