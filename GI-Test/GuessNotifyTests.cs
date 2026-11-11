using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Guess;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The guess ranking notice and the Start spotlight (session 59, design section 6): the overtake rule, the gate, the
    /// tolerant parse, the words, the poller's 401 path, the spotlight rules, the announcement dismissals and the strings.
    /// </summary>
    [TestClass]
    public class GuessNotifyTests
    {
        private const string Period = "2026-11";

        /// <summary>
        /// A standing; the player just above has more points unless <paramref name="above"/> says otherwise or
        /// <paramref name="noAbove"/> drops them.
        /// </summary>
        private static GuessStanding Standing(int? global, int globalPoints = 140, int? genshin = null, int genshinPoints = 140,
                                              string period = Period, GuessNeighbour above = null, bool noAbove = false)
        {
            if (above == null && !noAbove)
                above = new GuessNeighbour(1, "Ayvruu", null, false, Math.Max(globalPoints, genshinPoints) + 50, 50);
            var games = new Dictionary<string, GuessBoardStanding> { ["starrail"] = null, ["zzz"] = null, ["wuwa"] = null };
            games["genshin"] = genshin is int g ? new GuessBoardStanding(g, genshinPoints, 9, above, null) : null;
            return new GuessStanding
            {
                Period = period,
                Global = global is int r ? new GuessBoardStanding(r, globalPoints, 9, above, null) : null,
                Games = games,
            };
        }

        private static string En(string key, string fallback) => fallback;

        // ── GuessOvertakeRule ───────────────────────────────────────────

        [TestMethod]
        public void Rule_FirstStanding_StoresTheBaseline_WithoutANotice()
        {
            var o = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, Standing(3, genshin: 2), "genshin");
            Assert.IsNull(o.Candidate);
            Assert.AreEqual(3, o.Baselines["global"].Rank);
            Assert.AreEqual(2, o.Baselines["genshin"].Rank);
        }

        [TestMethod]
        public void Rule_ADropWithPoints_IsACandidate()
        {
            var b = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, Standing(3), null).Baselines;
            var o = GuessOvertakeRule.Evaluate(b, Standing(5), null);
            Assert.IsNotNull(o.Candidate);
            Assert.AreEqual("global", o.Candidate.Board);
            Assert.AreEqual(3, o.Candidate.From);
            Assert.AreEqual(5, o.Candidate.To);
            Assert.AreEqual(3, o.Baselines["global"].Rank, "a waiting notice keeps its baseline");
        }

        [TestMethod]
        public void Rule_ADropWithZeroPoints_IsNoNotice_TheOwnersCase()
        {
            var b = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, Standing(3, globalPoints: 0), null).Baselines;
            var o = GuessOvertakeRule.Evaluate(b, Standing(4, globalPoints: 0), null);
            Assert.IsNull(o.Candidate);
            Assert.AreEqual(4, o.Baselines["global"].Rank, "re-based");
        }

        [TestMethod]
        public void Rule_ANewMonth_RebasesWithoutANotice()
        {
            var b = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, Standing(1), null).Baselines;
            var o = GuessOvertakeRule.Evaluate(b, Standing(6, period: "2026-12"), null);
            Assert.IsNull(o.Candidate);
            Assert.AreEqual("2026-12", o.Baselines["global"].Period);
            Assert.AreEqual(6, o.Baselines["global"].Rank);
        }

        [TestMethod]
        public void Rule_AClimbRebases_AndALaterDropCountsFromIt()
        {
            var b = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, Standing(5), null).Baselines;
            b = GuessOvertakeRule.Evaluate(b, Standing(2), null).Baselines;
            Assert.AreEqual(2, b["global"].Rank);
            var o = GuessOvertakeRule.Evaluate(b, Standing(3), null);
            Assert.AreEqual(2, o.Candidate.From);
        }

        [TestMethod]
        public void Rule_AWaitingNotice_ReportsTheWholeDrop()
        {
            var b = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, Standing(3), null).Baselines;
            b = GuessOvertakeRule.Evaluate(b, Standing(4), null).Baselines; // not delivered: baseline stays at 3
            var o = GuessOvertakeRule.Evaluate(b, Standing(6), null);
            Assert.AreEqual(3, o.Candidate.From);
            Assert.AreEqual(6, o.Candidate.To);
            var delivered = GuessOvertakeRule.Delivered(o.Baselines, Standing(6), null);
            Assert.AreEqual(6, delivered["global"].Rank, "delivery re-bases");
            Assert.IsNull(GuessOvertakeRule.Evaluate(delivered, Standing(6), null).Candidate);
        }

        [TestMethod]
        public void Rule_BothBoardsDropped_TheGameBoardWins_ElseGlobal()
        {
            var b = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, Standing(3, genshin: 1), "genshin").Baselines;
            var both = GuessOvertakeRule.Evaluate(b, Standing(4, genshin: 2), "genshin");
            Assert.AreEqual("genshin", both.Candidate.Board);
            Assert.AreEqual(3, both.Baselines["global"].Rank, "the other drop waits too");
            var onlyGlobal = GuessOvertakeRule.Evaluate(b, Standing(4, genshin: 1), "genshin");
            Assert.AreEqual("global", onlyGlobal.Candidate.Board);
        }

        [TestMethod]
        public void Rule_ATieBreakDrop_IsNoNotice()
        {
            // Equal points: the player's own slower solve can put a tied player first. Nobody passed anyone.
            var b = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, Standing(3), null).Baselines;
            var tied = new GuessNeighbour(3, "Ayvruu", null, false, 140, 0);
            var o = GuessOvertakeRule.Evaluate(b, Standing(4, above: tied), null);
            Assert.IsNull(o.Candidate);
            Assert.AreEqual(GuessCheckResult.TieBreak, o.Checks.Single().Result);
            Assert.IsNotNull(GuessOvertakeRule.Evaluate(o.Baselines, Standing(5), null).Candidate);
        }

        [TestMethod]
        public void Rule_ATieBreakDrop_KeepsItsReference_SoALaterPassCountsFromIt()
        {
            var b = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, Standing(3), null).Baselines;
            var tied = new GuessNeighbour(3, "Ayvruu", null, false, 140, 0);
            var o = GuessOvertakeRule.Evaluate(b, Standing(4, above: tied), null);
            Assert.AreEqual(3, o.Baselines["global"].Rank, "kept: re-based, the tied player would count as someone who was always above");
            // The tied player scores again: the same place as at the last read, but someone who was behind now has more points.
            var passed = GuessOvertakeRule.Evaluate(o.Baselines, Standing(4), null);
            Assert.IsNotNull(passed.Candidate, "three players have more points and only two were above");
            Assert.AreEqual(3, passed.Candidate.From);
            Assert.AreEqual(4, passed.Candidate.To);
            // Back at the old place: nothing to tell, and the reference follows.
            var back = GuessOvertakeRule.Evaluate(o.Baselines, Standing(3), null);
            Assert.IsNull(back.Candidate);
            Assert.AreEqual(GuessCheckResult.NothingNew, back.Checks.Single().Result);
        }

        [TestMethod]
        public void Rule_TheCountAhead_DecidesWhenTheNeighbourIsTied()
        {
            // #3, so two players were above. Now #5 behind a tied player.
            var b = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, Standing(3), null).Baselines;
            GuessStanding Now(int? ahead) => new()
            {
                Period = Period,
                Global = new GuessBoardStanding(5, 140, 9, new GuessNeighbour(4, "Ayvruu", null, false, 140, 0), null) { Ahead = ahead },
            };
            Assert.IsNull(GuessOvertakeRule.Evaluate(b, Now(2), null).Candidate, "the same two with more points: both new players above are tie-breaks");
            Assert.IsNull(GuessOvertakeRule.Evaluate(b, Now(null), null).Candidate, "an older Worker: a tied neighbour proves nothing");
            var passed = GuessOvertakeRule.Evaluate(b, Now(3), null);
            Assert.IsNotNull(passed.Candidate, "three with more points, two were above: one of them passed");
            Assert.AreEqual(3, passed.Candidate.From);
            Assert.AreEqual(5, passed.Candidate.To);
            // The count wins over the neighbour: a neighbour with more points next to a count that cannot prove a pass.
            var odd = new GuessStanding { Period = Period, Global = new GuessBoardStanding(4, 140, 9, new GuessNeighbour(3, "X", null, false, 150, 10), null) { Ahead = 2 } };
            Assert.IsNull(GuessOvertakeRule.Evaluate(b, odd, null).Candidate);
        }

        [TestMethod]
        public void Rule_ADropWithNobodyAbove_IsNoNotice()
        {
            var b = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, Standing(3), null).Baselines;
            Assert.IsNull(GuessOvertakeRule.Evaluate(b, Standing(5, noAbove: true), null).Candidate);
            Assert.IsFalse(GuessOvertakeRule.PassedOnPoints(null, 1));
        }

        [TestMethod]
        public void Rule_DeliveredAfterAGameSwitch_RebasesTheNoticesBoard()
        {
            var b = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, Standing(3, genshin: 1), "genshin").Baselines;
            var drop = Standing(3, genshin: 3);
            var o = GuessOvertakeRule.Evaluate(b, drop, "genshin");
            Assert.AreEqual("genshin", o.Candidate.Board);
            // Delivered after the player switched to Star Rail: the Genshin board still starts again.
            var delivered = GuessOvertakeRule.Delivered(o.Baselines, drop, "starrail", o.Candidate.Board);
            Assert.AreEqual(3, delivered["genshin"].Rank);
            Assert.IsNull(GuessOvertakeRule.Evaluate(delivered, drop, "genshin").Candidate, "the same notice never comes back");
        }

        // ── GuessNoticeDelivery: the tray notice, the strip meanwhile ───

        private const string Today = "2026-11-05";

        private static GuessNotice BuildEn(GuessOvertake c) => GuessNoticeText.Build(c, En);

        /// <summary>A delivery with a waiting Gendle drop (3 to 5) found today with Genshin selected.</summary>
        private static (GuessNoticeDelivery d, GuessBaselines b, GuessStanding drop) Waiting()
        {
            var d = new GuessNoticeDelivery();
            var b = d.Read(GuessBaselines.Empty, Standing(2, genshin: 3), "genshin", Today, BuildEn, out _);
            var drop = Standing(2, genshin: 5);
            b = d.Read(b, drop, "genshin", Today, BuildEn, out var c);
            Assert.IsNotNull(c);
            Assert.AreEqual("genshin", d.Notice.Board);
            Assert.IsTrue(d.Undelivered);
            return (d, b, drop);
        }

        [TestMethod]
        public void Delivery_SettingsInFrontWhileSubtitlesRun_IsNoDelivery_TheOwnersCase()
        {
            // 2026-11-10, 15:32 to 15:38 on the owner's PC: the pass was found while subtitles ran, Settings came to the
            // front on Start 33 s later and the notice was marked delivered there. No tray notice ever showed, and the
            // build started three minutes later read "nothing new".
            var (d, b, drop) = Waiting();
            var step = d.Next(Today, true, null, translating: true, gameInFront: false, quns: 5);
            Assert.AreEqual(GuessDeliveryStep.Wait, step.Step, "the strip on Start is where the notice waits, not its delivery");
            Assert.AreEqual(GuessGateResult.Translating, step.Gate);
            Assert.IsTrue(d.Undelivered);
            Assert.AreEqual(3, b["genshin"].Rank, "the place before the pass stays the reference");
            // Kaption restarted before the tray notice could show: the next read finds the same drop again.
            var restarted = new GuessNoticeDelivery();
            restarted.Read(b, drop, "genshin", Today, BuildEn, out var again);
            Assert.IsNotNull(again);
            Assert.AreEqual(GuessDeliveryStep.ShowBalloon, restarted.Next(Today, true, null, false, false, 5).Step,
                "subtitles off, no game in front: the tray notice, with Settings in front or not");
        }

        [TestMethod]
        public void Delivery_WaitsForTheGate_ThenTheBalloon()
        {
            var (d, _, _) = Waiting();
            var wait = d.Next(Today, true, null, false, gameInFront: true, quns: 5);
            Assert.AreEqual(GuessDeliveryStep.Wait, wait.Step);
            Assert.AreEqual(GuessGateResult.GameInFront, wait.Gate);
            Assert.AreEqual(GuessDeliveryStep.Wait, d.Next(Today, true, null, true, false, 5).Step, "subtitles on");
            Assert.AreEqual(GuessDeliveryStep.Wait, d.Next(Today, true, null, false, false, 3).Step, "full screen");
            Assert.AreEqual(GuessDeliveryStep.ShowBalloon, d.Next(Today, true, null, false, false, 5).Step);
        }

        [TestMethod]
        public void Delivery_AfterTheBalloon_TheStripKeepsItsText_AndTheNextReadClearsIt()
        {
            var (d, b, drop) = Waiting();
            Assert.AreEqual(GuessDeliveryStep.ShowBalloon, d.Next(Today, true, null, false, false, 5).Step);
            b = d.Delivered(b, "genshin");
            Assert.IsFalse(d.Undelivered);
            Assert.IsNotNull(d.Notice, "the strip keeps its text until the next read");
            Assert.AreEqual(GuessDeliveryStep.Idle, d.Next(Today, true, Today, false, false, 5).Step);
            d.Read(b, drop, "genshin", Today, BuildEn, out var again);
            Assert.IsNull(again);
            Assert.IsNull(d.Notice, "a read without a drop clears the strip");
        }

        [TestMethod]
        public void Delivery_AGameSwitchWhileItWaits_DoesNotRepeatTheNotice()
        {
            var (d, b, drop) = Waiting();
            b = d.Delivered(b, "starrail"); // the balloon showed after the player picked Star Rail
            d.Read(b, drop, "genshin", "2026-11-06", BuildEn, out var again);
            Assert.IsNull(again, "back on Genshin: the delivered drop is not news");
        }

        [TestMethod]
        public void Delivery_CappedToday_KeepsTheStrip()
        {
            var (d, _, _) = Waiting();
            var capped = d.Next(Today, true, Today, false, false, 5);
            Assert.AreEqual(GuessDeliveryStep.CappedToday, capped.Step);
            Assert.IsTrue(d.Undelivered);
            Assert.IsNotNull(d.Notice);
        }

        [TestMethod]
        public void Delivery_YesterdaysNotice_NoBalloon_UntilTheNextReadFindsTheDropAgain()
        {
            var (d, b, drop) = Waiting();
            Assert.AreEqual(GuessDeliveryStep.Expired, d.Next("2026-11-06", true, null, false, false, 5).Step);
            Assert.IsTrue(d.Undelivered);
            Assert.AreEqual(3, b["genshin"].Rank, "nothing was delivered: the reference stays");
            // The next read still sees the drop and makes it today's notice.
            d.Read(b, drop, "genshin", "2026-11-06", BuildEn, out var again);
            Assert.IsNotNull(again);
            Assert.AreEqual(GuessDeliveryStep.ShowBalloon, d.Next("2026-11-06", true, null, false, false, 5).Step);
        }

        [TestMethod]
        public void Delivery_TurnedOff_Clears_AndTheLobbyRebases()
        {
            var (d, b, _) = Waiting();
            Assert.AreEqual(GuessDeliveryStep.Disabled, d.Next(Today, false, null, false, false, 5).Step);
            b = d.LobbyOpened(b, "starrail");
            Assert.IsNull(d.Notice);
            Assert.AreEqual(5, b["genshin"].Rank, "the notice's own board, though Star Rail is selected now");
            Assert.AreEqual(GuessDeliveryStep.Idle, d.Next(Today, true, null, false, false, 5).Step);
        }

        [TestMethod]
        public void Rule_NoStanding_KeepsTheBaseline()
        {
            var b = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, Standing(3, genshin: 2), "genshin").Baselines;
            var o = GuessOvertakeRule.Evaluate(b, Standing(3, genshin: null), "genshin");
            Assert.IsNull(o.Candidate);
            Assert.AreEqual(2, o.Baselines["genshin"].Rank);
            Assert.AreSame(b, GuessOvertakeRule.Evaluate(b, null, "genshin").Baselines);
        }

        [TestMethod]
        public void Baselines_RoundTripThroughConfig_AndAnEditedFileOnlyResets()
        {
            var b = GuessBaselines.Empty.With("global", new GuessBaseline(Period, 3, 140)).With("GenShin", new GuessBaseline(Period, 2, 85));
            var back = GuessBaselines.Parse(b.ToJson());
            Assert.AreEqual(3, back["global"].Rank);
            Assert.AreEqual(85, back["genshin"].Points);
            Assert.IsNull(GuessBaselines.Parse("not json")["global"]);
            Assert.IsNull(GuessBaselines.Parse("{\"global\":{\"rank\":0,\"period\":\"2026-11\"}}")["global"]);
            Assert.IsNull(GuessBaselines.Parse("[1,2]")["global"]);
        }

        // ── GuessNoticeGate ─────────────────────────────────────────────

        [TestMethod]
        public void Gate_EachConditionBlocks()
        {
            const string today = "2026-11-05";
            Assert.AreEqual(GuessGateResult.Show, GuessNoticeGate.Check(true, null, today, false, false, 5));
            Assert.AreEqual(GuessGateResult.Disabled, GuessNoticeGate.Check(false, null, today, false, false, 5));
            Assert.AreEqual(GuessGateResult.ShownToday, GuessNoticeGate.Check(true, today, today, false, false, 5));
            Assert.AreEqual(GuessGateResult.Translating, GuessNoticeGate.Check(true, null, today, true, false, 5));
            Assert.AreEqual(GuessGateResult.GameInFront, GuessNoticeGate.Check(true, null, today, false, true, 5));
        }

        [TestMethod]
        public void Gate_OnlyAcceptsNotifications_PassesWindows()
        {
            for (int quns = 0; quns <= 8; quns++)
                Assert.AreEqual(quns == 5, GuessNoticeGate.CanShow(true, null, "2026-11-05", false, false, quns), $"QUNS {quns}");
            Assert.IsFalse(GuessNoticeGate.CanShow(true, null, "2026-11-05", false, false, GuessNoticeGate.QunsUnknown), "a failed call waits");
        }

        [TestMethod]
        public void Gate_YesterdaysNotice_AllowsToday()
        {
            Assert.IsTrue(GuessNoticeGate.CanShow(true, "2026-11-04", "2026-11-05", false, false, 5));
            Assert.AreEqual("2026-11-05", GuessNotifySettings.Today(new DateTime(2026, 11, 5, 23, 59, 0)));
        }

        [TestMethod]
        public void Gate_KnowsEveryListedGamesExecutable()
        {
            var names = GameRegistry.Current.Profiles.Select(p => (IReadOnlyCollection<string>)p.ProcessNames).ToList();
            Assert.IsTrue(GuessNoticeGate.IsGameImage("GenshinImpact", names));
            Assert.IsTrue(GuessNoticeGate.IsGameImage("client-win64-shipping", names), "WuWa's Unreal executable, any case");
            Assert.IsFalse(GuessNoticeGate.IsGameImage("explorer", names));
            Assert.IsFalse(GuessNoticeGate.IsGameImage(null, names));
        }

        // ── GuessStanding.Parse ─────────────────────────────────────────

        private const string Contract = @"{
            ""period"": ""2026-11"", ""closesAt"": ""2026-12-01T00:00:00Z"",
            ""player"": { ""name"": ""Crisey"", ""tag"": null, ""hidden"": false },
            ""global"": { ""rank"": 4, ""players"": 4, ""points"": 0, ""solved"": 0, ""played"": 5,
                          ""above"": { ""rank"": 3, ""name"": null, ""tag"": ""4eb5d1"", ""hidden"": false, ""points"": 140, ""gap"": 140 } },
            ""games"": { ""genshin"": { ""rank"": 2, ""players"": 3, ""points"": 85,
                                         ""above"": { ""rank"": 1, ""name"": ""Ayvruu"", ""tag"": null, ""hidden"": false, ""points"": 160, ""gap"": 75 },
                                         ""below"": { ""rank"": 3, ""name"": ""X"", ""hidden"": true, ""points"": 10, ""gap"": 75 } },
                         ""starrail"": null, ""zzz"": null, ""wuwa"": null, ""NewGame"": { ""rank"": 1, ""points"": 5 } },
            ""today"": { ""played"": 2, ""of"": 11, ""solved"": 1 },
            ""cursor"": ""ignored"", ""somethingNew"": [1, 2, 3]
        }";

        [TestMethod]
        public void Parse_ReadsTheContract()
        {
            var s = GuessStanding.Parse(Contract);
            Assert.AreEqual("2026-11", s.Period);
            Assert.AreEqual(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), s.ClosesAt);
            Assert.AreEqual(4, s.Global.Rank);
            Assert.AreEqual("4eb5d1", s.Global.Above.Tag);
            Assert.AreEqual(2, s.Board("genshin").Rank);
            Assert.AreEqual("Ayvruu", s.Board("GENSHIN").Above.Name);
            Assert.IsTrue(s.Board("genshin").Below.Hidden);
            Assert.IsNull(s.Board("genshin").Below.Name, "a hidden player carries no name");
            Assert.IsNull(s.Board("starrail"));
            Assert.AreEqual(1, s.Board("newgame").Rank, "an unknown board is kept, lower-cased");
            Assert.AreEqual(2, s.TodayPlayed);
            Assert.AreEqual(11, s.TodayOf);
        }

        [TestMethod]
        public void Parse_ToleratesWhatIsMissingOrOdd()
        {
            Assert.IsNull(GuessStanding.Parse((string)null));
            Assert.IsNull(GuessStanding.Parse("<html>"));
            Assert.IsNull(GuessStanding.Parse("[]"));
            Assert.IsNull(GuessStanding.Parse("{\"global\":{\"rank\":1}}"), "no period, no standing");
            Assert.IsNull(GuessStanding.Parse(default(JsonElement)), "an empty body");
            var bare = GuessStanding.Parse("{\"period\":\"2026-11\"}");
            Assert.IsNull(bare.Global);
            Assert.AreEqual(0, bare.Games.Count);
            var odd = GuessStanding.Parse("{\"period\":\"2026-11\",\"global\":{\"rank\":\"3\",\"points\":1.6e1},\"games\":[1],\"today\":7}");
            Assert.IsNull(odd.Global, "a rank that is not a number is no standing");
            var noRank = GuessStanding.Parse("{\"period\":\"2026-11\",\"global\":{\"rank\":null,\"points\":0}}");
            Assert.IsNull(noRank.Global, "rank null = no place yet");
            var nums = GuessStanding.Parse("{\"period\":\"2026-11\",\"global\":{\"rank\":3,\"points\":16.0,\"above\":\"x\"}}");
            Assert.AreEqual(16, nums.Global.Points);
            Assert.IsNull(nums.Global.Above);
        }

        [TestMethod]
        public void Parse_AcceptsTheReviewsEarlierShape()
        {
            var s = GuessStanding.Parse("{\"period\":\"2026-11\",\"global\":{\"rank\":5,\"points\":140},\"game\":{\"id\":\"Genshin\",\"standing\":{\"rank\":2,\"points\":140}}}");
            Assert.AreEqual(2, s.Board("genshin").Rank);
        }

        // ── Words and links ─────────────────────────────────────────────

        [TestMethod]
        public void Text_NamesTheBoardAndThePlayerAbove()
        {
            var n = GuessNoticeText.Build(new GuessOvertake("genshin", 3, 5, new GuessNeighbour(4, "Ayvruu", null, false, 160, 20)), En);
            Assert.AreEqual("Someone passed you in Gendle", n.Title);
            Assert.AreEqual("You're now #5 on this month's Gendle board, down from #3. Just ahead of you: Ayvruu.", n.Body);
            var g = GuessNoticeText.Build(new GuessOvertake("global", 2, 3, new GuessNeighbour(2, null, "4eb5d1", false, 140, 0)), En);
            Assert.AreEqual("Someone passed you in the overall ranking", g.Title);
            StringAssert.EndsWith(g.Body, "Just ahead of you: Player 4eb5d1.");
            var hidden = GuessNoticeText.Build(new GuessOvertake("wuwa", 1, 2, new GuessNeighbour(1, null, null, true, 9, 1)), En);
            Assert.AreEqual("You're now #2 on this month's Wuwdle board, down from #1.", hidden.Body, "a hidden player is not named");
            Assert.AreEqual("You're now #2 on this month's Wuwdle board, down from #1.",
                GuessNoticeText.Build(new GuessOvertake("wuwa", 1, 2, null), En).Body);
        }

        [TestMethod]
        public void Text_ReadsPolishFromTheDictionary_AndAlwaysFitsTheBalloon()
        {
            var pl = Dictionary("Strings.pl-PL.xaml");
            string L(string k, string f) => pl.TryGetValue(k, out var v) ? v : f;
            var n = GuessNoticeText.Build(new GuessOvertake("genshin", 3, 5, new GuessNeighbour(4, null, "4eb5d1", false, 160, 20)), L);
            Assert.AreEqual("Ktoś Cię wyprzedził w Gendle", n.Title);
            Assert.AreEqual("Spadasz z 3. na 5. miejsce w miesięcznym rankingu Gendle. Tuż przed Tobą: Gracz 4eb5d1.", n.Body);
            var g = GuessNoticeText.Build(new GuessOvertake("global", 1, 2, null), L);
            Assert.AreEqual("Spadasz z 1. na 2. miejsce w miesięcznym rankingu ogólnym.", g.Body);
            var longName = GuessNoticeText.Build(new GuessOvertake("genshin", 1, 2, new GuessNeighbour(1, new string('x', 400), null, false, 1, 1)), L);
            Assert.IsTrue(longName.Body.Length <= 255 && longName.Title.Length <= 63);
        }

        [TestMethod]
        public void GuessNames_ComeFromTheManifests()
        {
            Assert.AreEqual("Gendle", GameRegistry.Current.FindByWireId("genshin").Site.GuessName);
            CollectionAssert.AreEqual(new[] { "Gendle", "Stardle", "Zendle", "Wuwdle" }, GuessNoticeText.AllGuessNames().ToArray());
            Assert.AreEqual("A, B and C", GuessNoticeText.JoinNames(new[] { "A", "B", "C" }, " and "));
            Assert.AreEqual("A", GuessNoticeText.JoinNames(new[] { "A" }, " i "));
            Assert.IsNull(GuessNoticeText.GuessNameOf("nope"));
        }

        [TestMethod]
        public void Links_FollowTheInterfaceLanguage_AndStayOnTheSite()
        {
            Assert.AreEqual("https://kaption.one/pl/zgadnij/rankingowa/", GuessLinks.RankedLobby("pl-PL"));
            Assert.AreEqual("https://kaption.one/guess/ranked/", GuessLinks.RankedLobby("en-US"));
            Assert.AreEqual("https://kaption.one/pl/zgadnij/", GuessLinks.Play("pl-PL"));
            Assert.AreEqual("https://kaption.one/guess/", GuessLinks.Play(null));
            Assert.IsTrue(GuessLinks.IsSitePage(GuessLinks.RankedLobby("pl-PL")));
            Assert.IsFalse(GuessLinks.IsSitePage("http://kaption.one/guess/"));
            Assert.IsFalse(GuessLinks.IsSitePage("https://kaption.one.evil.example/"));
        }

        [TestMethod]
        public void FixtureUrl_IsLocalHttpOrHttpsOnly()
        {
            Assert.IsTrue(GuessNotifySettings.IsAllowedFixtureUrl("http://127.0.0.1:8787/standing"));
            Assert.IsTrue(GuessNotifySettings.IsAllowedFixtureUrl("http://localhost:8787/standing"));
            Assert.IsTrue(GuessNotifySettings.IsAllowedFixtureUrl("https://example.org/standing.json"));
            Assert.IsFalse(GuessNotifySettings.IsAllowedFixtureUrl("http://example.org/standing"));
            Assert.IsFalse(GuessNotifySettings.IsAllowedFixtureUrl("file:///C:/x.json"));
            Assert.IsFalse(GuessNotifySettings.IsAllowedFixtureUrl(""));
        }

        // ── GuessStandingPoller: schedule and the 401 path ──────────────

        [TestMethod]
        public void Poller_Schedule()
        {
            var s = GuessPollSchedule.Default;
            Assert.AreEqual(TimeSpan.FromMinutes(2), s.FirstDelay);
            Assert.AreEqual(TimeSpan.FromHours(4), GuessStandingPoller.NextDelay(GuessPollOutcome.Read, 0, s));
            Assert.AreEqual(TimeSpan.FromHours(12), GuessStandingPoller.NextDelay(GuessPollOutcome.NoStanding, 0, s));
            Assert.AreEqual(TimeSpan.FromHours(4), GuessStandingPoller.NextDelay(GuessPollOutcome.Refused, 0, s));
            Assert.AreEqual(TimeSpan.FromMinutes(15), GuessStandingPoller.NextDelay(GuessPollOutcome.NoSession, 0, s));
            Assert.AreEqual(TimeSpan.FromMinutes(15), GuessStandingPoller.NextDelay(GuessPollOutcome.Failed, 1, s));
            Assert.AreEqual(TimeSpan.FromHours(1), GuessStandingPoller.NextDelay(GuessPollOutcome.Failed, 2, s));
            Assert.AreEqual(TimeSpan.FromHours(4), GuessStandingPoller.NextDelay(GuessPollOutcome.Failed, 9, s));
            Assert.AreEqual(TimeSpan.FromSeconds(60), s.GateRetry);
        }

        [TestMethod]
        public async Task Poller_NoSession_SendsNothing()
        {
            int calls = 0;
            using var p = new GuessStandingPoller(_ => { calls++; return Task.FromResult<GuessStanding>(null); }, () => false);
            Assert.AreEqual(GuessPollOutcome.NoSession, await p.PollOnceAsync(CancellationToken.None));
            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public async Task Poller_RaisesEveryParsedStanding_OnlyAPlaceIsARead()
        {
            var seen = new List<GuessStanding>();
            GuessStanding next = Standing(null);
            using var p = new GuessStandingPoller(_ => Task.FromResult(next), () => true);
            p.StandingRead += seen.Add;
            Assert.AreEqual(GuessPollOutcome.NoStanding, await p.PollOnceAsync(CancellationToken.None));
            next = Standing(3);
            Assert.AreEqual(GuessPollOutcome.Read, await p.PollOnceAsync(CancellationToken.None));
            Assert.AreEqual(2, seen.Count, "a read without a place still replaces the strip");
            next = null;
            Assert.AreEqual(GuessPollOutcome.NoStanding, await p.PollOnceAsync(CancellationToken.None));
            Assert.AreEqual(2, seen.Count, "nothing parsed, nothing raised");
            Assert.AreEqual(GuessPollOutcome.Failed, await new GuessStandingPoller(_ => throw new ApiUnavailableException("down", new Exception()), () => true)
                .PollOnceAsync(CancellationToken.None));
        }

        [TestMethod]
        public async Task Poller_401And403_AreSkipped_NotFailures()
        {
            using var p401 = new GuessStandingPoller(_ => throw new UnauthorizedException("expired"), () => true);
            Assert.AreEqual(GuessPollOutcome.Refused, await p401.PollOnceAsync(CancellationToken.None));
            using var p403 = new GuessStandingPoller(_ => throw new ForbiddenException("no"), () => true);
            Assert.AreEqual(GuessPollOutcome.Refused, await p403.PollOnceAsync(CancellationToken.None));
        }

        /// <summary>The whole client path against a local socket: a 401 from the standing route is an UnauthorizedException the poller skips.</summary>
        [TestMethod]
        public async Task Client_A401FromTheRoute_IsSkipped_AndA200IsParsed()
        {
            string Response(int code, string body) =>
                $"HTTP/1.1 {code} X\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
            using var server = new OneShotHttp(Response(401, "{\"error\":\"unauthorized\",\"message\":\"no session\"}"));
            var api = new KaptionApiClient(server.BaseUrl);
            using var poller = new GuessStandingPoller(ct => api.GetGuessStandingAsync("jwt", "genshin", ct), () => true);
            Assert.AreEqual(GuessPollOutcome.Refused, await poller.PollOnceAsync(CancellationToken.None));
            StringAssert.StartsWith(server.RequestLine, "GET /api/app/guess/standing?game=genshin ");
            StringAssert.Contains(server.Headers, "Authorization: Bearer jwt");

            using var ok = new OneShotHttp(Response(200, Contract));
            var s = await new KaptionApiClient(ok.BaseUrl).GetGuessStandingAsync(null, "zzz", CancellationToken.None, fixtureUrl: ok.BaseUrl + "/fixture");
            Assert.AreEqual(4, s.Global.Rank);
            StringAssert.StartsWith(ok.RequestLine, "GET /fixture?game=zzz ");
            Assert.IsFalse(ok.Headers.Contains("Authorization"), "a fixture never gets the session");
        }

        [TestMethod]
        public void TheFeature_HasNoPathToTheLicence()
        {
            string root = RenderTestKit.RepoRoot();
            var files = Directory.GetFiles(Path.Combine(root, "GI-Subtitles", "Services", "Guess"), "*.cs")
                .Append(Path.Combine(root, "GI-Subtitles", "Views", "MainWindow.GuessNotify.cs"))
                .Append(Path.Combine(root, "GI-Subtitles", "Views", "SettingsWindow.GuessNotify.cs")).ToList();
            Assert.IsTrue(files.Count >= 7);
            foreach (string f in files)
            {
                string code = File.ReadAllText(f);
                Assert.IsFalse(code.Contains("ReportRemoteRevocation"), $"{Path.GetFileName(f)} must never end the session");
                Assert.IsFalse(code.Contains("ForceReauth"), Path.GetFileName(f));
            }
            Assert.IsFalse(File.ReadAllText(Path.Combine(root, "GI-Subtitles", "Services", "Guess", "GuessNotifySettings.cs")).Contains("\"ApiUrl\""),
                "the fixture override never touches ApiUrl");
        }

        // ── The place after the last score (2026-11-09 report) ─────────

        private static readonly DateTimeOffset LastScore = new(2026, 11, 6, 0, 4, 37, TimeSpan.Zero);
        private static readonly DateTimeOffset ReadAt = new(2026, 11, 8, 23, 53, 55, TimeSpan.Zero);

        /// <summary>The owner's read on 2026-11-09: #3 on both boards, #1 after the last score, the players above with more points.</summary>
        private static GuessStanding OwnersRead(DateTimeOffset? at = null, GuessPlace lastScored = null, string period = Period)
        {
            lastScored ??= new GuessPlace(1, 220, LastScore);
            var games = new Dictionary<string, GuessBoardStanding>
            {
                ["genshin"] = new GuessBoardStanding(3, 220, 6, new GuessNeighbour(2, null, "f1b010", false, 240, 20), null) { LastScored = lastScored },
                ["starrail"] = null, ["zzz"] = null, ["wuwa"] = null,
            };
            return new GuessStanding
            {
                Period = period,
                At = at ?? ReadAt,
                Global = new GuessBoardStanding(3, 220, 6, new GuessNeighbour(2, null, "9c228e", false, 260, 40), null) { LastScored = lastScored },
                Games = games,
                PlayedThisMonth = true,
            };
        }

        [TestMethod]
        public void Rule_APassWhileTheAppWasClosed_IsReportedFromThePlaceAfterTheLastScore_TheOwnersReport()
        {
            // What the owner's Config held after 2.5.8's first read: #3 on both boards, stored without a time.
            var stored = GuessBaselines.Parse("{\"genshin\":{\"period\":\"2026-11\",\"rank\":3,\"points\":220},\"global\":{\"period\":\"2026-11\",\"rank\":3,\"points\":220}}");
            foreach (var start in new[] { stored, GuessBaselines.Empty })
            {
                var o = GuessOvertakeRule.Evaluate(start, OwnersRead(), "genshin");
                Assert.IsNotNull(o.Candidate, "passed twice while Kaption was closed");
                Assert.AreEqual("genshin", o.Candidate.Board, "the selected game's board first");
                Assert.AreEqual(1, o.Candidate.From);
                Assert.AreEqual(3, o.Candidate.To);
                Assert.AreEqual(240, o.Candidate.Above.Points);
                Assert.AreEqual(1, o.Baselines["genshin"].Rank, "the reference stays until the notice is delivered");
                Assert.AreEqual(LastScore, o.Baselines["genshin"].At);
                Assert.AreEqual(GuessCheckResult.Passed, o.Checks.Single(c => c.Board == "genshin").Result);
                Assert.IsTrue(o.Checks.Single(c => c.Board == "genshin").FromLastScore);
            }
        }

        /// <summary>
        /// The owner's read on 2026-11-10 at 14:22Z: #5 with 220 on both boards, #1 after the last score. Three players
        /// have more points (430, 260, 240) and one with the same 220 sits directly above on the tie-break.
        /// </summary>
        private static GuessStanding OwnersTiedRead(int? ahead, bool lastScored = true, DateTimeOffset? at = null)
        {
            var tied = new GuessNeighbour(4, null, "9412ec", false, 220, 0);
            GuessBoardStanding Board() => new(5, 220, 7, tied, null) { LastScored = lastScored ? new GuessPlace(1, 220, LastScore) : null, Ahead = ahead };
            return new GuessStanding
            {
                Period = Period,
                At = at ?? new DateTimeOffset(2026, 11, 10, 14, 22, 1, TimeSpan.Zero),
                Global = Board(),
                Games = new Dictionary<string, GuessBoardStanding> { ["genshin"] = Board(), ["starrail"] = null, ["zzz"] = null, ["wuwa"] = null },
                PlayedThisMonth = true,
            };
        }

        [TestMethod]
        public void Rule_APassHiddenBehindATiedNeighbour_IsFoundFromTheCountAhead_TheOwnersRead()
        {
            // 2.5.9 wrote this read off as "a tie-break only" and re-based on #5: three passes, no notice, lost for good.
            var o = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, OwnersTiedRead(ahead: 3), "genshin");
            Assert.IsNotNull(o.Candidate, "three players have more points; nobody was above after the last score");
            Assert.AreEqual("genshin", o.Candidate.Board);
            Assert.AreEqual(1, o.Candidate.From);
            Assert.AreEqual(5, o.Candidate.To);
            Assert.AreEqual(GuessCheckResult.Passed, o.Checks[0].Result);

            // A Worker that does not send the count: nothing is proven, so no notice, but the place after the last score
            // stays the reference...
            var old = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, OwnersTiedRead(ahead: null), "genshin");
            Assert.IsNull(old.Candidate);
            Assert.AreEqual(GuessCheckResult.TieBreak, old.Checks[0].Result);
            Assert.AreEqual(1, old.Baselines["genshin"].Rank);
            Assert.AreEqual(LastScore, old.Baselines["genshin"].At);
            // ...and the first read that carries the count reports the whole drop (no newer score, so no lastScored).
            var later = GuessOvertakeRule.Evaluate(old.Baselines, OwnersTiedRead(ahead: 3, lastScored: false, at: ReadAt.AddDays(3)), "genshin");
            Assert.IsNotNull(later.Candidate);
            Assert.AreEqual(1, later.Candidate.From);
            Assert.AreEqual(5, later.Candidate.To);
        }

        [TestMethod]
        public void Rule_AKeptPlaceFrom258_IsStampedOnce_SoTheServerIsNotAskedToReplayEveryRead()
        {
            // 2.5.8 told the player at #2 with 220 on 2026-11-10 and stored that place without a time. The first read
            // after the update: #3 behind a player with the same points, and the place after the last score (#1, from
            // the 6th) is the drop already told. The #2 stays the reference, and it must get a time: without one every
            // read sends "genshin:0" and the server replays the board again.
            var stored = GuessBaselines.Parse("{\"genshin\":{\"period\":\"2026-11\",\"rank\":2,\"points\":220}}");
            var read = OwnersTiedRead(ahead: null);
            var tied = read.Board("genshin") with { Rank = 3, Above = new GuessNeighbour(2, null, "9412ec", false, 220, 0) };
            var first = new GuessStanding { Period = Period, At = read.At, Global = null, Games = new Dictionary<string, GuessBoardStanding> { ["genshin"] = tied }, PlayedThisMonth = true };
            var o = GuessOvertakeRule.Evaluate(stored, first, "genshin", "2026-11-10", UtcDay);
            Assert.IsNull(o.Candidate);
            Assert.AreEqual(GuessCheckResult.TieBreak, o.Checks[0].Result);
            Assert.IsFalse(o.Checks[0].FromLastScore, "the told place, not the one before it");
            Assert.AreEqual(2, o.Baselines["genshin"].Rank, "the reference stays");
            Assert.AreEqual(220, o.Baselines["genshin"].Points);
            Assert.AreEqual(read.At, o.Baselines["genshin"].At, "stamped with the read's server time");
            StringAssert.Contains(GuessOvertakeRule.Since(o.Baselines, "genshin"), "genshin:2026-11-10T14:22:01.000Z");
            // A later pass is still measured from the kept place.
            var passed = new GuessStanding
            {
                Period = Period, At = read.At.Value.AddHours(4), PlayedThisMonth = true,
                Games = new Dictionary<string, GuessBoardStanding> { ["genshin"] = new GuessBoardStanding(4, 220, 7, new GuessNeighbour(3, null, "9412ec", false, 220, 0), null) { Ahead = 2 } },
            };
            var later = GuessOvertakeRule.Evaluate(o.Baselines, passed, "genshin", "2026-11-10", UtcDay);
            Assert.IsNotNull(later.Candidate);
            Assert.AreEqual(2, later.Candidate.From);
            Assert.AreEqual(4, later.Candidate.To);
        }

        [TestMethod]
        public void Rule_ThePlaceAfterTheLastScore_LosesToANewerReadOfTheApp()
        {
            // The app saw #2 after the last score: a drop is measured from #2, not from #1.
            var b = GuessBaselines.Empty.With("global", new GuessBaseline(Period, 2, 220, new DateTimeOffset(2026, 11, 7, 20, 0, 0, TimeSpan.Zero)));
            var o = GuessOvertakeRule.Evaluate(b, OwnersRead(), null);
            Assert.AreEqual(2, o.Candidate.From);
            Assert.IsFalse(o.Checks.Single().FromLastScore);
        }

        [TestMethod]
        public void Rule_ADeliveredPass_NeverComesBack_EvenWithThePlaceAfterTheLastScore()
        {
            var read = OwnersRead();
            var o = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, read, "genshin");
            var delivered = GuessOvertakeRule.Delivered(o.Baselines, read, "genshin", o.Candidate.Board);
            Assert.AreEqual(ReadAt, delivered["genshin"].At, "stamped with the server's time of the read");
            Assert.AreEqual(3, delivered["global"].Rank);
            Assert.IsNull(GuessOvertakeRule.Evaluate(delivered, OwnersRead(ReadAt.AddHours(4)), "genshin").Candidate);
            Assert.AreEqual(GuessCheckResult.NothingNew, GuessOvertakeRule.Evaluate(delivered, OwnersRead(ReadAt.AddHours(4)), "genshin").Checks[0].Result);
        }

        [TestMethod]
        public void Rule_ScoringAgain_MovesTheReference()
        {
            var o = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, OwnersRead(), "genshin");
            var delivered = GuessOvertakeRule.Delivered(o.Baselines, OwnersRead(), "genshin", "genshin");
            // A new score at #3: nothing new; a later read without the field keeps measuring from there.
            var again = OwnersRead(ReadAt.AddDays(1), new GuessPlace(3, 220, ReadAt.AddHours(10)));
            var p = GuessOvertakeRule.Evaluate(delivered, again, "genshin");
            Assert.IsNull(p.Candidate);
            Assert.AreEqual(3, p.Baselines["genshin"].Rank);
        }

        [TestMethod]
        public void Rule_ATieBreakBelowThePlaceAfterTheLastScore_IsNoNotice()
        {
            var read = OwnersRead();
            var tied = read.Global with { Above = new GuessNeighbour(2, "X", null, false, 220, 0) };
            var o = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, new GuessStanding { Period = Period, At = ReadAt, Global = tied, Games = read.Games }, null);
            Assert.IsNull(o.Candidate);
            Assert.AreEqual(GuessCheckResult.TieBreak, o.Checks[0].Result);
            Assert.AreEqual(1, o.Baselines["global"].Rank, "the place after the last score stays the reference");
            Assert.AreEqual(LastScore, o.Baselines["global"].At);
        }

        [TestMethod]
        public void Rule_AStaleStandingWithAFreshPlaceAfterTheLastScore_IsNoPass()
        {
            // A colo's 30-second copy says #3 with 220; the replay says #2 with 260 after a solve the copy predates.
            // Paired they would read "passed, 2 → 3" for a climb. Points that differ mean the two are not the same moment.
            var stale = OwnersRead(lastScored: new GuessPlace(2, 260, ReadAt.AddMinutes(-1)));
            var stamped = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
            foreach (var start in new[]
                     {
                         GuessBaselines.Empty,
                         GuessBaselines.Empty.With("global", new GuessBaseline(Period, 3, 220)),
                         GuessBaselines.Empty.With("global", new GuessBaseline(Period, 3, 220, stamped)),
                     })
            {
                var o = GuessOvertakeRule.Evaluate(start, stale, null);
                Assert.IsNull(o.Candidate);
                var check = o.Checks.Single();
                Assert.IsFalse(check.FromLastScore, "ignored");
                Assert.AreEqual(GuessCheckResult.StaleCopy, check.Result);
                // The stored place stays as it was: re-based on the stale place stamped with the read's time, the next
                // `since` would be newer than the score, the server would not replay it, and a pass after it would be lost.
                Assert.AreEqual(start["global"], o.Baselines["global"]);
                Assert.AreEqual(GuessOvertakeRule.Since(start, null), GuessOvertakeRule.Since(o.Baselines, null));
                StringAssert.Contains(GuessOvertakeRule.Describe(stale, o, TimeSpan.FromHours(4)), "stale copy");
            }
        }

        [TestMethod]
        public void Rule_AStaleStandingSkippedOnce_ThePassAfterTheScoreIsStillFound()
        {
            // The reviewer's scenario: #3 with 220 stored at 10:00; the player solves (#2 with 260); a stale read; then
            // someone passes them (#3 with 260). The pass must be told from #2, not lost against a re-based #3.
            var start = GuessBaselines.Empty.With("global", new GuessBaseline(Period, 3, 220, new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero)));
            var scored = new GuessPlace(2, 260, ReadAt.AddMinutes(-1));
            var o = GuessOvertakeRule.Evaluate(start, OwnersRead(lastScored: scored), null);
            Assert.IsNull(o.Candidate);
            // Four hours later, fresh: #3 with 260, the player above has more.
            var later = new GuessStanding
            {
                Period = Period, At = ReadAt.AddHours(4), PlayedThisMonth = true, Games = OwnersRead().Games,
                Global = new GuessBoardStanding(3, 260, 6, new GuessNeighbour(2, null, "9c228e", false, 300, 40), null) { LastScored = scored },
            };
            var p = GuessOvertakeRule.Evaluate(o.Baselines, later, null);
            Assert.AreEqual(2, p.Candidate?.From);
            Assert.AreEqual(3, p.Candidate.To);
        }

        [TestMethod]
        public void Rule_ADeliveryNeverStampsAStaleBoard()
        {
            // The notice's own board passed on this read; the global board on it is a stale copy. Delivery re-bases the
            // notice's board and leaves the stale one's stored place alone, for the same reason as in Evaluate.
            var stamped = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
            var start = GuessBaselines.Empty
                .With("genshin", new GuessBaseline(Period, 1, 220, stamped))
                .With("global", new GuessBaseline(Period, 3, 220, stamped));
            var read = OwnersRead(); // Genshin's last score (Nov 6) predates its stored place: measured from #1
            read = new GuessStanding
            {
                Period = read.Period, At = read.At, PlayedThisMonth = true, Games = read.Games,
                Global = new GuessBoardStanding(3, 220, 6, new GuessNeighbour(2, null, "9c228e", false, 260, 40), null)
                { LastScored = new GuessPlace(2, 260, ReadAt.AddMinutes(-1)) },
            };
            var o = GuessOvertakeRule.Evaluate(start, read, "genshin");
            Assert.AreEqual("genshin", o.Candidate?.Board);
            var delivered = GuessOvertakeRule.Delivered(o.Baselines, read, "genshin", o.Candidate.Board);
            Assert.AreEqual(new GuessBaseline(Period, 3, 220, ReadAt), delivered["genshin"]);
            Assert.AreEqual(start["global"], delivered["global"], "the stale board keeps its stored place");
        }

        [TestMethod]
        public void Rule_ANoticeAroundTheMonthsUtcEdge_CountsAsThisMonths()
        {
            // The month is UTC, the notice day is local. Stored without a time by 2.5.8 right after the notice.
            var told = GuessBaselines.Parse("{\"global\":{\"period\":\"2026-11\",\"rank\":3,\"points\":220}}");
            // UTC+1: told at 00:30 local on Dec 1, still November in UTC. The last score was on Nov 6.
            Func<DateTimeOffset, string> plusOne = t => t.ToOffset(TimeSpan.FromHours(1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var o = GuessOvertakeRule.Evaluate(told, OwnersRead(at: new DateTimeOffset(2026, 11, 30, 23, 45, 0, TimeSpan.Zero)), null,
                lastShownDay: "2026-12-01", localDay: plusOne);
            Assert.IsNull(o.Candidate, "told on Dec 1 local, inside November UTC: the same drop");
            // UTC-5: told at 20:00 local on Nov 30, already December in UTC; the score was 21:00 local on Nov 30 (Dec 1 02:00Z).
            var dec = GuessBaselines.Parse("{\"global\":{\"period\":\"2026-12\",\"rank\":3,\"points\":220}}");
            Func<DateTimeOffset, string> minusFive = t => t.ToOffset(TimeSpan.FromHours(-5)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var read = OwnersRead(at: new DateTimeOffset(2026, 12, 1, 3, 0, 0, TimeSpan.Zero), period: "2026-12",
                                  lastScored: new GuessPlace(1, 220, new DateTimeOffset(2026, 12, 1, 0, 30, 0, TimeSpan.Zero)));
            Assert.IsNull(GuessOvertakeRule.Evaluate(dec, read, null, lastShownDay: "2026-11-30", localDay: minusFive).Candidate,
                "told on Nov 30 local, inside December UTC: the same drop");
            // A notice from the local day before the month began is last month's.
            Assert.IsNotNull(GuessOvertakeRule.Evaluate(dec, read, null, lastShownDay: "2026-11-29", localDay: minusFive).Candidate);
        }

        [TestMethod]
        public void Rule_APlayerAlreadyToldUnder258_IsNotToldTheSameDropAgain()
        {
            // 2.5.8 delivered "1 → 3" on Nov 8 and stored #3 without a time. The first 2.5.9 read brings the place after
            // the Nov 6 score (#1): the notice must not come back.
            var told = GuessBaselines.Parse("{\"genshin\":{\"period\":\"2026-11\",\"rank\":3,\"points\":220},\"global\":{\"period\":\"2026-11\",\"rank\":3,\"points\":220}}");
            var o = GuessOvertakeRule.Evaluate(told, OwnersRead(), "genshin", lastShownDay: "2026-11-08");
            Assert.IsNull(o.Candidate);
            Assert.IsTrue(o.Checks.All(c => c.Result == GuessCheckResult.NothingNew && !c.FromLastScore));
            Assert.AreEqual(ReadAt, o.Baselines["genshin"].At, "stamped now, so the next read measures from here");
            // A score after that notice is newer than it: a pass after the new score is a new pass.
            var scoredAfter = OwnersRead(lastScored: new GuessPlace(1, 220, new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero)));
            var p = GuessOvertakeRule.Evaluate(told, scoredAfter, "genshin", lastShownDay: "2026-11-07");
            Assert.AreEqual(1, p.Candidate?.From);
            // A notice from last month says nothing about this month's place (the owner's case has none at all).
            Assert.IsNotNull(GuessOvertakeRule.Evaluate(told, OwnersRead(), "genshin", lastShownDay: "2026-10-30").Candidate);
            Assert.IsNotNull(GuessOvertakeRule.Evaluate(told, OwnersRead(), "genshin", lastShownDay: null).Candidate);
        }

        private static readonly Func<DateTimeOffset, string> UtcDay = t => t.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        [TestMethod]
        public void Rule_ToldUnder258_ThenScoredHigherTheSameDayAndPassed_IsANewPass()
        {
            // sol S1 (2026-11-09): 2.5.8 told the player at #3 with 140 on Nov 8 and stored that place without a time.
            // Later that day they scored again (#1 with 200) and were passed back to #3. The first 2.5.9 read must
            // report 1 -> 3: more points after the last score than in the stored place prove a score after it.
            var told = GuessBaselines.Parse("{\"global\":{\"period\":\"2026-11\",\"rank\":3,\"points\":140}}");
            var climbed = new GuessPlace(1, 200, new DateTimeOffset(2026, 11, 8, 18, 0, 0, TimeSpan.Zero));
            var read = new GuessStanding
            {
                Period = Period, At = ReadAt, PlayedThisMonth = true, Games = new Dictionary<string, GuessBoardStanding>(),
                Global = new GuessBoardStanding(3, 200, 6, new GuessNeighbour(2, null, "9c228e", false, 240, 40), null) { LastScored = climbed },
            };
            var o = GuessOvertakeRule.Evaluate(told, read, null, lastShownDay: "2026-11-08", localDay: UtcDay);
            Assert.IsNotNull(o.Candidate, "a pass after a newer score is a new pass");
            Assert.AreEqual(1, o.Candidate.From);
            Assert.AreEqual(3, o.Candidate.To);
            var check = o.Checks.Single();
            Assert.AreEqual(GuessCheckResult.Passed, check.Result);
            Assert.IsTrue(check.FromLastScore);
            Assert.AreEqual(new GuessBaseline(Period, 1, 200, climbed.At), o.Baselines["global"], "the reference stays until delivery");
        }

        [TestMethod]
        public void Rule_ToldUnder258_TheSameDayWithTheSamePoints_IsNotToldAgain()
        {
            // 2.5.8 told "1 -> 3" on Nov 8 after a score earlier that day (#1 with 220) and stored #3 with 220 without a
            // time. The first 2.5.9 read replays that score: the same points, the notice's day, so the same drop. Quiet.
            var told = GuessBaselines.Parse("{\"global\":{\"period\":\"2026-11\",\"rank\":3,\"points\":220}}");
            var read = OwnersRead(lastScored: new GuessPlace(1, 220, new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero)));
            var o = GuessOvertakeRule.Evaluate(told, read, null, lastShownDay: "2026-11-08", localDay: UtcDay);
            Assert.IsNull(o.Candidate, "never the same drop twice");
            var check = o.Checks.Single();
            Assert.AreEqual(GuessCheckResult.NothingNew, check.Result);
            Assert.IsFalse(check.FromLastScore);
            Assert.AreEqual(new GuessBaseline(Period, 3, 220, ReadAt), o.Baselines["global"], "stamped now");
        }

        [TestMethod]
        public void Rule_ANewMonth_LastMonthsPlaceGivesWayToThisMonthsLastScore()
        {
            var october = GuessBaselines.Empty
                .With("global", new GuessBaseline("2026-10", 1, 900, new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero)))
                .With("genshin", new GuessBaseline("2026-10", 2, 500));
            var o = GuessOvertakeRule.Evaluate(october, OwnersRead(), "genshin", lastShownDay: "2026-10-31");
            Assert.AreEqual("genshin", o.Candidate?.Board);
            Assert.AreEqual(1, o.Candidate.From, "this month's place after the last score, not October's #2");
            Assert.AreEqual(Period, o.Baselines["global"].Period);
            // Without this month's last score: a new month starts quietly, as before.
            var plain = new GuessStanding { Period = Period, At = ReadAt, Global = OwnersRead().Global with { LastScored = null }, Games = new Dictionary<string, GuessBoardStanding>() };
            Assert.IsNull(GuessOvertakeRule.Evaluate(october, plain, null).Candidate);
        }

        [TestMethod]
        public void Rule_AVoidOfThePlayersOwnResult_IsNoPass()
        {
            // The app saw #1 with 260; an admin voided one of the player's own results (now 220) and the player with 240
            // is above. Nobody passed them: their own points fell.
            var b = GuessBaselines.Empty.With("global", new GuessBaseline(Period, 1, 260, new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero)));
            var voided = new GuessStanding
            {
                Period = Period, At = ReadAt, Games = new Dictionary<string, GuessBoardStanding>(),
                Global = new GuessBoardStanding(2, 220, 6, new GuessNeighbour(1, "X", null, false, 240, 20), null),
            };
            var o = GuessOvertakeRule.Evaluate(b, voided, null);
            Assert.IsNull(o.Candidate);
            Assert.AreEqual(GuessCheckResult.OwnPointsFell, o.Checks.Single().Result);
            Assert.AreEqual(2, o.Baselines["global"].Rank, "re-based: a later real pass counts from here");
            StringAssert.Contains(GuessOvertakeRule.Describe(voided, o, TimeSpan.FromHours(4)), "own points fell");
        }

        [TestMethod]
        public void Since_IsPerBoard_ZeroForABoardWithoutAStampedPlace()
        {
            Assert.AreEqual("genshin:0,global:0", GuessOvertakeRule.Since(GuessBaselines.Empty, "genshin"));
            var unstamped = GuessBaselines.Empty.With("global", new GuessBaseline(Period, 3, 220));
            Assert.AreEqual("genshin:0,global:0", GuessOvertakeRule.Since(unstamped, "genshin"), "a baseline from 2.5.7 or 2.5.8 has no time");
            var b = GuessBaselines.Empty
                .With("global", new GuessBaseline(Period, 3, 220, ReadAt))
                .With("genshin", new GuessBaseline(Period, 3, 220, LastScore));
            Assert.AreEqual("genshin:2026-11-06T00:04:37.000Z,global:2026-11-08T23:53:55.000Z", GuessOvertakeRule.Since(b, "genshin"));
            Assert.AreEqual("global:2026-11-08T23:53:55.000Z", GuessOvertakeRule.Since(b, null), "the global board only");
            // A newly selected game: its board is replayed once, the global board keeps its own stamp.
            Assert.AreEqual("starrail:0,global:2026-11-08T23:53:55.000Z", GuessOvertakeRule.Since(b, "starrail"));
        }

        [TestMethod]
        public void Baselines_KeepTheirServerTime()
        {
            var b = GuessBaselines.Empty.With("global", new GuessBaseline(Period, 3, 220, LastScore));
            string json = b.ToJson();
            StringAssert.Contains(json, "\"at\":\"2026-11-06T00:04:37.000Z\"");
            Assert.AreEqual(LastScore, GuessBaselines.Parse(json)["global"].At);
            Assert.IsNull(GuessBaselines.Parse("{\"global\":{\"period\":\"2026-11\",\"rank\":3,\"points\":220,\"at\":\"soon\"}}")["global"].At);
        }

        [TestMethod]
        public void Parse_ReadsThePlaceAfterTheLastScore_TheServersTime_AndTheCadence()
        {
            var s = GuessStanding.Parse(@"{""period"":""2026-11"",""at"":""2026-11-08T23:53:55.000Z"",""poll"":{""minutes"":60,""idleMinutes"":180},
                ""global"":{""rank"":3,""points"":220,""played"":9,""lastScored"":{""rank"":1,""points"":220,""at"":""2026-11-06T00:04:37.000Z""}},
                ""games"":{""genshin"":{""rank"":3,""points"":220,""lastScored"":{""rank"":0,""points"":220,""at"":""x""}},
                           ""starrail"":{""rank"":2,""points"":90,""lastScored"":{""rank"":1,""at"":""2026-11-06T00:04:37.000Z""}}}}");
            Assert.AreEqual(ReadAt, s.At);
            Assert.AreEqual(TimeSpan.FromMinutes(60), s.PollEvery);
            Assert.AreEqual(TimeSpan.FromMinutes(180), s.PollIdle);
            Assert.AreEqual(new GuessPlace(1, 220, LastScore), s.Global.LastScored);
            Assert.IsNull(s.Board("genshin").LastScored, "an unreadable one is none");
            Assert.IsNull(s.Board("starrail").LastScored, "one without its points cannot be checked against the standing: none");
            Assert.IsTrue(s.PlayedThisMonth);
            var tried = GuessStanding.Parse("{\"period\":\"2026-11\",\"global\":{\"rank\":null,\"points\":0,\"played\":5}}");
            Assert.IsNull(tried.Global, "no place yet");
            Assert.IsTrue(tried.PlayedThisMonth, "but played: the normal cadence, not the idle one");
            var old = GuessStanding.Parse(Contract);
            Assert.IsNull(old.At);
            Assert.IsNull(old.PollEvery);
            Assert.IsNull(old.Global.LastScored, "a Worker older than the app");
            Assert.IsFalse(GuessStanding.Parse("{\"period\":\"2026-11\",\"global\":null}").PlayedThisMonth);
        }

        [TestMethod]
        public void Parse_ReadsTheCountAhead_AndDropsOneThatCannotBe()
        {
            var s = GuessStanding.Parse(@"{""period"":""2026-11"",""global"":{""rank"":5,""points"":220,""ahead"":3},
                ""games"":{""genshin"":{""rank"":1,""points"":220,""ahead"":0},""starrail"":{""rank"":2,""points"":90,""ahead"":2},
                           ""zzz"":{""rank"":2,""points"":90,""ahead"":-1},""wuwa"":{""rank"":2,""points"":90,""ahead"":""1""}}}");
            Assert.AreEqual(3, s.Global.Ahead);
            Assert.AreEqual(0, s.Board("genshin").Ahead);
            Assert.IsNull(s.Board("starrail").Ahead, "more players ahead than places above");
            Assert.IsNull(s.Board("zzz").Ahead);
            Assert.IsNull(s.Board("wuwa").Ahead, "not a number");
            Assert.IsNull(GuessStanding.Parse(Contract).Global.Ahead, "a Worker older than the app");
        }

        [TestMethod]
        public void Schedule_ConfigPinWins_ThenTheServer_ThenTheDefault_AllClamped()
        {
            var d = GuessPollSchedule.Resolve(GuessPollPins.None, null, null);
            Assert.AreEqual(GuessPollSchedule.Default, d);
            Assert.AreEqual(TimeSpan.FromMinutes(2), d.FirstDelay);
            Assert.AreEqual(TimeSpan.FromHours(4), d.Interval);
            Assert.AreEqual(TimeSpan.FromHours(12), d.IdleInterval);
            var server = GuessPollSchedule.Resolve(GuessPollPins.None, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(180));
            Assert.AreEqual(TimeSpan.FromMinutes(60), server.Interval);
            Assert.AreEqual(TimeSpan.FromMinutes(180), server.IdleInterval);
            var pinned = GuessPollSchedule.Resolve(new GuessPollPins(30, 90, null, 5, new[] { 2, 10 }, 20), TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(180));
            Assert.AreEqual(TimeSpan.FromSeconds(30), pinned.FirstDelay);
            Assert.AreEqual(TimeSpan.FromMinutes(90), pinned.Interval, "a Config pin wins over the server");
            Assert.AreEqual(TimeSpan.FromMinutes(180), pinned.IdleInterval);
            Assert.AreEqual(TimeSpan.FromMinutes(5), pinned.NoSessionRecheck);
            CollectionAssert.AreEqual(new[] { TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10) }, pinned.Retry.ToArray());
            Assert.AreEqual(TimeSpan.FromSeconds(20), pinned.GateRetry);
            var wild = GuessPollSchedule.Resolve(new GuessPollPins(0, 1, 999_999, 0, new[] { 0, -5 }, 1), TimeSpan.FromSeconds(1), null);
            Assert.AreEqual(GuessPollSchedule.MinFirstDelay, wild.FirstDelay);
            Assert.AreEqual(GuessPollSchedule.MinInterval, wild.Interval);
            Assert.AreEqual(GuessPollSchedule.MaxIdleInterval, wild.IdleInterval);
            Assert.AreEqual(GuessPollSchedule.MinNoSessionRecheck, wild.NoSessionRecheck);
            CollectionAssert.AreEqual(new[] { GuessPollSchedule.MinRetry, GuessPollSchedule.MinRetry }, wild.Retry.ToArray());
            Assert.AreEqual(GuessPollSchedule.MinGateRetry, wild.GateRetry);
            Assert.AreEqual(GuessPollSchedule.Default.Retry.Count, GuessPollSchedule.Resolve(new GuessPollPins(null, null, null, null, Array.Empty<int>(), null), null, null).Retry.Count,
                "an empty list keeps the default steps");
        }

        [TestMethod]
        public void Schedule_ConfigValues_OnlyNumbersArePins()
        {
            var values = new Dictionary<string, System.Text.Json.Nodes.JsonNode>
            {
                [GuessNotifySettings.PollMinutesKey] = System.Text.Json.Nodes.JsonNode.Parse("90"),
                [GuessNotifySettings.IdlePollMinutesKey] = System.Text.Json.Nodes.JsonNode.Parse("\"often\""),
                [GuessNotifySettings.FirstDelaySecondsKey] = System.Text.Json.Nodes.JsonNode.Parse("30.4"),
                [GuessNotifySettings.RetryMinutesKey] = System.Text.Json.Nodes.JsonNode.Parse("[5, \"x\", 30]"),
                [GuessNotifySettings.GateRetrySecondsKey] = System.Text.Json.Nodes.JsonNode.Parse("{\"a\":1}"),
                [GuessNotifySettings.NoSessionRecheckMinutesKey] = System.Text.Json.Nodes.JsonNode.Parse("2147483647.6"),
            };
            var pins = GuessNotifySettings.PinsFrom(k => values.TryGetValue(k, out var v) ? v : null);
            Assert.AreEqual(90, pins.PollMinutes);
            Assert.IsNull(pins.IdlePollMinutes, "a word is no pin");
            Assert.AreEqual(30, pins.FirstDelaySeconds);
            CollectionAssert.AreEqual(new[] { 5, 30 }, pins.RetryMinutes.ToArray());
            Assert.IsNull(pins.GateRetrySeconds);
            Assert.AreEqual(int.MaxValue, pins.NoSessionRecheckMinutes, "clamped before the cast, so it cannot wrap to a negative");
            Assert.AreEqual(int.MinValue, GuessNotifySettings.PinsFrom(k => k == GuessNotifySettings.PollMinutesKey
                ? System.Text.Json.Nodes.JsonNode.Parse("-2147483648.7") : null).PollMinutes);
            Assert.AreEqual(GuessPollPins.None, GuessNotifySettings.PinsFrom(_ => null) with { RetryMinutes = null });
        }

        [TestMethod]
        public void Poller_APlayerWithoutAPlaceButWithAPlay_KeepsTheNormalCadence()
        {
            var s = GuessPollSchedule.Default;
            Assert.AreEqual(GuessPollOutcome.Unplaced, GuessStandingPoller.OutcomeOf(new GuessStanding { Period = Period, PlayedThisMonth = true }));
            Assert.AreEqual(GuessPollOutcome.NoStanding, GuessStandingPoller.OutcomeOf(new GuessStanding { Period = Period }));
            Assert.AreEqual(GuessPollOutcome.Read, GuessStandingPoller.OutcomeOf(OwnersRead()));
            Assert.AreEqual(s.Interval, GuessStandingPoller.NextDelay(GuessPollOutcome.Unplaced, 0, s));
            Assert.AreEqual(s.IdleInterval, GuessStandingPoller.NextDelay(GuessPollOutcome.NoStanding, 0, s));
            using var p = new GuessStandingPoller(_ => Task.FromResult(OwnersRead()), () => true);
            Assert.AreEqual(TimeSpan.FromHours(4), p.DelayAfter(OwnersRead()));
            var tuned = new GuessStanding { Period = Period, PlayedThisMonth = true, PollEvery = TimeSpan.FromMinutes(45), PollIdle = TimeSpan.FromMinutes(90) };
            Assert.AreEqual(TimeSpan.FromMinutes(45), p.DelayAfter(tuned), "the server's cadence");
        }

        [TestMethod]
        public void ReadLog_SaysWhatTheReadFound()
        {
            var o = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, OwnersRead(), "genshin");
            string line = GuessOvertakeRule.Describe(OwnersRead(), o, TimeSpan.FromHours(4));
            StringAssert.Contains(line, "genshin #3 (220 pts)");
            StringAssert.Contains(line, "passed: #1 after the last score (2026-11-06 00:04Z) → #3");
            StringAssert.Contains(line, "notice: genshin");
            StringAssert.Contains(line, "next read in 04:00:00");
            var none = new GuessStanding { Period = Period, PlayedThisMonth = true };
            StringAssert.Contains(GuessOvertakeRule.Describe(none, GuessOvertakeRule.Evaluate(GuessBaselines.Empty, none, "genshin"), TimeSpan.FromHours(4)),
                "no place this month yet (played)");
            var quiet = GuessOvertakeRule.Delivered(o.Baselines, OwnersRead(), "genshin", "genshin");
            StringAssert.Contains(GuessOvertakeRule.Describe(OwnersRead(), GuessOvertakeRule.Evaluate(quiet, OwnersRead(), "genshin"), TimeSpan.FromHours(4)),
                "nothing new");
        }

        [TestMethod]
        public async Task Client_SendsSince_OnlyWhenGiven()
        {
            string Response(string body) =>
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
            using var server = new OneShotHttp(Response(Contract));
            await new KaptionApiClient(server.BaseUrl).GetGuessStandingAsync("jwt", "genshin", CancellationToken.None, since: "2026-11-06T00:04:37.000Z");
            StringAssert.StartsWith(server.RequestLine, "GET /api/app/guess/standing?game=genshin&since=2026-11-06T00%3A04%3A37.000Z ");
        }

        // ── Spotlight ───────────────────────────────────────────────────

        private static SpotlightInputs Ready(Func<SpotlightInputs, SpotlightInputs> edit = null)
        {
            var i = new SpotlightInputs { SetupCompleted = true, WuWaPolishListed = true, WuWaGameVersion = "3.7" };
            return edit == null ? i : edit(i);
        }

        [TestMethod]
        public void Spotlight_WuWaRow_NeedsTheLiveListing()
        {
            Assert.IsTrue(SpotlightRules.Rows(Ready()).WuWa);
            Assert.IsFalse(SpotlightRules.Rows(new SpotlightInputs { SetupCompleted = true }).WuWa, "not listed");
            Assert.IsFalse(SpotlightRules.Rows(new SpotlightInputs { SetupCompleted = true, WuWaPolishListed = true, WuWaComing = true }).WuWa, "still on the way");
            Assert.IsFalse(SpotlightRules.Rows(new SpotlightInputs { SetupCompleted = true, WuWaPolishListed = true, SeenWuWa = true }).WuWa, "seen");
            Assert.AreEqual("3.7", SpotlightRules.Rows(Ready()).WuWaVersion);
        }

        [TestMethod]
        public void Spotlight_WuWaAction_SwitchesUnlessWuWaIsSelected()
        {
            Assert.AreEqual(SpotlightWuWaAction.Switch, SpotlightRules.Rows(Ready()).WuWaAction);
            Assert.AreEqual(SpotlightWuWaAction.OpenGamePage,
                SpotlightRules.Rows(new SpotlightInputs { SetupCompleted = true, WuWaPolishListed = true, WuWaSelected = true }).WuWaAction);
        }

        [TestMethod]
        public void Spotlight_WaitsForSetup_TheOnboardingLaunch_AndTheIntroCard()
        {
            Assert.IsFalse(SpotlightRules.Rows(new SpotlightInputs { WuWaPolishListed = true }).Any, "setup not done");
            Assert.IsFalse(SpotlightRules.Rows(new SpotlightInputs { SetupCompleted = true, WuWaPolishListed = true, OnboardingRanThisLaunch = true }).Any);
            Assert.IsFalse(SpotlightRules.Rows(new SpotlightInputs { SetupCompleted = true, WuWaPolishListed = true, Intro25Visible = true }).Any);
            Assert.IsTrue(SpotlightRules.Rows(new SpotlightInputs { SetupCompleted = true }).Guess, "the guess row needs no listing");
        }

        [TestMethod]
        public void Spotlight_RetiresAfterThreeLaunches_RowByRow()
        {
            Assert.IsTrue(SpotlightRules.Rows(new SpotlightInputs { SetupCompleted = true, ShowsGuess = 2 }).Guess);
            var r = SpotlightRules.Rows(new SpotlightInputs { SetupCompleted = true, WuWaPolishListed = true, ShowsGuess = 3, ShowsWuWa = 1 });
            Assert.IsFalse(r.Guess);
            Assert.IsTrue(r.WuWa, "each row counts its own launches");
            Assert.IsFalse(SpotlightRules.Rows(new SpotlightInputs { SetupCompleted = true, SeenGuess = true }).Any);
        }

        [TestMethod]
        public void Spotlight_ReadsTheListing()
        {
            var packs = new[]
            {
                new LatestPack { Game = "genshin", Language = "pl", PackVersion = "7.2.0-r1", GameVersion = "7.2.0" },
                new LatestPack { Game = "wuwa", Language = "pl", PackVersion = "3.7.0-r2", GameVersion = "3.7.0" },
            };
            Assert.IsTrue(SpotlightRules.HasWuWaPolish(packs, out string v));
            Assert.AreEqual("3.7", v);
            Assert.IsFalse(SpotlightRules.HasWuWaPolish(packs.Take(1), out _));
            Assert.IsFalse(SpotlightRules.HasWuWaPolish(null, out _));
            Assert.IsFalse(SpotlightRules.HasWuWaPolish(new[] { new LatestPack { Game = "wuwa", Language = "en", PackVersion = "1" } }, out _));
            Assert.AreEqual("3.7.1", SpotlightRules.ShortVersion("3.7.1"));
        }

        [TestMethod]
        public void Spotlight_CountsAndMarksInConfig()
        {
            GI_Subtitles.Core.Config.Config.Remove(SpotlightRules.ShowsGuessKey);
            GI_Subtitles.Core.Config.Config.Remove(SpotlightRules.SeenGuessKey);
            SpotlightRules.CountShown(new SpotlightRows(false, SpotlightWuWaAction.None, null, true));
            SpotlightRules.CountShown(new SpotlightRows(false, SpotlightWuWaAction.None, null, true));
            var i = SpotlightRules.FromConfig(new SpotlightInputs { SetupCompleted = true });
            Assert.AreEqual(2, i.ShowsGuess);
            SpotlightRules.MarkSeen(wuwa: false, guess: true);
            Assert.IsFalse(SpotlightRules.Rows(SpotlightRules.FromConfig(new SpotlightInputs { SetupCompleted = true })).Guess);
            GI_Subtitles.Core.Config.Config.Remove(SpotlightRules.ShowsGuessKey);
            GI_Subtitles.Core.Config.Config.Remove(SpotlightRules.SeenGuessKey);
        }

        [TestMethod]
        public void Onboarding_PreMarksTheWuWaRow_NotTheGuessRow()
        {
            string setup = File.ReadAllText(Path.Combine(RenderTestKit.RepoRoot(), "GI-Subtitles", "Views", "OnboardingWindow.xaml.cs"));
            Assert.AreEqual(2, CountOf(setup, "Config.Set(Shell.SpotlightRules.SeenWuWaKey, true)"), "both completion paths");
            Assert.IsFalse(setup.Contains("SeenGuessKey"));
        }

        // ── Announcements ───────────────────────────────────────────────

        [TestMethod]
        public void Announcements_ClosedOnesStayClosed_CappedAtFifty()
        {
            var ids = Enumerable.Range(1, 50).Select(i => "a" + i).ToList();
            var added = AnnouncementDismissals.Add(ids, "new");
            Assert.AreEqual(50, added.Count);
            Assert.AreEqual("a2", added[0], "the oldest goes");
            Assert.AreEqual("new", added[^1]);
            Assert.AreEqual(2, AnnouncementDismissals.Add(new[] { "x", "y" }, "x").Count, "no duplicates");
            var items = new[] { ("x", 1), ("y", 2), ((string)null, 3) };
            var visible = AnnouncementDismissals.Visible(items, t => t.Item1, new[] { "x" });
            CollectionAssert.AreEqual(new[] { 2, 3 }, visible.Select(t => t.Item2).ToArray(), "an item without an id stays");
        }

        // ── Strings ─────────────────────────────────────────────────────

        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static Dictionary<string, string> Dictionary(string file) =>
            XDocument.Load(Path.Combine(RenderTestKit.RepoRoot(), "GI-Subtitles", "Resources", file)).Root.Elements()
                .Where(e => e.Attribute(X + "Key") != null).ToDictionary(e => (string)e.Attribute(X + "Key"), e => e.Value);

        private static readonly string[] Prefixes = { "shell_Spotlight_", "shell_Guess_", "GuessNotify_" };

        [TestMethod]
        public void Strings_EveryKeyInEnglishAndPolish_AndPolishIsTranslated()
        {
            var en = Dictionary("Strings.en-US.xaml");
            var pl = Dictionary("Strings.pl-PL.xaml");
            var keys = en.Keys.Where(k => Prefixes.Any(k.StartsWith)).ToList();
            Assert.IsTrue(keys.Count >= 20, $"the keys are there ({keys.Count})");
            foreach (string k in keys)
            {
                Assert.IsTrue(pl.ContainsKey(k), $"{k} missing in pl-PL");
                if (k != "GuessNotify_Pseudonym_Format") Assert.AreNotEqual(en[k], pl[k], $"{k} is not translated");
            }
            Assert.AreEqual(0, pl.Keys.Count(k => Prefixes.Any(k.StartsWith) && !en.ContainsKey(k)));
        }

        [TestMethod]
        public void Strings_PolishCopyRules()
        {
            var pl = Dictionary("Strings.pl-PL.xaml").Where(p => Prefixes.Any(p.Key.StartsWith)).ToList();
            var en = Dictionary("Strings.en-US.xaml").Where(p => Prefixes.Any(p.Key.StartsWith)).ToList();
            // No verb that genders the player (zagrałeś/zagrałaś, spadłeś), no AI wording, no prize words, no shouting.
            var gendered = new System.Text.RegularExpressions.Regex(@"\b\w+(łeś|łaś|łem|łam)\b");
            foreach (var (k, v) in pl)
                Assert.IsFalse(gendered.IsMatch(v), $"{k}: \"{v}\" genders the player");
            foreach (var (k, v) in pl.Concat(en))
            {
                Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(v, @"\b(AI|SI|sztuczn\w*|model\w*|nagrod\w*|prize\w*|reward\w*|win)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase), $"{k}: \"{v}\"");
                Assert.IsFalse(v.Contains('!') || v.Contains('—'), $"{k}: \"{v}\"");
            }
            // The balloon's {n} placeholders survive translation.
            var enMap = en.ToDictionary(p => p.Key, p => p.Value);
            foreach (var (k, v) in pl)
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(enMap[k], @"\{\d\}"))
                    StringAssert.Contains(v, m.Value, $"{k} keeps {m.Value}");
        }

        private static int CountOf(string s, string what)
        {
            int n = 0;
            for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        /// <summary>Answers one HTTP request on a loopback port with a canned response, and keeps what it was asked.</summary>
        private sealed class OneShotHttp : IDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly Task _serve;
            public string RequestLine { get; private set; } = "";
            public string Headers { get; private set; } = "";
            public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

            public OneShotHttp(string response)
            {
                _listener.Start();
                _serve = Task.Run(async () =>
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    var buffer = new byte[16384];
                    var sb = new StringBuilder();
                    while (!sb.ToString().Contains("\r\n\r\n"))
                    {
                        int n = await stream.ReadAsync(buffer, 0, buffer.Length);
                        if (n == 0) break;
                        sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    }
                    string request = sb.ToString();
                    int eol = request.IndexOf("\r\n", StringComparison.Ordinal);
                    RequestLine = eol > 0 ? request.Substring(0, eol) : request;
                    Headers = request;
                    byte[] bytes = Encoding.UTF8.GetBytes(response);
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                });
            }

            public void Dispose()
            {
                try { _serve.Wait(TimeSpan.FromSeconds(5)); } catch { /* the test already failed */ }
                _listener.Stop();
            }
        }
    }
}
