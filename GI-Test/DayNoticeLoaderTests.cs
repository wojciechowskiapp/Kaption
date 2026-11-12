using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Notices;
using GI_Subtitles.Services.Stamina;
using GI_Subtitles.Services.Today;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AppConfig = GI_Subtitles.Core.Config.Config;

namespace GI_Test
{
    /// <summary>
    /// The notice of the day is built for one snapshot (the games, the server, each game's day, the saved UID) and
    /// sent only if that snapshot still holds after the last wait (Services/Notices/DayNoticeLoader). The regression
    /// here: the player saves another account's UID on the same server while the showcase is still on its way. The
    /// notice then named the old account's characters, and the day was used up, so the right one never came.
    /// </summary>
    [TestClass]
    [DoNotParallelize] // the game, the server, the UID and "shown today" are Config keys
    public class DayNoticeLoaderTests
    {
        private const string First = "700000001", Second = "700000002";
        private static readonly CultureInfo En = new("en-US");
        private static readonly DateTimeOffset Wednesday = FarmCardSamples.Wednesday;
        /// <summary>Monday 01:00 UTC: 02:00 on Europe, still Sunday's game day; 09:00 on Asia, already Monday's.</summary>
        private static readonly DateTimeOffset AcrossTheBoundary = DateTimeOffset.Parse("2026-11-16T01:00:00Z", CultureInfo.InvariantCulture);

        private static string Fallback(string key, string fallback) => fallback;
        private static GameManifest Game(string id) => GameRegistry.Current.FindByWireId(id);

        private static readonly string[] Keys =
        {
            "Game", FarmCardSettings.RegionKey, FarmCardSettings.LastShownDayKey, FarmCardSettings.UidKeyPrefix + "Genshin",
            TodayCardSettings.LastShownDayPrefix + "genshin", TodayCardSettings.LastShownDayPrefix + "starrail",
            StaminaSettings.EnabledKey, "Stamina.Reading.genshin", "Stamina.Seen.genshin", "Stamina.Reading.starrail", "Stamina.Seen.starrail",
        };

        // The suite's Config is a folder of its own (TestEnvironment): the keys are cleared, not restored.
        [TestInitialize]
        public void StartFromGenshinOnEurope()
        {
            ClearTheSettings();
            AppConfig.Set("Game", "Genshin");
            AppConfig.Set(FarmCardSettings.RegionKey, "europe");
            Assert.IsTrue(FarmCardSettings.SaveUid(First));
        }

        [TestCleanup]
        public void ClearTheSettings()
        {
            foreach (string k in Keys)
                if (AppConfig.Has(k)) AppConfig.Remove(k);
        }

        /// <summary>Sources whose showcase answers only when the test lets it, and that note which UID was asked for.</summary>
        private sealed class DelayedShowcase
        {
            public readonly TaskCompletionSource<ShowcaseRead> Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public string AskedUid;
            public int Asked;

            public TodayCardSources Sources => field ??= new(
                Schedule: _ => Task.FromResult(FarmCardSamples.Schedule()),
                Showcase: (uid, day, server) => { AskedUid = uid; Asked++; return Gate.Task; },
                Catalog: () => Task.FromResult(FarmCardSamples.Catalog()),
                Week: _ => Task.FromResult<TodayWeek>(null),
                Seen: _ => Array.Empty<AssistStoredReading>());
        }

        // What MainWindow.StartupNotices does, with the test's clock and sources.
        private static DayNoticeRequest Capture(DateTimeOffset now) =>
            DayNoticeRequest.Capture(GameRegistry.Current.Listed, TodayCardSettings.ActiveGame(), TodayCardSettings.ServerId,
                FarmCardSettings.Uid, GI_Subtitles.Views.MainWindow.LastPlayedWithKaption, TodayCardSettings.LastShownDay, "en-US", now);

        private static bool StillCurrent(DayNoticeRequest request, DateTimeOffset now) =>
            request.StillCurrent(TodayCardSettings.ActiveGame(), TodayCardSettings.ServerId, FarmCardSettings.Uid, TodayCardSettings.LastShownDay, now);

