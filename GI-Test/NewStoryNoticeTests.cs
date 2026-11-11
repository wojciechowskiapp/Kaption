using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Services.Notices;
using GI_Subtitles.Services.Translation;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ManifestEntry = GI_Subtitles.Services.Translation.DictionarySyncService.ManifestEntry;

namespace GI_Test
{
    /// <summary>
    /// "Genshin Impact 7.2: the new story is in Polish now": what counts as a new game version, the once-only memory,
    /// the line on Start and its link, and what the pack sync reports. Storage is a dictionary; Config is never touched.
    /// </summary>
    [TestClass]
    public class NewStoryNoticeTests
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

        private sealed class MemoryStore : INewStoryStore
        {
            public bool Enabled { get; set; } = true;
            public readonly Dictionary<string, IReadOnlyList<string>> Values = new();
            public IReadOnlyList<string> Read(string key) => Values.TryGetValue(key, out var v) ? v : Array.Empty<string>();
            public void Write(string key, IReadOnlyList<string> values) => Values[key] = values.ToList();
        }

        private static PackVersionChange Change(string from, string to, string game = "genshin", string language = "pl") =>
            new(game, language, from, to);

        // ── The game version inside a pack version ──────────────────────────

        [TestMethod]
        [DataRow("7.2.0-r10", 7, 2)]
        [DataRow("7.2.0", 7, 2)]
        [DataRow("7.2", 7, 2)]
        [DataRow("3.7.1", 3, 7)]
        [DataRow(" 4.6.0-r11 ", 4, 6)]
        [DataRow("10.0.0", 10, 0)]
        public void GameVersion_IsMajorAndMinor(string packVersion, int major, int minor)
        {
            Assert.IsTrue(GameVersion.TryParse(packVersion, out var v));
            Assert.AreEqual(new GameVersion(major, minor), v);
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("7")]
        [DataRow("seven.two")]
        [DataRow("7.x.0")]
        [DataRow("7.2.0.1")]
        [DataRow("-7.2")]
        [DataRow("7.+2")]
        public void GameVersion_Unreadable_IsNotAVersion(string packVersion)
        {
            Assert.IsFalse(GameVersion.TryParse(packVersion, out _));
        }

        [TestMethod]
        public void GameVersion_ReadsAsPlayersNameIt() => Assert.AreEqual("7.2", new GameVersion(7, 2).ToString());

        // ── Is this a new game version ──────────────────────────────────────

        [TestMethod]
        [DataRow("7.1.0-r4", "7.2.0", true, DisplayName = "a new minor version")]
        [DataRow("7.1.0", "7.2.0-r3", true, DisplayName = "a new minor, first seen at a later revision")]
        [DataRow("7.8.0-r2", "8.0.0", true, DisplayName = "a new major version")]
        [DataRow("7.2.0", "7.2.0-r2", false, DisplayName = "a hotfix revision")]
        [DataRow("7.2.0-r2", "7.2.0-r10", false, DisplayName = "a later hotfix revision")]
        [DataRow("3.7.0", "3.7.1", false, DisplayName = "a third-number hotfix")]
        [DataRow("7.2.0", "7.2.0", false, DisplayName = "the same version again")]
        [DataRow("7.2.0", "7.1.0-r5", false, DisplayName = "a downgrade")]
        [DataRow(null, "7.2.0", false, DisplayName = "the first install of a game")]
        [DataRow("", "7.2.0", false, DisplayName = "no version on record")]
        [DataRow("old", "7.2.0", false, DisplayName = "an unreadable installed version")]
        [DataRow("7.1.0", "soon", false, DisplayName = "an unreadable new version")]
        public void NewGameVersion_Rule(string from, string to, bool expected)
        {
            Assert.AreEqual(expected, NewStoryRules.IsNewGameVersion(Change(from, to), out var arrived));
            if (expected) Assert.IsTrue(GameVersion.TryParse(to, out var v) && v == arrived);
        }

        [TestMethod]
        public void NewGameVersion_OnlyForAPolishPack()
        {
            // The notice says "in Polish"; a pack in another language must not borrow those words.
            Assert.IsFalse(NewStoryRules.IsNewGameVersion(Change("7.1.0", "7.2.0", language: "de"), out _));
            Assert.IsTrue(NewStoryRules.IsNewGameVersion(Change("7.1.0", "7.2.0", language: "PL"), out _));
            Assert.IsFalse(NewStoryRules.IsNewGameVersion(null, out _));
        }

