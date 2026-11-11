// The Start page's codes card (Services/Codes): the /api/app/codes contract, "never checked" and offline, the cap,
// the links per game and interface language (real manifests), the refresh and backoff schedule, and the poller.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Services.Codes;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Network;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AppLogger = GI_Subtitles.Common.Logger;

namespace GI_Test
{
    internal static class CodesSamples
    {
        public static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

        /// <summary>The live answer's shape (2026-11-09), trimmed.</summary>
        public const string Genshin = """
        {"game":"genshin","checkedAt":"2026-11-09T11:45:29Z","listedSince":"2026-11-03T07:00:42Z","active":[
          {"code":"CDOF4X83H7X2","rewards":[{"name":"Mora","count":10000},{"name":"Adventurer's Experience","count":10}],"source":"secondary","sourceUrl":null,"firstSeen":"2026-11-04T07:00:42Z","expiresAt":null},
          {"code":"MIJIQ23V2437","rewards":[{"name":"Primogem","count":60}],"source":"hoyolab_panel","sourceUrl":"https://www.hoyolab.com/x","firstSeen":"2026-11-03T07:00:42Z","expiresAt":"2026-11-20T00:00:00Z"}],
         "expired":[{"code":"OLDCODE1","rewards":[],"source":"secondary","sourceUrl":null,"firstSeen":"2026-10-01T00:00:00Z","expiresAt":"2026-10-20T00:00:00Z","expiredAt":"2026-10-20T00:00:00Z"}],
         "program":null}
        """;

        /// <summary>A game the server has never checked: no list yet, which is not "no codes".</summary>
        public const string NeverChecked = """{"game":"zzz","checkedAt":null,"listedSince":null,"active":[],"expired":[],"program":null}""";

        public static RedeemCodeList List(string game, int active, DateTimeOffset? checkedAt = null, DateTimeOffset? expiresAt = null) =>
            new(game, checkedAt ?? Now.AddMinutes(-15),
                Enumerable.Range(1, active).Select(i => new RedeemCode($"CODE{i:00}NEW", new[] { new CodeReward("Primogem", 60 * i) },
                    Now.AddDays(-i), expiresAt)).ToList());

        public static GameManifest Game(string wireId) =>
            GameRegistry.Current.FindByWireId(wireId) ?? throw new AssertInconclusiveException($"no embedded manifest for {wireId}");
    }

    [TestClass]
    public class RedeemCodeListTests
    {
        [TestMethod]
        public void ParsesTheLiveShape()
        {
            var list = RedeemCodeList.Parse(CodesSamples.Genshin, "genshin");
            Assert.AreEqual("genshin", list.Game);
            Assert.IsTrue(list.Checked);
            Assert.AreEqual(new DateTimeOffset(2026, 11, 9, 11, 45, 29, TimeSpan.Zero), list.CheckedAt);
            CollectionAssert.AreEqual(new[] { "CDOF4X83H7X2", "MIJIQ23V2437" }, list.Active.Select(c => c.Code).ToArray(), "server order kept");
            Assert.AreEqual(1, list.ExpiredCount);
            Assert.AreEqual(new CodeReward("Mora", 10000), list.Active[0].Rewards[0]);
            Assert.IsNull(list.Active[0].ExpiresAt);
            Assert.AreEqual(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), list.Active[1].ExpiresAt);
        }

        [TestMethod]
        public void NeverCheckedIsNotTheSameAsNoCodes()
        {
            var never = RedeemCodeList.Parse(CodesSamples.NeverChecked, "zzz");
            Assert.IsFalse(never.Checked);
            var none = RedeemCodeList.Parse("""{"game":"zzz","checkedAt":"2026-11-09T11:00:00Z","active":[],"expired":[]}""", "zzz");
            Assert.IsTrue(none.Checked);
            Assert.AreEqual(0, none.Active.Count);
        }

