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
using System.Reflection;
using GI_Subtitles.Services.Detection;
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

        /// <summary>
        /// Read the protected <c>ExpectedBundleGame</c> off a context.
        ///
        /// Reflection rather than widening production visibility: the property
        /// is deliberately protected because only DialogueContextBase.Load is
        /// supposed to consult it, and the cross-game bundle gate is the whole
        /// point of the factory's fail-closed default arm. Without this, the
        /// "distinct expected game" test below could only null-check — which is
        /// exactly what it used to do, so it passed no matter what the factory
        /// returned.
        /// </summary>
        private static string ExpectedBundleGameOf(IGameDialogueContext ctx)
        {
            var prop = typeof(DialogueContextBase).GetProperty(
                "ExpectedBundleGame",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(prop,
                "DialogueContextBase.ExpectedBundleGame not found — renamed or made public? Update this helper.");
            return (string)prop.GetValue(ctx);
        }

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

            // The actual contract the name promises: each context carries the
            // lowercased wire id of its own game, so a Genshin bundle can never
            // load into a Star Rail session.
            Assert.AreEqual("genshin", ExpectedBundleGameOf(g));
            Assert.AreEqual("starrail", ExpectedBundleGameOf(s));
        }

        [TestMethod]
        public void Factory_Zzz_ReturnsContextExpectingZzzBundle()
        {
            // Config["Game"] holds "ZZZ"; the wire id is the lowercased form.
            // Both spellings must land on the same passthrough arm — if "ZZZ"
            // ever falls through to the fail-closed default it still returns a
            // context, so only the expected-game value catches the mistake.
            var upper = GameDialogueContextFactory.Create("ZZZ");
            var lower = GameDialogueContextFactory.Create("zzz");
            var padded = GameDialogueContextFactory.Create("  ZZZ  ");

            Assert.IsNotNull(upper);
            Assert.IsInstanceOfType(upper, typeof(IGameDialogueContext));
            Assert.AreEqual("zzz", ExpectedBundleGameOf(upper));
            Assert.AreEqual("zzz", ExpectedBundleGameOf(lower));
            Assert.AreEqual("zzz", ExpectedBundleGameOf(padded));
        }

        /// <summary>
        /// Read the private <c>_nameNorm</c> strategy off a context. Same
        /// reasoning as <see cref="ExpectedBundleGameOf"/> — the field is
        /// private by design, and the alternative to reflection is widening
        /// production visibility purely so a test can look.
        /// </summary>
        private static INpcNameNormalizer NameNormalizerOf(IGameDialogueContext ctx)
        {
            var field = typeof(DialogueContextBase).GetField(
                "_nameNorm",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field,
                "DialogueContextBase._nameNorm not found — renamed? Update this helper.");
            return (INpcNameNormalizer)field.GetValue(ctx);
        }

        [TestMethod]
        public void Factory_Zzz_StripsSpeakerNameDecorationToTheCleanIndexKey()
        {
            // The whole point of wiring ZzzNameNormalizer in: PaddleOCR reads
            // ZZZ's middot-flanked cinematic speaker "··Remielle··" as
            // "-Remielle". If that reaches the name-to-role index un-stripped it
            // matches nothing, and the failure is silent — no wrong name, just
            // no hot-cache preload and no disambiguation tie-breaker.
            var zzz = NameNormalizerOf(GameDialogueContextFactory.Create("ZZZ"));

            Assert.AreEqual("remielle", zzz.NormalizeFull("-Remielle"));
            Assert.AreEqual("remielle", zzz.NormalizeFull("··Remielle··"));
            Assert.AreEqual("remielle", zzz.ExtractFirstName("-Remielle"));

            // The property that actually matters: decorated and clean spellings
            // have to land on the SAME index key.
            Assert.AreEqual(zzz.NormalizeFull("Remielle"), zzz.NormalizeFull("-Remielle"),
                "Decorated and clean speaker names must produce the same index key.");
        }

        [TestMethod]
        public void Factory_GenshinAndStarRail_KeepTheDefaultNameNormalizer()
        {
            // The one way the ZZZ fix could regress the shipping games. Assert
            // both the type and the behaviour: Genshin body/name text has always
            // been passed through with leading punctuation intact, and changing
            // that would move match keys for two live games.
            foreach (var game in new[] { "Genshin", "StarRail" })
            {
                var n = NameNormalizerOf(GameDialogueContextFactory.Create(game));
                Assert.IsInstanceOfType(n, typeof(TrimNameNormalizer),
                    $"{game} must keep TrimNameNormalizer.");
                Assert.AreEqual("-remielle", n.NormalizeFull("-Remielle"),
                    $"{game} must NOT strip leading decoration.");
                Assert.AreEqual("paimon", n.ExtractFirstName("Paimon, Emergency Food"));
            }
        }

        [TestMethod]
        public void Factory_EveryRegisteredGame_HasItsOwnExpectedBundleGame()
        {
            // Guards the registry against the failure mode that motivated the
            // consolidation: a game registered in GameRegionProfile but never
            // added to the factory's switch would silently take the
            // fail-closed arm and lose dialogue prediction with no UI signal.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var gameId in GameRegionProfile.RegisteredGameIds)
            {
                string expected = ExpectedBundleGameOf(GameDialogueContextFactory.Create(gameId));
                Assert.AreEqual(gameId.ToLowerInvariant(), expected,
                    $"Registered game '{gameId}' should expect its own lowercased wire id.");
                Assert.IsTrue(seen.Add(expected),
                    $"Duplicate expected bundle game '{expected}' — two profiles share a wire id.");
            }
            Assert.IsTrue(seen.Count >= 3, "Expected at least Genshin, StarRail and ZZZ to be registered.");
        }

        // ─── GameRegionProfile registry ─────────────────────────────────────

        [TestMethod]
        public void Registry_EveryProfile_HasIdDisplayNameAndDetectionHints()
        {
            foreach (var p in GameRegionProfile.RegisteredProfiles)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(p.GameId), "GameId must be set.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(p.DisplayName),
                    $"{p.GameId}: DisplayName drives every UI label — it can't be blank.");
                Assert.IsNotNull(p.ProcessNames, $"{p.GameId}: ProcessNames must not be null.");
                Assert.IsTrue(p.ProcessNames.Length > 0, $"{p.GameId}: needs at least one process name to detect.");
                Assert.IsNotNull(p.WindowTitles, $"{p.GameId}: WindowTitles must not be null.");

                // Region ratios must describe a box that fits on screen.
                Assert.IsTrue(p.DialogueXPct >= 0 && p.DialogueXPct + p.DialogueWPct <= 1.0,
                    $"{p.GameId}: dialogue box runs off the horizontal edge.");
                Assert.IsTrue(p.DialogueYPct >= 0 && p.DialogueYPct + p.DialogueHPct <= 1.0,
                    $"{p.GameId}: dialogue box runs off the vertical edge.");
                Assert.IsTrue(p.DialogueWPct > 0 && p.DialogueHPct > 0,
                    $"{p.GameId}: dialogue box has no area.");
            }
        }

        [TestMethod]
        public void Registry_DisplayNames_ResolveForShippingGamesAndFallBackForUnknown()
        {
            // The consolidation replaced three hardcoded display-name copies
            // with this one lookup — if it regresses, the Dashboard and the
            // Translations tab both start showing raw tags.
            Assert.AreEqual("Genshin Impact", GameRegionProfile.DisplayNameOf("Genshin"));
            Assert.AreEqual("Honkai: Star Rail", GameRegionProfile.DisplayNameOf("StarRail"));
            Assert.AreEqual("Zenless Zone Zero", GameRegionProfile.DisplayNameOf("ZZZ"));

            // Case-insensitive, so a hand-edited Config.json still renders.
            Assert.AreEqual("Genshin Impact", GameRegionProfile.DisplayNameOf("genshin"));
            Assert.AreEqual("Zenless Zone Zero", GameRegionProfile.DisplayNameOf("zzz"));

            // Unknown tags fall back to themselves rather than to null or "".
            Assert.AreEqual("Wuthering", GameRegionProfile.DisplayNameOf("Wuthering"));
        }

        [TestMethod]
        public void Registry_ShippingGames_KeepTheInlinedVisionDefaults()
        {
            // GameVisionProfile was folded into this class. Its own test asserted
            // these same numbers; keep asserting them here so the merge can't
            // regress Genshin/StarRail. Unknown and empty ids must degrade to the
            // accent defaults too — that's what the property initializers buy.
            foreach (string game in new[] { "Genshin", "StarRail", "", null, "Unknown" })
            {
                var p = GameRegionProfile.Get(game);
                string label = game ?? "(null)";
                Assert.IsFalse(p.UsesGeometryClassifier, label);
                Assert.IsFalse(p.StripsSpeakerNameDecoration, label);
                Assert.AreEqual(-140, p.SubtitlePadVertical, label);
                Assert.AreEqual(0.55, p.RegionSplitXFraction, 1e-9, label);
                Assert.AreEqual(0.70, p.RegionMinWidthFraction, 1e-9, label);
                Assert.IsFalse(p.StrictRegionJunkFilter, label);
            }
        }

        [TestMethod]
        public void Registry_Zzz_LiftsOverlayAndNarrowsRegionFloor()
        {
            var p = GameRegionProfile.Get("ZZZ");

            Assert.IsTrue(p.UsesGeometryClassifier);
            Assert.IsTrue(p.StripsSpeakerNameDecoration);
            Assert.IsTrue(p.SubtitlePadVertical < -140,
                "ZZZ captions wrap taller, so the overlay needs more clearance above the region.");
            Assert.IsTrue(p.RegionMinWidthFraction < 0.70,
                "A 0.70 floor over-widens the narrow comic panel and pulls in HUD/watermarks.");
            Assert.IsTrue(p.StrictRegionJunkFilter);

            // Deliberately left at the default — no ZZZ answer-choice frame has
            // been measured, and a third guessed geometry constant doesn't ship.
            Assert.AreEqual(0.55, p.RegionSplitXFraction, 1e-9);
        }

        [TestMethod]
        public void Registry_ZzzAliases_ResolveToTheSameProfile()
        {
            // Tolerance inherited from the merged GameVisionProfile.IsZenless.
            // A hand-edited Config.json is a real scenario, and silently
            // reverting ZZZ to the Genshin accent path is a hard failure to
            // diagnose from a bug report.
            var canonical = GameRegionProfile.Get("ZZZ");
            foreach (var spelling in new[]
            {
                "zzz", "ZZZ", "  ZZZ  ",
                "Zenless", "zenless",
                "ZenlessZoneZero", "zenlesszonezero",
                "Zenless Zone Zero", "zenless zone zero",
                "Zenless-Zone-Zero", "zenless-zone-zero",
            })
            {
                Assert.AreSame(canonical, GameRegionProfile.Get(spelling),
                    $"'{spelling}' should resolve to the ZZZ profile.");
            }

            // Aliases must not leak into the enumeration source — the UI would
            // render a pill per spelling.
            int zzzEntries = 0;
            foreach (var id in GameRegionProfile.RegisteredGameIds)
                if (GameRegionProfile.Get(id).GameId == "ZZZ") zzzEntries++;
            Assert.AreEqual(1, zzzEntries, "ZZZ must appear exactly once in RegisteredGameIds.");
        }

        [TestMethod]
        public void Registry_NameNormalizerFactory_TracksTheProfileFlag()
        {
            // The normalizer factory keys off StripsSpeakerNameDecoration rather
            // than a game-id check, so this is the seam that has to stay honest.
            Assert.IsInstanceOfType(NpcNameNormalizerFactory.Create("ZZZ"), typeof(ZzzNameNormalizer));
            Assert.IsInstanceOfType(NpcNameNormalizerFactory.Create("Zenless"), typeof(ZzzNameNormalizer));
            Assert.IsInstanceOfType(NpcNameNormalizerFactory.Create("Genshin"), typeof(TrimNameNormalizer));
            Assert.IsInstanceOfType(NpcNameNormalizerFactory.Create("StarRail"), typeof(TrimNameNormalizer));
            Assert.IsInstanceOfType(NpcNameNormalizerFactory.Create(null), typeof(TrimNameNormalizer));
        }

        [TestMethod]
        public void Registry_ZzzProfile_CoversBothMeasuredDialogueLayouts()
        {
            // Pixel bounds measured off 1919x1079 captures (see the profile's
            // comment). Style A cinematic: x 279..1622, name row at y 844.
            // Style B comic: x 473..1443, name pill at y 781, body ends y 1001.
            const double FrameW = 1919.0, FrameH = 1079.0;

            var p = GameRegionProfile.Get("ZZZ");
            Assert.AreEqual("ZZZ", p.GameId, "Get(\"ZZZ\") fell through to the generic fallback profile.");

            double left = p.DialogueXPct, right = p.DialogueXPct + p.DialogueWPct;
            double top = p.DialogueYPct, bottom = p.DialogueYPct + p.DialogueHPct;

            Assert.IsTrue(left <= 279.0 / FrameW && right >= 1622.0 / FrameW,
                "Region must span style A's wider body text.");
            Assert.IsTrue(left <= 473.0 / FrameW && right >= 1443.0 / FrameW,
                "Region must span style B's panel.");
            Assert.IsTrue(top <= 844.0 / FrameH, "Region must start above style A's speaker row.");

            // Bare coverage isn't enough at the top edge. Style B's name pill is
            // the highest text either layout draws, and clipping it costs the
            // geometry classifier the block it keys on — a failure that reads as
            // "ZZZ speaker detection is broken" rather than "the region is a few
            // pixels short". So assert real headroom, not just `top <= pill`.
            // 20 px on the measurement frame is the floor; the shipped 0.70
            // gives ~26 px. An earlier 0.72 draft gave ~4 px and would fail here.
            const double MinTopMarginPx = 20.0;
            double topMarginPx = 781.0 - top * FrameH;
            Assert.IsTrue(topMarginPx >= MinTopMarginPx,
                $"Only {topMarginPx:F0} px of headroom above style B's speaker pill — " +
                $"need at least {MinTopMarginPx:F0} px so the pill can't be clipped.");

            double bottomMarginPx = bottom * FrameH - 1001.0;
            Assert.IsTrue(bottomMarginPx >= MinTopMarginPx,
                $"Only {bottomMarginPx:F0} px below the last body line — need at least {MinTopMarginPx:F0} px.");

            // The top edge was lowered by extending the height, deliberately
            // leaving the bottom where it was. Pin that so a future ratio tweak
            // has to be explicit about moving it.
            Assert.AreEqual(0.97, bottom, 1e-6,
                "Bottom edge moved — DialogueYPct + DialogueHPct should still be 0.97.");

            // Region top doubles as the diagnostics harness crop top
            // (ZenlessLayoutDiagnostics.BottomCropFraction = 0.30), which is what
            // keeps harness measurements comparable to runtime captures.
            Assert.AreEqual(0.70, top, 1e-6,
                "Region top no longer matches the diagnostics harness bottom-30% crop.");
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