        // ── Once per game and version ───────────────────────────────────────

        [TestMethod]
        public void Offer_NewVersion_RaisesOneNotice()
        {
            var store = new MemoryStore();
            var notices = new NewStoryNotices(store);
            int raised = 0;
            notices.Raised += () => raised++;

            var got = notices.Offer(new[] { Change("7.1.0-r4", "7.2.0") }, Now);

            Assert.AreEqual(1, got.Count);
            Assert.AreEqual("genshin/7.2", got[0].Id);
            Assert.AreEqual(1, raised);
            Assert.AreEqual("genshin/7.2", notices.Current(Now).Id);
            CollectionAssert.AreEqual(new[] { "genshin/7.2" }, store.Read(NewStoryNotices.NoticedKey).ToList());
        }

        [TestMethod]
        public void Offer_SameVersionAgain_NeverRaisesASecond()
        {
            var store = new MemoryStore();
            var notices = new NewStoryNotices(store);
            notices.Offer(new[] { Change("7.1.0-r4", "7.2.0") }, Now);

            // A reinstall, a restored manifest or a second PC profile offering the same step again.
            Assert.AreEqual(0, notices.Offer(new[] { Change("7.1.0-r4", "7.2.0-r2") }, Now.AddDays(1)).Count);
            // And a fresh instance over the same storage (the next launch).
            Assert.AreEqual(0, new NewStoryNotices(store).Offer(new[] { Change("7.1.0", "7.2.0") }, Now.AddDays(2)).Count);
        }

        [TestMethod]
        public void Offer_Hotfix_FirstInstall_Downgrade_RaiseNothing()
        {
            var notices = new NewStoryNotices(new MemoryStore());
            var got = notices.Offer(new[]
            {
                Change("7.2.0", "7.2.0-r2"), Change(null, "4.6.0", game: "starrail"), Change("3.3.0", "3.2.0", game: "zzz"),
            }, Now);
            Assert.AreEqual(0, got.Count);
            Assert.IsNull(notices.Current(Now));
            Assert.AreEqual(0, notices.TakeUndelivered().Count);
        }

        [TestMethod]
        public void Offer_SecondGame_HasItsOwnNotice()
        {
            var notices = new NewStoryNotices(new MemoryStore());
            notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now);
            var got = notices.Offer(new[] { Change("4.5.0-r3", "4.6.0", game: "starrail") }, Now.AddHours(1));