        private static Task<DayNoticeText> Build(DayNoticeRequest request, TodayCardSources sources) =>
            DayNoticeLoader.BuildAsync(request, sources, TodayCardSettings.HasCard, Fallback);

        private sealed class Delivery
        {
            public readonly List<DayNoticeText> Sent = new();
            public bool Take(DayNoticeRequest request, DayNoticeText text)
            {
                Sent.Add(text);
                return true;
            }
        }

        /// <summary>
        /// The sequence of the defect: the notice starts for the first account, the player saves the second account's
        /// UID (same server) while the showcase is on its way, the first account's showcase arrives.
        /// </summary>
        private static async Task<(DayNoticeOutcome Outcome, Delivery Delivery, DayNoticeRequest Request)> SwitchAccountsWhileTheShowcaseLoads(
            Func<DayNoticeRequest, bool> check)
        {
            Assert.IsTrue(FarmCardSettings.SaveUid(First));
            DayNoticeRequest request = Capture(Wednesday);
            Assert.AreEqual(First, request.Uid, "the notice is built for the account saved at that moment");
            var showcase = new DelayedShowcase();
            var delivery = new Delivery();
            Task<DayNoticeOutcome> run = DayNoticeLoader.PrepareAndDeliverAsync(request, r => Build(r, showcase.Sources), check, delivery.Take);
            Assert.IsFalse(run.IsCompleted, "the notice waits for the showcase");
            Assert.AreEqual(First, showcase.AskedUid);

            Assert.IsTrue(FarmCardSettings.SaveUid(Second));
            showcase.Gate.SetResult(new ShowcaseRead(FarmCardSamples.Roster(Wednesday), false));
            return (await run, delivery, request);
        }

        [TestMethod]
        public async Task TheUidChangesWhileTheShowcaseLoads_TheNoticeIsNotSent_AndTheDayIsNotUsed()
        {
            // The defect, reproduced with what the reviewed head checked after the wait (the server and the day): both
            // still hold, so the notice goes out, with the first account's characters, while the second's UID is saved.
            bool ServerAndDayOnly(DayNoticeRequest r) =>
                string.Equals(r.ServerId, TodayCardSettings.ServerId, StringComparison.Ordinal)
                && string.Equals(r.DayKey, TodayCardSettings.DayKey(r.Selected, Wednesday, TodayCardSettings.ServerId), StringComparison.Ordinal);
            var old = await SwitchAccountsWhileTheShowcaseLoads(ServerAndDayOnly);
            Assert.AreEqual(DayNoticeOutcome.Delivered, old.Outcome);
            Assert.AreEqual(Second, FarmCardSettings.Uid);
            StringAssert.StartsWith(old.Delivery.Sent.Single().Body, "Books for your characters: ", "the first account's showcase, sent to the second");

            // The request's own check knows the account the notice was built for.
            var now = await SwitchAccountsWhileTheShowcaseLoads(r => StillCurrent(r, Wednesday));
            Assert.AreEqual(DayNoticeOutcome.Stale, now.Outcome, "a notice built for another account is not the one to send");
            Assert.AreEqual(0, now.Delivery.Sent.Count, "nothing reached the tray, so nothing remembered the day");
            Assert.AreNotEqual(now.Request.Uid, FarmCardSettings.Uid);

            // The next look takes a fresh snapshot: the second account's UID is the one asked for, and that notice goes out.
            var again = new DelayedShowcase();
            again.Gate.SetResult(ShowcaseRead.None);
            DayNoticeRequest fresh = Capture(Wednesday);
            Assert.AreEqual(Second, fresh.Uid);
            Assert.AreEqual(DayNoticeOutcome.Delivered,
                await DayNoticeLoader.PrepareAndDeliverAsync(fresh, r => Build(r, again.Sources), r => StillCurrent(r, Wednesday), now.Delivery.Take));
            Assert.AreEqual(Second, again.AskedUid, "the showcase read is the saved account's");
            Assert.AreEqual(1, now.Delivery.Sent.Count);
            Assert.IsFalse(now.Delivery.Sent[0].Body.StartsWith("Books for your characters", StringComparison.Ordinal),
                "nothing of the first account's showcase is in it");
        }

