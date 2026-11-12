using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using AppConfig = GI_Subtitles.Core.Config.Config;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Today;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// A card is built from one snapshot of the server and the saved UID (Services/Today/TodayCardLoader). The
    /// regressions here: the player picks another server while the card's data is still on its way, across the two
    /// servers' day boundary, and the card must not come out with one server's books under another server's day; and
    /// the player saves another account's UID while the showcase is on its way, and the card must not show the first
    /// account's characters, nor use up the day's card.
    /// </summary>
    [TestClass]
    [DoNotParallelize] // the server choice and the UID are Config keys
    public class TodayCardLoaderTests
    {
        /// <summary>Monday 01:00 UTC: 02:00 on Europe, still Sunday's game day; 09:00 on Asia, already Monday's.</summary>
        private static readonly DateTimeOffset AcrossTheBoundary = Utc("2026-11-16T01:00:00Z");

        private static GameManifest Game(string id) => GameRegistry.Current.FindByWireId(id);
        private static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

        private const string First = "700000001", Second = "700000002";
        private static readonly string UidKey = FarmCardSettings.UidKey("Genshin");

        private string _serverBefore, _uidBefore;

        // Every test starts with no UID saved; the ones about accounts save their own.
        [TestInitialize]
        public void RememberTheServerAndTheUid()
        {
            _serverBefore = AppConfig.Get<string>(FarmCardSettings.RegionKey, null);
            _uidBefore = AppConfig.Get<string>(UidKey, null);
            if (AppConfig.Has(UidKey)) AppConfig.Remove(UidKey);
        }

        [TestCleanup]
        public void PutTheServerAndTheUidBack()
        {
            if (_serverBefore == null) AppConfig.Remove(FarmCardSettings.RegionKey);
            else AppConfig.Set(FarmCardSettings.RegionKey, _serverBefore);
            if (_uidBefore == null) { if (AppConfig.Has(UidKey)) AppConfig.Remove(UidKey); }
            else AppConfig.Set(UidKey, _uidBefore);
        }

        /// <summary>Sources whose schedule answers only when the test lets it, and that note what they were asked.</summary>
        private sealed class DelayedSources
        {
            public readonly TaskCompletionSource<TodaySchedule> ScheduleGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public ServerDay? ScheduleDay;
            public FarmServer? ShowcaseServer;

            public TodayCardSources Sources => field ??= new(
                Schedule: day => { ScheduleDay = day; return ScheduleGate.Task; },
                Showcase: (uid, day, server) => { ShowcaseServer = server; return Task.FromResult(ShowcaseRead.None); },
                Catalog: () => Task.FromResult(FarmCardSamples.Catalog()),
                Week: _ => Task.FromResult<TodayWeek>(null),
                Seen: _ => Array.Empty<AssistStoredReading>());
        }

        [TestMethod]
        public async Task TheServerChangesWhileTheDataLoads_TheCardKeepsTheOneItStartedWith()
        {
            AppConfig.Set(FarmCardSettings.RegionKey, "europe");
            GameManifest genshin = Game("genshin");
            TodayCardRequest request = TodayCardRequest.Capture(genshin, AcrossTheBoundary);
            Assert.AreEqual("europe", request.ServerId);
            Assert.AreEqual("2026-11-15", request.DayKey, "Europe is still on Sunday's game day");

            var delayed = new DelayedSources();
            Task<TodayCardModel> build = TodayCardLoader.BuildAsync(request, "en-US", delayed.Sources);
            Assert.IsFalse(build.IsCompleted, "the build waits for the schedule");

            // The player picks Asia while the schedule is on its way. Asia is already on Monday.
            AppConfig.Set(FarmCardSettings.RegionKey, "asia");
            Assert.AreEqual("2026-11-16", TodayCardSettings.DayKey(genshin, AcrossTheBoundary, TodayCardSettings.ServerId));
            delayed.ScheduleGate.SetResult(FarmCardSamples.Schedule());
            TodayCardModel model = await build;

            // Every section is Europe's Sunday: the books, the card's day, the resets, the server asked for the showcase.
            Assert.AreEqual(DayOfWeek.Sunday, delayed.ScheduleDay.Value.Weekday);
            Assert.AreEqual(FarmServer.Europe, delayed.ShowcaseServer);
            Assert.IsNotNull(model.Farm);
            Assert.AreEqual(DayOfWeek.Sunday, model.Farm.Weekday);
            Assert.AreEqual(DayOfWeek.Sunday, model.Weekday, "the card's day is the day its books are for");
            Assert.AreEqual("Europe", model.Resets.ServerName);
            Assert.AreEqual(Utc("2026-11-16T03:00:00Z"), model.Resets.Daily);
            Assert.AreEqual(model.Farm.Reset, model.Resets.Daily, "one reset, not Europe's books under Asia's countdown");

            // And the host can tell the card is no longer what is selected: it discards or builds again.
            Assert.IsFalse(request.StillCurrent(genshin, TodayCardSettings.ServerId, FarmCardSettings.Uid, AcrossTheBoundary));
        }

        /// <summary>
        /// The host's own steps on a fake: the data (through the real loader), then a second wait standing for the
        /// icons and the screen, which answers only when the test lets it.
        /// </summary>
        private sealed class Showing
        {
            public readonly List<TodayCardRequest> Prepared = new();
            /// <summary>What was shown, and the server selected at that moment.</summary>
            public readonly List<(TodayCardRequest Request, string SelectedThen)> Shown = new();
            public readonly List<TaskCompletionSource<bool>> IconGates = new();
            private readonly GameManifest _game;

            public Showing(GameManifest game) => _game = game;

            public async Task<TodayCardModel> Prepare(TodayCardRequest request)
            {
                Prepared.Add(request);
                var sources = new DelayedSources();
                sources.ScheduleGate.SetResult(FarmCardSamples.Schedule());
                TodayCardModel model = await TodayCardLoader.BuildAsync(request, "en-US", sources.Sources);
                var icons = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                IconGates.Add(icons);
                await icons.Task;
                return model;
            }

            public Task<TodayCardAttempt<TodayCardModel>> Run(TodayCardRequest first, int rebuilds) =>
                TodayCardLoader.PrepareAndShowAsync(first, Prepare,
                    r => r.StillCurrent(_game, TodayCardSettings.ServerId, FarmCardSettings.Uid, AcrossTheBoundary),
                    () => TodayCardRequest.Capture(_game, AcrossTheBoundary), rebuilds,
                    (r, _) => Shown.Add((r, TodayCardSettings.ServerId)));

            public async Task WaitForIconGate(int count)
            {
                for (int i = 0; i < 200 && IconGates.Count < count; i++) await Task.Delay(5);
                Assert.AreEqual(count, IconGates.Count, "the build reached its icon wait");
            }
        }

        [TestMethod]
        public async Task AskedFor_TheServerChangesDuringTheIconWait_TheCardIsBuiltAgainForTheNewServer()
        {
            AppConfig.Set(FarmCardSettings.RegionKey, "europe");
            GameManifest genshin = Game("genshin");
            var showing = new Showing(genshin);
            var run = showing.Run(TodayCardRequest.Capture(genshin, AcrossTheBoundary), rebuilds: 1);

            // The model is built and passed; the icons are still loading when the player picks Asia.
            await showing.WaitForIconGate(1);
            AppConfig.Set(FarmCardSettings.RegionKey, "asia");
            showing.IconGates[0].SetResult(true);
            await showing.WaitForIconGate(2);
            Assert.IsFalse(run.IsCompleted, "Europe's card is not handed over: it is built again");
            showing.IconGates[1].SetResult(true);

            TodayCardAttempt<TodayCardModel> attempt = await run;
            Assert.IsTrue(attempt.Current);
            Assert.AreEqual(2, attempt.Builds);
            Assert.AreEqual("asia", attempt.Request.ServerId);
            Assert.AreEqual(DayOfWeek.Monday, attempt.Prepared.Weekday, "Asia's Monday, books and resets alike");
            Assert.AreEqual(DayOfWeek.Monday, attempt.Prepared.Farm.Weekday);
            Assert.AreEqual("Asia", attempt.Prepared.Resets.ServerName);
            Assert.AreEqual(1, showing.Shown.Count, "Europe's card was never shown; Asia's was, once");
            Assert.AreEqual(("asia", "asia"), (showing.Shown[0].Request.ServerId, showing.Shown[0].SelectedThen));
        }

        [TestMethod]
        public async Task AskedFor_TheServerChangesAgainDuringTheRetry_NothingStaleIsShown()
        {
            AppConfig.Set(FarmCardSettings.RegionKey, "europe");
            GameManifest genshin = Game("genshin");
            var showing = new Showing(genshin);
            var run = showing.Run(TodayCardRequest.Capture(genshin, AcrossTheBoundary), rebuilds: 1);

            await showing.WaitForIconGate(1);
            AppConfig.Set(FarmCardSettings.RegionKey, "asia");
            showing.IconGates[0].SetResult(true);
            // The second build is for Asia; the player moves on to America while it loads.
            await showing.WaitForIconGate(2);
            AppConfig.Set(FarmCardSettings.RegionKey, "america");
            showing.IconGates[1].SetResult(true);

            TodayCardAttempt<TodayCardModel> attempt = await run;
            Assert.IsFalse(attempt.Current, "one rebuild is the allowance; a card for a server just left is dropped");
            Assert.AreEqual(2, attempt.Builds);
            CollectionAssert.AreEqual(new[] { "europe", "asia" }, showing.Prepared.ConvertAll(r => r.ServerId));
            Assert.AreEqual(0, showing.Shown.Count, "nothing is shown");
        }

        [TestMethod]
        public async Task ByItself_AStaleCardIsDroppedWithoutARebuild_AndAnUnchangedOneIsShown()
        {
            AppConfig.Set(FarmCardSettings.RegionKey, "europe");
            GameManifest genshin = Game("genshin");

            var stale = new Showing(genshin);
            var run = stale.Run(TodayCardRequest.Capture(genshin, AcrossTheBoundary), rebuilds: 0);
            await stale.WaitForIconGate(1);
            AppConfig.Set(FarmCardSettings.RegionKey, "asia");
            stale.IconGates[0].SetResult(true);
            TodayCardAttempt<TodayCardModel> dropped = await run;
            Assert.IsFalse(dropped.Current, "the watcher comes back for the new server; nothing is shown now");
            Assert.AreEqual(1, dropped.Builds);
            Assert.AreEqual(0, stale.Shown.Count);

            AppConfig.Set(FarmCardSettings.RegionKey, "europe");
            var steady = new Showing(genshin);
            var kept = steady.Run(TodayCardRequest.Capture(genshin, AcrossTheBoundary), rebuilds: 1);
            await steady.WaitForIconGate(1);
            steady.IconGates[0].SetResult(true);
            TodayCardAttempt<TodayCardModel> shown = await kept;
            Assert.IsTrue(shown.Current);
            Assert.AreEqual(1, shown.Builds, "nothing changed: one build, shown");
            Assert.AreEqual("Europe", shown.Prepared.Resets.ServerName);
            Assert.AreEqual(1, steady.Shown.Count);
        }

        [TestMethod]
        public void ARequest_IsCurrentOnlyForItsGameServerAndGameDay()
        {
            AppConfig.Set(FarmCardSettings.RegionKey, "europe");
            GameManifest genshin = Game("genshin");
            TodayCardRequest request = TodayCardRequest.Capture(genshin, AcrossTheBoundary);

            Assert.IsTrue(request.StillCurrent(genshin, "europe", null, AcrossTheBoundary.AddSeconds(20)), "nothing changed during the build");
            Assert.IsTrue(request.StillCurrent(genshin, "EUROPE", null, AcrossTheBoundary), "the same server, however it is written");
            Assert.IsFalse(request.StillCurrent(Game("starrail"), "europe", null, AcrossTheBoundary), "another game was selected");
            Assert.IsFalse(request.StillCurrent(null, "europe", null, AcrossTheBoundary));
            Assert.IsFalse(request.StillCurrent(genshin, "asia", null, AcrossTheBoundary), "another server");
            // America is on the same game day as Europe here, and it is still another server: its resets differ.
            Assert.AreEqual(request.DayKey, TodayCardSettings.DayKey(genshin, AcrossTheBoundary, "america"));
            Assert.IsFalse(request.StillCurrent(genshin, "america", null, AcrossTheBoundary));
            // The reset passed while the card was being built: the card is yesterday's.
            Assert.IsFalse(request.StillCurrent(genshin, "europe", null, Utc("2026-11-16T03:00:00Z")));

            // The saved UID is part of what Genshin's card is for: another account's, or none, is another card.
            Assert.IsNull(request.Uid);
            Assert.IsFalse(request.StillCurrent(genshin, "europe", First, AcrossTheBoundary), "a UID saved while a card without one was built");
            Assert.IsTrue(FarmCardSettings.SaveUid(First));
            TodayCardRequest mine = TodayCardRequest.Capture(genshin, AcrossTheBoundary);
            Assert.AreEqual(First, mine.Uid);
            Assert.IsTrue(mine.StillCurrent(genshin, "europe", First, AcrossTheBoundary));
            Assert.IsFalse(mine.StillCurrent(genshin, "europe", Second, AcrossTheBoundary), "another account");
            Assert.IsFalse(mine.StillCurrent(genshin, "europe", null, AcrossTheBoundary), "the UID was removed");
            FarmCardSettings.ForgetUid();

            // No choice saved yet is the game's first server; an unknown id falls back to it too.
            AppConfig.Remove(FarmCardSettings.RegionKey);
            TodayCardRequest unset = TodayCardRequest.Capture(genshin, AcrossTheBoundary);
            Assert.IsNull(unset.ServerId);
            Assert.AreEqual(request.DayKey, unset.DayKey);
            Assert.IsTrue(unset.StillCurrent(genshin, "europe", null, AcrossTheBoundary));
            Assert.IsTrue(unset.StillCurrent(genshin, "mars", null, AcrossTheBoundary));

            // A game without reset data has no server: only its local day counts.
            GameManifest wuwa = Game("wuwa");
            TodayCardRequest local = TodayCardRequest.Capture(wuwa, AcrossTheBoundary);
            Assert.IsTrue(local.StillCurrent(wuwa, "asia", null, AcrossTheBoundary));
            Assert.IsFalse(local.StillCurrent(wuwa, "asia", null, AcrossTheBoundary.AddDays(1)));
        }

        [TestMethod]
        public async Task AGameWithoutTheFarmSection_BuildsFromTheSameSnapshot()
        {
            AppConfig.Set(FarmCardSettings.RegionKey, "europe");
            GameManifest hsr = Game("starrail");
            TodayCardRequest request = TodayCardRequest.Capture(hsr, AcrossTheBoundary);
            var weekGate = new TaskCompletionSource<TodayWeek>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sources = new DelayedSources().Sources with { Week = _ => weekGate.Task };

            Task<TodayCardModel> build = TodayCardLoader.BuildAsync(request, "en-US", sources);
            Assert.IsFalse(build.IsCompleted);
            AppConfig.Set(FarmCardSettings.RegionKey, "asia");
            weekGate.SetResult(null);
            TodayCardModel model = await build;

            Assert.IsNull(model.Farm);
            Assert.AreEqual(DayOfWeek.Sunday, model.Weekday);
            Assert.AreEqual("Europe", model.Resets.ServerName);
            Assert.AreEqual(Utc("2026-11-16T03:00:00Z"), model.Resets.Daily);
        }

        // ── The account ──

        /// <summary>
        /// Sources whose showcase answers only when the test lets it, one gate per ask, noting the UID asked for.
        /// The schedule answers at once unless a test holds it.
        /// </summary>
        private sealed class Accounts
        {
            private readonly List<(string Uid, TaskCompletionSource<ShowcaseRead> Gate)> _asked = new();
            public Task<TodaySchedule> Schedule = Task.FromResult(FarmCardSamples.Schedule());

            public TodayCardSources Sources => field ??= new(
                Schedule: _ => Schedule,
                Showcase: (uid, day, server) =>
                {
                    var gate = new TaskCompletionSource<ShowcaseRead>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_asked) _asked.Add((uid, gate));
                    return gate.Task;
                },
                Catalog: () => Task.FromResult(FarmCardSamples.Catalog()),
                Week: _ => Task.FromResult<TodayWeek>(null),
                Seen: _ => Array.Empty<AssistStoredReading>());

            public string[] AskedUids { get { lock (_asked) return _asked.ConvertAll(a => a.Uid).ToArray(); } }

            /// <summary>Waits for the showcase to be asked the <paramref name="count"/>th time; returns the UID it was asked for.</summary>
            public async Task<string> WaitForAsk(int count)
            {
                for (int i = 0; i < 400 && AskedUids.Length < count; i++) await Task.Delay(5);
                Assert.AreEqual(count, AskedUids.Length, "the build reached its showcase read");
                return AskedUids[count - 1];
            }

            public void Answer(int ask, ShowcaseRead read)
            {
                lock (_asked) _asked[ask - 1].Gate.SetResult(read);
            }
        }

        /// <summary>The first account's showcase: characters with books to farm, so the card has the player's rows.</summary>
        private static ShowcaseRead Characters() => new(FarmCardSamples.Roster(FarmCardSamples.Wednesday), false);

        /// <summary>The host's steps (MainWindow.FarmCard.cs) for Genshin's card, with the test's clock and sources.</summary>
        private static Task<TodayCardAttempt<TodayCardModel>> ShowGenshin(Accounts accounts, int rebuilds, List<TodayCardModel> shown)
        {
            GameManifest genshin = Game("genshin");
            DateTimeOffset now = FarmCardSamples.Wednesday;
            return TodayCardLoader.PrepareAndShowAsync(TodayCardRequest.Capture(genshin, now),
                r => TodayCardLoader.BuildAsync(r, "en-US", accounts.Sources),
                r => r.StillCurrent(genshin, TodayCardSettings.ServerId, FarmCardSettings.Uid, now),
                () => TodayCardRequest.Capture(genshin, now), rebuilds, (_, m) => shown.Add(m));
        }

        [TestMethod]
        public async Task ByItself_AnotherAccountsUidIsSavedWhileTheShowcaseLoads_TheCardIsDropped()
        {
            AppConfig.Set(FarmCardSettings.RegionKey, "europe");
            Assert.IsTrue(FarmCardSettings.SaveUid(First));
            var accounts = new Accounts();
            var shown = new List<TodayCardModel>();
            var run = ShowGenshin(accounts, rebuilds: 0, shown);
            Assert.AreEqual(First, await accounts.WaitForAsk(1));

            // The player saves the second account's UID while the first one's showcase is on its way.
            Assert.IsTrue(FarmCardSettings.SaveUid(Second));
            accounts.Answer(1, Characters());
            TodayCardAttempt<TodayCardModel> attempt = await run;

            Assert.AreEqual(FarmPlayerState.Rows, attempt.Prepared.Farm.Player, "what was built is the first account's characters");
            Assert.IsFalse(attempt.Current, "so it is not the card to show: the host puts the day back and the watcher returns");
            Assert.AreEqual(First, attempt.Request.Uid);
            Assert.AreEqual(0, shown.Count, "the first account's characters never show under the second account's UID");

            // The watcher's next look takes a fresh snapshot: the second account's showcase is read, and that card shows.
            var next = ShowGenshin(accounts, rebuilds: 0, shown);
            Assert.AreEqual(Second, await accounts.WaitForAsk(2));
            accounts.Answer(2, ShowcaseRead.None);
            TodayCardAttempt<TodayCardModel> second = await next;
            Assert.IsTrue(second.Current);
            Assert.AreEqual(Second, second.Request.Uid);
            Assert.AreEqual(1, shown.Count);
            Assert.AreEqual(FarmPlayerState.Unavailable, shown[0].Farm.Player, "nothing of the first account's showcase is on it");
        }

        [TestMethod]
        public async Task AskedFor_AnotherAccountsUidIsSavedWhileTheShowcaseLoads_TheCardIsBuiltAgainForThatAccount()
        {
            AppConfig.Set(FarmCardSettings.RegionKey, "europe");
            Assert.IsTrue(FarmCardSettings.SaveUid(First));
            var accounts = new Accounts();
            var shown = new List<TodayCardModel>();
            var run = ShowGenshin(accounts, rebuilds: 1, shown);
            Assert.AreEqual(First, await accounts.WaitForAsk(1));

            Assert.IsTrue(FarmCardSettings.SaveUid(Second));
            accounts.Answer(1, Characters());
            Assert.AreEqual(Second, await accounts.WaitForAsk(2), "the fresh build reads the account saved now");
            accounts.Answer(2, new ShowcaseRead(FarmCardSamples.Roster(FarmCardSamples.Wednesday, empty: true), false));
            TodayCardAttempt<TodayCardModel> attempt = await run;

            Assert.IsTrue(attempt.Current);
            Assert.AreEqual(2, attempt.Builds);
            Assert.AreEqual(Second, attempt.Request.Uid);
            Assert.AreEqual(1, shown.Count, "the first account's card was never shown; the second's was, once");
            Assert.AreEqual(FarmPlayerState.ShowcaseEmpty, shown[0].Farm.Player, "the second account's own (empty) showcase");
        }

        [TestMethod]
        public async Task AskedFor_AnotherUidIsSavedAgainDuringTheRetry_NothingStaleIsShown()
        {
            AppConfig.Set(FarmCardSettings.RegionKey, "europe");
            Assert.IsTrue(FarmCardSettings.SaveUid(First));
            var accounts = new Accounts();
            var shown = new List<TodayCardModel>();
            var run = ShowGenshin(accounts, rebuilds: 1, shown);
            Assert.AreEqual(First, await accounts.WaitForAsk(1));

            Assert.IsTrue(FarmCardSettings.SaveUid(Second));
            accounts.Answer(1, Characters());
            // The second build is for the second account; the player goes back to the first while it loads.
            Assert.AreEqual(Second, await accounts.WaitForAsk(2));
            Assert.IsTrue(FarmCardSettings.SaveUid(First));
            accounts.Answer(2, ShowcaseRead.None);
            TodayCardAttempt<TodayCardModel> attempt = await run;

            Assert.IsFalse(attempt.Current, "one rebuild is the allowance; a card for an account just left is dropped");
            Assert.AreEqual(2, attempt.Builds);
            Assert.AreEqual(0, shown.Count, "nothing is shown");
        }

        [TestMethod]
        public async Task AskedFor_TheUidIsRemovedWhileTheShowcaseLoads_TheCardIsBuiltAgainWithoutCharacters()
        {
            AppConfig.Set(FarmCardSettings.RegionKey, "europe");
            Assert.IsTrue(FarmCardSettings.SaveUid(First));
            var accounts = new Accounts();
            var shown = new List<TodayCardModel>();
            var run = ShowGenshin(accounts, rebuilds: 1, shown);
            Assert.AreEqual(First, await accounts.WaitForAsk(1));

            // "Remove the UID and its data" while the showcase is on its way.
            FarmCardSettings.ForgetUid();
            accounts.Answer(1, Characters());
            Assert.IsNull(await accounts.WaitForAsk(2), "the fresh build has no account to read");
            accounts.Answer(2, ShowcaseRead.None);
            TodayCardAttempt<TodayCardModel> attempt = await run;

            Assert.IsTrue(attempt.Current);
            Assert.AreEqual(2, attempt.Builds);
            Assert.IsNull(attempt.Request.Uid);
            Assert.AreEqual(1, shown.Count);
            Assert.AreEqual(FarmPlayerState.NoUid, shown[0].Farm.Player, "the removed account's characters are not on the card");
        }

        [TestMethod]
        public async Task ACardWithoutCharacters_IsNotStaleWhenTheUidChanges()
        {
            // Star Rail's card reads no showcase: a Genshin UID saved while it loads is nothing to it.
            AppConfig.Set(FarmCardSettings.RegionKey, "europe");
            Assert.IsTrue(FarmCardSettings.SaveUid(First));
            GameManifest hsr = Game("starrail");
            TodayCardRequest request = TodayCardRequest.Capture(hsr, AcrossTheBoundary);
            Assert.IsNull(request.Uid, "the saved UID is Genshin's");
            var weekGate = new TaskCompletionSource<TodayWeek>(TaskCreationOptions.RunContinuationsAsynchronously);
            var accounts = new Accounts();
            TodayCardSources sources = accounts.Sources with { Week = _ => weekGate.Task };
            var shown = new List<TodayCardModel>();
            var run = TodayCardLoader.PrepareAndShowAsync(request, r => TodayCardLoader.BuildAsync(r, "en-US", sources),
                r => r.StillCurrent(hsr, TodayCardSettings.ServerId, FarmCardSettings.Uid, AcrossTheBoundary),
                () => TodayCardRequest.Capture(hsr, AcrossTheBoundary), rebuilds: 0, (_, m) => shown.Add(m));
            Assert.IsFalse(run.IsCompleted);

            Assert.IsTrue(FarmCardSettings.SaveUid(Second));
            weekGate.SetResult(null);
            TodayCardAttempt<TodayCardModel> attempt = await run;

            Assert.IsTrue(attempt.Current, "the card is still what it was built for");
            Assert.AreEqual(1, attempt.Builds);
            Assert.AreEqual(1, shown.Count);
            Assert.AreEqual(0, accounts.AskedUids.Length, "and no showcase was read for it");
        }

        [TestMethod]
        public async Task TheUidGoesAwayAndComesBackWhileItLoads_TheShowcaseAskedForIsTheRequests()
        {
            AppConfig.Set(FarmCardSettings.RegionKey, "europe");
            Assert.IsTrue(FarmCardSettings.SaveUid(First));
            // The schedule waits too: the loader used to read the saved UID only after it.
            var schedule = new TaskCompletionSource<TodaySchedule>(TaskCreationOptions.RunContinuationsAsynchronously);
            var accounts = new Accounts { Schedule = schedule.Task };
            var shown = new List<TodayCardModel>();
            var run = ShowGenshin(accounts, rebuilds: 0, shown);

            // First -> Second while the schedule is on its way.
            Assert.IsTrue(FarmCardSettings.SaveUid(Second));
            schedule.SetResult(FarmCardSamples.Schedule());
            Assert.AreEqual(First, await accounts.WaitForAsk(1), "the showcase asked for is the account the card was started for");

            // Second -> First before the showcase arrives: the saved account is the request's again.
            Assert.IsTrue(FarmCardSettings.SaveUid(First));
            accounts.Answer(1, Characters());
            TodayCardAttempt<TodayCardModel> attempt = await run;

            Assert.IsTrue(attempt.Current, "nothing differs from what the card was built for");
            Assert.AreEqual(1, attempt.Builds);
            CollectionAssert.AreEqual(new[] { First }, accounts.AskedUids, "one read, and never the account in between");
            Assert.AreEqual(1, shown.Count);
            Assert.AreEqual(FarmPlayerState.Rows, shown[0].Farm.Player);
        }

        // ── On the dispatcher ──

        /// <summary>
        /// The app's own conditions: a WPF dispatcher, where a method that resumes after a wait is a queued operation
        /// and others can run before it. A server change is queued, ahead of everything else, at the very moment the
        /// freshness check says "current". The card must be shown before that change runs, never after it: the check
        /// and the showing are one step, with no wait between them.
        /// </summary>
        [TestMethod]
        public void OnTheDispatcher_NothingRunsBetweenTheLastCheckAndTheCard()
        {
            string shownFor = null, selectedWhenShown = null;
            int shows = 0;
            Exception error = null;
            RunSta(() =>
            {
                AppConfig.Set(FarmCardSettings.RegionKey, "europe");
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                GameManifest genshin = Game("genshin");
                TodayCardRequest first = TodayCardRequest.Capture(genshin, AcrossTheBoundary);
                var sources = new DelayedSources();
                sources.ScheduleGate.SetResult(FarmCardSamples.Schedule());
                var frame = new DispatcherFrame();

                // A wait that really suspends, so every step after it is a new dispatcher operation.
                async Task<TodayCardModel> Prepare(TodayCardRequest r)
                {
                    await Task.Delay(20);
                    return await TodayCardLoader.BuildAsync(r, "en-US", sources.Sources);
                }

                bool StillCurrent(TodayCardRequest r)
                {
                    bool current = r.StillCurrent(genshin, TodayCardSettings.ServerId, FarmCardSettings.Uid, AcrossTheBoundary);
                    // The player's click lands now: queued before anything that is posted after this answer.
                    if (current) dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => AppConfig.Set(FarmCardSettings.RegionKey, "asia")));
                    return current;
                }

                void Show(TodayCardRequest r, TodayCardModel m)
                {
                    shows++;
                    shownFor = r.ServerId;
                    selectedWhenShown = TodayCardSettings.ServerId;
                }

                async Task Host()
                {
                    var attempt = await TodayCardLoader.PrepareAndShowAsync(first, Prepare, StillCurrent,
                        () => TodayCardRequest.Capture(genshin, AcrossTheBoundary), rebuilds: 0, Show);
                    Assert.IsTrue(attempt.Current);
                }

                // Started from a dispatcher operation, as every showing in the app is (a timer tick, a click).
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try { await Host(); }
                    catch (Exception ex) { error = ex; }
                    finally { frame.Continue = false; }
                }));
                var guard = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Normal, (_, __) => frame.Continue = false, dispatcher);
                Dispatcher.PushFrame(frame);
                guard.Stop();
                // Let the queued server change run: it comes after the card, not before it.
                dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            });
            if (error != null) throw new AssertFailedException("the host failed: " + error);
            Assert.AreEqual(1, shows);
            Assert.AreEqual("europe", shownFor);
            Assert.AreEqual("europe", selectedWhenShown, "the card went up while its server was still the selected one");
            Assert.AreEqual("asia", TodayCardSettings.ServerId, "the change itself was not lost: it ran after the card");
        }
    }
}