        [TestMethod]
        public void SkipsEntriesTheCardCannotShow()
        {
            const string json = """
            {"game":"wuwa","checkedAt":"2026-11-09T11:45:54Z","active":[
              {"code":"wutheringgift","rewards":[],"firstSeen":"2026-11-04T10:20:03Z","expiresAt":null},
              {"code":"WUTHERINGGIFT","rewards":[{"name":"Astrite","count":50}]},
              {"code":"no spaces allowed","rewards":[]},
              {"code":"AB","rewards":[]},
              {"code":42},
              "not an object",
              {"code":"SHELL2026","rewards":[{"name":"","count":1},{"name":"Shell Credit","count":0},{"name":"Astrite","count":"50"},{"name":"Shell Credit","count":10000}]}]}
            """;
            var list = RedeemCodeList.Parse(json, "wuwa");
            CollectionAssert.AreEqual(new[] { "WUTHERINGGIFT", "SHELL2026" }, list.Active.Select(c => c.Code).ToArray(), "upper-cased, deduplicated, junk skipped");
            Assert.AreEqual(0, list.Active[0].Rewards.Count, "a code without rewards is still a code");
            CollectionAssert.AreEqual(new[] { new CodeReward("Shell Credit", 10000) }, list.Active[1].Rewards.ToArray());
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("<html>502 Bad Gateway</html>")]
        [DataRow("[]")]
        [DataRow("""{"checkedAt":null,"active":[]}""")]
        [DataRow("""{"game":"starrail","checkedAt":null,"active":[]}""")]
        [DataRow("""{"game":"genshin","checkedAt":"yesterday","active":[]}""")]
        [DataRow("""{"game":"genshin","checkedAt":null}""")]
        public void RefusesABodyThatIsNotTheList(string json)
        {
            Assert.ThrowsExactly<CodesFormatException>(() => RedeemCodeList.Parse(json, "genshin"));
        }

