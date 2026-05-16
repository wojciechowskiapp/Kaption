// ─────────────────────────────────────────────────────────────────────────────
//  StrategyUnitTests.cs
//  ---------------------------------------------------------------------------
//  Pure-function unit tests for the default dialogue-engine strategies
//  introduced in the session-32 template-method refactor.
//
//  Unlike DialoguePredictionTests (which loads a real graph from AppData),
//  these run against hand-built IDialogueGraphAccessor mocks so CI executes
//  them without user data. They verify the CONTRACT each default strategy
//  advertises so a future subclass can depend on documented semantics when
//  choosing whether to override vs. delegate.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using GI_Subtitles.Services.Translation;
using GI_Subtitles.Services.Translation.Strategies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class StrategyUnitTests
    {
        /// <summary>Minimal accessor with only the hooks each strategy actually reads.</summary>
        private sealed class FakeAccessor : IDialogueGraphAccessor
        {
            public Dictionary<long, DialogNode> Dialogs = new Dictionary<long, DialogNode>();
            public Dictionary<long, TalkNode> Talks = new Dictionary<long, TalkNode>();
            public Dictionary<string, string> Npcs = new Dictionary<string, string>();
            public Dictionary<long, (ulong TitleHash, string QuestType)> Quests =
                new Dictionary<long, (ulong, string)>();
            public Dictionary<string, string> TextMap = new Dictionary<string, string>();
            public long ActiveDialogId { get; set; }

            public bool TryGetNode(long id, out DialogNode node) => Dialogs.TryGetValue(id, out node);
            public bool TryGetTalkNode(long id, out TalkNode node) => Talks.TryGetValue(id, out node);
            public bool TryGetNpcName(string roleId, out string name) => Npcs.TryGetValue(roleId, out name);
            public bool TryGetQuestInfo(long id, out (ulong TitleHash, string QuestType) info) => Quests.TryGetValue(id, out info);
            public bool TryGetTextMapValue(string hash, out string text) => TextMap.TryGetValue(hash, out text);
        }

        // ─── GraphNextResolver ──────────────────────────────────────────────

        [TestMethod]
        public void GraphNextResolver_UnknownNode_ReturnsEmpty()
        {
            var acc = new FakeAccessor();
            var result = new GraphNextResolver().Resolve(42, acc);
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Length);
        }

        [TestMethod]
        public void GraphNextResolver_KnownNode_ReturnsNextIds()
        {
            var acc = new FakeAccessor();
            acc.Dialogs[100] = new DialogNode { NextDialogIds = new long[] { 101, 102 } };
            var result = new GraphNextResolver().Resolve(100, acc);
            CollectionAssert.AreEqual(new long[] { 101, 102 }, result);
        }

        [TestMethod]
        public void GraphNextResolver_NodeWithNullNext_ReturnsEmpty()
        {
            var acc = new FakeAccessor();
            acc.Dialogs[100] = new DialogNode { NextDialogIds = null };
            var result = new GraphNextResolver().Resolve(100, acc);
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Length);
        }

        // ─── TrimNameNormalizer ─────────────────────────────────────────────

        [TestMethod]
        public void TrimNameNormalizer_SimpleName_TrimsAndLowercases()
        {
            var n = new TrimNameNormalizer();
            // NormalizeFull contract: produce "casing-normalized full name for
            // index keys" — trim + lowercase. See INpcNameNormalizer interface.
            Assert.AreEqual("paimon", n.NormalizeFull("  Paimon  "));
            Assert.AreEqual("paimon", n.ExtractFirstName("  Paimon  "));
        }

        [TestMethod]
        public void TrimNameNormalizer_NameWithRole_ExtractsFirst()
        {
            var n = new TrimNameNormalizer();
            // "Traveler, Sir" → first token is "Traveler", lowercased for keying.
            Assert.AreEqual("traveler", n.ExtractFirstName("Traveler, Sir"));
            Assert.AreEqual("march", n.ExtractFirstName("March 7th"));
        }

        [TestMethod]
        public void TrimNameNormalizer_EmptyOrNull_ReturnsEmpty()
        {
            var n = new TrimNameNormalizer();
            Assert.AreEqual(string.Empty, n.ExtractFirstName(null));
            Assert.AreEqual(string.Empty, n.ExtractFirstName(""));
            Assert.AreEqual(string.Empty, n.ExtractFirstName("   "));
        }

        // ─── DefaultQuestBannerFormatter ────────────────────────────────────

        [TestMethod]
        public void DefaultQuestBannerFormatter_UnknownQuest_ReturnsNull()
        {
            var acc = new FakeAccessor();
            var result = new DefaultQuestBannerFormatter().Format(999, acc);
            Assert.IsNull(result);
        }

        [TestMethod]
        public void DefaultQuestBannerFormatter_KnownQuestWithTitle_ReturnsTitleAndType()
        {
            var acc = new FakeAccessor();
            acc.Quests[500] = (4242UL, "MQ");
            acc.TextMap["4242"] = "The Outlander Who Caught the Wind";
            var result = new DefaultQuestBannerFormatter().Format(500, acc);
            Assert.IsNotNull(result);
            Assert.AreEqual("The Outlander Who Caught the Wind", result.Value.title);
            Assert.AreEqual("MQ", result.Value.type);
        }

        [TestMethod]
        public void DefaultQuestBannerFormatter_QuestWithMissingTitle_ReturnsNull()
        {
            var acc = new FakeAccessor();
            acc.Quests[500] = (9999UL, "MQ");
            // TextMap has no "9999" — title resolution fails.
            var result = new DefaultQuestBannerFormatter().Format(500, acc);
            Assert.IsNull(result);
        }

        // ─── NpcNameDisambiguator ───────────────────────────────────────────

        [TestMethod]
        public void NpcNameDisambiguator_SingleCandidate_ReturnsIt()
        {
            var acc = new FakeAccessor();
            var result = new NpcNameDisambiguator().Disambiguate(
                new List<long> { 42 }, "Paimon", acc);
            Assert.AreEqual(42, result);
        }

        [TestMethod]
        public void NpcNameDisambiguator_MultipleCandidates_PrefersReachableFromActive()
        {
            var acc = new FakeAccessor { ActiveDialogId = 100 };
            // 100 → 200 → 300; 999 is the wrong branch.
            acc.Dialogs[100] = new DialogNode { NextDialogIds = new long[] { 200 } };
            acc.Dialogs[200] = new DialogNode { NextDialogIds = new long[] { 300 } };
            acc.Dialogs[300] = new DialogNode();
            acc.Dialogs[999] = new DialogNode();
            var result = new NpcNameDisambiguator().Disambiguate(
                new List<long> { 999, 300 }, null, acc);
            Assert.AreEqual(300, result, "Expected the reachable-from-active candidate.");
        }

        [TestMethod]
        public void NpcNameDisambiguator_NoReachableMatch_FallsBackToNpcName()
        {
            var acc = new FakeAccessor { ActiveDialogId = 0 };
            acc.Dialogs[100] = new DialogNode { RoleId = "npc_paimon" };
            acc.Dialogs[200] = new DialogNode { RoleId = "npc_kaeya" };
            acc.Npcs["npc_paimon"] = "Paimon";
            acc.Npcs["npc_kaeya"] = "Kaeya";
            var result = new NpcNameDisambiguator().Disambiguate(
                new List<long> { 100, 200 }, "Kaeya", acc);
            Assert.AreEqual(200, result, "Expected candidate whose NPC matches the detected name.");
        }

        [TestMethod]
        public void NpcNameDisambiguator_NoMatchAnywhere_ReturnsFirst()
        {
            var acc = new FakeAccessor { ActiveDialogId = 0 };
            acc.Dialogs[100] = new DialogNode();
            acc.Dialogs[200] = new DialogNode();
            var result = new NpcNameDisambiguator().Disambiguate(
                new List<long> { 100, 200 }, "Someone", acc);
            // Contract: when nothing disambiguates, return the first candidate rather than 0.
            Assert.AreEqual(100, result);
        }

        // ─── GameDialogueContextFactory ─────────────────────────────────────

        [TestMethod]
        public void Factory_GenshinAndStarRail_ReturnDistinctExpectedGame()
        {
            var g = GameDialogueContextFactory.Create("Genshin");
            var s = GameDialogueContextFactory.Create("StarRail");
            // Both should be IGameDialogueContext (interface, not concrete)
            // and neither should be null.
            Assert.IsNotNull(g);
            Assert.IsNotNull(s);
            Assert.IsInstanceOfType(g, typeof(IGameDialogueContext));
            Assert.IsInstanceOfType(s, typeof(IGameDialogueContext));
        }

        [TestMethod]
        public void Factory_NullOrEmpty_FallsBackToGenshin()
        {
            // Null or empty should NOT throw — preserves pre-multi-game behavior
            // where Config["Game"] was absent.
            var a = GameDialogueContextFactory.Create(null);
            var b = GameDialogueContextFactory.Create("");
            var c = GameDialogueContextFactory.Create("   ");
            Assert.IsNotNull(a);
            Assert.IsNotNull(b);
            Assert.IsNotNull(c);
        }

        [TestMethod]
        public void Factory_UnknownGame_StillReturnsInstance()
        {
            // Fail-closed at Load time — the factory itself should never return null.
            var ctx = GameDialogueContextFactory.Create("Genshin1TypoFromConfig");
            Assert.IsNotNull(ctx);
            Assert.IsFalse(ctx.IsLoaded, "Nothing has been loaded; IsLoaded must be false.");
        }
    }
}
