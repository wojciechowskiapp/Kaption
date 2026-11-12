using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Stamina;
using GI_Subtitles.Services.Today;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The Today card's sections, one pure builder each (Services/Today/TodayCardModel): reset rules read from the
    /// manifests, the stamina estimate and how it is worded when the reading is old, the reserve pool, what ends soon,
    /// what was seen on this PC, a game with nothing to show, the week file, and where the card goes.
    /// </summary>
    [TestClass]
    public class TodayCardTests
    {
        /// <summary>Wednesday noon UTC.</summary>
        private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

        private static GameManifest Game(string id) => GameRegistry.Current.FindByWireId(id);
        private static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);
        private static string En(string key, string fallback) => fallback;

        private static TodayInputs Inputs(string game, DateTimeOffset? now = null, string server = "europe", bool staminaOn = true, StaminaReading reading = null,
            DateTimeOffset? seenAt = null, IReadOnlyList<WeekPeriod> periods = null, IReadOnlyList<AssistStoredReading> read = null, FarmCardModel farm = null) =>
            new(Game(game), now ?? Now, server, staminaOn, reading, seenAt, periods, read, farm, "en-US");

        // ── Resets as data ───────────────────────────────────────────────

        [TestMethod]
        public void Resets_ComeFromTheManifests_AndWutheringWavesHasNoneYet()
        {
            foreach (string id in new[] { "genshin", "starrail", "zzz" })
            {
                GameResets r = Game(id).Resets;
                Assert.IsNotNull(r, id);
                CollectionAssert.AreEqual(new[] { "europe", "america", "asia" }, r.Servers.Select(s => s.Id).ToArray(), id);
                Assert.AreEqual(4, r.DailyHour, id);
                Assert.AreEqual(1, r.WeeklyDay, id + ": Monday");
            }
            // Not confirmed on Kuro's own pages: no block, so no reset countdown is ever shown for it.
            Assert.IsNull(Game("wuwa").Resets);

            // The farm card's server day reads the same data.
            Assert.AreEqual(4, ServerDay.ResetHour);
            Assert.AreEqual(1, ServerDay.OffsetOf(FarmServer.Europe));
            Assert.AreEqual(-5, ServerDay.OffsetOf(FarmServer.America));
            Assert.AreEqual(8, ServerDay.OffsetOf(FarmServer.Asia));
            Assert.AreEqual("europe", Game("genshin").Resets.Server("mars").Id, "an unknown server is the first one");
        }

        [TestMethod]
        public void Resets_TheNextDailyAndWeeklyOnThePlayersServer()
        {
            GameResets hsr = Game("starrail").Resets;
            // Europe: 13:00 server time on Wednesday. The day turns at 04:00 (03:00 UTC), the week on Monday.
            TodayResets eu = TodayCardBuilder.Resets(hsr, "europe", Now);
            Assert.AreEqual(Utc("2026-11-12T03:00:00Z"), eu.Daily);
            Assert.AreEqual(Utc("2026-11-16T03:00:00Z"), eu.Weekly);
            Assert.AreEqual(DayOfWeek.Monday, eu.WeeklyDay);
            Assert.AreEqual("Europe", eu.ServerName);

            // America: 07:00 server time, so today's reset was three hours ago and the next is tomorrow 09:00 UTC.
            TodayResets us = TodayCardBuilder.Resets(hsr, "america", Now);
            Assert.AreEqual(Utc("2026-11-12T09:00:00Z"), us.Daily);
            Assert.AreEqual(Utc("2026-11-16T09:00:00Z"), us.Weekly);

            Assert.IsNull(TodayCardBuilder.Resets(null, "europe", Now), "a game without reset data has no section");
            Assert.IsNull(TodayCardBuilder.Resets(Game("wuwa").Resets, "europe", Now));
        }

        [TestMethod]
        public void Resets_TheWeeklyResetOnItsOwnDay_BeforeAndAfterTheHour()
        {
            // Monday 02:59 UTC on Europe is 03:59 server time: the week turns in a minute.
            Assert.AreEqual(Utc("2026-11-16T03:00:00Z"), ServerDay.NextWeekly(Utc("2026-11-16T02:59:00Z"), 1, 4, 1));
            // A minute later it has turned: the next one is a week away.
            Assert.AreEqual(Utc("2026-11-23T03:00:00Z"), ServerDay.NextWeekly(Utc("2026-11-16T03:00:00Z"), 1, 4, 1));
            // Sunday evening: tomorrow.
            Assert.AreEqual(Utc("2026-11-16T03:00:00Z"), ServerDay.NextWeekly(Utc("2026-11-15T20:00:00Z"), 1, 4, 1));
        }

        [TestMethod]
        public void TheServersDay_NotTheLocalOne_NamesTheCardsDay()
        {
            // 21:00 UTC on Wednesday is Thursday 05:00 on the Asia server, past its reset: the card says Thursday.
            var evening = Utc("2026-11-11T21:00:00Z");
            TodayCardModel asia = TodayCardBuilder.Build(Inputs("zzz", evening, "asia"));
            Assert.AreEqual(DayOfWeek.Thursday, asia.Weekday);
            Assert.AreEqual(Utc("2026-11-12T20:00:00Z"), asia.Resets.Daily);
            // The same moment on America is Wednesday 16:00.
            Assert.AreEqual(DayOfWeek.Wednesday, TodayCardBuilder.Build(Inputs("zzz", evening, "america")).Weekday);

            // "Once a day" follows the same day: the key changes at the server's reset, not at local midnight.
            GameManifest zzz = Game("zzz");
            Assert.AreEqual("2026-11-12", TodayCardSettings.DayKey(zzz, evening, "asia"));
            Assert.AreEqual("2026-11-11", TodayCardSettings.DayKey(zzz, evening, "america"));
            Assert.AreEqual("2026-11-10", TodayCardSettings.DayKey(zzz, Utc("2026-11-11T02:59:00Z"), "europe"), "before 04:00 it is still yesterday's game day");
            // A game without reset data counts local days.
            Assert.AreEqual(evening.ToLocalTime().ToString("yyyy-MM-dd"), TodayCardSettings.DayKey(Game("wuwa"), evening, "europe"));
            // Genshin's key is the farm card's own, so a card shown before this change is not shown twice.
            var day = ServerDay.At(evening, FarmServer.Europe);
            Assert.AreEqual(day.Key(FarmServer.Europe), TodayCardSettings.DayKey(Game("genshin"), evening, "europe"));
        }

        // ── Stamina ──────────────────────────────────────────────────────

        [TestMethod]
        public void Stamina_NoReading_NoSection()
        {
            Assert.IsNull(TodayCardBuilder.Stamina(Game("genshin"), null, null, Now, "en-US"));
            Assert.IsNull(TodayCardBuilder.Stamina(null, new StaminaReading(10, Now, 100), null, Now, "en-US"));
            // Switched off: an old reading is not shown either.
            Assert.IsNull(TodayCardBuilder.Build(Inputs("genshin", staminaOn: false, reading: new StaminaReading(143, Now.AddMinutes(-5), 310))).Stamina);
            // A reading dated after the clock is a clock that moved: refused, not guessed.
            Assert.IsNull(TodayCardBuilder.Stamina(Game("genshin"), new StaminaReading(143, Now.AddHours(2), 310), null, Now, "en-US"));
        }

        [TestMethod]
        public void Stamina_AnEstimateByTheRule_WithWhenItWasRead()
        {
            GameManifest g = Game("genshin");
            var reading = new StaminaReading(143, Now.AddMinutes(-65), 310);
            TodayStamina s = TodayCardBuilder.Stamina(g, reading, null, Now, "en-US");
            Assert.AreEqual("Original Resin", s.PoolName);
            Assert.AreEqual(StaminaClock.ValueAt(g.Stamina, reading, Now), s.Value);
            Assert.IsTrue(s.Value > 143 && s.Value < 200, "an hour of regeneration, not the number that was read");
            Assert.AreEqual(200, s.Max);
            Assert.AreEqual(TodayStaminaState.Filling, s.State);
            Assert.AreEqual(StaminaClock.FullAt(g.Stamina, reading), s.FullAt);
            Assert.AreEqual(reading.At, s.ReadAt);
            Assert.IsNull(s.Reserve);
            StringAssert.Contains(s.TimerUrl, "/tools/stamina-timer/genshin-impact/#v=143");

            // The number on screen agreed with the timer later: the read is that fresh.
            var seen = Now.AddMinutes(-2);
            Assert.AreEqual(seen, TodayCardBuilder.Stamina(g, reading, seen, Now, "en-US").ReadAt);
            // A "seen" from before the reading, or from the future, is not used.
            Assert.AreEqual(reading.At, TodayCardBuilder.Stamina(g, reading, Now.AddHours(-5), Now, "en-US").ReadAt);
            Assert.AreEqual(reading.At, TodayCardBuilder.Stamina(g, reading, Now.AddHours(5), Now, "en-US").ReadAt);
        }

        [TestMethod]
        public void Stamina_AStaleReading_IsOnlyFullSinceAbout()
        {
            GameManifest g = Game("genshin");
            // Genshin fills from zero in 200 x 8 min = 26 h 40 min.
            var fresh = TodayCardBuilder.Stamina(g, new StaminaReading(150, Now.AddHours(-20), 240), null, Now, "en-US");
            Assert.AreEqual(TodayStaminaState.Full, fresh.State, "full by the timer, read within one fill: it can say when it filled");
            Assert.AreEqual(200, fresh.Value);

            var stale = TodayCardBuilder.Stamina(g, new StaminaReading(100, Now.AddHours(-30), 240), null, Now, "en-US");
            Assert.AreEqual(TodayStaminaState.FullLongAgo, stale.State);
            Assert.AreEqual(200, stale.Value);

            // The words: never "it is full", only since about when, and always when it was read.
            var model = TodayCardBuilder.Build(Inputs("genshin", reading: new StaminaReading(100, Now.AddHours(-30), 240)));
            var line = new TodayCardViewModel(model, "en-US", En).Sections[0].Lines[0];
            Assert.AreEqual("200/200", line.Lead);
            StringAssert.StartsWith(line.Text, "Full since about ");
            StringAssert.StartsWith(line.Sub, "Read ");
            Assert.IsTrue(line.HasLinkText, "the site's timer, with the reading in the link's fragment");

            var filling = new TodayCardViewModel(TodayCardBuilder.Build(Inputs("genshin", reading: new StaminaReading(143, Now.AddMinutes(-5), 310))), "en-US", En);
            StringAssert.StartsWith(filling.Sections[0].Lines[0].Text, "Full ");
            StringAssert.Contains(filling.Sections[0].Lines[0].Text, ", in ");
        }

        [TestMethod]
        public void Stamina_AReservePool_GainsByItsRuleOnceTheMainOneIsFull()
        {
            GameManifest hsr = Game("starrail");
            // 290/300 ten hours ago: full an hour later, and the reserve gains a point every 18 minutes after that.
            var reading = new StaminaReading(290, Now.AddHours(-10), 360);
            TodayStamina s = TodayCardBuilder.Stamina(hsr, reading, null, Now, "en-US");
            Assert.AreEqual(300, s.Value);
            Assert.AreEqual("Reserved Trailblaze Power", s.Reserve.Name);
            long expected = (long)Math.Floor((Now - s.FullAt).TotalSeconds / hsr.Stamina.Reserve.RegenSeconds);
            Assert.AreEqual(expected, s.Reserve.Gained);
            Assert.IsTrue(s.Reserve.Gained is > 25 and < 35);
            Assert.IsFalse(s.Reserve.AtCap);

            // Still filling: nothing goes to the reserve yet.
            Assert.IsNull(TodayCardBuilder.Stamina(hsr, new StaminaReading(100, Now.AddMinutes(-30), 360), null, Now, "en-US").Reserve);
            // Weeks ago: the rule alone has filled the reserve; the number is capped, never invented past it.
            TodayStamina old = TodayCardBuilder.Stamina(hsr, new StaminaReading(290, Now.AddDays(-40), 360), null, Now, "en-US");
            Assert.AreEqual(hsr.Stamina.Reserve.Max, old.Reserve.Gained);
            Assert.IsTrue(old.Reserve.AtCap);
            Assert.AreEqual(TodayStaminaState.FullLongAgo, old.State);
            // A game without a reserve never has one.
            Assert.IsNull(TodayCardBuilder.Stamina(Game("genshin"), new StaminaReading(190, Now.AddDays(-2), 240), null, Now, "en-US").Reserve);

            var lines = new TodayCardViewModel(TodayCardBuilder.Build(Inputs("starrail", reading: reading)), "en-US", En).Sections[0].Lines;
            Assert.AreEqual(2, lines.Count);
            StringAssert.StartsWith(lines[1].Text, "Reserved Trailblaze Power: about +");
        }

        // ── Ending soon ──────────────────────────────────────────────────

        private static WeekPeriod Period(string title, string ends, string starts = null, string game = "starrail", string path = "/news/some-article/") =>
            new(game, "banner", title, null, starts == null ? null : Utc(starts), Utc(ends), path);

        [TestMethod]
        public void Ending_WhatRunsAndEndsWithinTheWeek_SoonestFirst()
        {
            var periods = new[]
            {
                Period("ends in two days", "2026-11-13T12:00:00Z", "2026-11-01T00:00:00Z"),
                Period("ends in the next hour", "2026-11-11T12:40:00Z"),
                Period("ends in eight days", "2026-11-19T12:00:01Z"),
                Period("not started yet", "2026-11-14T00:00:00Z", "2026-11-12T00:00:00Z"),
                Period("ended a minute ago", "2026-11-11T11:59:00Z"),
                Period("another game", "2026-11-12T00:00:00Z", game: "genshin"),
                Period("not a page of the site", "2026-11-12T00:00:00Z", path: "@evil.example/x"),
            };
            var (rows, more) = TodayCardBuilder.Ending(periods, "starrail", Now);
            CollectionAssert.AreEqual(new[] { "ends in the next hour", "ends in two days" }, rows.Select(r => r.Title).ToArray());
            Assert.AreEqual(0, more);
            Assert.AreEqual("https://kaption.one/news/some-article/", rows[0].Url);
            Assert.AreEqual(Utc("2026-11-11T12:40:00Z"), rows[0].EndsAt);

            // The words count down in minutes inside the last hour.
            var vm = new TodayCardViewModel(TodayCardBuilder.Build(Inputs("starrail", periods: periods)), "en-US", En);
            var section = vm.Sections.Single(s => s.Title == "Ending soon");
            Assert.AreEqual("ends in 40 min", section.Lines[0].Note);
            Assert.AreEqual("ends in 2 d", section.Lines[1].Note);
            Assert.IsTrue(section.Lines[0].IsLink, "the line opens its article");

            // More than fit: the rest is a count.
            var many = Enumerable.Range(1, 5).Select(i => Period($"p{i}", $"2026-11-12T0{i}:00:00Z")).ToArray();
            var (shown, rest) = TodayCardBuilder.Ending(many, "starrail", Now);
            Assert.AreEqual(TodayCardBuilder.MaxEnding, shown.Count);
            Assert.AreEqual(2, rest);

            Assert.AreEqual(0, TodayCardBuilder.Ending(null, "starrail", Now).Rows.Count, "no file: the section is empty and hidden");
        }

        [TestMethod]
        public void TheWeekFile_IsReadTolerantly()
        {
            const string json = """
                {"schema":1,"built":"2026-11-11T08:00:00.000Z",
                 "en":[
                   {"game":"genshin","kind":"banner","title":"Event Wishes","label":"Phase I","startsAt":"2026-11-01T00:00:00Z","endsAt":"2026-11-13T12:00:00Z","path":"/news/genshin-banners/"},
                   {"game":"genshin","kind":"event","title":"No end","startsAt":"2026-11-01T00:00:00Z","path":"/news/x/"},
                   {"game":"genshin","kind":"event","title":"Off the site","endsAt":"2026-11-13T12:00:00Z","path":"https://example.com/"},
                   {"game":"genshin","kind":"event","title":"Backwards","startsAt":"2026-11-20T00:00:00Z","endsAt":"2026-11-13T12:00:00Z","path":"/news/y/"},
                   {"game":"genshin","kind":"event","title":"Double slash","endsAt":"2026-11-13T12:00:00Z","path":"//example.com/"},
                   {"game":"genshin","kind":"event","title":"Unreadable start","startsAt":"soon","endsAt":"2026-11-13T12:00:00Z","path":"/news/z/"},
                   {"game":"genshin","kind":"event","title":"Null start","startsAt":null,"endsAt":"2026-11-14T12:00:00Z","path":"/news/n/"},
                   "not a row"
                 ],
                 "pl":[]}
                """;
            TodayWeek week = TodayWeek.Parse(json);
            CollectionAssert.AreEqual(new[] { "Event Wishes", "Null start" }, week.English.Select(r => r.Title).ToArray(),
                "a start that is there and cannot be read drops the row; a null one means it runs already");
            Assert.IsNull(week.English[1].StartsAt);
            WeekPeriod p = week.English[0];
            Assert.AreEqual(("genshin", "banner", "Event Wishes", "Phase I", "/news/genshin-banners/"), (p.Game, p.Kind, p.Title, p.Label, p.Path));
            Assert.AreEqual(Utc("2026-11-13T12:00:00Z"), p.EndsAt);
            Assert.AreSame(week.English, week.For("pl-PL"), "no Polish rows: the English ones");
            Assert.AreSame(week.English, week.For("en-US"));

            Assert.IsNull(TodayWeek.Parse("{\"schema\":2,\"en\":[]}"), "a schema this build does not know");
            Assert.IsNull(TodayWeek.Parse("not json"));
            // A list of another shape, or a missing one, is not a file to replace the last good copy with.
            Assert.IsNull(TodayWeek.Parse("{\"schema\":1,\"en\":{},\"pl\":[]}"));
            Assert.IsNull(TodayWeek.Parse("{\"schema\":1,\"en\":[]}"));
            Assert.IsNotNull(TodayWeek.Parse("{\"schema\":1,\"en\":[],\"pl\":[]}"), "two empty lists are a quiet week");

            // The download is counted as it arrives, and a failed read is not tried again at once.
            var small = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"schema\":1}"));
            Assert.AreEqual("{\"schema\":1}", TodayWeekData.ReadBoundedAsync(small, 64, default).GetAwaiter().GetResult());
            var big = new System.IO.MemoryStream(new byte[65]);
            Assert.IsNull(TodayWeekData.ReadBoundedAsync(big, 64, default).GetAwaiter().GetResult());
            var failedAt = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
            Assert.IsFalse(TodayWeekData.MayTry(failedAt.AddMinutes(5), failedAt + TodayWeekData.RetryAfter));
            Assert.IsTrue(TodayWeekData.MayTry(failedAt + TodayWeekData.RetryAfter, failedAt + TodayWeekData.RetryAfter));
            Assert.IsTrue(TodayWeekData.MayTry(failedAt.AddDays(-1), failedAt + TodayWeekData.RetryAfter), "the clock was set back: try");
            Assert.IsNull(TodayWeek.Parse(""));

            var now = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
            Assert.IsTrue(TodayWeekData.IsFresh(now.AddHours(-5), now));
            Assert.IsFalse(TodayWeekData.IsFresh(now.AddHours(-7), now), "older than six hours: read again");
            Assert.IsFalse(TodayWeekData.IsFresh(now.AddHours(3), now), "written after the clock: the clock moved, read again");
            Assert.IsFalse(TodayWeekData.IsFresh(null, now));
        }

        // ── Seen on this PC ──────────────────────────────────────────────

        [TestMethod]
        public void Seen_OtherReadersLines_EachWithItsTime_AndNothingMeansNoSection()
        {
            var readings = new[]
            {
                new AssistStoredReading("local", AssistSettings.StaminaReader, "143/200", "", Now.AddMinutes(-3)),
                new AssistStoredReading("local", "weekly", "2/3", "", Now.AddHours(-2)),
                new AssistStoredReading("local", "map", "Chest nearby", "", Now.AddMinutes(-20)),
                new AssistStoredReading("local", "party", "   ", "", Now.AddMinutes(-1)),
                new AssistStoredReading("local", "future", "x", "", Now.AddHours(1)),
            };
            var seen = TodayCardBuilder.Seen(readings, Now);
            CollectionAssert.AreEqual(new[] { "map", "weekly" }, seen.Select(s => s.ReaderId).ToArray(), "newest first; stamina has its own section");
            Assert.AreEqual(Now.AddMinutes(-20), seen[0].SeenAt);

            var vm = new TodayCardViewModel(TodayCardBuilder.Build(Inputs("wuwa", read: readings)), "en-US", En);
            var section = vm.Sections.Single();
            Assert.AreEqual("Seen on this PC", section.Title);
            StringAssert.StartsWith(section.Lines[0].Sub, "Seen ");

            // The empty state is no section at all: only the stamina reader exists today, and it is not listed here.
            Assert.AreEqual(0, TodayCardBuilder.Seen(readings.Take(1).ToList(), Now).Count);
            Assert.AreEqual(0, TodayCardBuilder.Seen(null, Now).Count);
        }

        // ── A game with nothing ──────────────────────────────────────────

        [TestMethod]
        public void AGameWithNoSections_GetsNoCard_AndResetsAloneAreNotWorthAnInterruption()
        {
            TodayCardModel wuwa = TodayCardBuilder.Build(Inputs("wuwa"));
            Assert.IsFalse(wuwa.HasSections, "no reading, no reset data, nothing ending: nothing to lay out");
            Assert.IsFalse(wuwa.WorthShowing);
            Assert.AreEqual(0, new TodayCardViewModel(wuwa, "en-US", En).BlockSections.Count, "and no block on Start");

            TodayCardModel hsr = TodayCardBuilder.Build(Inputs("starrail"));
            Assert.IsTrue(hsr.HasSections, "the resets: on demand and on Start");
            Assert.IsFalse(hsr.WorthShowing, "but not over the game unasked");

            Assert.IsTrue(TodayCardBuilder.Build(Inputs("wuwa", reading: new StaminaReading(100, Now.AddMinutes(-10), 180))).WorthShowing);
            Assert.IsTrue(TodayCardBuilder.Build(Inputs("starrail", periods: new[] { Period("x", "2026-11-12T00:00:00Z") })).WorthShowing);

            // Every listed game can have a card; the trigger watches a game only while today's has not shown.
            foreach (GameManifest g in GameRegistry.Current.Listed) Assert.IsTrue(TodayCardSettings.HasCard(g), g.Id);
            Assert.IsFalse(TodayCardSettings.HasCard(null));
            Assert.IsTrue(FarmCardTrigger.ShouldWatch(true, true, "2026-11-10", "2026-11-11"));
            Assert.IsFalse(FarmCardTrigger.ShouldWatch(true, false, null, "2026-11-11"), "a game without a card is never probed");
            Assert.IsFalse(FarmCardTrigger.ShouldWatch(true, true, "2026-11-11", "2026-11-11"));
        }

        // ── The card and the block read one model ────────────────────────

        [TestMethod]
        public void TheCardAndTheStartBlock_AreTheSameModel()
        {
            var model = TodayCardBuilder.Build(Inputs("starrail", reading: new StaminaReading(143, Now.AddMinutes(-5), 310),
                periods: new[] { Period("Warp event", "2026-11-13T12:00:00Z") }));
            var vm = new TodayCardViewModel(model, "en-US", En);
            Assert.AreEqual("Today in Honkai: Star Rail", vm.Eyebrow);
            Assert.AreEqual("Wednesday", vm.DayTitle);
            StringAssert.StartsWith(vm.ResetText, "Resets in ");
            Assert.IsFalse(vm.HasFarm);
            CollectionAssert.AreEqual(new[] { "Trailblaze Power", "This week", "Ending soon" }, vm.Sections.Select(s => s.Title).ToArray());
            // The card keeps the daily reset in its header; the weekly one is the section's line.
            Assert.AreEqual(1, vm.Sections[1].Lines.Count);
            Assert.AreEqual("Weekly reset, Monday", vm.Sections[1].Lines[0].Text);

            // Start: the stamina timer has its own card there, and the daily reset becomes a line.
            CollectionAssert.AreEqual(new[] { "This week", "Ending soon" }, vm.BlockSections.Select(s => s.Title).ToArray());
            CollectionAssert.AreEqual(new[] { "Daily reset", "Weekly reset, Monday" }, vm.BlockSections[0].Lines.Select(l => l.Text).ToArray());
            Assert.AreEqual("in 15 h", vm.BlockSections[0].Lines[0].Note);

            // A game without reset data has no reset chip to show.
            var wuwa = new TodayCardViewModel(TodayCardBuilder.Build(Inputs("wuwa", reading: new StaminaReading(100, Now.AddMinutes(-10), 180))), "en-US", En);
            Assert.IsFalse(wuwa.HasReset);
            Assert.AreEqual("Waveplates", wuwa.Sections.Single().Title);
        }

        [TestMethod]
        public void Genshin_KeepsItsFarmCard_AndTheOtherSectionsSitWithIt()
        {
            FarmCardModel farm = FarmCardSamples.Model(FarmCardSamples.Wednesday, hasUid: true);
            var model = TodayCardBuilder.Build(Inputs("genshin", FarmCardSamples.Wednesday, farm: farm, reading: new StaminaReading(143, FarmCardSamples.Wednesday.AddMinutes(-5), 310)));
            Assert.AreSame(farm, model.Farm);
            Assert.IsTrue(model.WorthShowing);
            var vm = new TodayCardViewModel(model, "en-US", En);
            var alone = new FarmCardViewModel(farm, "en-US", En);
            // The farm content is the farm card's own view model, word for word.
            Assert.AreEqual("What to farm today", vm.Eyebrow);
            Assert.AreEqual(alone.DayTitle, vm.DayTitle);
            Assert.AreEqual(alone.ResetText, vm.ResetText);
            Assert.AreEqual(alone.TwinText, vm.TwinText);
            CollectionAssert.AreEqual(alone.Books.Select(b => b.Series).ToArray(), vm.Books.Select(b => b.Series).ToArray());
            CollectionAssert.AreEqual(alone.Rows.Select(r => r.Name).ToArray(), vm.Rows.Select(r => r.Name).ToArray());
            // Over the game Genshin's card is the tall one: the weekly reset stays on Start, and it lists two periods.
            CollectionAssert.AreEqual(new[] { "Original Resin" }, vm.Sections.Select(s => s.Title).ToArray());
            CollectionAssert.Contains(vm.BlockSections.Select(s => s.Title).ToArray(), "This week");
            var three = Enumerable.Range(1, 3).Select(i => Period($"p{i}", $"2026-11-05T0{i}:00:00Z", game: "genshin")).ToArray();
            var crowded = new TodayCardViewModel(TodayCardBuilder.Build(Inputs("genshin", FarmCardSamples.Wednesday, farm: farm, periods: three)), "en-US", En);
            var ending = crowded.Sections.Single(s => s.Title == "Ending soon");
            Assert.AreEqual(TodayCardViewModel.EndingBesideFarm + 1, ending.Lines.Count, "two periods and the count of the rest");
            Assert.AreEqual("+1 more on the site", ending.Lines.Last().Text);
            Assert.IsTrue(ending.Lines.Last().IsLink && ending.Lines.Last().HasText, "a link the card and the Start block both draw as one");
            Assert.AreEqual(3, crowded.BlockSections.Single(s => s.Title == "Ending soon").Lines.Count, "Start has room for all three");
            // On Start the books and who can level come down to one section.
            Assert.AreEqual(alone.BooksLabel, vm.BlockSections.Last().Title);

            // The farm card alone, as the render tests show it: no sections, so nothing new is laid out.
            var wrapped = new TodayCardViewModel(alone);
            Assert.AreEqual(0, wrapped.Sections.Count);
            Assert.IsFalse(wrapped.HasSections);
            Assert.IsNull(wrapped.Eyebrow, "the view keeps its own header text");
            Assert.AreSame(alone, wrapped.Farm);
        }

        // ── Where the card goes ──────────────────────────────────────────

        [TestMethod]
        public void Placement_StaysOffTheGamesTopBar_WhereTheStaminaNumberSits()
        {
            // Genshin at 1920x1080: the stamina number is in the strip the manifest measured, top right.
            Rect bar = MainWindow.StaminaBar(Game("genshin"), new Rect(0, 0, 1920, 1080));
            var search = Game("genshin").Desktop.StaminaScreen.Search;
            Assert.AreEqual(new Rect(search.X * 1920, search.Y * 1080, search.W * 1920, search.H * 1080), bar);
            Assert.IsTrue(bar.Right > 1900 && bar.Top < 100, "the top-right corner, where the card would otherwise go");

            var work = new Rect(0, 0, 1920, 1040);
            var card = new Size(404, 520);
            Point at = FarmCardPlacement.Compute(work, card, new[] { bar, new Rect(480, 860, 960, 160) });
            Assert.IsFalse(new Rect(at, card).IntersectsWith(bar));
            Assert.AreEqual(1920 - 404 - FarmCardPlacement.Margin, at.X, "still on the right");
            Assert.AreEqual(bar.Bottom + FarmCardPlacement.Margin, at.Y, "just under the bar");

            // A game window on a second monitor keeps its origin; a game without a measured strip has no bar.
            Rect second = MainWindow.StaminaBar(Game("genshin"), new Rect(2560, 0, 1920, 1080));
            Assert.AreEqual(2560 + search.X * 1920, second.X);
            Assert.IsTrue(MainWindow.StaminaBar(Game("zzz"), new Rect(0, 0, 1920, 1080)).IsEmpty);
            Assert.IsTrue(MainWindow.StaminaBar(null, new Rect(0, 0, 1920, 1080)).IsEmpty);
        }
    }
}
