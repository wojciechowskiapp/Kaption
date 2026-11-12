using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Stamina;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The stamina timer's rules without a screen: the clock (the site's timer maths, landing/src/lib/stamina.ts, case
    /// for case), what text counts as a value, when a value read off the screen is believed, the one notice per fill,
    /// the stored reading, the link to the site's timer and the Start card's texts in both languages.
    /// </summary>
    [TestClass]
    public class StaminaLogicTests
    {
        private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        private static readonly GameStamina Resin = new() { Name = "Original Resin", Max = 200, RegenSeconds = 480, Hold = 2000 };
        private static readonly GameStamina Power = new() { Name = "Trailblaze Power", Max = 300, RegenSeconds = 360 };
        private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

        private static StaminaReading At(int value, double nextIn = 480) => new(value, T0, nextIn);
        private static string En(string key, string fallback) => fallback;

        // ── the clock ────────────────────────────────────────────────────

        [TestMethod]
        public void Clock_ReachesAValueOnePointPerInterval_TheFirstAfterTheCountdown()
        {
            Assert.AreEqual(T0 + 8 * Min, StaminaClock.ReachAt(Resin, At(40), 41));
            Assert.AreEqual(T0 + 160 * 8 * Min, StaminaClock.ReachAt(Resin, At(40), 200));
            Assert.AreEqual(T0 + 160 * 8 * Min, StaminaClock.FullAt(Resin, At(40)));
            // With the next point 3 minutes away, every later point moves 5 minutes earlier.
            Assert.AreEqual(T0 + 3 * Min, StaminaClock.ReachAt(Resin, At(40, 180), 41));
            Assert.AreEqual(T0 + 3 * Min + 159 * 8 * Min, StaminaClock.FullAt(Resin, At(40, 180)));
        }

        [TestMethod]
        public void Clock_Boundaries_EmptyOneShortFullOverTheCap()
        {
            Assert.AreEqual(T0 + 200 * 8 * Min, StaminaClock.FullAt(Resin, At(0)));
            Assert.AreEqual(T0 + 8 * Min, StaminaClock.FullAt(Resin, At(199)));
            Assert.AreEqual(T0, StaminaClock.FullAt(Resin, At(200)), "already full: now");
            Assert.AreEqual(T0, StaminaClock.FullAt(Resin, At(260)), "over the cap (refills): full now, no regeneration");
            Assert.AreEqual(T0, StaminaClock.ReachAt(Resin, At(120), 120));
            Assert.AreEqual(T0, StaminaClock.ReachAt(Resin, At(120), 60));
            Assert.IsNull(StaminaClock.ReachAt(Resin, At(120), 201), "regeneration never goes past max");
        }

        [TestMethod]
        public void Clock_ValueAtCountsWholePoints_StopsAtMax_NeverPastAnOverflow()
        {
            StaminaReading r = At(100, 60);
            Assert.AreEqual(100, StaminaClock.ValueAt(Resin, r, T0));
            Assert.AreEqual(100, StaminaClock.ValueAt(Resin, r, T0 + TimeSpan.FromSeconds(59)));
            Assert.AreEqual(101, StaminaClock.ValueAt(Resin, r, T0 + Min));
            Assert.AreEqual(101, StaminaClock.ValueAt(Resin, r, T0 + 8 * Min + TimeSpan.FromSeconds(59)));
            Assert.AreEqual(102, StaminaClock.ValueAt(Resin, r, T0 + 9 * Min));
            Assert.AreEqual(200, StaminaClock.ValueAt(Resin, r, T0 + TimeSpan.FromDays(30)));
            Assert.AreEqual(260, StaminaClock.ValueAt(Resin, At(260), T0 + TimeSpan.FromDays(1)), "an overflow stays");

            Assert.AreEqual(60.0, StaminaClock.NextInAt(Resin, r, T0));
            Assert.AreEqual(480.0, StaminaClock.NextInAt(Resin, r, T0 + Min));
            Assert.AreEqual(240.0, StaminaClock.NextInAt(Resin, r, T0 + 5 * Min));
            Assert.IsNull(StaminaClock.NextInAt(Resin, At(200), T0));
            Assert.AreEqual(240.0, StaminaClock.UnknownPhase(Resin));
        }

        // ── what text is a value ─────────────────────────────────────────

        [TestMethod]
        [DataRow("121/200", 121)]
        [DataRow("93 / 200", 93)]
        [DataRow(" 0/200 ", 0)]
        [DataRow("200/200", 200)]
        [DataRow("232/200", 232)]          // refills: over the cap, under the hold
        [DataRow("2000/200", 2000)]
        [DataRow("121/200+", 121)]         // the "+" button caught in the cut
        [DataRow("121/200 |", 121)]
        [DataRow("１２１／２００", 121)]      // full-width forms fold to ASCII
        [DataRow("121/200 | +", 121)]      // the divider and the button
        public void Parser_TakesAValueWithTheGamesOwnCap(string text, int expected)
        {
            Assert.IsTrue(StaminaTextParser.TryParse(text, Resin, out int value), text);
            Assert.AreEqual(expected, value);
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("121")]                   // a bare count: Fragile Resin's pill
        [DataRow("121/")]
        [DataRow("/200")]
        [DataRow("121/20")]                // a clipped cap
        [DataRow("121/160")]               // another counter, or an old version's cap
        [DataRow("121/2000")]              // the cap runs on into another digit
        [DataRow("121/2001")]
        [DataRow("l21/200")]               // a letter is never taken for a digit
        [DataRow("12l/200")]
        [DataRow("121l200")]
        [DataRow("1211200")]               // the slash read as a one
        [DataRow("12121/200")]             // five digits
        [DataRow("2001/200")]              // over what a player can hold
        [DataRow("C121/200")]              // something before the number
        [DataRow("121/200/3")]             // only the button or the divider may follow the cap
        [DataRow("121/200O")]              // 121/2000 with its last zero read as a letter
        [DataRow("121/200 0")]             // the cap going on after a space
        [DataRow("121/200\u0660")]         // a digit in another script
        [DataRow("121/200a")]
        [DataRow("-5/200")]
        public void Parser_RefusesAnythingElse(string text)
        {
            Assert.IsFalse(StaminaTextParser.TryParse(text, Resin, out _), text);
        }

        [TestMethod]
        public void Parser_WithoutAHold_AllowsWhatFourDigitsShow_AndNeedsAPool()
        {
            Assert.IsTrue(StaminaTextParser.TryParse("360/300", Power, out int v) && v == 360);
            Assert.IsTrue(StaminaTextParser.TryParse("9999/300", Power, out _));
            Assert.IsFalse(StaminaTextParser.TryParse("121/200", Power, out _), "another game's cap");
            Assert.IsFalse(StaminaTextParser.TryParse("121/200", null, out _));
        }

        // ── when a value is believed ─────────────────────────────────────

        private static StaminaTracker Tracker(StaminaReading known = null) => new(Resin, 2, 3, TimeSpan.FromSeconds(12), known);

        [TestMethod]
        public void Tracker_FirstValueNeedsTwoAgreeingSamples()
        {
            StaminaTracker t = Tracker();
            Assert.AreEqual(StaminaVerdict.Waiting, t.Offer(121, T0));
            Assert.IsNull(t.Reading);
            Assert.AreEqual(StaminaVerdict.Accepted, t.Offer(121, T0.AddSeconds(3)));
            Assert.AreEqual(new StaminaReading(121, T0.AddSeconds(3), 240), t.Reading, "the moment within the interval is unknown: half of it");
            Assert.AreEqual(StaminaVerdict.AsExpected, t.Offer(121, T0.AddSeconds(6)));
        }

        [TestMethod]
        public void Tracker_ADifferentValueStartsTheCountAgain_AndOldSamplesDoNotConfirm()
        {
            StaminaTracker t = Tracker();
            Assert.AreEqual(StaminaVerdict.Waiting, t.Offer(121, T0));
            Assert.AreEqual(StaminaVerdict.Waiting, t.Offer(127, T0.AddSeconds(3)), "a one-frame misread does not confirm, and is not confirmed");
            Assert.AreEqual(StaminaVerdict.Waiting, t.Offer(121, T0.AddSeconds(6)));
            Assert.IsNull(t.Reading);
            // The same value a minute later is a new sighting, not the second half of the old one.
            Assert.AreEqual(StaminaVerdict.Waiting, t.Offer(121, T0.AddSeconds(70)));
            Assert.AreEqual(StaminaVerdict.Accepted, t.Offer(121, T0.AddSeconds(73)));
        }

        [TestMethod]
        public void Tracker_SpendingNeedsTwo_ARefillNeedsThree_OnePointOfSlackIsNotARefill()
        {
            var known = new StaminaReading(160, T0, 240);
            StaminaTracker t = Tracker(known);
            // The player spent 40: lower than the timer expected.
            Assert.AreEqual(StaminaVerdict.Waiting, t.Offer(120, T0.AddSeconds(30)));
            Assert.AreEqual(StaminaVerdict.Accepted, t.Offer(120, T0.AddSeconds(33)));
            Assert.AreEqual(120, t.Reading.Value);

            // A refill (+60): higher than regeneration explains, so one more sample.
            Assert.AreEqual(StaminaVerdict.Waiting, t.Offer(180, T0.AddSeconds(60)));
            Assert.AreEqual(StaminaVerdict.Waiting, t.Offer(180, T0.AddSeconds(63)));
            Assert.AreEqual(StaminaVerdict.Accepted, t.Offer(180, T0.AddSeconds(66)));

            // One point more than expected is the unknown moment within the interval, not a refill: two samples.
            StaminaTracker u = Tracker(known);
            Assert.AreEqual(160, StaminaClock.ValueAt(Resin, known, T0.AddSeconds(200)));
            Assert.AreEqual(StaminaVerdict.Waiting, u.Offer(161, T0.AddSeconds(200)));
            Assert.AreEqual(StaminaVerdict.Accepted, u.Offer(161, T0.AddSeconds(203)));
        }

        [TestMethod]
        public void Tracker_APointSeenArriving_SetsThePhase_OnceTheNextLookAgrees()
        {
            var known = new StaminaReading(160, T0, 240);
            StaminaTracker t = Tracker(known);
            Assert.AreEqual(StaminaVerdict.AsExpected, t.Offer(160, T0.AddSeconds(100)));
            // Three seconds later the screen shows one more: regeneration, if the next look says so too.
            Assert.AreEqual(StaminaVerdict.Waiting, t.Offer(161, T0.AddSeconds(103)));
            Assert.AreEqual(known, t.Reading, "one look is not enough");
            Assert.AreEqual(StaminaVerdict.Accepted, t.Offer(161, T0.AddSeconds(106)));
            Assert.AreEqual(new StaminaReading(161, T0.AddSeconds(106), 477), t.Reading, "a whole interval from the moment the point was first seen");
            Assert.AreEqual(StaminaChange.Corrected, t.LastChange);
            Assert.AreEqual(T0.AddSeconds(106), t.LastSeenAt);

            // A single look that shows one more and is gone again was a misread: the timer does not move.
            StaminaTracker u = Tracker(known);
            Assert.AreEqual(StaminaVerdict.AsExpected, u.Offer(160, T0.AddSeconds(100)));
            Assert.AreEqual(StaminaVerdict.Waiting, u.Offer(161, T0.AddSeconds(103)));
            Assert.AreEqual(StaminaVerdict.AsExpected, u.Offer(160, T0.AddSeconds(106)));
            Assert.AreEqual(known, u.Reading);
            Assert.AreEqual(StaminaChange.None, u.LastChange);
            // Not past the cap, and not across a long gap.
            StaminaTracker full = Tracker(new StaminaReading(200, T0, 240));
            Assert.AreEqual(StaminaVerdict.AsExpected, full.Offer(200, T0.AddSeconds(3)));
            Assert.AreEqual(StaminaVerdict.Waiting, full.Offer(201, T0.AddSeconds(6)));
        }

        [TestMethod]
        public void Tracker_ValuesThatKeepClimbing_ConfirmNothing()
        {
            // 180, 181, 182 three seconds apart: no value repeats, and two points in six seconds is not regeneration.
            StaminaTracker t = Tracker(new StaminaReading(160, T0, 240));
            Assert.AreEqual(StaminaVerdict.Waiting, t.Offer(180, T0.AddSeconds(60)));
            Assert.AreEqual(StaminaVerdict.Waiting, t.Offer(181, T0.AddSeconds(63)));
            Assert.AreEqual(StaminaVerdict.Waiting, t.Offer(182, T0.AddSeconds(66)));
            Assert.AreEqual(StaminaVerdict.Waiting, t.Offer(183, T0.AddSeconds(69)));
            Assert.AreEqual(160, t.Reading.Value);
            // A value going down in between starts again too.
            Assert.AreEqual(StaminaVerdict.Waiting, t.Offer(182, T0.AddSeconds(72)));
            Assert.AreEqual(160, t.Reading.Value);
            // One point arriving while a refill is being confirmed is fine: 180, 181, 181.
            StaminaTracker u = Tracker(new StaminaReading(160, T0, 240));
            Assert.AreEqual(StaminaVerdict.Waiting, u.Offer(180, T0.AddSeconds(60)));
            Assert.AreEqual(StaminaVerdict.Waiting, u.Offer(181, T0.AddSeconds(63)));
            Assert.AreEqual(StaminaVerdict.Accepted, u.Offer(181, T0.AddSeconds(66)));
            Assert.AreEqual(new StaminaReading(181, T0.AddSeconds(66), 477), u.Reading);
            Assert.AreEqual(StaminaChange.Refilled, u.LastChange);
        }

        [TestMethod]
        public void Tracker_SaysWhyTheTimerMoved()
        {
            StaminaTracker t = Tracker();
            t.Offer(160, T0);
            t.Offer(160, T0.AddSeconds(3));
            Assert.AreEqual(StaminaChange.First, t.LastChange);
            t.Offer(120, T0.AddSeconds(30));
            t.Offer(120, T0.AddSeconds(33));
            Assert.AreEqual(StaminaChange.Spent, t.LastChange);
            t.Offer(121, T0.AddSeconds(60));
            t.Offer(121, T0.AddSeconds(63));
            Assert.AreEqual(StaminaChange.Corrected, t.LastChange, "one point off: the same fill");

            // One point under what the timer expected is the same thing the other way round (the timer ran from an
            // unknown moment within the interval), not spending: nothing costs one point.
            StaminaTracker u = Tracker(new StaminaReading(190, T0, 240));
            DateTimeOffset later = T0.AddMinutes(52);
            Assert.AreEqual(197, StaminaClock.ValueAt(Resin, u.Reading, later));
            Assert.AreEqual(StaminaVerdict.Waiting, u.Offer(196, later));
            Assert.AreEqual(StaminaVerdict.Accepted, u.Offer(196, later.AddSeconds(3)));
            Assert.AreEqual(StaminaChange.Corrected, u.LastChange);
            // Two under is spending.
            StaminaTracker v = Tracker(new StaminaReading(190, T0, 240));
            v.Offer(195, later);
            v.Offer(195, later.AddSeconds(3));
            Assert.AreEqual(StaminaChange.Spent, v.LastChange);
        }

        [TestMethod]
        public void Tracker_ARegeneratedValueAfterALongBreak_IsWhatTheTimerExpected()
        {
            StaminaTracker t = Tracker(new StaminaReading(10, T0, 240));
            Assert.AreEqual(StaminaVerdict.AsExpected, t.Offer(200, T0.AddDays(3)), "filled up while away");
            Assert.AreEqual(10, t.Reading.Value, "the reading the timer runs from is unchanged");
        }

        // ── one notice per fill ──────────────────────────────────────────

        [TestMethod]
        public void Notice_ArmsOnlyWithMoreThanTheLeadLeft_AndShowsOnceWhenDue()
        {
            TimeSpan lead = TimeSpan.FromMinutes(30);
            var far = new StaminaReading(100, T0, 480);        // 800 minutes to full
            var close = new StaminaReading(198, T0, 480);      // 16 minutes to full: the player just saw it
            Assert.IsTrue(StaminaNotice.Arms(Resin, far, lead));
            Assert.IsFalse(StaminaNotice.Arms(Resin, close, lead));
            Assert.IsFalse(StaminaNotice.Arms(Resin, At(200), lead));
            Assert.IsFalse(StaminaNotice.Arms(Resin, null, lead));

            DateTimeOffset due = StaminaNotice.DueAt(Resin, far, lead);
            Assert.AreEqual(T0 + 770 * Min, due);
            Assert.AreEqual(new StaminaNoticeDecision(StaminaNoticeStep.Sleep, due), StaminaNotice.Next(Resin, far, true, true, lead, T0, true));
            Assert.AreEqual(StaminaNoticeStep.Wait, StaminaNotice.Next(Resin, far, true, true, lead, due, false).Step, "a game is in front");
            Assert.AreEqual(StaminaNoticeStep.Show, StaminaNotice.Next(Resin, far, true, true, lead, due, true).Step);
            Assert.AreEqual(StaminaNoticeStep.Idle, StaminaNotice.Next(Resin, far, false, true, lead, due, true).Step, "already shown for this fill");
            Assert.AreEqual(StaminaNoticeStep.Idle, StaminaNotice.Next(Resin, far, true, false, lead, due, true).Step, "switched off");
            Assert.AreEqual(StaminaNoticeStep.Idle, StaminaNotice.Next(Resin, null, true, true, lead, due, true).Step);
        }

        [TestMethod]
        public void NoticeText_SaysAlmostFullOrFull_InBothLanguages()
        {
            var culture = new CultureInfo("en-GB");
            var reading = new StaminaReading(100, T0, 480);
            DateTimeOffset due = StaminaClock.FullAt(Resin, reading) - TimeSpan.FromMinutes(30);
            StaminaNoticeText almost = StaminaText.Notice(Resin, reading, due, En, culture);
            Assert.AreEqual("Original Resin is almost full", almost.Title);
            StringAssert.StartsWith(almost.Body, "About 196/200 now, full ");
            StaminaNoticeText full = StaminaText.Notice(Resin, reading, due.AddHours(2), En, culture);
            Assert.AreEqual("Original Resin is full", full.Title);
            StringAssert.StartsWith(full.Body, "200/200. It filled ");
            StringAssert.EndsWith(full.Body, "anything more is lost until you spend some.");
            // A game with a reserve pool loses nothing at the cap, and the notice does not say it does.
            foreach (string id in new[] { "starrail", "wuwa" })
            {
                GameStamina pool = GameRegistry.Current.FindByWireId(id).Stamina;
                StaminaNoticeText body = StaminaText.Notice(pool, new StaminaReading(pool.Max, T0, pool.RegenSeconds), T0.AddHours(1), En, culture);
                StringAssert.EndsWith(body.Body, $"what comes next goes to {pool.Reserve.Name}, slower, until that is full too.", id);
                Assert.IsFalse(body.Body.Contains("lost"), id);
            }
            Assert.IsNull(GameRegistry.Current.FindByWireId("genshin").Stamina.Reserve);

            RenderTestKit.RunSta(() =>
            {
                var host = new FrameworkElement();
                foreach (var d in RenderTestKit.Strings("pl-PL")) host.Resources.MergedDictionaries.Add(d);
                string Pl(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                StaminaNoticeText pl = StaminaText.Notice(Resin, reading, due, Pl, new CultureInfo("pl-PL"));
                Assert.AreEqual("Original Resin: zaraz limit", pl.Title, "the pool keeps its English name");
                StringAssert.StartsWith(pl.Body, "Masz teraz około 196/200, zapełni się ");
            });
        }

        // ── storage, link, card ──────────────────────────────────────────

        [TestMethod]
        public void StoredReading_RoundTrips_AndABrokenOneIsNoReading()
        {
            var r = new StaminaReading(143, DateTimeOffset.FromUnixTimeMilliseconds(1794225600123), 187.5);
            Assert.AreEqual("143|1794225600123|187.5", StaminaSettings.FormatReading(r));
            Assert.AreEqual(r, StaminaSettings.ParseReading(StaminaSettings.FormatReading(r)));
            foreach (string bad in new[] { null, "", "143", "143|x|1", "143|1794225600123", "-1|1794225600123|1", "143|1794225600123|-1",
                         "143|1794225600123|NaN", "143|1794225600123|1e9", "1e3|1794225600123|1", "143|99999999999999999999|1", "999999|1794225600123|1" })
                Assert.IsNull(StaminaSettings.ParseReading(bad), bad ?? "null");
        }

        [TestMethod]
        public void TimerLink_CarriesTheReadingInTheFragment_AndFollowsTheLanguage()
        {
            GameManifest genshin = GameRegistry.Current.FindByWireId("genshin");
            var r = new StaminaReading(143, DateTimeOffset.FromUnixTimeSeconds(1794225600), 187.4);
            Assert.AreEqual("https://kaption.one/tools/stamina-timer/genshin-impact/", StaminaLinks.TimerUrl(genshin, "en-US"));
            Assert.AreEqual("https://kaption.one/pl/narzedzia/licznik-energii/genshin-impact/#v=143&t=1794225600&n=187",
                StaminaLinks.TimerUrl(genshin, "pl-PL", r));
            // Nothing of the reading is in the path or the query: only the part a browser keeps to itself.
            var uri = new Uri(StaminaLinks.TimerUrl(genshin, "en-US", r));
            Assert.AreEqual(string.Empty, uri.Query);
            Assert.AreEqual("#v=143&t=1794225600&n=187", uri.Fragment);
            Assert.IsNull(StaminaLinks.TimerUrl(new GameManifest { Id = "x", GameId = "X" }, "en-US", r));
            // The number seen to agree later than the reading goes along; an earlier or equal moment adds nothing.
            StringAssert.EndsWith(StaminaLinks.TimerUrl(genshin, "en-US", r, r.At.AddHours(5)), "#v=143&t=1794225600&n=187&s=1794243600");
            StringAssert.EndsWith(StaminaLinks.TimerUrl(genshin, "en-US", r, r.At), "#v=143&t=1794225600&n=187");
            StringAssert.EndsWith(StaminaLinks.TimerUrl(genshin, "en-US", r, r.At.AddHours(-1)), "#v=143&t=1794225600&n=187");
        }

        [TestMethod]
        public void Card_OffWaitingTracking_AndHiddenForAnUnmeasuredGame()
        {
            GameManifest genshin = GameRegistry.Current.FindByWireId("genshin");
            var culture = new CultureInfo("en-GB");
            TimeSpan lead = TimeSpan.FromMinutes(30);

            StaminaCardView off = StaminaCardModel.Build(genshin, false, true, lead, null, null, T0, "en-US", En, culture);
            Assert.AreEqual(StaminaCardState.Off, off.State);
            Assert.IsFalse(off.On);
            StringAssert.Contains(off.Line, "It reads that one number and keeps it on this PC.");
            Assert.AreEqual("Read Original Resin from the game screen", off.ToggleLabel);
            Assert.IsNull(off.Number);
            Assert.IsNull(off.TimerUrl, "no link before the player switches it on");

            StaminaCardView waiting = StaminaCardModel.Build(genshin, true, true, lead, null, null, T0, "en-US", En, culture);
            Assert.AreEqual(StaminaCardState.Waiting, waiting.State);
            StringAssert.StartsWith(waiting.Line, "Waiting for the number. ");
            Assert.AreEqual("One notice, 30 min before it fills, never over the game.", waiting.NoticeHint);

            var reading = new StaminaReading(143, T0, 240);
            StaminaCardView tracking = StaminaCardModel.Build(genshin, true, false, lead, reading, null, T0.AddMinutes(20), "en-US", En, culture);
            Assert.AreEqual(StaminaCardState.Tracking, tracking.State);
            Assert.AreEqual("146/200", tracking.Number, "20 minutes on: points arrived at 4, 12 and 20 minutes");
            StringAssert.EndsWith(tracking.Line, ", in 7 h 12 min.");
            Assert.AreEqual("Read from the game 20 min ago.", tracking.Fresh);
            StringAssert.EndsWith(tracking.TimerUrl, "#v=143&t=" + T0.ToUnixTimeSeconds() + "&n=240");
            Assert.IsFalse(tracking.NoticeOn);

            StaminaCardView full = StaminaCardModel.Build(genshin, true, true, lead, reading, null, T0.AddDays(2), "en-US", En, culture);
            Assert.AreEqual("200/200", full.Number);
            StringAssert.StartsWith(full.Line, "Full. It filled ");
            StaminaCardView fresh = StaminaCardModel.Build(genshin, true, true, lead, reading, null, T0.AddSeconds(20), "en-US", En, culture);
            Assert.AreEqual("Read from the game just now.", fresh.Fresh);

            // The number seen again later, as the timer expected: "read" follows the sighting, the timer does not move.
            // A full pool seen at 23:00 still filled when it filled.
            StaminaCardView seen = StaminaCardModel.Build(genshin, true, true, lead, reading, T0.AddMinutes(18), T0.AddMinutes(20), "en-US", En, culture);
            Assert.AreEqual("Read from the game 2 min ago.", seen.Fresh);
            StringAssert.EndsWith(seen.TimerUrl, "&n=240&s=" + T0.AddMinutes(18).ToUnixTimeSeconds());
            Assert.AreEqual(tracking.Line, seen.Line);
            StaminaCardView fullSeen = StaminaCardModel.Build(genshin, true, true, lead, reading, T0.AddDays(2).AddMinutes(-1), T0.AddDays(2), "en-US", En, culture);
            Assert.AreEqual(full.Line, fullSeen.Line, "seeing a full pool again does not move the moment it filled");
            Assert.AreEqual("Read from the game 1 min ago.", fullSeen.Fresh);
            // A sighting older than the reading (a stale key) is ignored.
            StaminaCardView stale = StaminaCardModel.Build(genshin, true, true, lead, reading, T0.AddHours(-5), T0.AddMinutes(20), "en-US", En, culture);
            Assert.AreEqual(tracking.Fresh, stale.Fresh);

            Assert.IsFalse(StaminaCardModel.Build(GameRegistry.Current.FindByWireId("zzz"), true, true, lead, reading, null, T0, "en-US", En, culture).Visible);
            Assert.IsFalse(StaminaCardModel.Build(null, true, true, lead, reading, null, T0, "en-US", En, culture).Visible);
        }

        [TestMethod]
        public void Durations_RoundUpToWholeMinutes()
        {
            Assert.AreEqual("0 min", StaminaText.Duration(TimeSpan.Zero, En));
            Assert.AreEqual("1 min", StaminaText.Duration(TimeSpan.FromSeconds(1), En));
            Assert.AreEqual("59 min", StaminaText.Duration(TimeSpan.FromMinutes(59), En));
            Assert.AreEqual("1 h", StaminaText.Duration(TimeSpan.FromMinutes(60), En));
            Assert.AreEqual("7 h 36 min", StaminaText.Duration(TimeSpan.FromMinutes(456), En));
            Assert.AreEqual("1 d 2 h", StaminaText.Duration(TimeSpan.FromMinutes(1440 + 125), En));
            Assert.AreEqual("2 d", StaminaText.Duration(TimeSpan.FromDays(2), En));
        }

        [TestMethod]
        public void EveryMeasuredGame_HasItsWhereLine_AndEveryStringIsInBothLanguages()
        {
            RenderTestKit.RunSta(() =>
            {
                string[] keys =
                {
                    "Stamina_Toggle", "Stamina_Off", "Stamina_Waiting", "Stamina_Where_Other", "Stamina_FullAt", "Stamina_FullSince", "Stamina_At",
                    "Stamina_Tomorrow", "Stamina_Yesterday", "Stamina_OnDay", "Stamina_Unit_Day", "Stamina_Unit_Hour", "Stamina_Unit_Minute",
                    "Stamina_Read_Now", "Stamina_Read_Ago", "Stamina_OpenTimer", "Stamina_OpenTimer_Tip", "Stamina_Notice_Toggle", "Stamina_Notice_Hint",
                    "Stamina_Notice_Almost_Title", "Stamina_Notice_Almost_Body", "Stamina_Notice_Full_Title", "Stamina_Notice_Full_Body",
                    "Stamina_Notice_Full_Body_Reserve",
                };
                var games = GameRegistry.Current.Listed.Where(StaminaCardModel.Supports).Select(g => "Stamina_Where_" + g.GameId).ToArray();
                Assert.AreEqual(3, games.Length);
                foreach (string lang in new[] { "en-US", "pl-PL" })
                {
                    var host = new FrameworkElement();
                    foreach (var d in RenderTestKit.Strings(lang)) host.Resources.MergedDictionaries.Add(d);
                    foreach (string key in keys.Concat(games))
                    {
                        string text = host.TryFindResource(key) as string;
                        Assert.IsFalse(string.IsNullOrWhiteSpace(text), $"{lang}: {key}");
                        // The words a player must never read here.
                        foreach (string banned in new[] { "OCR", "AI", "model" })
                            Assert.IsFalse(text.Split(' ', '.', ',', ':').Contains(banned), $"{lang}: {key} says {banned}");
                    }
                }
            });
        }
    }
}