        [TestMethod]
        public async Task TheUidGoesAwayAndComesBackWhileItLoads_TheShowcaseAskedForIsStillTheRequests()
        {
            // The schedule waits too here: the card's loader reaches the showcase only after it.
            var schedule = new TaskCompletionSource<TodaySchedule>(TaskCreationOptions.RunContinuationsAsynchronously);
            var showcase = new DelayedShowcase();
            TodayCardSources sources = showcase.Sources with { Schedule = _ => schedule.Task };
            var delivery = new Delivery();
            DayNoticeRequest request = Capture(Wednesday);
            Task<DayNoticeOutcome> run = DayNoticeLoader.PrepareAndDeliverAsync(request, r => Build(r, sources),
                r => StillCurrent(r, Wednesday), delivery.Take);

            // First -> Second while the schedule is on its way: a loader reading the setting would now ask for the second account.
            Assert.IsTrue(FarmCardSettings.SaveUid(Second));
            schedule.SetResult(FarmCardSamples.Schedule());
            await Task.Run(() => System.Threading.SpinWait.SpinUntil(() => showcase.Asked > 0, TimeSpan.FromSeconds(10)));
            Assert.AreEqual(1, showcase.Asked);
            Assert.AreEqual(First, showcase.AskedUid, "the showcase asked for is the account the notice was started for");

            // Second -> First before the showcase arrives: the final check sees the request's UID again.
            Assert.IsTrue(FarmCardSettings.SaveUid(First));
            showcase.Gate.SetResult(new ShowcaseRead(FarmCardSamples.Roster(Wednesday), false));
            Assert.AreEqual(DayNoticeOutcome.Delivered, await run, "the saved account is the request's again, and so is every name in the text");
            StringAssert.StartsWith(delivery.Sent.Single().Body, "Books for your characters: ");
        }

