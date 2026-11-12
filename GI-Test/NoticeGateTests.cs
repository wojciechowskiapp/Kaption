using System;
using GI_Subtitles.Services.Guess;
using GI_Subtitles.Services.Notices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The one gate every come-back notice passes (Services/Notices/NoticeGate): what holds a notice back, the day's
    /// cap for the whole app, the order when two wait, and a waiting notice that stops asking.
    /// </summary>
    [TestClass]
    public class NoticeGateTests
    {
        private sealed class Ledger : INoticeLedger
        {
            public int MaxPerDay { get; set; } = NoticeGate.DefaultMaxPerDay;
            public TimeSpan Gap { get; set; } = TimeSpan.FromSeconds(NoticeGate.DefaultGapSeconds);
            public string Day { get; private set; }
            public int Count { get; private set; }
            public void Save(string day, int count) { Day = day; Count = count; }
        }

        // Noon local time, so a few hours either way stay on one local day in any time zone.
        private static readonly DateTimeOffset Noon = new DateTimeOffset(new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Local));
        private const int Accepts = GuessNoticeGate.QunsAcceptsNotifications;
        private static readonly NoticeScreen Clear = new(false, false, Accepts);
        private static readonly NoticeScreen GameInFront = new(false, true, Accepts);

        private static Func<NoticeScreen> Never => () => throw new AssertFailedException("the screen must not be asked");

        [TestMethod]
        public void Check_GivesTheFirstReasonThatHolds()
        {
            NoticeGateResult R(bool on = true, bool kindToday = false, int shown = 0, int max = 2, bool translating = false, bool game = false,
                int quns = Accepts, bool higher = false, bool soon = false) =>
                NoticeGate.Check(on, kindToday, shown, max, new NoticeScreen(translating, game, quns), higher, soon);

            Assert.AreEqual(NoticeGateResult.Show, R());
            Assert.AreEqual(NoticeGateResult.Disabled, R(on: false, kindToday: true, shown: 9, translating: true));
            Assert.AreEqual(NoticeGateResult.KindShownToday, R(kindToday: true, shown: 9, game: true));
            Assert.AreEqual(NoticeGateResult.CapReached, R(shown: 2, translating: true, game: true));
            Assert.AreEqual(NoticeGateResult.CapReached, R(max: 0), "a cap of zero sends nothing");
            Assert.AreEqual(NoticeGateResult.Translating, R(translating: true, game: true, quns: 3));
            Assert.AreEqual(NoticeGateResult.GameInFront, R(game: true, quns: 3, higher: true));
            Assert.AreEqual(NoticeGateResult.WindowsBusy, R(quns: 3, higher: true), "a full-screen app");
            Assert.AreEqual(NoticeGateResult.WindowsBusy, R(quns: GuessNoticeGate.QunsUnknown), "Windows would not answer");
            Assert.AreEqual(NoticeGateResult.Outranked, R(higher: true, soon: true));
            Assert.AreEqual(NoticeGateResult.TooSoon, R(soon: true));
        }

        [TestMethod]
        public void TwoKindsWaitingOnOneDay_TheStoryGoesFirst_ThenTheOtherAfterTheGap()
        {
            var ledger = new Ledger();
            var gate = new NoticeGate(ledger);

            // Both fall due while the game is in front: both wait, and keep their place.
            Assert.AreEqual(NoticeGateResult.GameInFront, gate.Ask(NoticeKind.StaminaNearlyFull, true, false, Noon, () => GameInFront));
            Assert.AreEqual(NoticeGateResult.GameInFront, gate.Ask(NoticeKind.StoryReady, true, false, Noon.AddSeconds(5), () => GameInFront));
            Assert.IsTrue(gate.IsWaiting(NoticeKind.StoryReady, Noon.AddSeconds(6)));

            // The game is closed. Stamina happens to ask first: the story notice goes before it.
            var t = Noon.AddMinutes(1);
            Assert.AreEqual(NoticeGateResult.Outranked, gate.Ask(NoticeKind.StaminaNearlyFull, true, false, t, () => Clear));
            Assert.AreEqual(NoticeGateResult.Show, gate.Ask(NoticeKind.StoryReady, true, false, t, () => Clear));
            gate.Shown(NoticeKind.StoryReady, t);
            Assert.AreEqual(1, gate.ShownToday(t));

            // One at a time: the second waits out the gap, then shows.
            Assert.AreEqual(NoticeGateResult.TooSoon, gate.Ask(NoticeKind.StaminaNearlyFull, true, false, t.AddSeconds(10), () => Clear));
            var later = t + ledger.Gap;
            Assert.AreEqual(NoticeGateResult.Show, gate.Ask(NoticeKind.StaminaNearlyFull, true, false, later, () => Clear));
            gate.Shown(NoticeKind.StaminaNearlyFull, later);
            Assert.AreEqual(2, gate.ShownToday(later));
            Assert.IsFalse(gate.IsWaiting(NoticeKind.StaminaNearlyFull, later));
        }

        [TestMethod]
        public void TheCapReached_NothingMoreToday_AndTheScreenIsNotEvenAsked()
        {
            var ledger = new Ledger { Gap = TimeSpan.Zero };
            var gate = new NoticeGate(ledger);
            foreach (var kind in new[] { NoticeKind.StoryReady, NoticeKind.StaminaNearlyFull })
            {
                Assert.AreEqual(NoticeGateResult.Show, gate.Ask(kind, true, false, Noon, () => Clear));
                gate.Shown(kind, Noon);
            }

            Assert.AreEqual(NoticeGateResult.CapReached, gate.Ask(NoticeKind.GuessRank, true, false, Noon.AddHours(1), Never));
            Assert.IsFalse(gate.IsWaiting(NoticeKind.GuessRank, Noon.AddHours(1)), "a capped notice stays on Start; it holds nobody back");
            Assert.AreEqual(NoticeGateResult.CapReached, gate.Ask(NoticeKind.StoryReady, true, false, Noon.AddHours(2), Never), "the first in line is capped too");

            // The kind's own answers come before the cap, and need no screen either.
            Assert.AreEqual(NoticeGateResult.Disabled, gate.Ask(NoticeKind.GuessRank, false, false, Noon.AddHours(1), Never));
            Assert.AreEqual(NoticeGateResult.KindShownToday, gate.Ask(NoticeKind.GuessRank, true, true, Noon.AddHours(1), Never));

            // A new local day starts from nothing.
            var tomorrow = Noon.AddDays(1);
            Assert.AreEqual(0, gate.ShownToday(tomorrow));
            Assert.AreEqual(NoticeGateResult.Show, gate.Ask(NoticeKind.GuessRank, true, false, tomorrow, () => Clear));

            // The setting is the cap: three a day lets a third through, zero lets none.
            ledger.MaxPerDay = 3;
            Assert.AreEqual(NoticeGateResult.Show, gate.Ask(NoticeKind.GuessRank, true, false, Noon.AddHours(3), () => Clear));
            ledger.MaxPerDay = 0;
            Assert.AreEqual(NoticeGateResult.CapReached, gate.Ask(NoticeKind.GuessRank, true, false, tomorrow, Never));
        }

        [TestMethod]
        public void ANoticeThatExpiresUnseen_StopsHoldingTheOthersBack()
        {
            var gate = new NoticeGate(new Ledger());
            Assert.AreEqual(NoticeGateResult.GameInFront, gate.Ask(NoticeKind.StoryReady, true, false, Noon, () => GameInFront));

            // While it keeps its place, a lower kind waits behind it.
            Assert.AreEqual(NoticeGateResult.Outranked, gate.Ask(NoticeKind.GuessRank, true, false, Noon.AddMinutes(1), () => Clear));

            // Its owner never asks again (the line was closed, the notice expired): the place lapses.
            var lapsed = Noon + NoticeGate.WaitingFor;
            Assert.IsFalse(gate.IsWaiting(NoticeKind.StoryReady, lapsed));
            Assert.AreEqual(NoticeGateResult.Show, gate.Ask(NoticeKind.GuessRank, true, false, lapsed, () => Clear));

            // An owner that says so frees the place at once.
            var other = new NoticeGate(new Ledger());
            Assert.AreEqual(NoticeGateResult.GameInFront, other.Ask(NoticeKind.StoryReady, true, false, Noon, () => GameInFront));
            other.Withdraw(NoticeKind.StoryReady);
            Assert.AreEqual(NoticeGateResult.Show, other.Ask(NoticeKind.StaminaNearlyFull, true, false, Noon.AddSeconds(1), () => Clear));

            // A higher kind is never held back by a lower one that waits.
            var third = new NoticeGate(new Ledger());
            Assert.AreEqual(NoticeGateResult.GameInFront, third.Ask(NoticeKind.StaminaNearlyFull, true, false, Noon, () => GameInFront));
            Assert.AreEqual(NoticeGateResult.Show, third.Ask(NoticeKind.StoryReady, true, false, Noon.AddSeconds(1), () => Clear));
        }

        [TestMethod]
        public void TheCountIsKeptWithItsDay()
        {
            var ledger = new Ledger();
            var gate = new NoticeGate(ledger);
            gate.Shown(NoticeKind.GuessRank, Noon);
            Assert.AreEqual(NoticeGate.DayOf(Noon), ledger.Day);
            Assert.AreEqual(1, ledger.Count);
            // Another gate on the same ledger (the app restarted) carries the day's count on.
            var restarted = new NoticeGate(ledger);
            Assert.AreEqual(1, restarted.ShownToday(Noon.AddHours(1)));
            restarted.Shown(NoticeKind.StoryReady, Noon.AddHours(1));
            Assert.AreEqual(2, ledger.Count);
            Assert.AreEqual(NoticeGateResult.CapReached, restarted.Ask(NoticeKind.StaminaNearlyFull, true, false, Noon.AddHours(2), Never));
        }
    }
}
