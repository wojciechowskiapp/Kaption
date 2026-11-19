using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using GI_Subtitles.Services.Answers;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Dialogue choices are matched exactly, or against the few options the graph expects, or not at all: a dash is
    /// always better than a wrong line. Each case is one the old answer service got wrong or a real short option.
    /// </summary>
    [TestClass]
    public class AnswerMatchingTests
    {
        private static readonly string[] OptionRoles = { "WUWA_OPTION" };

        // A WuWa-shaped menu: an NPC line whose successors are five options, an NPC line and a menu item.
        private static readonly (long id, string text, string role, long[] next)[] Graph =
        {
            (1, "Our farmhouse restaurant is finally open! Here to sample the new menu?", "WUWA_TALK", new long[] { 2, 3, 4, 5, 6, 7, 8 }),
            (2, "Okay", "WUWA_OPTION", Array.Empty<long>()),
            (3, "Okay?", "WUWA_OPTION", Array.Empty<long>()),
            (4, "Why?", "WUWA_OPTION", Array.Empty<long>()),
            (5, "I'm here for that Waking Moon Feast even the Golden-Eyed One swears by.", "WUWA_OPTION", Array.Empty<long>()),
            (6, "Tell me about the market.", "WUWA_OPTION", Array.Empty<long>()),
            (7, "Come in, come in.", "WUWA_TALK", Array.Empty<long>()),
            (8, "Leave", "WUWA_OPTION_MENU", Array.Empty<long>()),
            (9, "Okay", "WUWA_OPTION", Array.Empty<long>()),
        };

        private static readonly Dictionary<string, string> Polish = new()
        {
            ["Our farmhouse restaurant is finally open! Here to sample the new menu?"] = "Nasza restauracja jest wreszcie otwarta! Menu na próbę?",
            ["Okay"] = "Dobrze.",
            ["Okay?"] = "Dobrze?",
            ["Why?"] = "Dlaczego?",
            ["I'm here for that Waking Moon Feast even the Golden-Eyed One swears by."] = "Przyszłam na Ucztę Budzącego się Księżyca, którą zachwala nawet Złotooka.",
            ["Tell me about the market."] = "Opowiedz o targu.",
            ["Come in, come in."] = "Wejdź, wejdź.",
            ["Leave"] = "Wyjdź",
        };

        // WuWa's answers box at 1920x1080 (desktop.answers: bottomRight -682, -724, 500x454), line height 26, pad 12.
        private static readonly Rectangle Box = new Rectangle(1238, 356, 500, 454);
        private const int LineHeight = 26;
        private const int PillPad = 12;
        private static readonly Rectangle Row1 = new Rectangle(1284, 600, 300, 26);
        private static readonly Rectangle Row2 = new Rectangle(1284, 678, 260, 26);
        private static readonly Rectangle Row3 = new Rectangle(1284, 756, 280, 26);

        private static string _dir;
        private static NormalizedDialogueContext _engine;

        [ClassInitialize]
        public static void LoadGraph(TestContext _)
        {
            _dir = Path.Combine(Path.GetTempPath(), "AnswerMatchingTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            var graph = new Dictionary<string, object>();
            var textMap = new Dictionary<string, string>();
            foreach (var (id, text, role, next) in Graph)
            {
                graph[id.ToString()] = new { h = id, nh = 0, n = next, rt = role, ri = role == "WUWA_TALK" ? "npc-owner" : null };
                textMap[id.ToString()] = text;
            }
            File.WriteAllText(Path.Combine(_dir, "DialogGraph.json"), System.Text.Json.JsonSerializer.Serialize(graph));
            File.WriteAllText(Path.Combine(_dir, "TextMapEN.json"), System.Text.Json.JsonSerializer.Serialize(textMap));
            _engine = new NormalizedDialogueContext();
            _engine.Load(_dir, Path.Combine(_dir, "TextMapEN.json"));
            _engine.SetCurrentDialog(1, Polish);
        }

        [ClassCleanup]
        public static void Cleanup()
        {
            try { if (_dir != null && Directory.Exists(_dir)) Directory.Delete(_dir, true); }
            catch { /* best-effort */ }
        }

        [TestMethod]
        public void GetAnswerCandidates_KeepsOnlyTheExactOptionRole()
        {
            var ids = _engine.GetAnswerCandidates(OptionRoles, Polish).Select(c => c.NodeId).OrderBy(i => i).ToArray();

            CollectionAssert.AreEqual(new long[] { 2, 3, 4, 5, 6 }, ids,
                "an NPC successor (7) and a WUWA_OPTION_MENU item (8) are not options the player picks");
        }

        [TestMethod]
        [DataRow("Why?", "Dlaczego?", DisplayName = "a short option matches its candidate")]
        [DataRow("Wny?", "Dlaczego?", DisplayName = "a short option one misread letter off still matches")]
        [DataRow("Okay", "Dobrze.", DisplayName = "Okay is not Okay?")]
        [DataRow("Okay?", "Dobrze?", DisplayName = "Okay? is not Okay")]
        [DataRow("Okay!", null, DisplayName = "alike candidates with other Polish give a dash")]
        [DataRow("Come in, come in.", null, DisplayName = "an NPC successor is never a candidate")]
        [DataRow("Tell me abaut the market.", "Opowiedz o targu.", DisplayName = "one letter off a long option matches")]
        [DataRow("Tall ma ebout the murket", null, DisplayName = "20% off a candidate is refused")]
        [DataRow("Something nobody says here.", null, DisplayName = "no match gives a dash, never English")]
        public void Match_GraphMenu(string row, string expected)
        {
            var match = GraphMatcher().Match(row, _engine.GetAnswerCandidates(OptionRoles, Polish));

            Assert.AreEqual(expected, match.Translation);
        }

        [TestMethod]
        public void MatchAll_TwoRowsClaimingOneOption_BothBecomeDashes()
        {
            // The second row is really "Okay?" with its "?" lost to the read: one of the two would paint wrong Polish.
            var rows = AnswerRows.Build(
                new[] { new AnswerReadBox("Okay", Row1), new AnswerReadBox("Okay", Row2) },
                Array.Empty<AnswerInkBand>(), Box, LineHeight, PillPad, Array.Empty<Rectangle>());

            var matches = GraphMatcher().MatchAll(rows, _engine.GetAnswerCandidates(OptionRoles, Polish));

            Assert.AreEqual(2, matches.Length);
            Assert.IsNull(matches[0].Translation);
            Assert.IsNull(matches[1].Translation);
        }

        [TestMethod]
        public void PackKeysThatNormaliseAlike_WithOtherPolish_GiveNothing()
        {
            var pack = new Dictionary<string, string> { ["Let's go!"] = "Chodźmy!", ["Let's go."] = "Idziemy." };
            using var optimized = new OptimizedMatcher(pack, "EN");
            var matcher = new AnswerMatcher(null, Array.Empty<string>(), pack, optimized);

            Assert.IsNull(matcher.Match("Lets go", Array.Empty<AnswerCandidate>()).Translation);
            Assert.AreEqual("Chodźmy!", matcher.Match("Let's go!", Array.Empty<AnswerCandidate>()).Translation,
                "the key whose own text is the row wins the tie");
        }

        [TestMethod]
        public void AWrappedTwoLineOption_JoinsAndMatches()
        {
            var rows = AnswerRows.Build(
                new[]
                {
                    new AnswerReadBox("Why?", new Rectangle(1284, 522, 60, 26)),
                    new AnswerReadBox("I'm here for that Waking Moon Feast even the", new Rectangle(1284, 600, 400, 26)),
                    new AnswerReadBox("Golden-Eyed One swears by.", new Rectangle(1285, 633, 240, 26)),
                },
                Array.Empty<AnswerInkBand>(), Box, LineHeight, PillPad, Array.Empty<Rectangle>());

            var matches = GraphMatcher().MatchAll(rows, _engine.GetAnswerCandidates(OptionRoles, Polish));

            Assert.AreEqual(2, rows.Rows.Count);
            Assert.AreEqual("Dlaczego?", matches[0].Translation);
            Assert.AreEqual("Przyszłam na Ucztę Budzącego się Księżyca, którą zachwala nawet Złotooka.", matches[1].Translation);
        }

        [TestMethod]
        public void AnUnreadBandBetweenTwoReadRows_IsADashRow_AndKeepsTheNumbering()
        {
            var rows = AnswerRows.Build(
                new[] { new AnswerReadBox("Why?", Row1), new AnswerReadBox("Okay", Row3) },
                new[] { Band(Row1), Band(Row2), Band(Row3) }, Box, LineHeight, PillPad, Array.Empty<Rectangle>());

            Assert.AreEqual(3, rows.Rows.Count);
            Assert.AreEqual("Why?", rows.Rows[0].Text);
            Assert.IsTrue(rows.Rows[1].IsUnread);
            Assert.AreEqual("Okay", rows.Rows[2].Text);
            Assert.IsTrue(rows.Numbered);
        }

        [TestMethod]
        public void AGapThatCouldHoldAnOption_SwitchesNumeralsOff()
        {
            var rows = AnswerRows.Build(
                new[] { new AnswerReadBox("Why?", Row1), new AnswerReadBox("Okay", Row3) },
                new[] { Band(Row1), Band(Row3) }, Box, LineHeight, PillPad, Array.Empty<Rectangle>());

            Assert.AreEqual(2, rows.Rows.Count);
            Assert.IsFalse(rows.Numbered);
        }

        [TestMethod]
        public void OwnPanelThatMayHideAnOption_SwitchesNumeralsOff()
        {
            var read = new[] { new AnswerReadBox("Why?", Row1), new AnswerReadBox("Okay", Row2), new AnswerReadBox("Okay?", Row3) };
            var bands = new[] { Band(Row1), Band(Row2), Band(Row3) };
            var acrossTheStack = new Rectangle(900, 700, 600, 140);
            var justAboveTheTopRow = new Rectangle(1270, 470, 320, 110);
            var clearOfTheHeadroom = new Rectangle(1270, 400, 320, 100);

            Assert.IsTrue(AnswerRows.Build(read, bands, Box, LineHeight, PillPad, Array.Empty<Rectangle>()).Numbered);
            Assert.IsFalse(AnswerRows.Build(read, bands, Box, LineHeight, PillPad, new[] { acrossTheStack }).Numbered);
            Assert.IsFalse(AnswerRows.Build(read, bands, Box, LineHeight, PillPad, new[] { justAboveTheTopRow }).Numbered,
                "an option above the top row may be under it");
            Assert.IsTrue(AnswerRows.Build(read, bands, Box, LineHeight, PillPad, new[] { clearOfTheHeadroom }).Numbered);
        }

        [TestMethod]
        [DataRow(0, DisplayName = "ink touching the box's top")]
        [DataRow(40, DisplayName = "the box's top falling in the gap after an option above it")]
        public void AStackThatMayGoOnAboveTheBox_SwitchesNumeralsOff(int below)
        {
            // The first row seen need not be the game's first option.
            int y = Box.Top + below;
            var rows = AnswerRows.Build(
                new[] { new AnswerReadBox("Why?", new Rectangle(1284, y, 300, 26)), new AnswerReadBox("Okay", new Rectangle(1284, y + 78, 260, 26)) },
                new[] { new AnswerInkBand(y, y + 26), new AnswerInkBand(y + 78, y + 104) },
                Box, LineHeight, PillPad, Array.Empty<Rectangle>());

            Assert.AreEqual(2, rows.Rows.Count);
            Assert.IsFalse(rows.Numbered);
        }

        [TestMethod]
        [DataRow(true, DisplayName = "only the first line read")]
        [DataRow(false, DisplayName = "only the second line read")]
        public void AHalfReadWrappedOption_IsOneDashRow_AndTheNextKeepsItsNumeral(bool firstLineRead)
        {
            var line1 = new Rectangle(1284, 600, 400, 26);
            var line2 = new Rectangle(1285, 630, 240, 26);
            var next = new Rectangle(1284, 708, 60, 26);
            // Detector boxes are padded, so the read line's box reaches into its unread neighbour's band.
            var read = firstLineRead
                ? new AnswerReadBox("I'm here for that Waking Moon Feast even the", Rectangle.FromLTRB(1284, 596, 1684, 632))
                : new AnswerReadBox("Golden-Eyed One swears by.", Rectangle.FromLTRB(1285, 624, 1525, 660));

            var rows = AnswerRows.Build(new[] { read, new AnswerReadBox("Why?", next) },
                new[] { Band(line1), Band(line2), Band(next) }, Box, LineHeight, PillPad, Array.Empty<Rectangle>());

            Assert.AreEqual(2, rows.Rows.Count);
            Assert.IsTrue(rows.Rows[0].IsUnread, "half an option never shows a translation of half its text");
            Assert.AreEqual("Why?", rows.Rows[1].Text);
            Assert.IsTrue(rows.Numbered);
        }

        [TestMethod]
        public void WithoutTheGraphsPlace_AnOptionRepeatedInTheGraph_StillMatchesExactly()
        {
            // "Okay" is an option twice (nodes 2 and 9), "Okay?" once with other Polish.
            var match = GraphMatcher().Match("Okay", Array.Empty<AnswerCandidate>());

            Assert.AreEqual(AnswerTier.OptionExact, match.Tier);
            Assert.AreEqual("Dobrze.", match.Translation);
        }

        private static AnswerMatcher GraphMatcher() => new AnswerMatcher(_engine, OptionRoles, Polish, null);

        private static AnswerInkBand Band(Rectangle r) => new AnswerInkBand(r.Top, r.Bottom);
    }
}