        [TestMethod]
        public void TheDaysEnd_WhenMidnightComesTwice_IsItsFirstOccurrence()
        {
            // A zone whose clocks go back from 01:00 to 00:00 on the first Sunday of November (Cuba's rule), made here
            // so the test does not depend on the PC's time zone data. On 2026-11-01 midnight comes twice: first at
            // UTC-4 (04:00 UTC), when the local date turns, and again an hour later at UTC-5.
            var spring = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 2, DayOfWeek.Sunday);
            var autumn = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 11, 1, DayOfWeek.Sunday);
            var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), spring, autumn);
            TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("Midnight twice", TimeSpan.FromHours(-5), "Midnight twice", "Standard", "Daylight", new[] { rule });
            Assert.IsTrue(zone.IsAmbiguousTime(new DateTime(2026, 11, 1, 0, 0, 0)), "the zone repeats that midnight");

            DateTimeOffset evening = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero); // 16:00 local, still daylight time
            Assert.AreEqual(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), DayNoticeContent.LocalDayEnds(evening, zone),
                "the day ends when the date turns, not an hour later at the repeated midnight");
            // So what happens at 00:30 on the new date (04:30 UTC) is tomorrow's, not today's "last day".
            var late = new[] { new TodayPeriod("event", "Ends after midnight", "", new DateTimeOffset(2026, 11, 1, 4, 30, 0, TimeSpan.Zero), "https://kaption.one/news/e/") };
            Assert.IsNull(DayNoticeContent.EndsToday(late, evening, DayNoticeContent.LocalDayEnds(evening, zone), Fallback, En));
            // The day after, with no repeated hour: the next midnight at the standard offset.
            Assert.AreEqual(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero),
                DayNoticeContent.LocalDayEnds(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), zone));
        }

        [TestMethod]
        public async Task TheGamesCardShowsByItselfWhileTheNoticeLoads_TheNoticeIsNotSentOnTop()
        {
            var showcase = new DelayedShowcase();
            var delivery = new Delivery();
            DayNoticeRequest request = Capture(Wednesday);
            Task<DayNoticeOutcome> run = DayNoticeLoader.PrepareAndDeliverAsync(request, r => Build(r, showcase.Sources),
                r => StillCurrent(r, Wednesday), delivery.Take);
            Assert.IsFalse(run.IsCompleted, "the notice waits for the showcase");

            // The game came to the front meanwhile and its Today card showed by itself (the watcher's own write);
            // the player then left the game again before the showcase arrived.
            TodayCardSettings.SetLastShownDay(Game("genshin"), request.DayKey);
            showcase.Gate.SetResult(new ShowcaseRead(FarmCardSamples.Roster(Wednesday), false));
            Assert.AreEqual(DayNoticeOutcome.Stale, await run, "the card has just said it: the notice does not say it again");
            Assert.AreEqual(0, delivery.Sent.Count, "nothing reached the tray, so no slot of the day's notices was taken");

            // The next look starts from the card being shown: nothing to add, and nothing fetched.
            var again = new DelayedShowcase();
            Assert.AreEqual(DayNoticeOutcome.Nothing,
                await DayNoticeLoader.PrepareAndDeliverAsync(Capture(Wednesday), r => Build(r, again.Sources), r => StillCurrent(r, Wednesday), delivery.Take));
            Assert.AreEqual(0, again.Asked);
            Assert.AreEqual(0, delivery.Sent.Count);
        }

        [TestMethod]
        public void TheDaysEnd_AndTheClick_FollowTheClockAndTheSelection()
        {
            // Warsaw, the night the clocks go forward (2027-03-28, 02:00 -> 03:00). At 00:30 local the next midnight
            // is 23.5 hours away, at +02:00; today's +01:00 would put it an hour late.
            TimeZoneInfo warsaw = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
            DateTimeOffset spring = new(2027, 3, 27, 23, 30, 0, TimeSpan.Zero);
            Assert.AreEqual(new DateTimeOffset(2027, 3, 28, 22, 0, 0, TimeSpan.Zero), DayNoticeContent.LocalDayEnds(spring, warsaw));
            // And the night they go back (2027-10-31, 03:00 -> 02:00): 24.5 hours to the next midnight, at +01:00.
            DateTimeOffset autumn = new(2027, 10, 30, 22, 30, 0, TimeSpan.Zero);
            Assert.AreEqual(new DateTimeOffset(2027, 10, 31, 23, 0, 0, TimeSpan.Zero), DayNoticeContent.LocalDayEnds(autumn, warsaw));
            // An ordinary day.
            Assert.AreEqual(new DateTimeOffset(2027, 6, 10, 22, 0, 0, TimeSpan.Zero),
                DayNoticeContent.LocalDayEnds(new DateTimeOffset(2027, 6, 10, 9, 0, 0, TimeSpan.Zero), warsaw));

            // A click, perhaps hours later: the card opens only for the game the notice was about, still selected.
            Assert.IsTrue(DayNoticeContent.ClickOpensCard(true, "genshin", "genshin"));
            Assert.IsFalse(DayNoticeContent.ClickOpensCard(true, "genshin", "starrail"), "another game selected since: Start, not that game's card");
            Assert.IsFalse(DayNoticeContent.ClickOpensCard(false, "genshin", "genshin"), "the notice promised no card");
            Assert.IsFalse(DayNoticeContent.ClickOpensCard(true, null, null));

            // The tray's news line while it waits: sent, unless the card was opened meanwhile or nothing is left.
            Assert.AreEqual(StartNoticePlanner.NewsTrayStep.Send, StartNoticePlanner.NewsTray(cardPresented: false, rowsLeft: 3));
            Assert.AreEqual(StartNoticePlanner.NewsTrayStep.CardSaidIt, StartNoticePlanner.NewsTray(cardPresented: true, rowsLeft: 3),
                "the player opened the window while it waited: the card was the notice, no tray line and no slot of the day's notices");
            Assert.AreEqual(StartNoticePlanner.NewsTrayStep.NothingLeft, StartNoticePlanner.NewsTray(cardPresented: false, rowsLeft: 0));
        }

        [TestMethod]
        public async Task NothingChanges_TheNoticeNamesTheSavedAccountsCharacters_AndGoesOutOnce()
        {
            var showcase = new DelayedShowcase();
            showcase.Gate.SetResult(new ShowcaseRead(FarmCardSamples.Roster(Wednesday), false));
            var delivery = new Delivery();
            DayNoticeRequest request = Capture(Wednesday);
            DayNoticeOutcome outcome = await DayNoticeLoader.PrepareAndDeliverAsync(request, r => Build(r, showcase.Sources),
                r => StillCurrent(r, Wednesday), delivery.Take);

            Assert.AreEqual(DayNoticeOutcome.Delivered, outcome);
            Assert.AreEqual(1, delivery.Sent.Count);
            Assert.AreEqual(1, showcase.Asked);
            DayNoticeText text = delivery.Sent[0];
            Assert.AreEqual("Genshin Impact today, Wednesday", text.Title);
            StringAssert.StartsWith(text.Body, "Books for your characters: ");
            Assert.IsTrue(text.OpensCard, "the selected game has a card of the day to open");
            Assert.AreEqual("2026-11-04", request.DayKey);
            Assert.AreEqual(DayOfWeek.Wednesday, request.Weekday);

            // A delivery the tray or the gate did not take keeps the day: the caller tries again.
            Assert.AreEqual(DayNoticeOutcome.Held,
                await DayNoticeLoader.PrepareAndDeliverAsync(request, r => Build(r, showcase.Sources), r => StillCurrent(r, Wednesday), (_, __) => false));
        }

        [TestMethod]
        public async Task TheServerTheGameOrTheDayChangesWhileItLoads_TheNoticeIsNotSent()
        {
            // Another server, across the two servers' day boundary.
            var showcase = new DelayedShowcase();
            var delivery = new Delivery();
            DayNoticeRequest request = Capture(AcrossTheBoundary);
            Assert.AreEqual("2026-11-15", request.DayKey, "Europe is still on Sunday's game day");
            Assert.AreEqual(DayOfWeek.Sunday, request.Weekday);
            Task<DayNoticeOutcome> run = DayNoticeLoader.PrepareAndDeliverAsync(request, r => Build(r, showcase.Sources),
                r => StillCurrent(r, AcrossTheBoundary), delivery.Take);
            AppConfig.Set(FarmCardSettings.RegionKey, "asia");
            showcase.Gate.SetResult(ShowcaseRead.None);
            Assert.AreEqual(DayNoticeOutcome.Stale, await run);

            // Another game selected.
            AppConfig.Set(FarmCardSettings.RegionKey, "europe");
            showcase = new DelayedShowcase();
            request = Capture(Wednesday);
            run = DayNoticeLoader.PrepareAndDeliverAsync(request, r => Build(r, showcase.Sources), r => StillCurrent(r, Wednesday), delivery.Take);
            AppConfig.Set("Game", "StarRail");
            showcase.Gate.SetResult(ShowcaseRead.None);
            Assert.AreEqual(DayNoticeOutcome.Stale, await run);

            // The game day turned while the data was on its way.
            AppConfig.Set("Game", "Genshin");
            showcase = new DelayedShowcase();
            request = Capture(Wednesday);
            DateTimeOffset nextDay = Wednesday.AddDays(1);
            run = DayNoticeLoader.PrepareAndDeliverAsync(request, r => Build(r, showcase.Sources), r => StillCurrent(r, nextDay), delivery.Take);
            showcase.Gate.SetResult(ShowcaseRead.None);
            Assert.AreEqual(DayNoticeOutcome.Stale, await run);

            Assert.AreEqual(0, delivery.Sent.Count);
        }

        [TestMethod]
        public async Task NothingToSay_IsNotANotice_AndACardThatShowedTodaySaysItAlready()
        {
            // Genshin's card already came up by itself today: the notice has nothing to add, and asks for nothing.
            TodayCardSettings.SetLastShownDay(Game("genshin"), Capture(Wednesday).DayKey);
            DayNoticeRequest request = Capture(Wednesday);
            CollectionAssert.AreEqual(new[] { "genshin" }, request.CardsShown.ToList());
            var showcase = new DelayedShowcase();
            var delivery = new Delivery();
            Assert.AreEqual(DayNoticeOutcome.Nothing,
                await DayNoticeLoader.PrepareAndDeliverAsync(request, r => Build(r, showcase.Sources), r => StillCurrent(r, Wednesday), delivery.Take));
            Assert.AreEqual(0, showcase.Asked, "no showcase read for a game whose card showed today");
            Assert.AreEqual(0, delivery.Sent.Count);
        }

        [TestMethod]
        public async Task ASecondGameReadLately_GetsItsOwnLine_AndTheClickStillOpensTheSelectedGamesCard()
        {
            // Star Rail's Trailblaze Power was read yesterday and has filled since; Genshin is the selected game.
            GameManifest hsr = Game("starrail");
            AppConfig.Set(StaminaSettings.EnabledKey, true);
            StaminaSettings.SaveReading("starrail", new StaminaReading(hsr.Stamina.Max - 5, Wednesday.AddHours(-20), 0));
            var showcase = new DelayedShowcase();
            showcase.Gate.SetResult(new ShowcaseRead(FarmCardSamples.Roster(Wednesday), false));
            DayNoticeRequest request = Capture(Wednesday);
            CollectionAssert.AreEqual(new[] { "genshin", "starrail" }, request.Games.Select(g => g.Game.Id).ToList());

            DayNoticeText text = await Build(request, showcase.Sources);
            Assert.AreEqual("Today in your games, Wednesday", text.Title);
            string[] lines = text.Body.Split('\n');
            Assert.AreEqual(2, lines.Length, text.Body);
            StringAssert.StartsWith(lines[0], "Genshin Impact: Books for your characters: ");
            StringAssert.StartsWith(lines[1], $"Honkai: Star Rail: {hsr.Stamina.Name} is at the cap; it filled ");
            Assert.IsTrue(text.OpensCard);

            // Star Rail selected instead (Genshin was never read on this PC, so it is not one of the player's games
            // then): the notice is Star Rail's alone, and it has a card to open.
            AppConfig.Set("Game", "StarRail");
            DayNoticeRequest other = Capture(Wednesday);
            Assert.AreEqual("starrail", other.Selected.Id);
            DayNoticeText alone = await Build(other, showcase.Sources);
            Assert.AreEqual("Honkai: Star Rail today, Wednesday", alone.Title);
            StringAssert.StartsWith(alone.Body, hsr.Stamina.Name + " is at the cap");
            Assert.IsTrue(alone.OpensCard);
        }

        [TestMethod]
        public void Section_SaysWhatTheTodayCardWouldLeadWith()
        {
            GameManifest hsr = Game("starrail");
            DateTimeOffset now = Wednesday;
            var periods = new[]
            {
                new WeekPeriod("starrail", "banner", "Castorice's banner", "Banner", now.AddDays(-20), now.AddHours(5), "/news/castorice/"),
                new WeekPeriod("starrail", "event", "Garden of Plenty", "Event", now.AddDays(-6), now.AddDays(3), "/news/garden/"),
                new WeekPeriod("genshin", "event", "Another game's event", "Event", now.AddDays(-6), now.AddHours(5), "/news/other/"),
            };
            TodayCardModel model = TodayCardBuilder.Build(new TodayInputs(hsr, now, "europe", true,
                new StaminaReading(hsr.Stamina.Max - 5, now.AddHours(-20), 0), null, periods, Array.Empty<AssistStoredReading>(), null, "en-US"));
            DayNoticeSection section = DayNoticeContent.Section(model, hsr, opensCard: true, Fallback, En);
            Assert.AreEqual(2, section.Lines.Count, string.Join(" | ", section.Lines));
            StringAssert.StartsWith(section.Lines[0], hsr.Stamina.Name + " is at the cap; it filled ");
            Assert.AreEqual("Last day: Castorice's banner.", section.Lines[1], "only what ends before the game day does, in this game");
            Assert.IsTrue(section.HasCard);

            // No data at all, or a card that already showed: the game says nothing.
            Assert.AreEqual(0, DayNoticeContent.Section(null, hsr, opensCard: true, Fallback, En).Lines.Count);

            // Several things on their last day: two are named, the rest counted.
            DateTimeOffset ends = now.AddHours(3);
            var many = Enumerable.Range(1, 4).Select(i => new TodayPeriod("event", "Event " + i, "", ends, "https://kaption.one/news/e/")).ToList();
            Assert.AreEqual("Last day: Event 1, Event 2 +2.", DayNoticeContent.EndsToday(many, now, now.AddHours(10), Fallback, En));
            Assert.IsNull(DayNoticeContent.EndsToday(many, now, now.AddHours(1), Fallback, En), "ending after the game day does is not today's");
            Assert.IsNull(DayNoticeContent.EndsToday(null, now, now.AddHours(10), Fallback, En));
        }

        [TestMethod]
        public void TheSharedGate_TakesTheStartNoticesInTurn_AndCountsThemTowardTheDay()
        {
            var ledger = new Ledger { MaxPerDay = 2, Gap = TimeSpan.FromSeconds(45) };
            var gate = new NoticeGate(ledger);
            var clear = new NoticeScreen(false, false, GI_Subtitles.Services.Guess.GuessNoticeGate.QunsAcceptsNotifications);
            var inGame = new NoticeScreen(false, true, GI_Subtitles.Services.Guess.GuessNoticeGate.QunsAcceptsNotifications);
            DateTimeOffset t = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

            // Over a game both wait; once the screen is free the story's notice goes first, the day's after the gap.
            Assert.AreEqual(NoticeGateResult.GameInFront, gate.Ask(NoticeKind.StoryReady, true, false, t, () => inGame));
            Assert.AreEqual(NoticeGateResult.GameInFront, gate.Ask(NoticeKind.Today, true, false, t, () => inGame));
            t = t.AddMinutes(1);
            Assert.AreEqual(NoticeGateResult.Outranked, gate.Ask(NoticeKind.Today, true, false, t, () => clear));
            Assert.AreEqual(NoticeGateResult.Show, gate.Ask(NoticeKind.StoryReady, true, false, t, () => clear));
            gate.Shown(NoticeKind.StoryReady, t);
            Assert.AreEqual(NoticeGateResult.TooSoon, gate.Ask(NoticeKind.Today, true, false, t.AddSeconds(10), () => clear));
            t = t.AddSeconds(50);
            Assert.AreEqual(NoticeGateResult.Show, gate.Ask(NoticeKind.Today, true, false, t, () => clear));
            gate.Shown(NoticeKind.Today, t);

            // The day's two notices are used: the tray's news line stays on Start's card.
            Assert.AreEqual(NoticeGateResult.CapReached, gate.Ask(NoticeKind.KaptionNews, true, false, t.AddMinutes(5), () => clear));
            Assert.AreEqual(2, ledger.Count);
            // The news line goes before the day's notice when both wait.
            // Read the order at run time: a comparison of constants is folded by the compiler (MSTEST0032).
            NoticeKind[] byValue = Enum.GetValues<NoticeKind>();
            Assert.IsTrue(Array.IndexOf(byValue, NoticeKind.StaminaNearlyFull) < Array.IndexOf(byValue, NoticeKind.KaptionNews)
                && Array.IndexOf(byValue, NoticeKind.KaptionNews) < Array.IndexOf(byValue, NoticeKind.Today));
        }

        private sealed class Ledger : INoticeLedger
        {
            public int MaxPerDay { get; set; }
            public TimeSpan Gap { get; set; }
            public string Day { get; private set; }
            public int Count { get; private set; }
            public void Save(string day, int count)
            {
                Day = day;
                Count = count;
            }
        }
    }
}
