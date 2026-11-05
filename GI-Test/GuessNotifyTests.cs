using System;
using System.Collections.Generic;
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
            Assert.AreEqual(4, o.Baselines["global"].Rank, "re-based: a later real pass counts from here");
            Assert.IsNotNull(GuessOvertakeRule.Evaluate(o.Baselines, Standing(5), null).Candidate);
        }

        [TestMethod]
        public void Rule_ADropWithNobodyAbove_IsNoNotice()
        {
            var b = GuessOvertakeRule.Evaluate(GuessBaselines.Empty, Standing(3), null).Baselines;
            Assert.IsNull(GuessOvertakeRule.Evaluate(b, Standing(5, noAbove: true), null).Candidate);
            Assert.IsFalse(GuessOvertakeRule.PassedOnPoints(null));
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

        // ── GuessNoticeDelivery: strip or balloon ───────────────────────

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
        public void Delivery_StripCountsAsSeen_OnlyWhenSettingsIsInFrontOnStart()
        {
            Assert.IsTrue(GuessNoticeDelivery.StripCountsAsSeen(true, false, true, true));
            Assert.IsFalse(GuessNoticeDelivery.StripCountsAsSeen(false, false, true, true), "open behind the game");
            Assert.IsFalse(GuessNoticeDelivery.StripCountsAsSeen(true, true, true, true), "minimised");
            Assert.IsFalse(GuessNoticeDelivery.StripCountsAsSeen(true, false, false, true), "no strip");
            Assert.IsFalse(GuessNoticeDelivery.StripCountsAsSeen(true, false, true, false), "another page");
        }

        [TestMethod]
        public void Delivery_SettingsBehindTheGame_WaitsForTheBalloon()
        {
            var (d, _, _) = Waiting();
            bool seen = GuessNoticeDelivery.StripCountsAsSeen(false, false, true, true);
            var wait = d.Next(Today, seen, true, null, false, gameInFront: true, quns: 5);
            Assert.AreEqual(GuessDeliveryStep.Wait, wait.Step);
            Assert.AreEqual(GuessGateResult.GameInFront, wait.Gate);
            Assert.AreEqual(GuessDeliveryStep.Wait, d.Next(Today, seen, true, null, true, false, 5).Step, "subtitles on");
            Assert.AreEqual(GuessDeliveryStep.Wait, d.Next(Today, seen, true, null, false, false, 3).Step, "full screen");
            Assert.AreEqual(GuessDeliveryStep.ShowBalloon, d.Next(Today, seen, true, null, false, false, 5).Step);
        }

        [TestMethod]
        public void Delivery_SeenOnTheStrip_NoBalloon_AndTheNextReadClearsIt()
        {
            var (d, b, drop) = Waiting();
            Assert.AreEqual(GuessDeliveryStep.SeenOnStrip, d.Next(Today, true, true, null, true, true, 0).Step);
            b = d.Delivered(b, "genshin");
            Assert.IsFalse(d.Undelivered);
            Assert.IsNotNull(d.Notice, "the strip keeps its text until the next read");
            Assert.AreEqual(GuessDeliveryStep.Idle, d.Next(Today, false, true, Today, false, false, 5).Step);
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
            var capped = d.Next(Today, false, true, Today, false, false, 5);
            Assert.AreEqual(GuessDeliveryStep.CappedToday, capped.Step);
            Assert.IsTrue(d.Undelivered);
            Assert.IsNotNull(d.Notice);
        }

        [TestMethod]
        public void Delivery_YesterdaysNotice_NoBalloon_ButTheStripStillCounts()
        {
            var (d, _, _) = Waiting();
            Assert.AreEqual(GuessDeliveryStep.Expired, d.Next("2026-11-06", false, true, null, false, false, 5).Step);
            Assert.AreEqual(GuessDeliveryStep.SeenOnStrip, d.Next("2026-11-06", true, true, null, false, false, 5).Step,
                "read on the strip: re-based, so it never comes back as a balloon");
        }

        [TestMethod]
        public void Delivery_TurnedOff_Clears_AndTheLobbyRebases()
        {
            var (d, b, _) = Waiting();
            Assert.AreEqual(GuessDeliveryStep.Disabled, d.Next(Today, true, false, null, false, false, 5).Step);
            b = d.LobbyOpened(b, "starrail");
            Assert.IsNull(d.Notice);
            Assert.AreEqual(5, b["genshin"].Rank, "the notice's own board, though Star Rail is selected now");
            Assert.AreEqual(GuessDeliveryStep.Idle, d.Next(Today, true, true, null, false, false, 5).Step);
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
            Assert.AreEqual(TimeSpan.FromMinutes(2), GuessStandingPoller.FirstDelay);
            Assert.AreEqual(TimeSpan.FromHours(4), GuessStandingPoller.NextDelay(GuessPollOutcome.Read, 0));
            Assert.AreEqual(TimeSpan.FromHours(12), GuessStandingPoller.NextDelay(GuessPollOutcome.NoStanding, 0));
            Assert.AreEqual(TimeSpan.FromHours(4), GuessStandingPoller.NextDelay(GuessPollOutcome.Refused, 0));
            Assert.AreEqual(TimeSpan.FromMinutes(15), GuessStandingPoller.NextDelay(GuessPollOutcome.Failed, 1));
            Assert.AreEqual(TimeSpan.FromHours(1), GuessStandingPoller.NextDelay(GuessPollOutcome.Failed, 2));
            Assert.AreEqual(TimeSpan.FromHours(4), GuessStandingPoller.NextDelay(GuessPollOutcome.Failed, 9));
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