        [TestMethod]
        public void ActiveAtDropsCodesPastTheirKnownExpiry()
        {
            var list = RedeemCodeList.Parse(CodesSamples.Genshin, "genshin");
            Assert.AreEqual(2, list.ActiveAt(CodesSamples.Now).Count);
            CollectionAssert.AreEqual(new[] { "CDOF4X83H7X2" }, list.ActiveAt(new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero)).Select(c => c.Code).ToArray());
        }
    }

    [TestClass]
    public class CodesLinksTests
    {
        [TestMethod]
        [DataRow("genshin", "pl-PL", "https://kaption.one/pl/kody/genshin-impact/")]
        [DataRow("genshin", "en-US", "https://kaption.one/codes/genshin-impact/")]
        [DataRow("starrail", "pl-PL", "https://kaption.one/pl/kody/honkai-star-rail/")]
        [DataRow("starrail", "en-US", "https://kaption.one/codes/honkai-star-rail/")]
        [DataRow("zzz", "pl-PL", "https://kaption.one/pl/kody/zenless-zone-zero/")]
        [DataRow("zzz", "en-US", "https://kaption.one/codes/zenless-zone-zero/")]
        [DataRow("wuwa", "pl-PL", "https://kaption.one/pl/kody/wuthering-waves/")]
        [DataRow("wuwa", "en-US", "https://kaption.one/codes/wuthering-waves/")]
        public void AllCodesGoesToTheGamesPageInTheInterfaceLanguage(string game, string lang, string expected)
        {
            Assert.AreEqual(expected, CodesLinks.AllCodesUrl(CodesSamples.Game(game), lang));
        }

        [TestMethod]
        [DataRow("genshin", "https://genshin.hoyoverse.com/en/gift?code=ABCD1234")]
        [DataRow("starrail", "https://hsr.hoyoverse.com/gift?code=ABCD1234")]
        [DataRow("zzz", "https://zenless.hoyoverse.com/redemption?code=ABCD1234")]
        [DataRow("wuwa", null)]
        public void RedeemIsTheManifestsOfficialPage(string game, string expected)
        {
            Assert.AreEqual(expected, CodesLinks.RedeemUrl(CodesSamples.Game(game), "ABCD1234"));
        }

        [TestMethod]
        public void AnInGameGameGetsTheMenuPathInstead()
        {
            var wuwa = CodesSamples.Game("wuwa");
            Assert.IsTrue(CodesLinks.IsInGame(wuwa));
            Assert.IsFalse(CodesLinks.IsHoyolab(wuwa));
            Assert.AreEqual("Redemption Code", CodesLinks.InGamePath(wuwa).Last());
            Assert.IsNotNull(CodesLinks.InGameUnlock(wuwa));
            Assert.IsNull(CodesLinks.InGamePath(CodesSamples.Game("genshin")));
        }

        [TestMethod]
        public void AGameWithoutCodesHasNoCard()
        {
            var bare = new GameManifest { Id = "newgame", Name = "New Game", Site = new GameSite { Hub = "new-game" } };
            Assert.IsFalse(CodesLinks.HasCodesPage(bare));
            Assert.IsNull(CodesLinks.AllCodesUrl(bare, "pl-PL"));
            var badTemplate = new GameManifest
            {
                Id = "newgame", Site = new GameSite { Hub = "new-game" },
                News = new GameNews { Codes = new GameCodes { Kind = GameCodes.KindHoyolab, RedeemUrl = "http://example.com/?code={code}" } },
            };
            Assert.IsFalse(CodesLinks.HasCodesPage(badTemplate), "only https templates with {code}");
            Assert.IsNull(CodesLinks.RedeemUrl(badTemplate, "ABCD1234"));
            Assert.IsFalse(CodesLinks.HasCodesPage(null));
        }
    }

    [TestClass]
    public class CodesCardModelTests
    {
        private static readonly TimeSpan Stale = CodesSchedule.Default.StaleAfter;
        private static readonly CultureInfo En = new("en-US");

        private static CodesCardView Build(string game, RedeemCodeList list, DateTimeOffset? readAt, string lang = "pl-PL") =>
            CodesCardModel.Build(CodesSamples.Game(game), list, readAt, CodesSamples.Now, Stale, lang, En);

        [TestMethod]
        public void ShowsTheNewestThreeAndCountsTheRest()
        {
            var view = Build("genshin", CodesSamples.List("genshin", 5), CodesSamples.Now.AddMinutes(-1));
            Assert.IsTrue(view.Visible);
            Assert.AreEqual(CodesCardModel.MaxShown, view.Rows.Count);
            CollectionAssert.AreEqual(new[] { "CODE01NEW", "CODE02NEW", "CODE03NEW" }, view.Rows.Select(r => r.Code).ToArray());
            Assert.AreEqual(5, view.ActiveCount);
            Assert.AreEqual(2, view.MoreCount);
            Assert.AreEqual("Genshin Impact", view.GameName);
            Assert.AreEqual("https://kaption.one/pl/kody/genshin-impact/", view.AllCodesUrl);
            Assert.AreEqual("https://genshin.hoyoverse.com/en/gift?code=CODE01NEW", view.Rows[0].RedeemUrl);
            Assert.IsNull(view.InGamePath);
        }

        [TestMethod]
        public void FewerThanTheCapShowsThemAll()
        {
            var view = Build("starrail", CodesSamples.List("starrail", 2), CodesSamples.Now);
            Assert.AreEqual(2, view.Rows.Count);
            Assert.AreEqual(0, view.MoreCount);
        }

        [TestMethod]
        public void NothingToRedeemMeansNoCard()
        {
            DateTimeOffset fresh = CodesSamples.Now.AddMinutes(-1);
            Assert.AreEqual(CodesCardHidden.NotRead, Build("genshin", null, null).HiddenBecause, "first read under way, or offline from the start");
            Assert.AreEqual(CodesCardHidden.NeverChecked,
                Build("genshin", new RedeemCodeList("genshin", null, Array.Empty<RedeemCode>()), fresh).HiddenBecause);
            Assert.AreEqual(CodesCardHidden.NoActive, Build("genshin", CodesSamples.List("genshin", 0), fresh).HiddenBecause);
            Assert.AreEqual(CodesCardHidden.NoActive,
                Build("genshin", CodesSamples.List("genshin", 2, expiresAt: CodesSamples.Now.AddMinutes(-1)), fresh).HiddenBecause,
                "codes past their expiry are gone even before the next read");
            Assert.AreEqual(CodesCardHidden.NotRead, Build("zzz", CodesSamples.List("genshin", 2), fresh).HiddenBecause, "another game's list");
            Assert.AreEqual(CodesCardHidden.NoCodesPage,
                CodesCardModel.Build(new GameManifest { Id = "x" }, CodesSamples.List("x", 2), fresh, CodesSamples.Now, Stale, "pl-PL", En).HiddenBecause);
        }

        [TestMethod]
        public void OfflineKeepsTheLastListUntilItGoesStale()
        {
            var list = CodesSamples.List("zzz", 2);
            Assert.IsTrue(Build("zzz", list, CodesSamples.Now - Stale).Visible, "two intervals old: still shown");
            Assert.AreEqual(CodesCardHidden.Stale, Build("zzz", list, CodesSamples.Now - Stale - TimeSpan.FromSeconds(1)).HiddenBecause);
        }

        [TestMethod]
        public void WuWaRowsHaveNoRedeemLinkButTheMenuPath()
        {
            var view = Build("wuwa", CodesSamples.List("wuwa", 1), CodesSamples.Now, "en-US");
            Assert.IsTrue(view.Visible);
            Assert.IsNull(view.Rows[0].RedeemUrl);
            Assert.AreEqual("https://kaption.one/codes/wuthering-waves/", view.AllCodesUrl);
            Assert.IsTrue(view.InGamePath.Count > 0);
        }

        [TestMethod]
        public void RewardLineKeepsGameNamesAndUsesTheCulturesNumbers()
        {
            var rewards = new[] { new CodeReward("Primogem", 60), new CodeReward("Mora", 10000) };
            Assert.AreEqual("Primogem ×60 · Mora ×10,000", CodesCardModel.RewardLine(rewards, En));
            Assert.AreEqual("Primogem ×60 · Mora ×10 000", CodesCardModel.RewardLine(rewards, new CultureInfo("pl-PL")));
            Assert.AreEqual(string.Empty, CodesCardModel.RewardLine(Array.Empty<CodeReward>(), En));
        }
    }

    [TestClass]
    public class CodesScheduleTests
    {
        [TestMethod]
        public void DefaultsReadOnStartThenEveryHalfHour()
        {
            var d = CodesSchedule.Resolve(CodesSchedulePins.None);
            Assert.AreEqual(CodesSchedule.Default, d);
            Assert.AreEqual(TimeSpan.FromSeconds(3), d.FirstDelay);
            Assert.AreEqual(TimeSpan.FromMinutes(30), d.Interval);
            Assert.AreEqual(TimeSpan.FromHours(1), d.StaleAfter);
            CollectionAssert.AreEqual(new[] { 5.0, 15, 30, 30, 30 }, Enumerable.Range(1, 5).Select(n => d.RetryAfter(n).TotalMinutes).ToArray(),
                "backoff steps; the last repeats");
        }

        [TestMethod]
        public void RetryAfterLengthensAWaitButNeverShortensIt()
        {
            var d = CodesSchedule.Default;
            Assert.AreEqual(TimeSpan.FromMinutes(5), d.RetryAfter(1, TimeSpan.FromSeconds(30)), "a short Retry-After keeps our own step");
            Assert.AreEqual(TimeSpan.FromMinutes(45), d.RetryAfter(1, TimeSpan.FromMinutes(45)));
            Assert.AreEqual(CodesSchedule.MaxRetryAfter, d.RetryAfter(1, TimeSpan.FromDays(30)), "a hostile header is capped");
            Assert.AreEqual(TimeSpan.FromMinutes(15), d.RetryAfter(2, null));
        }

        [TestMethod]
        public void PinsAreClamped()
        {
            var low = CodesSchedule.Resolve(new CodesSchedulePins(-5, 1, new[] { 0, 2, 100000 }));
            Assert.AreEqual(TimeSpan.Zero, low.FirstDelay);
            Assert.AreEqual(CodesSchedule.MinInterval, low.Interval);
            CollectionAssert.AreEqual(new[] { CodesSchedule.MinRetry, TimeSpan.FromMinutes(2), CodesSchedule.MaxRetry }, low.Retry.ToArray());
            var high = CodesSchedule.Resolve(new CodesSchedulePins(100000, 100000, Enumerable.Repeat(5, 20).ToArray()));
            Assert.AreEqual(CodesSchedule.MaxFirstDelay, high.FirstDelay);
            Assert.AreEqual(CodesSchedule.MaxInterval, high.Interval);
            Assert.AreEqual(CodesSchedule.MaxRetrySteps, high.Retry.Count);
        }

        [TestMethod]
        public void ConfigValuesThatAreNotNumbersAreNoPins()
        {
            var values = new Dictionary<string, JsonNode>
            {
                [CodesSchedulePins.RefreshMinutesKey] = JsonValue.Create("thirty"),
                [CodesSchedulePins.FirstDelaySecondsKey] = JsonValue.Create(12.6),
                [CodesSchedulePins.RetryMinutesKey] = new JsonArray(JsonValue.Create(10), JsonValue.Create("x"), JsonValue.Create(20)),
            };
            var pins = CodesSchedulePins.From(k => values.TryGetValue(k, out var v) ? v : null);
            Assert.IsNull(pins.RefreshMinutes);
            Assert.AreEqual(13, pins.FirstDelaySeconds);
            CollectionAssert.AreEqual(new[] { 10, 20 }, pins.RetryMinutes.ToArray());
        }

        [TestMethod]
        public void RetryAfterHeaderReadsSecondsAndDates()
        {
            var now = CodesSamples.Now;
            Assert.IsNull(KaptionApiClient.RetryAfterOf(null, now));
            Assert.AreEqual(TimeSpan.FromSeconds(120), KaptionApiClient.RetryAfterOf(new RetryConditionHeaderValue(TimeSpan.FromSeconds(120)), now));
            Assert.AreEqual(TimeSpan.FromSeconds(90), KaptionApiClient.RetryAfterOf(new RetryConditionHeaderValue(now.AddSeconds(90)), now));
            Assert.AreEqual(TimeSpan.Zero, KaptionApiClient.RetryAfterOf(new RetryConditionHeaderValue(now.AddSeconds(-90)), now));
            Assert.IsTrue(RetryConditionHeaderValue.TryParse("Mon, 09 Nov 2026 12:10:00 GMT", out var parsed));
            Assert.AreEqual(TimeSpan.FromMinutes(10), KaptionApiClient.RetryAfterOf(parsed, now));
        }
    }

    [TestClass]
    [DoNotParallelize]
    public class CodesPollerTests
    {
        private MemoryAppender _log;
        private bool _repoWasConfigured;
        private static Hierarchy Repo => (Hierarchy)log4net.LogManager.GetRepository(typeof(AppLogger).Assembly);

        [TestInitialize]
        public void SetUp()
        {
            _repoWasConfigured = Repo.Configured;
            Repo.Configured = true;
            _log = new MemoryAppender();
            _log.ActivateOptions();
            Repo.Root.AddAppender(_log);
        }

        [TestCleanup]
        public void TearDown()
        {
            Repo.Root.RemoveAppender(_log);
            Repo.Configured = _repoWasConfigured;
        }

        private IEnumerable<LoggingEvent> Codes(Level level) =>
            _log.GetEvents().Where(e => e.Level == level && e.RenderedMessage.StartsWith("Codes:", StringComparison.Ordinal));

        private sealed class Fake
        {
            public readonly Queue<Func<RedeemCodeList>> Answers = new();
            public int Calls;
            public string Game = "genshin";
            public bool Active = true;
            public DateTimeOffset Now = CodesSamples.Now;

            public Task<RedeemCodeList> Fetch(string game, CancellationToken ct)
            {
                Calls++;
                return Task.FromResult(Answers.Dequeue()());
            }

            public CodesPoller Poller(CodesSchedule schedule = null) =>
                new(Fetch, () => Game, () => Active, () => schedule ?? CodesSchedule.Default, () => Now);
        }

        private static Func<RedeemCodeList> Throws(Exception ex) => () => throw ex;
        private static Exception Offline() => new ApiUnavailableException("codes: network error", new HttpRequestException("No such host is known."));

        [TestMethod]
        public async Task AGoodReadIsHandedToTheCardAndKept()
        {
            var fake = new Fake();
            fake.Answers.Enqueue(() => CodesSamples.List("genshin", 2));
            using var poller = fake.Poller();
            CodesSnapshot seen = null;
            poller.ListRead += s => seen = s;
            var (outcome, next) = await poller.PollOnceAsync(CancellationToken.None);
            Assert.AreEqual(CodesFetchOutcome.Read, outcome);
            Assert.AreEqual(TimeSpan.FromMinutes(30), next);
            Assert.AreEqual("genshin", seen.Game);
            Assert.AreEqual(CodesSamples.Now, seen.ReadAt);
            Assert.AreSame(seen, poller.Last("genshin"));
            Assert.AreEqual(1, Codes(Level.Info).Count(e => e.RenderedMessage.Contains("first read")), "the Worker's counts, once per new list");
        }

        [TestMethod]
        public async Task NothingIsSentWhileTheWindowIsOffScreenOrNoGameHasCodes()
        {
            var fake = new Fake { Active = false };
            using var poller = fake.Poller();
            Assert.AreEqual(CodesFetchOutcome.Skipped, (await poller.PollOnceAsync(CancellationToken.None)).Outcome);
            fake.Active = true;
            fake.Game = null;
            Assert.AreEqual(CodesFetchOutcome.Skipped, (await poller.PollOnceAsync(CancellationToken.None)).Outcome);
            Assert.AreEqual(0, fake.Calls);
        }

        [TestMethod]
        public async Task FailuresBackOffLogOncePerKindAndKeepTheLastList()
        {
            var fake = new Fake();
            fake.Answers.Enqueue(() => CodesSamples.List("genshin", 3));
            fake.Answers.Enqueue(Throws(Offline()));
            fake.Answers.Enqueue(Throws(Offline()));
            fake.Answers.Enqueue(Throws(new RateLimitedException(TimeSpan.FromHours(2))));
            fake.Answers.Enqueue(Throws(new RateLimitedException(TimeSpan.FromSeconds(5))));
            fake.Answers.Enqueue(() => CodesSamples.List("genshin", 3));
            using var poller = fake.Poller();
            int failedEvents = 0;
            poller.ReadFailed += _ => failedEvents++;

            await poller.PollOnceAsync(CancellationToken.None);
            var waits = new List<(CodesFetchOutcome, TimeSpan)>();
            for (int i = 0; i < 4; i++) waits.Add(await poller.PollOnceAsync(CancellationToken.None));
            CollectionAssert.AreEqual(new[]
            {
                (CodesFetchOutcome.NetworkFailed, TimeSpan.FromMinutes(5)),
                (CodesFetchOutcome.NetworkFailed, TimeSpan.FromMinutes(15)),
                (CodesFetchOutcome.RateLimited, TimeSpan.FromHours(2)),
                (CodesFetchOutcome.RateLimited, TimeSpan.FromMinutes(30)),
            }, waits);
            Assert.AreEqual(4, poller.Failures);
            Assert.AreEqual(4, failedEvents);
            Assert.AreEqual(3, poller.Last("genshin").List.Active.Count, "a failed read never drops the last good list");
            Assert.AreEqual(CodesSamples.Now + TimeSpan.FromMinutes(30), poller.BackoffUntil);
            Assert.AreEqual(2, Codes(Level.Warn).Count(), "one Warn per failure kind in a streak");
            Assert.AreEqual(0, Codes(Level.Error).Count(), "never Error: nothing reaches Sentry as an event");

            Assert.AreEqual(CodesFetchOutcome.Read, (await poller.PollOnceAsync(CancellationToken.None)).Outcome);
            Assert.AreEqual(0, poller.Failures);
            Assert.AreEqual(DateTimeOffset.MinValue, poller.BackoffUntil);
            Assert.AreEqual(1, Codes(Level.Info).Count(e => e.RenderedMessage.Contains("reachable again")));
            Assert.AreEqual(0, Codes(Level.Info).Count(e => e.RenderedMessage.Contains("list changed")), "same codes: no new line");
        }

        [TestMethod]
        public async Task ABadBodyIsItsOwnKind()
        {
            var fake = new Fake();
            fake.Answers.Enqueue(Throws(new CodesFormatException("not JSON")));
            fake.Answers.Enqueue(() => null);
            using var poller = fake.Poller();
            Assert.AreEqual(CodesFetchOutcome.BadResponse, (await poller.PollOnceAsync(CancellationToken.None)).Outcome);
            Assert.AreEqual(CodesFetchOutcome.BadResponse, (await poller.PollOnceAsync(CancellationToken.None)).Outcome);
            Assert.AreEqual(1, Codes(Level.Warn).Count());
        }

        [TestMethod]
        public void FailuresAreClassifiedByKind()
        {
            Assert.AreEqual(CodesFetchOutcome.NetworkFailed, CodesPoller.Classify(Offline()));
            Assert.AreEqual(CodesFetchOutcome.ServerFailed, CodesPoller.Classify(
                new ApiUnavailableException("codes: HTTP 503", new HttpRequestException("HTTP 503", null, HttpStatusCode.ServiceUnavailable))));
            Assert.AreEqual(CodesFetchOutcome.RateLimited, CodesPoller.Classify(new RateLimitedException(TimeSpan.Zero)));
            Assert.AreEqual(CodesFetchOutcome.BadResponse, CodesPoller.Classify(new CodesFormatException("x")));
            Assert.AreEqual(CodesFetchOutcome.BadResponse, CodesPoller.Classify(new ApiValidationException(HttpStatusCode.NotFound, new ApiError())));
            Assert.AreEqual(CodesFetchOutcome.NetworkFailed, CodesPoller.Classify(new TaskCanceledException()));
        }

        [TestMethod]
        public void NextDelayFollowsTheOutcome()
        {
            var d = CodesSchedule.Default;
            Assert.AreEqual(d.Interval, CodesPoller.NextDelay(CodesFetchOutcome.Read, 0, null, d));
            Assert.AreEqual(d.Interval, CodesPoller.NextDelay(CodesFetchOutcome.Skipped, 0, null, d));
            Assert.AreEqual(TimeSpan.FromMinutes(5), CodesPoller.NextDelay(CodesFetchOutcome.ServerFailed, 1, null, d));
            Assert.AreEqual(TimeSpan.FromMinutes(30), CodesPoller.NextDelay(CodesFetchOutcome.BadResponse, 9, null, d));
        }

        [TestMethod]
        public async Task RequestRefreshUsesTheCacheAndRespectsTheBackoff()
        {
            var fake = new Fake();
            fake.Answers.Enqueue(() => CodesSamples.List("genshin", 1));
            fake.Answers.Enqueue(Throws(Offline()));
            // A first delay long enough that the timer never fires during the test.
            var slow = new CodesSchedule(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30), CodesSchedule.Default.Retry);
            using var poller = fake.Poller(slow);
            Assert.IsFalse(poller.RequestRefresh(), "not started: nothing scheduled");
            poller.Start();
            await poller.PollOnceAsync(CancellationToken.None);

            int handed = 0;
            poller.ListRead += _ => handed++;
            Assert.IsFalse(poller.RequestRefresh(), "fresh list: no read");
            Assert.AreEqual(1, handed, "the cached list is handed over at once");

            fake.Now += TimeSpan.FromMinutes(31);
            await poller.PollOnceAsync(CancellationToken.None); // fails: 5 min backoff
            Assert.IsFalse(poller.RequestRefresh(), "inside the backoff: no read");
            fake.Now += TimeSpan.FromMinutes(6);
            fake.Game = "zzz";
            fake.Answers.Enqueue(() => CodesSamples.List("zzz", 1)); // the read RequestRefresh starts
            Assert.IsTrue(poller.RequestRefresh(), "another game with no list, backoff over: read now");
            poller.Stop();
        }
    }
}
