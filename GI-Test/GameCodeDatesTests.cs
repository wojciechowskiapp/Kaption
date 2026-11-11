using System;
using System.Globalization;
using System.Linq;
using GI_Subtitles.Services.Codes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The dates on the Start page's codes card (Services/Codes/CodeDates.cs): which codes are new or ending soon, their
    /// order, the wording in English and Polish in a given time zone, the honest line of a code without an end date,
    /// and when the card has to redraw itself. The rules are the site's (landing/src/lib/codes-view.ts).
    /// </summary>
    [TestClass]
    public class CodeDatesTests
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);
        private static readonly CultureInfo En = new("en-GB");
        private static readonly CultureInfo Pl = new("pl-PL");
        private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
        private static readonly CodeDateWords PlWords = new("Dodany {0}", "Na liście od {0}", "ważny do {0}", "bez podanej daty końca",
            "Kończy się za {0}", "{0} dzień|{0} dni|{0} dni");
        private static readonly TimeSpan Stale = CodesSchedule.Default.StaleAfter;

        private static RedeemCode Code(string name, TimeSpan seenAgo, TimeSpan? endsIn = null) =>
            new(name, new[] { new CodeReward("Primogem", 60) }, Now - seenAgo, endsIn.HasValue ? Now + endsIn.Value : null);

        /// <summary>Whatever space the culture's time pattern uses (a narrow no-break one before "PM" on some systems).</summary>
        private static string Plain(string s) => string.Join(" ", s.Split(new[] { ' ', '\u00a0', '\u202f' }, StringSplitOptions.RemoveEmptyEntries));

        [TestMethod]
        public void NewIsTwoDaysAndNeverACodeTheFirstCheckFound()
        {
            DateTimeOffset since = Now.AddDays(-10);
            Assert.IsTrue(CodeDates.IsNew(Code("A", TimeSpan.FromHours(1)), Now, since));
            Assert.IsTrue(CodeDates.IsNew(Code("B", CodeDates.FreshFor - TimeSpan.FromSeconds(1)), Now, since));
            Assert.IsFalse(CodeDates.IsNew(Code("C", CodeDates.FreshFor), Now, since), "two days old is no longer new");
            Assert.IsFalse(CodeDates.IsNew(Code("D", TimeSpan.FromHours(-1)), Now, since), "a time ahead of the clock is not new");
            Assert.IsFalse(CodeDates.IsNew(new RedeemCode("E", Array.Empty<CodeReward>(), null, null), Now, since), "no first-seen time");
            // The list began an hour ago: what it found then was out before it.
            DateTimeOffset young = Now.AddHours(-1);
            Assert.IsFalse(CodeDates.IsNew(Code("F", TimeSpan.FromHours(1)), Now, young));
            Assert.IsTrue(CodeDates.IsNew(Code("G", TimeSpan.Zero), Now, young),
                "a later check found it");
            Assert.IsTrue(CodeDates.IsNew(Code("H", TimeSpan.FromHours(1)), Now, null), "no list start known: the clock alone decides");
        }

        [TestMethod]
        public void EndsSoonIsAnEndInsideTwoDays()
        {
            Assert.IsFalse(CodeDates.EndsSoon(Code("A", TimeSpan.FromDays(3)), Now), "no end date");
            Assert.IsTrue(CodeDates.EndsSoon(Code("B", TimeSpan.FromDays(3), CodeDates.EndsSoonWithin), Now));
            Assert.IsFalse(CodeDates.EndsSoon(Code("C", TimeSpan.FromDays(3), CodeDates.EndsSoonWithin + TimeSpan.FromSeconds(1)), Now));
            Assert.IsFalse(CodeDates.EndsSoon(Code("D", TimeSpan.FromDays(3), TimeSpan.FromSeconds(-1)), Now), "already over");
        }

        [TestMethod]
        public void OrderIsEndingSoonFirstThenTheNewest()
        {
            var list = new[]
            {
                Code("OLDEST", TimeSpan.FromDays(20)),
                Code("NEWB", TimeSpan.FromHours(2)),
                Code("NEWA", TimeSpan.FromHours(2)),
                Code("LATER", TimeSpan.FromDays(9), TimeSpan.FromHours(30)),
                Code("SOONEST", TimeSpan.FromDays(9), TimeSpan.FromHours(3)),
                Code("FAR", TimeSpan.FromDays(1), TimeSpan.FromDays(6)),
            };
            string[] want = { "SOONEST", "LATER", "NEWA", "NEWB", "FAR", "OLDEST" };
            CollectionAssert.AreEqual(want, CodeDates.Order(list, Now).Select(c => c.Code).ToArray());
            CollectionAssert.AreEqual(want, CodeDates.Order(list.Reverse(), Now).Select(c => c.Code).ToArray(), "whatever order it came in");
            Assert.AreEqual(0, CodeDates.Order(null, Now).Count);
        }

        [TestMethod]
        public void DatesReadNaturallyInBothLanguagesAndInTheGivenZone()
        {
            var added = new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
            var end = new DateTimeOffset(2026, 11, 12, 16, 0, 0, TimeSpan.Zero);
            Assert.AreEqual("7 listopada", CodeDates.Day(added, Now, Pl, Warsaw));
            Assert.AreEqual("7 November", CodeDates.Day(added, Now, En, Utc));
            Assert.AreEqual("31 grudnia 2025", CodeDates.Day(new DateTimeOffset(2025, 12, 31, 10, 0, 0, TimeSpan.Zero), Now, Pl, Warsaw), "another year is named");
            Assert.AreEqual("12 listopada, 17:00", Plain(CodeDates.DayTime(end, Now, Pl, Warsaw)));
            Assert.AreEqual("12 November, 16:00", Plain(CodeDates.DayTime(end, Now, En, Utc)));
            // The same moment is another day across the date line.
            var late = new DateTimeOffset(2026, 11, 12, 15, 59, 0, TimeSpan.Zero);
            Assert.AreEqual("13 listopada, 00:59", Plain(CodeDates.DayTime(late, Now, Pl, TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"))));
        }

        [TestMethod]
        public void ALineWithAnEndDateAndOneThatSaysNoneWasAnnounced()
        {
            DateTimeOffset since = Now.AddDays(-30);
            var dated = new RedeemCode("A", Array.Empty<CodeReward>(), new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 16, 15, 59, 0, TimeSpan.Zero));
            Assert.AreEqual("Dodany 7 listopada · ważny do 16 listopada, 16:59", Plain(CodeDates.Line(dated, Now, since, PlWords, Pl, Warsaw)));
            Assert.AreEqual("Added 7 November · valid until 16 November, 15:59", Plain(CodeDates.Line(dated, Now, since, null, En, Utc)));

            var open = new RedeemCode("B", Array.Empty<CodeReward>(), new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero), null);
            Assert.AreEqual("Dodany 6 listopada · bez podanej daty końca", CodeDates.Line(open, Now, since, PlWords, Pl, Warsaw));
            Assert.AreEqual("Added 6 November · no end date announced", CodeDates.Line(open, Now, since, CodeDateWords.English, En, Utc));

            // The live list's first check (4 November): those codes were out before the list.
            var first = new DateTimeOffset(2026, 11, 4, 3, 24, 41, TimeSpan.Zero);
            var found = new RedeemCode("C", Array.Empty<CodeReward>(), first, null);
            Assert.AreEqual("Na liście od 4 listopada · bez podanej daty końca", CodeDates.Line(found, Now, first, PlWords, Pl, Warsaw));
            Assert.AreEqual("On the list since 4 November · no end date announced", CodeDates.Line(found, Now, first, null, En, Utc));
            Assert.AreEqual("no end date announced", CodeDates.Line(new RedeemCode("D", Array.Empty<CodeReward>(), null, null), Now, since, null, En, Utc),
                "no first-seen time: nothing is said about it");
            Assert.AreEqual(string.Empty, CodeDates.Line(null, Now, since, null, En, Utc));
        }

        [TestMethod]
        public void EndsInCountsDownAndIsAbsentForACodeThatDoesNotEndSoon()
        {
            Assert.AreEqual("Ends in 7 h", CodeDates.EndsIn(Code("A", TimeSpan.FromDays(1), new TimeSpan(7, 14, 0)), Now, null, En), "whole hours, never more time than there is");
            Assert.AreEqual("Kończy się za 7 h", CodeDates.EndsIn(Code("A", TimeSpan.FromDays(1), new TimeSpan(7, 14, 0)), Now, PlWords, Pl));
            Assert.AreEqual("Ends in 1 h", CodeDates.EndsIn(Code("A", TimeSpan.FromDays(1), TimeSpan.FromHours(1)), Now, null, En));
            Assert.AreEqual("Ends in 59 min", CodeDates.EndsIn(Code("A", TimeSpan.FromDays(1), new TimeSpan(0, 58, 30)), Now, null, En), "minutes in the last hour, rounded up");
            Assert.AreEqual("Kończy się za 1 dzień 3 h", CodeDates.EndsIn(Code("B", TimeSpan.FromDays(1), new TimeSpan(1, 3, 0, 0)), Now, PlWords, Pl));
            Assert.AreEqual("Ends in 2 days", CodeDates.EndsIn(Code("C", TimeSpan.FromDays(1), TimeSpan.FromDays(2)), Now, null, En));
            Assert.AreEqual("Ends in 1 min", CodeDates.EndsIn(Code("D", TimeSpan.FromDays(1), TimeSpan.FromSeconds(20)), Now, null, En), "never 0 before the end");
            Assert.IsNull(CodeDates.EndsIn(Code("E", TimeSpan.FromDays(1), TimeSpan.FromDays(6)), Now, null, En));
            Assert.IsNull(CodeDates.EndsIn(Code("F", TimeSpan.FromDays(1)), Now, null, En));
        }

        [TestMethod]
        public void TheListStartIsTheServersOrTheOldestCodeAtHand()
        {
            var codes = new[] { Code("A", TimeSpan.FromDays(1)), Code("B", TimeSpan.FromDays(3)) };
            Assert.AreEqual(Now.AddDays(-3), CodeDates.ListStart(new RedeemCodeList("genshin", Now, codes)));
            Assert.AreEqual(Now.AddDays(-9), CodeDates.ListStart(new RedeemCodeList("genshin", Now, codes, 0, Now.AddDays(-9))));
            Assert.IsNull(CodeDates.ListStart(new RedeemCodeList("genshin", Now, Array.Empty<RedeemCode>())));
            Assert.IsNull(CodeDates.ListStart(null));

            var parsed = RedeemCodeList.Parse("""{"game":"zzz","checkedAt":"2026-11-09T11:00:00Z","listedSince":"2026-11-04T03:24:56Z","active":[],"expired":[]}""", "zzz");
            Assert.AreEqual(new DateTimeOffset(2026, 11, 4, 3, 24, 56, TimeSpan.Zero), parsed.ListedSince);
            Assert.AreEqual(new DateTimeOffset(2026, 11, 3, 7, 0, 42, TimeSpan.Zero), RedeemCodeList.Parse(CodesSamples.Genshin, "genshin").ListedSince);
            // A Worker from before listedSince: the list is read as before, and the oldest code at hand stands in.
            var older = RedeemCodeList.Parse("""{"game":"zzz","checkedAt":"2026-11-09T11:00:00Z","active":[{"code":"OLDSHAPE1","rewards":[],"source":"secondary","sourceUrl":null,"firstSeen":"2026-11-04T03:24:56Z","expiresAt":null}],"expired":[],"program":null}""", "zzz");
            Assert.IsNull(older.ListedSince, "an older server sends none");
            Assert.AreEqual(1, older.Active.Count);
            Assert.AreEqual(older.Active[0].FirstSeen, CodeDates.ListStart(older));
            Assert.IsNull(RedeemCodeList.Parse("""{"game":"zzz","checkedAt":null,"listedSince":42,"active":[]}""", "zzz").ListedSince, "not a time: ignored, the list is still read");
        }

        [TestMethod]
        public void TheCardShowsEndingSoonFirstWithItsMarksAndDates()
        {
            var game = CodesSamples.Game("genshin");
            var list = new RedeemCodeList("genshin", Now.AddMinutes(-10), new[]
            {
                Code("NEWEST", TimeSpan.FromHours(3)),
                Code("OLDGIFT", TimeSpan.FromDays(20)),
                Code("STREAM", TimeSpan.FromDays(2.5), TimeSpan.FromHours(5)),
                Code("MIDDLE", TimeSpan.FromDays(4), TimeSpan.FromDays(9)),
            }, 0, Now.AddDays(-20));
            var view = CodesCardModel.Build(game, list, Now.AddMinutes(-1), Now, Stale, "pl-PL", Pl, words: PlWords, zone: Warsaw);
            CollectionAssert.AreEqual(new[] { "STREAM", "NEWEST", "MIDDLE" }, view.Rows.Select(r => r.Code).ToArray());
            Assert.AreEqual(4, view.ActiveCount);
            Assert.AreEqual("Kończy się za 5 h", view.Rows[0].EndsIn);
            Assert.IsFalse(view.Rows[0].IsNew);
            Assert.IsTrue(view.Rows[1].IsNew);
            Assert.IsNull(view.Rows[1].EndsIn);
            Assert.AreEqual("Dodany 10 listopada · bez podanej daty końca", view.Rows[1].Dates);
            Assert.AreEqual("Dodany 6 listopada · ważny do 19 listopada, 09:00", Plain(view.Rows[2].Dates));
            // Without words or a zone the card still says its dates, in English and the machine's own zone.
            var plain = CodesCardModel.Build(game, list, Now.AddMinutes(-1), Now, Stale, "en-US", En);
            StringAssert.StartsWith(plain.Rows[0].EndsIn, "Ends in ");
            StringAssert.Contains(plain.Rows[1].Dates, "no end date announced");
        }

        [TestMethod]
        public void TheCardRedrawsWhenACountdownDropsAMinuteACodeStopsBeingNewOrStartsEndingSoon()
        {
            var game = CodesSamples.Game("genshin");
            DateTimeOffset readAt = Now.AddMinutes(-1);
            RedeemCodeList One(RedeemCode c) => new("genshin", readAt, new[] { c }, 0, Now.AddDays(-30));
            // Ending soon: when its time left reads differently. Hours until the last hour, then each minute.
            Assert.AreEqual(Now.AddMinutes(14).AddTicks(1), CodesCardModel.NextChangeAt(game, One(Code("A", TimeSpan.FromDays(3), new TimeSpan(5, 14, 0))), readAt, Now, Stale),
                "5 h 14 min left reads \"5 h\" until just under 5 h are left");
            // With exactly 5 h left the text is \"5 h\" and turns to \"4 h\" at once, not an hour later.
            Assert.AreEqual(Now.AddTicks(1), CodeDates.NextChange(Code("A", TimeSpan.FromDays(3), TimeSpan.FromHours(5)), Now, Now.AddDays(-30)));
            Assert.AreEqual(Now.AddSeconds(30), CodesCardModel.NextChangeAt(game, One(Code("B", TimeSpan.FromDays(3), new TimeSpan(0, 20, 30))), readAt, Now, Stale));
            Assert.AreEqual(Now.AddMinutes(1), CodesCardModel.NextChangeAt(game, One(Code("B", TimeSpan.FromDays(3), TimeSpan.FromMinutes(20))), readAt, Now, Stale));
            // At exactly one hour the text is "1 h"; just under it the minutes take over.
            Assert.AreEqual(Now.AddTicks(1), CodeDates.NextChange(Code("B", TimeSpan.FromDays(3), TimeSpan.FromHours(1)), Now, Now.AddDays(-30)));
            // Whatever the time left, the scheduled moment is the first at which the text differs, and the one before it is not.
            foreach (var left in new[] { new TimeSpan(47, 59, 59), new TimeSpan(5, 0, 0), new TimeSpan(4, 59, 59), new TimeSpan(1, 0, 0), new TimeSpan(0, 59, 30), new TimeSpan(0, 20, 0), new TimeSpan(0, 0, 40) })
            {
                RedeemCode c = Code("T", TimeSpan.FromDays(3), left);
                DateTimeOffset next = CodeDates.NextChange(c, Now, Now.AddDays(-30)).Value;
                string before = CodeDates.EndsIn(c, Now, null, En);
                Assert.AreEqual(before, CodeDates.EndsIn(c, next.AddTicks(-1), null, En), $"{left}: unchanged until the scheduled moment");
                Assert.AreNotEqual(before, CodeDates.EndsIn(c, next, null, En) ?? "over", $"{left}: changed at the scheduled moment");
            }
            // A clock set back: a code first seen ten minutes ahead of it is not new yet, and the card redraws when it is.
            RedeemCode ahead = Code("F", TimeSpan.FromMinutes(-10));
            Assert.IsFalse(CodeDates.IsNew(ahead, Now, Now.AddDays(-30)));
            Assert.AreEqual(Now.AddMinutes(10), CodeDates.NextChange(ahead, Now, Now.AddDays(-30)));
            Assert.IsTrue(CodeDates.IsNew(ahead, Now.AddMinutes(10), Now.AddDays(-30)));
            Assert.IsNull(CodeDates.NextChange(ahead, Now, ahead.FirstSeen), "one the first check found never turns new");
            // New for ten more minutes; then it is an ordinary code.
            Assert.AreEqual(Now.AddMinutes(10), CodesCardModel.NextChangeAt(game, One(Code("C", CodeDates.FreshFor - TimeSpan.FromMinutes(10))), readAt, Now, Stale));
            // Starts ending soon in twenty minutes.
            Assert.AreEqual(Now.AddMinutes(20),
                CodesCardModel.NextChangeAt(game, One(Code("D", TimeSpan.FromDays(5), CodeDates.EndsSoonWithin + TimeSpan.FromMinutes(20))), readAt, Now, Stale));
            // Codes past the three shown do not wake the card for their own countdown: only for their expiry, or when
            // they start ending soon and move up into view.
            var crowded = new RedeemCodeList("genshin", readAt, new[]
            {
                Code("S1", TimeSpan.FromDays(3), new TimeSpan(0, 20, 50)), Code("S2", TimeSpan.FromDays(3), new TimeSpan(0, 30, 50)),
                Code("S3", TimeSpan.FromDays(3), new TimeSpan(0, 40, 50)), Code("HIDDEN", TimeSpan.FromDays(3), new TimeSpan(0, 50, 10)),
            }, 0, Now.AddDays(-30));
            Assert.AreEqual(Now.AddSeconds(50), CodesCardModel.NextChangeAt(game, crowded, readAt, Now, Stale), "the shown rows' next minute, not the hidden code's 10 s");
            var rising = new RedeemCodeList("genshin", readAt, new[]
            {
                Code("N1", TimeSpan.FromDays(3)), Code("N2", TimeSpan.FromDays(4)), Code("N3", TimeSpan.FromDays(5)),
                Code("RISES", TimeSpan.FromDays(9), CodeDates.EndsSoonWithin + TimeSpan.FromMinutes(7)),
            }, 0, Now.AddDays(-30));
            Assert.AreEqual(Now.AddMinutes(7), CodesCardModel.NextChangeAt(game, rising, readAt, Now, Stale), "a hidden code that starts ending soon moves up");
            // Nothing moves with the clock: only the stale deadline is left.
            Assert.AreEqual(readAt + Stale, CodesCardModel.NextChangeAt(game, One(Code("E", TimeSpan.FromDays(5))), readAt, Now, Stale));
            Assert.IsNull(CodeDates.NextChange(Code("E", TimeSpan.FromDays(5)), Now, Now.AddDays(-30)));
            Assert.IsNull(CodeDates.NextChange(null, Now, null));
        }
    }
}