            Assert.AreEqual(1, got.Count);
            Assert.AreEqual("starrail/4.6", got[0].Id);
            Assert.AreEqual("starrail/4.6", notices.Current(Now.AddHours(1)).Id, "the newest line shows first");
        }

        [TestMethod]
        public void Offer_NextVersionOfTheSameGame_ReplacesItsLine()
        {
            var store = new MemoryStore();
            var notices = new NewStoryNotices(store);
            notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now);
            notices.Offer(new[] { Change("7.2.0-r9", "7.3.0") }, Now.AddDays(3));

            Assert.AreEqual(1, store.Read(NewStoryNotices.PendingKey).Count, "one line per game");
            Assert.AreEqual("genshin/7.3", notices.Current(Now.AddDays(3)).Id);
        }

        [TestMethod]
        public void SwitchedOff_RaisesNothing_AndBringsNothingBackLater()
        {
            var store = new MemoryStore { Enabled = false };
            var notices = new NewStoryNotices(store);

            Assert.AreEqual(0, notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now).Count);
            Assert.IsNull(notices.Current(Now));
            Assert.AreEqual(0, notices.TakeUndelivered().Count);

            store.Enabled = true;
            Assert.IsNull(notices.Current(Now), "turning the switch on shows nothing old");
            Assert.AreEqual(0, notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now.AddDays(1)).Count, "that version already passed");
        }

        [TestMethod]
        public void SwitchedOffLater_HidesTheLine_AndDropsTheBalloon()
        {
            var store = new MemoryStore();
            var notices = new NewStoryNotices(store);
            notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now);

            store.Enabled = false;
            notices.OnEnabledChanged();

            Assert.IsNull(notices.Current(Now));
            Assert.AreEqual(0, notices.TakeUndelivered().Count);

            store.Enabled = true;
            notices.OnEnabledChanged();
            Assert.IsNull(notices.Current(Now), "off ended the line; switching back on does not bring it back");
        }

        [TestMethod]
        public void Dismiss_ClosesTheLineForGood()
        {
            var store = new MemoryStore();
            var notices = new NewStoryNotices(store);
            var notice = notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now)[0];
            int changed = 0;
            notices.Changed += () => changed++;

            notices.Dismiss(notice);

            Assert.AreEqual(1, changed);
            Assert.IsNull(notices.Current(Now));
            Assert.IsNull(new NewStoryNotices(store).Current(Now), "still closed after a restart");
            Assert.AreEqual(0, notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now.AddDays(1)).Count, "and never raised again");
            Assert.AreEqual(0, notices.TakeUndelivered().Count, "a closed line takes its balloon with it");
        }

        [TestMethod]
        public void Balloon_IsHandedOutOnce()
        {
            var notices = new NewStoryNotices(new MemoryStore());
            notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now);

            Assert.AreEqual(1, notices.TakeUndelivered().Count);
            Assert.AreEqual(0, notices.TakeUndelivered().Count);
            Assert.IsNotNull(notices.Current(Now), "the line on Start stays until it is closed");
        }

        [TestMethod]
        public void Line_LeavesStart_AfterTheFreshWindow()
        {
            var notices = new NewStoryNotices(new MemoryStore());
            notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now);

            Assert.IsNotNull(notices.Current(Now.AddDays(NewStoryRules.PendingDays).AddMinutes(-1)));
            Assert.IsNull(notices.Current(Now.AddDays(NewStoryRules.PendingDays)));
        }

        [TestMethod]
        public void AListenerThatThrows_DoesNotFailTheSync()
        {
            var notices = new NewStoryNotices(new MemoryStore());
            notices.Raised += () => throw new InvalidOperationException("listener bug");
            Assert.AreEqual(1, notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now).Count);
        }

        [TestMethod]
        public void Marks_AreOnePerGame_TheHighestVersion_AndNeverLowered()
        {
            var marks = NewStoryMarks.With(new[] { "genshin/7.1", "starrail/4.6", "nonsense", "genshin/x", "/7.2", "" }, "Genshin", new GameVersion(7, 2));
            CollectionAssert.AreEqual(new[] { "genshin/7.2", "starrail/4.6" }, marks.ToList());

            CollectionAssert.AreEqual(new[] { "genshin/7.2", "starrail/4.6" }, NewStoryMarks.With(marks, "genshin", new GameVersion(7, 1)).ToList(), "never lowered");
            CollectionAssert.AreEqual(new[] { "genshin/7.3" }, NewStoryMarks.With(new[] { "genshin/7.1", "GENSHIN/7.3", "genshin/7.2" }, "", default).ToList(),
                "a repeated game keeps its highest, an empty game adds nothing");

            Assert.IsTrue(NewStoryMarks.Covers(marks, "GENSHIN", new GameVersion(7, 2)));
            Assert.IsTrue(NewStoryMarks.Covers(marks, "genshin", new GameVersion(6, 8)), "anything at or below the mark");
            Assert.IsFalse(NewStoryMarks.Covers(marks, "genshin", new GameVersion(7, 3)));
            Assert.IsFalse(NewStoryMarks.Covers(marks, "zzz", new GameVersion(1, 0)), "a game without a mark");
            Assert.IsFalse(NewStoryMarks.Covers(null, "genshin", new GameVersion(7, 2)));
        }

        [TestMethod]
        public void Memory_NeverForgets_HoweverManyVersionsPass()
        {
            // A capped list of ids would drop 7.2 after enough versions; the per-game mark cannot.
            var store = new MemoryStore();
            var notices = new NewStoryNotices(store);
            for (int minor = 1; minor <= 80; minor++)
                Assert.AreEqual(1, notices.Offer(new[] { Change($"7.{minor - 1}.0", $"7.{minor}.0") }, Now.AddDays(minor)).Count, $"7.{minor}");

            Assert.AreEqual(1, store.Read(NewStoryNotices.NoticedKey).Count, "one mark for the game, not a growing list");
            // The pack is rolled back and upgraded again to versions announced long ago: nothing is announced twice.
            Assert.AreEqual(0, notices.Offer(new[] { Change("7.80.0", "7.1.0") }, Now.AddDays(100)).Count, "a downgrade");
            Assert.AreEqual(0, notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now.AddDays(101)).Count, "an old version again");
            Assert.AreEqual(0, notices.Offer(new[] { Change("7.2.0", "7.80.0") }, Now.AddDays(102)).Count, "back to where it was");
            Assert.AreEqual(1, notices.Offer(new[] { Change("7.80.0", "7.81.0") }, Now.AddDays(103)).Count, "a really new one still is news");
        }

        [TestMethod]
        public void Line_DatedInTheFuture_IsIgnored_AndHidesNothing()
        {
            var store = new MemoryStore();
            var fresh = new NewStoryNotice("starrail", new GameVersion(4, 6), Now.AddHours(-2));
            var farAhead = new NewStoryNotice("genshin", new GameVersion(9, 9), Now.AddYears(5));
            store.Values[NewStoryNotices.PendingKey] = new[] { NewStoryRules.Encode(farAhead), NewStoryRules.Encode(fresh) };
            var notices = new NewStoryNotices(store);

            Assert.AreEqual("starrail/4.6", notices.Current(Now).Id, "a hand-edited or wrong-clock entry does not win");
            Assert.IsFalse(notices.IsPending(farAhead, Now));
            Assert.IsTrue(notices.IsPending(fresh, Now));

            // A clock set back a little after the notice was raised keeps its line.
            var justAhead = new NewStoryNotice("zzz", new GameVersion(3, 4), Now.AddHours(3));
            Assert.IsTrue(NewStoryRules.IsFresh(justAhead, Now));
            Assert.IsFalse(NewStoryRules.IsFresh(new NewStoryNotice("zzz", new GameVersion(3, 4), Now + NewStoryRules.ClockSkew), Now));
            Assert.IsFalse(NewStoryRules.IsFresh(null, Now));
        }

        [TestMethod]
        public void IsPending_FollowsTheLine()
        {
            var store = new MemoryStore();
            var notices = new NewStoryNotices(store);
            var genshin = notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now)[0];
            var starrail = notices.Offer(new[] { Change("4.5.0", "4.6.0", game: "starrail") }, Now)[0];

            Assert.IsTrue(notices.IsPending(genshin, Now) && notices.IsPending(starrail, Now));
            notices.Dismiss(genshin);
            Assert.IsFalse(notices.IsPending(genshin, Now), "closed");
            Assert.IsTrue(notices.IsPending(starrail, Now), "the other game's line is its own");
            Assert.IsFalse(notices.IsPending(starrail, Now.AddDays(NewStoryRules.PendingDays)), "expired");
            store.Enabled = false;
            Assert.IsFalse(notices.IsPending(starrail, Now), "switched off");
            Assert.IsFalse(notices.IsPending(null, Now));
        }

        // ── The balloons ────────────────────────────────────────────────────

        [TestMethod]
        public void Balloons_TwoGames_EachGetsItsOwn_InOrder()
        {
            var notices = new NewStoryNotices(new MemoryStore());
            notices.Offer(new[] { Change("7.1.0", "7.2.0"), Change("4.5.0", "4.6.0", game: "starrail") }, Now);
            var queue = new NewStoryBalloonQueue();
            queue.Add(notices.TakeUndelivered());
            queue.Add(notices.TakeUndelivered()); // nothing new: each notice is handed out once

            Assert.AreEqual(2, queue.Count);
            Assert.AreEqual("genshin/7.2", queue.TakeNext().Id);
            Assert.AreEqual(1, queue.Count, "showing one balloon keeps the other waiting");
            Assert.AreEqual("starrail/4.6", queue.TakeNext().Id);
            Assert.IsNull(queue.TakeNext());
        }

        [TestMethod]
        public void Balloons_AClosedLine_TakesOnlyItsOwnBalloon()
        {
            var notices = new NewStoryNotices(new MemoryStore());
            var raised = notices.Offer(new[] { Change("7.1.0", "7.2.0"), Change("4.5.0", "4.6.0", game: "starrail") }, Now);
            var queue = new NewStoryBalloonQueue();
            queue.Add(raised);
            queue.Add(raised); // the same notices again add nothing
            Assert.AreEqual(2, queue.Count);

            notices.Dismiss(raised[1]); // the player closes Star Rail's line
            queue.KeepOnly(n => notices.IsPending(n, Now));

            Assert.AreEqual(1, queue.Count);
            Assert.AreEqual("genshin/7.2", queue.TakeNext().Id, "Genshin's balloon still comes");
        }

        [TestMethod]
        public void Balloons_ANoticeThatExpiredWhileWaiting_IsNotShown()
        {
            var notices = new NewStoryNotices(new MemoryStore());
            var queue = new NewStoryBalloonQueue();
            queue.Add(notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now));

            // Delivery was blocked (subtitles running) until the line's fresh window passed: the gate opens on nothing.
            var later = Now.AddDays(NewStoryRules.PendingDays).AddHours(1);
            queue.KeepOnly(n => notices.IsPending(n, later));

            Assert.AreEqual(0, queue.Count);
            Assert.IsNull(queue.TakeNext());
        }

        /// <summary>A store whose writes start failing after a given number, like a process that stops mid-way.</summary>
        private sealed class FailingStore : INewStoryStore
        {
            public readonly MemoryStore Inner = new();
            public int WritesLeft = int.MaxValue;
            public bool Enabled => Inner.Enabled;
            public IReadOnlyList<string> Read(string key) => Inner.Read(key);
            public void Write(string key, IReadOnlyList<string> values)
            {
                if (WritesLeft-- <= 0) throw new IOException("disk full");
                Inner.Write(key, values);
            }
        }

        [TestMethod]
        public void StoppedBetweenTheTwoWrites_TheLineIsAlreadySafe()
        {
            // The sync will not offer this step again (the pack is already 7.2 next time), so the line must be the
            // write that lands first: a mark without a line would lose the notice for good.
            var store = new FailingStore { WritesLeft = 1 };
            var notices = new NewStoryNotices(store);

            Assert.ThrowsExactly<IOException>(() => notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now));

            Assert.AreEqual(0, store.Inner.Read(NewStoryNotices.NoticedKey).Count, "the mark did not land");
            Assert.AreEqual("genshin/7.2", new NewStoryNotices(store.Inner).Current(Now).Id, "the line did, and shows after a restart");
        }

        [TestMethod]
        public void DismissStoppedBetweenItsTwoWrites_TheLineIsClosedAllTheSame()
        {
            // The acknowledgement lands first. Without the second write the entry is still in Config, but it is no
            // longer shown, and the What's new card of the same article stays closed after a restart.
            var store = new FailingStore();
            var notices = new NewStoryNotices(store);
            var notice = notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now)[0];

            store.WritesLeft = 1;
            Assert.ThrowsExactly<IOException>(() => notices.Dismiss(notice));

            var restarted = new NewStoryNotices(store.Inner);
            Assert.AreEqual(1, store.Inner.Read(NewStoryNotices.PendingKey).Count, "the removal did not land");
            Assert.IsNull(restarted.Current(Now), "and the line is closed anyway");
            Assert.IsFalse(restarted.IsPending(notice, Now), "so no balloon follows");
            Assert.IsTrue(restarted.Acknowledges("genshin", "7.2", "new_version"), "and What's new does not repeat it");
        }

        [TestMethod]
        public async Task SyncStoppedBeforeTheManifestIsSaved_TheNextSyncReportsTheStepAgain_AndItStaysOneNotice()
        {
            // The notice is raised before the manifest is saved. A stop between the two leaves the old version in the
            // manifest with the new pack on disk: the next sync relabels it, reports 7.1 to 7.2 again, and the mark
            // keeps that to the one notice already raised.
            var notices = new NewStoryNotices(new MemoryStore());
            var first = await Sync(Meta("7.2.0", "dist-new", packSha: PackSha), Installed("7.1.0-r4"));
            Assert.AreEqual(1, notices.Offer(first.VersionChanges, Now).Count);

            var again = await Sync(Meta("7.2.0", "dist-new", packSha: PackSha), Installed("7.1.0-r4")); // the manifest never saved
            Assert.AreEqual(1, again.VersionChanges.Count, "the step is seen again");
            Assert.AreEqual(0, notices.Offer(again.VersionChanges, Now.AddMinutes(5)).Count, "and raises nothing new");
            Assert.AreEqual("genshin/7.2", notices.Current(Now.AddMinutes(5)).Id, "the line from the first time is still there");
        }

        [TestMethod]
        public void NothingWritten_NothingMarked_SoALaterOfferStillRaises()
        {
            var store = new FailingStore { WritesLeft = 0 };
            var notices = new NewStoryNotices(store);
            Assert.ThrowsExactly<IOException>(() => notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now));

            store.WritesLeft = int.MaxValue;
            Assert.AreEqual(1, notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now).Count);
        }

        // ── Opening the page ────────────────────────────────────────────────

        [TestMethod]
        public void OpeningThePage_CountsOnlyWhenItReallyOpened()
        {
            string opened = null;
            Assert.IsTrue(MainWindow.TryOpenNewStoryPage("https://kaption.one/pl/aktualizacje/genshin-7-2/", u => opened = u));
            Assert.AreEqual("https://kaption.one/pl/aktualizacje/genshin-7-2/", opened);

            // No browser to hand the link to: nothing opened, so the line must stay for another try.
            Assert.IsFalse(MainWindow.TryOpenNewStoryPage("https://kaption.one/pl/aktualizacje/genshin-7-2/",
                _ => throw new System.ComponentModel.Win32Exception(1155, "No application is associated with the specified file")));

            bool launched = false;
            Assert.IsFalse(MainWindow.TryOpenNewStoryPage(null, _ => launched = true), "a link that is not on the site");
            Assert.IsFalse(launched);
        }

        // ── The What's new card next to the notice ──────────────────────────

        [TestMethod]
        public void ClosedOrOpenedNotice_IsAcknowledged_WheneverTheFeedLoads()
        {
            var store = new MemoryStore();
            var notices = new NewStoryNotices(store);
            var notice = notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now)[0];

            Assert.IsFalse(notices.Acknowledges("genshin", "7.2", "new_version"), "a line that is still up is not acknowledged");
            notices.Dismiss(notice); // closed, or its page opened from the line or the tray balloon

            // The release-notes feed answers later (a slow connection): the same article must not pop up now.
            Assert.IsTrue(notices.Acknowledges("genshin", "7.2", "new_version"));
            Assert.IsTrue(new NewStoryNotices(store).Acknowledges("Genshin", "7.2", "NEW_VERSION"), "also after a restart");
            Assert.IsFalse(notices.Acknowledges("genshin", "7.2", "revision"), "fixes are different news");
            Assert.IsFalse(notices.Acknowledges("genshin", "7.3", "new_version"), "the next version is not acknowledged yet");
            Assert.IsFalse(notices.Acknowledges("starrail", "7.2", "new_version"));
            Assert.IsFalse(notices.Acknowledges("genshin", "soon", "new_version"));
        }

        [TestMethod]
        public void SwitchingOff_AcknowledgesNothing()
        {
            var store = new MemoryStore();
            var notices = new NewStoryNotices(store);
            notices.Offer(new[] { Change("7.1.0", "7.2.0") }, Now);

            store.Enabled = false;
            notices.OnEnabledChanged();

            Assert.IsFalse(notices.Acknowledges("genshin", "7.2", "new_version"), "the What's new card may come back");
        }

        [TestMethod]
        public void WhatsNewCard_NextToTheNotice()
        {
            // seen wins: a card the player closed is never touched again.
            Assert.AreEqual(SettingsWindow.WhatsNewFate.Leave, SettingsWindow.WhatsNewUnderNewStory(acknowledged: true, covered: true, seen: true));
            // The notice was closed or opened: the card for the same article is closed for good.
            Assert.AreEqual(SettingsWindow.WhatsNewFate.MarkSeen, SettingsWindow.WhatsNewUnderNewStory(acknowledged: true, covered: false, seen: false));
            // The line is up: the card steps aside.
            Assert.AreEqual(SettingsWindow.WhatsNewFate.Hide, SettingsWindow.WhatsNewUnderNewStory(acknowledged: false, covered: true, seen: false));
            // The line went without being closed (switched off), or was never about this article: the card shows.
            Assert.AreEqual(SettingsWindow.WhatsNewFate.Show, SettingsWindow.WhatsNewUnderNewStory(acknowledged: false, covered: false, seen: false));
        }

        [TestMethod]
        public void Pending_RoundTrips_AndIgnoresBrokenEntries()
        {
            var notice = new NewStoryNotice("starrail", new GameVersion(4, 6), Now);
            Assert.AreEqual("starrail/4.6@" + Now.ToUnixTimeSeconds(), NewStoryRules.Encode(notice));
            Assert.IsTrue(NewStoryRules.TryDecode(NewStoryRules.Encode(notice), out var back));
            Assert.AreEqual(notice, back);

            foreach (string broken in new[] { null, "", "genshin", "genshin/7.2", "/7.2@5", "genshin/x@5", "genshin/7.2@soon", "genshin/7.2@99999999999999999" })
                Assert.IsFalse(NewStoryRules.TryDecode(broken, out _), broken ?? "(null)");

            // A hand-edited Config with one bad entry still shows the good one.
            var store = new MemoryStore();
            store.Values[NewStoryNotices.PendingKey] = new[] { "nonsense", NewStoryRules.Encode(notice) };
            Assert.AreEqual("starrail/4.6", new NewStoryNotices(store).Current(Now).Id);
        }

        // ── Words and link ──────────────────────────────────────────────────

        [TestMethod]
        public void Line_NamesTheGameAndVersion()
        {
            var v = new GameVersion(7, 2);
            Assert.AreEqual("Genshin Impact 7.2: nowa fabuła jest już po polsku",
                NewStoryText.Line("{0} {1}: nowa fabuła jest już po polsku", "Genshin Impact", v));
            Assert.AreEqual("Genshin Impact 7.2", NewStoryText.Line("{0} {1} {2}", "Genshin Impact", v), "a broken format falls back");
        }

        [TestMethod]
        public void Link_IsTheVersionsPage_OnTheSiteOnly()
        {
            var notice = new NewStoryNotice("genshin", new GameVersion(7, 2), Now);
            Assert.AreEqual("genshin-7-2", notice.PageSlug, "backend slugOf for a version's first pack");
            Assert.AreEqual("https://kaption.one/pl/aktualizacje/genshin-7-2/",
                NewStoryText.PageUrl("https://kaption.one/pl/aktualizacje/{0}/", notice));

            Assert.IsNull(NewStoryText.PageUrl("https://example.com/{0}/", notice));
            Assert.IsNull(NewStoryText.PageUrl("http://kaption.one/{0}/", notice));
            Assert.IsNull(NewStoryText.PageUrl("https://kaption.one.example.com/{0}/", notice));
            Assert.IsNull(NewStoryText.PageUrl("https://kaption.one/{0}/{1}/", notice));
            Assert.IsNull(NewStoryText.PageUrl(null, notice));
            Assert.IsNull(NewStoryText.PageUrl("https://kaption.one/{0}/", null));
        }

        [TestMethod]
        public void WhatsNewCard_StepsAside_OnlyForTheSameVersionsArticle()
        {
            var notice = new NewStoryNotice("genshin", new GameVersion(7, 2), Now);
            Assert.IsTrue(NewStoryText.CoversReleaseNote(notice, "genshin", "7.2", "new_version"));
            Assert.IsFalse(NewStoryText.CoversReleaseNote(notice, "genshin", "7.2", "revision"), "fixes are different news");
            Assert.IsFalse(NewStoryText.CoversReleaseNote(notice, "genshin", "7.1", "new_version"));
            Assert.IsFalse(NewStoryText.CoversReleaseNote(notice, "starrail", "7.2", "new_version"));
            Assert.IsFalse(NewStoryText.CoversReleaseNote(null, "genshin", "7.2", "new_version"));
        }

        // ── What the pack sync reports ──────────────────────────────────────

        private string _dir;
        private string _pack;

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "kaption-newstory-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_dir);
            _pack = Path.Combine(_dir, "TextMapPL.gisub");
        }

        [TestCleanup]
        public void Teardown()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private const string PackSha = "4f6c8e2a0b1d3c5e7f9a2b4c6d8e0f1a3b5c7d9e1f2a4b6c8d0e2f4a6b8c0d2e";

        private static FileMetadata Meta(string version, string distSha, string packSha = null)
        {
            var row = new Dictionary<string, object>
            {
                ["id"] = "fv_" + version, ["game"] = "genshin", ["language"] = "pl", ["version"] = version,
                ["file_size"] = 1024L, ["file_sha256"] = distSha,
            };
            if (packSha != null) row["pack_sha256"] = packSha;
            return JsonSerializer.Deserialize<FileMetadata>(JsonSerializer.Serialize(row));
        }

        private Dictionary<string, ManifestEntry> Installed(string version)
        {
            File.WriteAllText(_pack, "machine-bound pack bytes");
            return new(StringComparer.OrdinalIgnoreCase)
            {
                ["genshin/pl"] = new ManifestEntry
                {
                    Game = "genshin", Language = "pl", Version = version, FileVersionId = "fv_" + version, FileSha256 = "dist-old",
                    LocalPath = _pack, DownloadedAtUnix = 1793500000, FileSizeBytes = 1024,
                },
            };
        }

        private DictionarySyncService.PackOps Ops() => new()
        {
            Download = (meta, path, progress, ct) => { File.WriteAllText(path, "pack plaintext"); return Task.CompletedTask; },
            Encrypt = (plain, final) => File.Copy(plain, final, overwrite: true),
            HashInstalledPack = _ => PackSha,
            InvalidateCaches = (game, lang) => 0,
            FinalPath = (game, lang) => _pack,
        };

        private async Task<DictionarySyncResult> Sync(FileMetadata meta, Dictionary<string, ManifestEntry> manifest)
        {
            var result = new DictionarySyncResult();
            await DictionarySyncService.SyncOneCoreAsync(meta, manifest, result, Ops(), CancellationToken.None);
            return result;
        }

        [TestMethod]
        public async Task Sync_DownloadedNewVersion_ReportsTheStep()
        {
            var r = await Sync(Meta("7.2.0", "dist-new"), Installed("7.1.0-r4"));
            Assert.AreEqual(1, r.Downloaded);
            Assert.AreEqual(new PackVersionChange("genshin", "pl", "7.1.0-r4", "7.2.0"), r.VersionChanges.Single());
        }

        [TestMethod]
        public async Task Sync_RelabelledVersion_ReportsTheStep_WithoutADownload()
        {
            // The same pack under a new game version label: no bytes moved, and the version still changed.
            var r = await Sync(Meta("7.2.0", "dist-new", packSha: PackSha), Installed("7.1.0-r4"));
            Assert.AreEqual(1, r.Relabelled);
            Assert.AreEqual(0, r.Downloaded);
            Assert.AreEqual(new PackVersionChange("genshin", "pl", "7.1.0-r4", "7.2.0"), r.VersionChanges.Single());
        }

        [TestMethod]
        public async Task Sync_FirstInstall_ReportsNoStep()
        {
            var r = await Sync(Meta("7.2.0", "dist-new"), new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase));
            Assert.AreEqual(1, r.Downloaded);
            Assert.AreEqual(0, r.VersionChanges.Count);
        }

        [TestMethod]
        public async Task Sync_UpToDate_ReportsNoStep()
        {
            var manifest = Installed("7.2.0");
            manifest["genshin/pl"].FileSha256 = "dist-same";
            var r = await Sync(Meta("7.2.0", "dist-same"), manifest);
            Assert.AreEqual(1, r.UpToDate);
            Assert.AreEqual(0, r.VersionChanges.Count);
        }

        [TestMethod]
        public async Task Sync_ThroughTheRules_OnlyANewGameVersionIsNews()
        {
            var notices = new NewStoryNotices(new MemoryStore());

            var hotfix = await Sync(Meta("7.2.0-r2", "dist-r2"), Installed("7.2.0"));
            Assert.AreEqual(1, hotfix.VersionChanges.Count, "the step is reported");
            Assert.AreEqual(0, notices.Offer(hotfix.VersionChanges, Now).Count, "and is not news");

            var version = await Sync(Meta("7.3.0", "dist-73"), Installed("7.2.0-r2"));
            Assert.AreEqual("genshin/7.3", notices.Offer(version.VersionChanges, Now).Single().Id);
        }
    }
}
