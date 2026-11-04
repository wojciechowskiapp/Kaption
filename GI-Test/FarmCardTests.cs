using System;
using System.Linq;
using System.Windows;
using GI_Subtitles.Services.Today;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>The farm card's data and decisions: schedule and showcase parsing, the model, the trigger, placement.</summary>
    [TestClass]
    public class FarmCardTests
    {
        [TestMethod]
        public void Schedule_ReadsTheSitesFile()
        {
            var s = FarmCardSamples.Schedule();
            Assert.IsTrue(s.Regions.Count >= 8);
            Assert.AreEqual(s.Regions.Count, s.OpenOn(DayOfWeek.Wednesday).Count, "one series per region on a weekday");
            Assert.AreEqual(s.Regions.Count * 3, s.OpenOn(DayOfWeek.Sunday).Count, "everything on Sunday");
            Assert.AreEqual("Mondstadt", s.RegionName(s.OpenOn(DayOfWeek.Monday)[0].City), "region order");
        }

        [TestMethod]
        public void Schedule_RejectsWhatIsNotOne()
        {
            Assert.IsNull(TodaySchedule.Parse(null));
            Assert.IsNull(TodaySchedule.Parse("<html>"));
            Assert.IsNull(TodaySchedule.Parse("{\"schema\":2,\"game\":\"genshin\",\"domains\":[]}"));
            Assert.IsNull(TodaySchedule.Parse("{\"schema\":1,\"game\":\"starrail\",\"domains\":[]}"));
            Assert.IsNull(TodaySchedule.Parse("{\"schema\":1,\"game\":\"genshin\",\"domains\":[{\"kind\":\"talent\",\"name\":\"X\"}]}"));
        }

        [TestMethod]
        public void Schedule_GatesAVersionUntilItIsOut()
        {
            string json = "{\"schema\":1,\"game\":\"genshin\",\"regions\":[{\"city\":9,\"name\":\"New\"}],\"out\":[],\"domains\":[" +
                "{\"kind\":\"talent\",\"city\":9,\"name\":\"D\",\"days\":[0,3,6],\"since\":\"8.0\",\"items\":[" +
                "{\"id\":1,\"name\":\"Teachings of A\",\"rarity\":2},{\"id\":2,\"name\":\"Guide to A\",\"rarity\":3},{\"id\":3,\"name\":\"Philosophies of A\",\"rarity\":4}]}]}";
            var s = TodaySchedule.Parse(json);
            Assert.AreEqual(0, s.OpenOn(DayOfWeek.Wednesday).Count);
            Assert.AreEqual(1, s.OpenOn(DayOfWeek.Wednesday, new System.Collections.Generic.HashSet<string> { "8.0" }).Count);
            var published = TodaySchedule.Parse(json.Replace("\"out\":[]", "\"out\":[\"8.0\"]"));
            Assert.AreEqual(1, published.OpenOn(DayOfWeek.Saturday).Count);
        }

        [TestMethod]
        public void Schedule_OrdersTheBooksByRarity_WhateverTheFileOrder()
        {
            string Domain(string items) => "{\"schema\":1,\"game\":\"genshin\",\"regions\":[{\"city\":1,\"name\":\"M\"}],\"domains\":[" +
                "{\"kind\":\"talent\",\"city\":1,\"name\":\"D\",\"days\":[0,1,4],\"items\":[" + items + "]}]}";
            const string T = "{\"id\":1,\"name\":\"Teachings of A\",\"rarity\":2}";
            const string G = "{\"id\":2,\"name\":\"Guide to A\",\"rarity\":3}";
            const string P = "{\"id\":3,\"name\":\"Philosophies of A\",\"rarity\":4}";
            var d = TodaySchedule.Parse(Domain(string.Join(",", P, T, G))).TalentDomains.Single();
            CollectionAssert.AreEqual(new[] { 2, 3, 4 }, d.Items.Select(i => i.Rarity).ToArray());
            Assert.AreEqual("A", FarmCardBuilder.Series(d));
            Assert.IsNull(TodaySchedule.Parse(Domain(string.Join(",", T, G, G.Replace("\"id\":2", "\"id\":4")))), "two Guides, no Philosophies");
            Assert.IsNull(TodaySchedule.Parse(Domain(string.Join(",", T, G, P.Replace("\"rarity\":4", "\"rarity\":5")))), "a rarity that is not a book tier");

            // The site's own file, shuffled: the card's tiles and steps name the same books.
            var shuffled = System.Text.Json.Nodes.JsonNode.Parse(FarmCardSamples.ScheduleJson());
            foreach (var dom in shuffled["domains"].AsArray())
            {
                var items = dom["items"]?.AsArray();
                if (items == null || items.Count != 3) continue;
                var copy = items.Select(i => i.DeepClone()).Reverse().ToList();
                items.Clear();
                foreach (var i in copy) items.Add(i);
            }
            var a = FarmCardSamples.Schedule();
            var b = TodaySchedule.Parse(shuffled.ToJsonString());
            CollectionAssert.AreEqual(a.TalentDomains.SelectMany(x => x.Items).ToArray(), b.TalentDomains.SelectMany(x => x.Items).ToArray());
        }

        [TestMethod]
        public void Showcase_TellsAnUnknownUidFromAnOutage()
        {
            Assert.IsTrue(ShowcaseRoster.IsUnknownUidAnswer(404, "{\"error\":\"unknown_uid\",\"message\":\"No player with this UID\"}"));
            Assert.IsFalse(ShowcaseRoster.IsUnknownUidAnswer(404, "Not Found"), "a missing route is an outage");
            Assert.IsFalse(ShowcaseRoster.IsUnknownUidAnswer(404, "{\"error\":\"character_not_in_showcase\"}"));
            Assert.IsFalse(ShowcaseRoster.IsUnknownUidAnswer(502, "{\"error\":\"unknown_uid\"}"));
            Assert.IsFalse(ShowcaseRoster.IsUnknownUidAnswer(404, null));
        }

        [TestMethod]
        public void Showcase_ReadsTheRosterAndRoundTripsTheCache()
        {
            var r = FarmCardSamples.Roster(FarmCardSamples.Wednesday);
            Assert.AreEqual(8, r.Characters.Count);
            var raiden = r.Characters[0];
            Assert.AreEqual("Raiden Shogun", raiden.Name);
            Assert.AreEqual("Electro", raiden.Element);
            Assert.AreEqual(3, raiden.Talents.Single(t => t.Kind == "ultimate").Bonus);
            Assert.AreEqual("2026-11-04", r.Day);

            var back = ShowcaseRoster.FromCache(r.ToCacheJson(), "700000001");
            Assert.AreEqual(r.Characters.Count, back.Characters.Count);
            Assert.AreEqual(r.Day, back.Day);
            Assert.AreEqual(9, back.Characters[0].Talents.Single(t => t.Kind == "ultimate").Level);
            Assert.IsNull(ShowcaseRoster.FromCache(r.ToCacheJson(), "700000002"), "another player's cache is not read");
            Assert.IsNull(ShowcaseRoster.FromCache("{broken", "700000001"));
            Assert.IsNull(ShowcaseRoster.FromApi("{\"game\":\"starrail\",\"characters\":[]}", "1", "d", DateTimeOffset.UtcNow));
        }

        [TestMethod]
        public void Model_Wednesday_ShowsEveryOpenSeriesAndTheRows()
        {
            var m = FarmCardSamples.Model(FarmCardSamples.Wednesday, hasUid: true);
            Assert.AreEqual(DayOfWeek.Wednesday, m.Weekday);
            Assert.AreEqual(FarmPlayerState.Rows, m.Player);
            Assert.AreEqual(8, m.Books.Count);
            Assert.AreEqual("Light", m.Books.First(b => b.Mine > 0 && b.Region == "Inazuma").Series);
            Assert.IsTrue(m.Books.TakeWhile(b => b.Mine > 0).Count() == m.Books.Count(b => b.Mine > 0), "the player's books first");
            Assert.AreEqual(2, m.Books.Single(b => b.Series == "Gold").Mine, "Zhongli and Xingqiu");
            CollectionAssert.AreEqual(new[] { "Raiden Shogun", "Venti", "Nilou", "Zhongli", "Xingqiu" }, m.Rows.Select(r => r.Name).ToArray());
            var raiden = m.Rows[0];
            Assert.AreEqual("Q", raiden.Steps[0].Short, "the top talent first");
            Assert.IsTrue(raiden.Steps[0].IsTop);
            Assert.IsTrue(raiden.Steps[0].Crown, "9 → 10 takes a Crown");
            Assert.AreEqual("Philosophies of Light", raiden.Steps[0].Item);
            Assert.AreEqual(3, raiden.Steps[0].Bonus);
            Assert.AreEqual(FarmCardModel.MaxStepsPerRow, raiden.Steps.Count);
            Assert.AreEqual("https://gi.yatta.moe/assets/UI/UI_ItemIcon_104328.png", m.Books.Single(b => b.Series == "Light").IconUrl);
            Assert.AreEqual("https://enka.network/ui/UI_AvatarIcon_Shougun.png", raiden.IconUrl);
            Assert.AreEqual("https://kaption.one/pl/dzisiaj/", m.FullListUrl);
            Assert.AreEqual(TimeSpan.FromHours(15), m.ResetIn, "Europe resets at 03:00 UTC");
        }

        [TestMethod]
        public void Model_Sunday_ShowsOnlyThePlayersBooks()
        {
            var m = FarmCardSamples.Model(FarmCardSamples.Sunday, hasUid: true, lang: "en-US");
            Assert.IsTrue(m.IsSunday);
            Assert.AreEqual(FarmCardModel.MaxRows, m.Rows.Count);
            Assert.AreEqual(3, m.MoreRows);
            Assert.IsTrue(m.Books.All(b => b.Mine > 0));
            Assert.AreEqual("https://kaption.one/today/", m.FullListUrl);
            Assert.AreEqual(0, FarmCardSamples.Model(FarmCardSamples.Sunday, hasUid: false).Books.Count, "no UID on Sunday: the banner says it all");
        }

        [TestMethod]
        public void Model_PlayerStates()
        {
            Assert.AreEqual(FarmPlayerState.NoUid, FarmCardSamples.Model(FarmCardSamples.Wednesday, hasUid: false).Player);
            Assert.AreEqual(FarmPlayerState.ShowcaseEmpty, FarmCardSamples.Model(FarmCardSamples.Wednesday, hasUid: true, emptyShowcase: true).Player);
            var s = FarmCardSamples.Schedule();
            var day = ServerDay.At(FarmCardSamples.Wednesday, FarmServer.Europe);
            Assert.AreEqual(FarmPlayerState.Unavailable, FarmCardBuilder.Build(s, day, FarmCardSamples.Wednesday, true, null, FarmCardSamples.Catalog(), "pl").Player);
            var unknown = FarmCardBuilder.Build(s, day, FarmCardSamples.Wednesday, true, null, FarmCardSamples.Catalog(), "pl", unknownUid: true);
            Assert.AreEqual(FarmPlayerState.UnknownUid, unknown.Player);
            Assert.IsTrue(unknown.Books.Count > 0, "today's books still show");
            var noData = FarmCardBuilder.Build(s, day, FarmCardSamples.Wednesday, true, FarmCardSamples.Roster(FarmCardSamples.Wednesday),
                GI_Subtitles.Services.Characters.CharacterCatalog.Empty, "pl");
            Assert.AreEqual(FarmPlayerState.CatalogMissing, noData.Player, "the showcase was read: the missing piece is the character data");
            // Monday: nobody in the sample takes Freedom, Prosperity, Transience... books.
            var monday = DateTimeOffset.Parse("2026-11-02T12:00:00Z");
            Assert.AreEqual(FarmPlayerState.NothingToday, FarmCardSamples.Model(monday, hasUid: true).Player);
        }

        [TestMethod]
        public void ViewModel_WordsTheDay()
        {
            var vm = new FarmCardViewModel(FarmCardSamples.Model(FarmCardSamples.Wednesday, hasUid: true), "pl-PL", null);
            Assert.AreEqual("Środa", vm.DayTitle);
            var lit = vm.Week.Where(w => w.IsSame).Select(w => w.Letter).ToArray();
            Assert.AreEqual(2, lit.Length, "Saturday and Sunday bring Wednesday's books back");
            Assert.IsTrue(vm.Week[2].IsToday, "Monday first: Wednesday is the third cell");
            Assert.AreEqual(3, FarmCardViewModel.Twin(6));
            Assert.AreEqual(-1, FarmCardViewModel.Twin(0));
            Assert.IsTrue(vm.Rows[0].Steps[0].Tooltip.Contains("Crown of Insight"));
        }

        [TestMethod]
        public void ViewModel_PointsAnUnknownUidToTheField()
        {
            var s = FarmCardSamples.Schedule();
            var day = ServerDay.At(FarmCardSamples.Wednesday, FarmServer.Europe);
            string L(string k, string f) => k;
            var unknown = new FarmCardViewModel(FarmCardBuilder.Build(s, day, FarmCardSamples.Wednesday, true, null, FarmCardSamples.Catalog(), "pl", unknownUid: true), "pl-PL", L);
            Assert.AreEqual("FarmCard_UnknownUid", unknown.PlayerMessage);
            Assert.IsTrue(unknown.ShowAddUid);
            Assert.AreEqual("FarmCard_CheckUid", unknown.AddUidText);
            var noUid = new FarmCardViewModel(FarmCardSamples.Model(FarmCardSamples.Wednesday, hasUid: false), "pl-PL", L);
            Assert.AreEqual("FarmCard_AddUid", noUid.AddUidText);
            var noData = new FarmCardViewModel(FarmCardBuilder.Build(s, day, FarmCardSamples.Wednesday, true, FarmCardSamples.Roster(FarmCardSamples.Wednesday),
                GI_Subtitles.Services.Characters.CharacterCatalog.Empty, "pl"), "pl-PL", L);
            Assert.AreEqual("FarmCard_CatalogMissing", noData.PlayerMessage);
            Assert.IsFalse(noData.ShowAddUid);
        }

        [TestMethod]
        public void Trigger_OncePerServerDay_AfterTheGameSettles()
        {
            var t = new FarmCardTrigger();
            var t0 = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
            Assert.IsFalse(t.Tick(true, true, "2026-11-03", "2026-11-04", t0));
            Assert.IsFalse(t.Tick(true, true, "2026-11-03", "2026-11-04", t0 + TimeSpan.FromSeconds(5)), "still loading");
            Assert.IsTrue(t.Tick(true, true, "2026-11-03", "2026-11-04", t0 + FarmCardTrigger.SettleTime));
            Assert.IsFalse(t.Tick(true, true, "2026-11-04", "2026-11-04", t0 + TimeSpan.FromMinutes(1)), "already shown today");
            Assert.IsFalse(t.Tick(false, true, null, "2026-11-05", t0 + TimeSpan.FromDays(1)), "turned off");
            Assert.IsFalse(t.Tick(true, false, null, "2026-11-05", t0 + TimeSpan.FromDays(1)), "game not in front");
            Assert.IsFalse(t.Tick(true, true, null, "2026-11-05", t0 + TimeSpan.FromDays(1)), "the settle time starts again");
        }

        [TestMethod]
        public void Trigger_WatchesOnlyWhileTodaysCardIsDue()
        {
            Assert.IsTrue(FarmCardTrigger.ShouldWatch(true, true, "2026-11-03", "2026-11-04"));
            Assert.IsTrue(FarmCardTrigger.ShouldWatch(true, true, null, "2026-11-04"), "never shown");
            Assert.IsFalse(FarmCardTrigger.ShouldWatch(true, true, "2026-11-04", "2026-11-04"), "shown today: no probe until the reset");
            Assert.IsFalse(FarmCardTrigger.ShouldWatch(false, true, null, "2026-11-04"), "turned off");
            Assert.IsFalse(FarmCardTrigger.ShouldWatch(true, false, null, "2026-11-04"), "another game selected");

            // A skipped stretch never counts as settle time: after Reset the game has to settle again.
            var t = new FarmCardTrigger();
            var t0 = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
            Assert.IsFalse(t.Tick(true, true, "2026-11-03", "2026-11-04", t0));
            t.Reset();
            Assert.IsFalse(t.Tick(true, true, "2026-11-03", "2026-11-04", t0 + TimeSpan.FromHours(1)));
            Assert.IsTrue(t.Tick(true, true, "2026-11-03", "2026-11-04", t0 + TimeSpan.FromHours(1) + FarmCardTrigger.SettleTime));
        }

        [TestMethod]
        public void Placement_TopRight_ClearOfTheDialogueRegion()
        {
            var work = new Rect(0, 0, 2560, 1400);
            var card = new Size(505, 700);
            var p = FarmCardPlacement.Compute(work, card, new[] { new Rect(560, 1100, 1440, 260) });
            Assert.AreEqual(new Point(2560 - 505 - FarmCardPlacement.Margin, FarmCardPlacement.Margin), p);

            // A region reaching into the top-right corner pushes the card to the top-left.
            var tall = FarmCardPlacement.Compute(work, card, new[] { new Rect(1800, 0, 760, 900) });
            Assert.AreEqual(FarmCardPlacement.Margin, tall.X);
            Assert.IsFalse(new Rect(tall, card).IntersectsWith(new Rect(1800, 0, 760, 900)));

            // A second monitor's work area keeps its own origin.
            var right = FarmCardPlacement.Compute(new Rect(2560, 0, 1920, 1040), new Size(404, 500), Array.Empty<Rect>());
            Assert.AreEqual(2560 + 1920 - 404 - FarmCardPlacement.Margin, right.X);
        }

        [TestMethod]
        public void Settings_KeepOnlyAUidsShape()
        {
            string key = FarmCardSettings.UidKey("Genshin");
            Assert.AreEqual("ShowcaseUid_Genshin", key);
            string before = GI_Subtitles.Core.Config.Config.Get<string>(key, null);
            try
            {
                Assert.IsFalse(FarmCardSettings.SaveUid("12345"));
                Assert.IsFalse(FarmCardSettings.SaveUid("0123456789"));
                Assert.IsTrue(FarmCardSettings.SaveUid(" 700000001 "));
                Assert.AreEqual("700000001", FarmCardSettings.Uid);
                FarmCardSettings.ForgetUid();
                Assert.IsNull(FarmCardSettings.Uid);
            }
            finally
            {
                if (before != null) GI_Subtitles.Core.Config.Config.Set(key, before);
                else if (GI_Subtitles.Core.Config.Config.Has(key)) GI_Subtitles.Core.Config.Config.Remove(key);
            }
        }
    }
}
