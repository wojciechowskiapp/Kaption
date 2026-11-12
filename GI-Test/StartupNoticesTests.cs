using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Notices;
using GI_Subtitles.Services.Notices.WhatsNew;
using GI_Subtitles.Services.Stamina;
using GI_Subtitles.Services.Today;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AppConfig = GI_Subtitles.Core.Config.Config;

namespace GI_Test
{
    /// <summary>
    /// What Kaption says on its own at a start: "New in Kaption" once per release with its questions asked once, the
    /// notice of the day once per game day, and one notice per start at most. The rules are pure; these tests are the
    /// record of what is shown on which start and what is remembered.
    /// </summary>
    [TestClass]
    public class StartupNoticesTests
    {
        private static readonly CultureInfo En = new("en-US");
        private static string Fallback(string key, string fallback) => fallback;

        // ── The shipped entries ─────────────────────────────────────────

        private static string ShippedJson() =>
            File.ReadAllText(Path.Combine(RenderTestKit.RepoRoot(), "GI-Subtitles", "Resources", "WhatsNew", "entries.json"));

        [TestMethod]
        public void Shipped_EveryEntryLoads_AndTheBuildCarriesTheSameFile()
        {
            var parsed = WhatsNewCatalog.Parse(ShippedJson(), out var skipped);
            Assert.AreEqual(0, skipped.Count, string.Join("; ", skipped));
            Assert.IsTrue(parsed.Entries.Count >= 3, "the first release of the card has its entries");
            CollectionAssert.AreEqual(parsed.Entries.Select(e => e.Id).ToList(), WhatsNewCatalog.Default.Entries.Select(e => e.Id).ToList(),
                "the embedded resource is the file in the repo");
        }

        [TestMethod]
        public void Shipped_EveryEntryNamesOnlyWhatTheCodeKnows()
        {
            var known = new HashSet<string> { "game", "stamina", "staminaWhere", "staminaLead" };
            foreach (WhatsNewEntry e in WhatsNewCatalog.Parse(ShippedJson(), out _).Entries)
            {
                if (e.Feature != null) Assert.IsTrue(WhatsNewFeatures.Known.Contains(e.Feature), $"{e.Id}: feature {e.Feature}");
                if (e.Requires != null) Assert.IsTrue(WhatsNewRequirements.Known.Contains(e.Requires), $"{e.Id}: requires {e.Requires}");
                if (e.Action != null) Assert.IsTrue(WhatsNewActions.Known.Contains(e.Action), $"{e.Id}: action {e.Action}");
                foreach (WhatsNewText t in new[] { e.English, e.Polish })
                foreach (string text in new[] { t.Title, t.Body, t.Done, t.Action })
                foreach (string p in WhatsNewCatalog.PlaceholdersOf(text))
                    Assert.IsTrue(known.Contains(p), $"{e.Id}: placeholder {{{p}}} has no value");
                // Words that name a pool need a game that has one.
                bool namesStamina = new[] { e.English, e.Polish }.SelectMany(t => new[] { t.Title, t.Body, t.Done })
                    .SelectMany(WhatsNewCatalog.PlaceholdersOf).Any(p => p.StartsWith("stamina", StringComparison.Ordinal));
                if (namesStamina) Assert.AreEqual(WhatsNewRequirements.StaminaScreen, e.Requires, $"{e.Id}: names the pool, so it needs a game with one");
                if (e.Kind == WhatsNewKind.Ask)
                {
                    Assert.IsNotNull(e.English.Done, $"{e.Id}: a question says what happened once it is on");
                    Assert.IsNotNull(e.Polish.Done, $"{e.Id}: a question says what happened once it is on");
                }
            }
        }

        [TestMethod]
        public void Shipped_TheWordsArePlayerWords()
        {
            // Never how it is made, never the reader's jargon, never the old name; both languages, every field.
            string[] banned = { " AI ", "AI-", "model", "OCR", "GI-Subtitles", "machine learning", "sztuczn", "algorytm" };
            foreach (WhatsNewEntry e in WhatsNewCatalog.Parse(ShippedJson(), out _).Entries)
            foreach (WhatsNewText t in new[] { e.English, e.Polish })
            foreach (string text in new[] { t.Title, t.Body, t.Done, t.Action }.Where(x => x != null))
            {
                foreach (string word in banned)
                    Assert.IsFalse((" " + text + " ").Contains(word, StringComparison.OrdinalIgnoreCase), $"{e.Id}: \"{word.Trim()}\" in \"{text}\"");
                Assert.IsFalse(text.Contains('—'), $"{e.Id}: no long dashes in player text");
            }
        }

        [TestMethod]
        public void Parse_SkipsWhatItCannotShow_AndKeepsTheRest()
        {
            const string json = """
            { "schema": 1, "entries": [
              { "id": "ok", "since": "2.7.0", "kind": "info", "en": { "title": "T", "body": "B" }, "pl": { "title": "T", "body": "B" } },
              { "id": "ok", "since": "2.7.0", "kind": "info", "en": { "title": "T", "body": "B" }, "pl": { "title": "T", "body": "B" } },
              { "id": "one-language", "since": "2.7.0", "kind": "info", "en": { "title": "T", "body": "B" } },
              { "id": "no-release", "kind": "info", "en": { "title": "T", "body": "B" }, "pl": { "title": "T", "body": "B" } },
              { "id": "ask-nothing", "since": "2.7.0", "kind": "ask", "en": { "title": "T", "body": "B" }, "pl": { "title": "T", "body": "B" } },
              { "id": "odd-kind", "since": "2.7.0", "kind": "banner", "en": { "title": "T", "body": "B" }, "pl": { "title": "T", "body": "B" } },
              { "id": "action-no-label", "since": "2.7.0", "kind": "info", "action": "today-card", "en": { "title": "T", "body": "B" }, "pl": { "title": "T", "body": "B" } },
              { "since": "2.7.0", "kind": "info" }
            ] }
            """;
            var catalog = WhatsNewCatalog.Parse(json, out var skipped);
            CollectionAssert.AreEqual(new[] { "ok" }, catalog.Entries.Select(e => e.Id).ToList());
            Assert.AreEqual(7, skipped.Count, string.Join("; ", skipped));
            Assert.AreSame(WhatsNewCatalog.Empty, WhatsNewCatalog.Parse("not json", out _));
            Assert.AreSame(WhatsNewCatalog.Empty, WhatsNewCatalog.Parse("""{ "schema": 2, "entries": [] }""", out _));
            Assert.AreSame(WhatsNewCatalog.Empty, WhatsNewCatalog.Parse(null, out _));
        }

        [TestMethod]
        public void Fill_PutsValuesIn_AndRefusesAHole()
        {
            var values = new Dictionary<string, string> { ["stamina"] = "Original Resin", ["game"] = "Genshin Impact", ["empty"] = " " };
            Assert.AreEqual("Your Original Resin in Genshin Impact", WhatsNewCatalog.Fill("Your {stamina} in {game}", values));
            Assert.IsNull(WhatsNewCatalog.Fill("Your {staminaWhere}", values), "no value: the row is left out");
            Assert.IsNull(WhatsNewCatalog.Fill("Your {empty}", values), "a blank value is no value");
            Assert.AreEqual("Plain.", WhatsNewCatalog.Fill("Plain.", null));
            Assert.IsNull(WhatsNewCatalog.Fill(null, values));
        }

        // ── Which entries lead the Start page ───────────────────────────

        private static WhatsNewEntry Entry(string id, string since, int order, WhatsNewKind kind = WhatsNewKind.Info, string feature = null,
            string requires = null, string[] games = null) =>
            new(id, Version.Parse(since), order, kind, feature, requires, games ?? Array.Empty<string>(), null, null,
                new WhatsNewText("T " + id, "B " + id, "D " + id, null), new WhatsNewText("T " + id, "B " + id, "D " + id, null));

        private static readonly WhatsNewCatalog Sample = new(new[]
        {
            Entry("stamina", "2.6.1", 10, WhatsNewKind.Ask, WhatsNewFeatures.StaminaNotice, WhatsNewRequirements.StaminaScreen),
            Entry("day", "2.6.1", 20, WhatsNewKind.Info, WhatsNewFeatures.DayNotice, WhatsNewRequirements.TodayCard, new[] { "genshin" }),
            Entry("windows", "2.6.1", 30, WhatsNewKind.Ask, WhatsNewFeatures.StartWithWindows),
            Entry("recordings", "2.6.1", 40, WhatsNewKind.Ask, WhatsNewFeatures.ShowInRecordings),
        });

        /// <summary>A player on Genshin who updated: nothing answered, only the day notice on (its default).</summary>
        private static WhatsNewInputs Updated(Version seen = null) => new()
        {
            SeenThrough = seen,
            Game = "genshin",
            Meets = _ => true,
            FeatureOn = f => f == WhatsNewFeatures.DayNotice,
        };

        [TestMethod]
        public void Lead_FirstStartAfterAnUpdate_LeadsWithTheThreeThatMatter()
        {
            var lead = WhatsNewPlanner.Lead(Sample, Updated());
            Assert.AreEqual(WhatsNewPlanState.Show, lead.State);
            Assert.AreEqual(new Version(2, 6, 1), lead.Level);
            CollectionAssert.AreEqual(new[] { "stamina", "day", "windows" }, lead.Rows.Select(r => r.Id).ToList(), "three rows at most, in their order");
        }

        [TestMethod]
        public void Lead_AThingAlreadyOn_IsNotOfferedAgain_AndTheNextOneTakesItsPlace()
        {
            var lead = WhatsNewPlanner.Lead(Sample, Updated() with
            {
                FeatureOn = f => f is WhatsNewFeatures.DayNotice or WhatsNewFeatures.StaminaNotice or WhatsNewFeatures.StartWithWindows,
            });
            CollectionAssert.AreEqual(new[] { "day", "recordings" }, lead.Rows.Select(r => r.Id).ToList());
        }

        [TestMethod]
        public void Lead_NewsAboutASwitchThePlayerTurnedOff_IsNotNews()
        {
            var lead = WhatsNewPlanner.Lead(Sample, Updated() with { FeatureOn = _ => false });
            Assert.IsFalse(lead.Rows.Any(r => r.Id == "day"));
        }

        [TestMethod]
        public void Lead_ANewInstall_IsToldNothing_AndStartsFromThisRelease()
        {
            var lead = WhatsNewPlanner.Lead(Sample, Updated() with { FreshInstall = true });
            Assert.AreEqual(WhatsNewPlanState.Baseline, lead.State);
            Assert.AreEqual(new Version(2, 6, 1), lead.Level);
            Assert.AreEqual(0, lead.Rows.Count);
            // The same profile on a later start: nothing, until a release brings something newer.
            Assert.AreEqual(WhatsNewPlanState.None, WhatsNewPlanner.Lead(Sample, Updated(new Version(2, 6, 1))).State);
        }

        [TestMethod]
        public void Lead_OncePerRelease_OnlyTheNewerEntriesComeBack()
        {
            var next = new WhatsNewCatalog(Sample.Entries.Append(Entry("later", "2.7.0", 10)));
            var lead = WhatsNewPlanner.Lead(next, Updated(new Version(2, 6, 1)));
            Assert.AreEqual(WhatsNewPlanState.Show, lead.State);
            Assert.AreEqual(new Version(2, 7, 0), lead.Level);
            CollectionAssert.AreEqual(new[] { "later" }, lead.Rows.Select(r => r.Id).ToList(), "2.6.1's rows were told already");
        }

        [TestMethod]
        public void Lead_AnAnsweredQuestion_IsNeverAskedAtALaterStart_WhateverTheAnswer()
        {
            // Asked about stamina and starting with Windows before; neither is on.
            var answered = new HashSet<string> { WhatsNewFeatures.StaminaNotice, WhatsNewFeatures.StartWithWindows };
            var lead = WhatsNewPlanner.Lead(Sample, Updated() with { Answered = answered.Contains });
            CollectionAssert.AreEqual(new[] { "day", "recordings" }, lead.Rows.Select(r => r.Id).ToList());

            // A later release brings the same feature up under a new entry: still not asked on its own. "Not now",
            // "Don't ask again" and "Turn on" followed by turning it off by hand all end the asking.
            var next = new WhatsNewCatalog(Sample.Entries.Append(Entry("stamina-zzz", "2.7.0", 10, WhatsNewKind.Ask,
                WhatsNewFeatures.StaminaNotice, WhatsNewRequirements.StaminaScreen)));
            var later = WhatsNewPlanner.Lead(next, Updated(new Version(2, 6, 1)) with { Answered = f => f == WhatsNewFeatures.StaminaNotice });
            Assert.AreEqual(WhatsNewPlanState.Retire, later.State);
            Assert.AreEqual(0, later.Rows.Count);
            // Never asked before: the new entry asks.
            var fresh = WhatsNewPlanner.Lead(next, Updated(new Version(2, 6, 1)));
            CollectionAssert.AreEqual(new[] { "stamina-zzz" }, fresh.Rows.Select(r => r.Id).ToList());
            // The list the player opens himself still has it.
            Assert.IsTrue(WhatsNewPlanner.List(next, Updated(new Version(2, 6, 1)) with { Answered = _ => true }).Any(e => e.Id == "stamina-zzz"));
        }

        [TestMethod]
        public void Lead_ASkippedRelease_DoesNotAskTheSameQuestionTwice()
        {
            // 2.6.1 and 2.7.0 both ask about the stamina notice; the player comes from before both.
            var two = new WhatsNewCatalog(Sample.Entries.Append(Entry("stamina-zzz", "2.7.0", 10, WhatsNewKind.Ask,
                WhatsNewFeatures.StaminaNotice, WhatsNewRequirements.StaminaScreen)));
            var lead = WhatsNewPlanner.Lead(two, Updated());
            CollectionAssert.AreEqual(new[] { "stamina-zzz", "day", "windows" }, lead.Rows.Select(r => r.Id).ToList(),
                "the newest entry asks, once, and the row it would have taken goes to the next thing");
            // The list the player opens himself keeps both entries.
            Assert.AreEqual(2, WhatsNewPlanner.List(two, Updated()).Count(e => e.Feature == WhatsNewFeatures.StaminaNotice));
        }

        [TestMethod]
        public void Lead_OnlyWhatThisGameHas()
        {
            // Zenless Zone Zero: no stamina screen measured, no card of the day.
            var lead = WhatsNewPlanner.Lead(Sample, Updated() with { Game = "zzz", Meets = _ => false });
            CollectionAssert.AreEqual(new[] { "windows", "recordings" }, lead.Rows.Select(r => r.Id).ToList());
            // Star Rail has the stamina screen; the Genshin-only entry stays out even though its condition holds.
            var hsr = WhatsNewPlanner.Lead(Sample, Updated() with { Game = "starrail", Meets = r => r == WhatsNewRequirements.StaminaScreen });
            CollectionAssert.AreEqual(new[] { "stamina", "windows", "recordings" }, hsr.Rows.Select(r => r.Id).ToList());
        }

        [TestMethod]
        public void Lead_AnEntryThisBuildDoesNotKnow_IsLeftOut()
        {
            var odd = new WhatsNewCatalog(new[]
            {
                Entry("unknown-switch", "2.6.1", 1, WhatsNewKind.Ask, "telepathy"),
                Entry("unknown-condition", "2.6.1", 2, requires: "full-moon"),
                Entry("plain", "2.6.1", 3),
            });
            CollectionAssert.AreEqual(new[] { "plain" }, WhatsNewPlanner.Lead(odd, Updated()).Rows.Select(r => r.Id).ToList());
        }

        [TestMethod]
        public void Lead_OnceShown_NoLaterStartLeadsWithIt()
        {
            // The first start after the update: the card. The caller records the level once it is on screen.
            Assert.AreEqual(WhatsNewPlanState.Show, WhatsNewPlanner.Lead(Sample, Updated()).State);
            // The next start, nothing answered, nothing closed: nothing. Once per release.
            Assert.AreEqual(WhatsNewPlanState.None, WhatsNewPlanner.Lead(Sample, Updated(new Version(2, 6, 1))).State);
            // Never on screen (started with Windows, no window): it still waits.
            Assert.AreEqual(WhatsNewPlanState.Show, WhatsNewPlanner.Lead(Sample, Updated(seen: null)).State);
        }

        [TestMethod]
        public void Lead_StepsAside_WhenSwitchedOff_AndWhenNothingIsForThisPlayer()
        {
            Assert.AreEqual(WhatsNewPlanState.Retire, WhatsNewPlanner.Lead(Sample, Updated() with { Enabled = false }).State,
                "switched off still counts the release as told: switching it on later brings no old news");
            Assert.AreEqual(WhatsNewPlanState.Retire,
                WhatsNewPlanner.Lead(Sample, Updated() with { Answered = _ => true, FeatureOn = _ => false }).State);
            Assert.AreEqual(WhatsNewPlanState.None, WhatsNewPlanner.Lead(WhatsNewCatalog.Empty, Updated()).State);
        }

        [TestMethod]
        public void List_ThePlayerOpensIt_AndSeesEverythingForTheirGame_AnsweredOrNot()
        {
            var list = WhatsNewPlanner.List(Sample, Updated(new Version(2, 6, 1)) with { Answered = _ => true });
            CollectionAssert.AreEqual(new[] { "stamina", "day", "windows", "recordings" }, list.Select(r => r.Id).ToList());
        }

        [TestMethod]
        public void Rows_FillTheWords_AndDropARowWithAHole()
        {
            var entries = WhatsNewCatalog.Parse(ShippedJson(), out _).Entries;
            var values = new Dictionary<string, string>
            {
                ["game"] = "Genshin Impact", ["stamina"] = "Original Resin", ["staminaLead"] = "30 min",
                ["staminaWhere"] = "It shows on the map.",
            };
            foreach (bool polish in new[] { false, true })
            {
                var rows = WhatsNewRows.Build(entries, polish, values, f => f == WhatsNewFeatures.DayNotice);
                Assert.AreEqual(entries.Count, rows.Count);
                foreach (WhatsNewRow r in rows)
                {
                    Assert.IsFalse(r.Title.Contains('{') || r.Body.Contains('{') || (r.Done ?? "").Contains('{'), r.Entry.Id);
                    Assert.AreEqual(r.Entry.Feature == WhatsNewFeatures.DayNotice, r.FeatureOn, r.Entry.Id);
                }
                Assert.IsTrue(rows[0].Title.Contains("Original Resin"));
            }
            // A game without a pool: the stamina row cannot be worded, so it is not there.
            var bare = WhatsNewRows.Build(entries, false, new Dictionary<string, string> { ["game"] = "Zenless Zone Zero" }, _ => false);
            Assert.IsFalse(bare.Any(r => r.Entry.Feature == WhatsNewFeatures.StaminaNotice));
            Assert.IsTrue(bare.Any(r => r.Entry.Feature == WhatsNewFeatures.StartWithWindows));
        }

        [TestMethod]
        public void App_EachGameIsToldOnlyWhatItHas()
        {
            GameManifest genshin = GameRegistry.Current.FindByWireId("genshin"), zzz = GameRegistry.Current.FindByWireId("zzz"),
                hsr = GameRegistry.Current.FindByWireId("starrail");
            Assert.IsTrue(GI_Subtitles.Views.SettingsWindow.WhatsNewMeets(WhatsNewRequirements.StaminaScreen, genshin));
            Assert.IsTrue(GI_Subtitles.Views.SettingsWindow.WhatsNewMeets(WhatsNewRequirements.StaminaScreen, hsr));
            Assert.IsFalse(GI_Subtitles.Views.SettingsWindow.WhatsNewMeets(WhatsNewRequirements.StaminaScreen, zzz), "its screen is not measured");
            // Every listed game has a card of the day since the Today card.
            foreach (GameManifest game in GameRegistry.Current.Listed)
                Assert.IsTrue(GI_Subtitles.Views.SettingsWindow.WhatsNewMeets(WhatsNewRequirements.TodayCard, game), game.Id);
            Assert.IsFalse(GI_Subtitles.Views.SettingsWindow.WhatsNewMeets(WhatsNewRequirements.TodayCard, null));
            Assert.IsFalse(GI_Subtitles.Views.SettingsWindow.WhatsNewMeets("full-moon", genshin));

            var values = GI_Subtitles.Views.SettingsWindow.WhatsNewValues(genshin, Fallback, En);
            Assert.AreEqual(genshin.Name, values["game"]);
            Assert.AreEqual(genshin.Stamina.Name, values["stamina"]);
            Assert.AreEqual("30 min", values["staminaLead"], "the lead time the notice really uses, not a number in the text");
            Assert.IsFalse(string.IsNullOrWhiteSpace(values["staminaWhere"]));
            var bare = GI_Subtitles.Views.SettingsWindow.WhatsNewValues(zzz, Fallback, En);
            Assert.IsTrue(bare.ContainsKey("game") && !bare.ContainsKey("stamina"), "no pool to name: rows that name one are left out");
            Assert.AreEqual(0, GI_Subtitles.Views.SettingsWindow.WhatsNewValues(null, Fallback, En).Count);

            // Every shipped entry can be worded for the game it applies to, in both languages.
            foreach (GameManifest game in GameRegistry.Current.Listed)
            {
                var inputs = new WhatsNewInputs { Game = game.Id, Meets = r => GI_Subtitles.Views.SettingsWindow.WhatsNewMeets(r, game) };
                var applies = WhatsNewPlanner.List(WhatsNewCatalog.Default, inputs);
                foreach (bool polish in new[] { false, true })
                    Assert.AreEqual(applies.Count,
                        WhatsNewRows.Build(applies, polish, GI_Subtitles.Views.SettingsWindow.WhatsNewValues(game, Fallback, En), _ => false).Count, game.Id);
            }
        }

        [TestMethod]
        public void App_WhoIsANewInstall()
        {
            Assert.IsTrue(GI_Subtitles.Views.SettingsWindow.WhatsNewIsFreshInstall(setupRanThisLaunch: true, setupCompleted: true, setupDismissed: false),
                "the setup ran in this launch: they have just been shown everything");
            Assert.IsTrue(GI_Subtitles.Views.SettingsWindow.WhatsNewIsFreshInstall(false, false, false), "the setup has not run yet");
            Assert.IsFalse(GI_Subtitles.Views.SettingsWindow.WhatsNewIsFreshInstall(false, true, false), "a player who updated");
            Assert.IsFalse(GI_Subtitles.Views.SettingsWindow.WhatsNewIsFreshInstall(false, false, true),
                "closed the setup guide and plays anyway: an update is news to them too");
        }

        [TestMethod]
        public void Tray_ANoticeNeedsAnIconInTheTray()
        {
            // The tray's own call says nothing when there is no icon: the app's wrapper reports it, so a caller that
            // remembers "shown" does not on a notice that never went out.
            Assert.IsTrue(GI_Subtitles.Core.UI.INotifyIcon.CanShowBalloon(hasIcon: true, iconVisible: true));
            Assert.IsFalse(GI_Subtitles.Core.UI.INotifyIcon.CanShowBalloon(hasIcon: true, iconVisible: false));
            Assert.IsFalse(GI_Subtitles.Core.UI.INotifyIcon.CanShowBalloon(hasIcon: false, iconVisible: false));
            Assert.IsFalse(new GI_Subtitles.Core.UI.INotifyIcon().ShowBalloonTip("t", "b"), "no tray icon built: not shown, and it says so");
        }

        // ── What is remembered ──────────────────────────────────────────

        [TestMethod]
        public void Memory_RemembersAnswersAndTheLevel()
        {
            string[] keys =
            {
                ConfigWhatsNewMemory.EnabledKey, ConfigWhatsNewMemory.SeenThroughKey, ConfigWhatsNewMemory.AnswersKey, ConfigWhatsNewMemory.TrayToldKey,
            };
            foreach (string k in keys) if (AppConfig.Has(k)) AppConfig.Remove(k);
            try
            {
                var memory = new ConfigWhatsNewMemory();
                Assert.IsTrue(memory.Enabled, "on unless the player turns it off");
                Assert.IsNull(memory.SeenThrough);
                Assert.IsNull(memory.TrayTold);
                Assert.IsNull(memory.AnswerOf(WhatsNewFeatures.StaminaNotice));

                memory.Answer(WhatsNewFeatures.StaminaNotice, WhatsNewAnswer.Later);
                memory.Answer(WhatsNewFeatures.StartWithWindows, WhatsNewAnswer.Never);
                memory.Answer(WhatsNewFeatures.ShowInRecordings, WhatsNewAnswer.On);
                Assert.AreEqual(WhatsNewAnswer.Later, memory.AnswerOf(WhatsNewFeatures.StaminaNotice));
                Assert.AreEqual(WhatsNewAnswer.Never, memory.AnswerOf(WhatsNewFeatures.StartWithWindows));
                Assert.AreEqual(WhatsNewAnswer.On, memory.AnswerOf(WhatsNewFeatures.ShowInRecordings));
                Assert.IsNull(memory.AnswerOf(WhatsNewFeatures.DayNotice));
                // A second answer replaces the first; the list does not grow.
                memory.Answer(WhatsNewFeatures.StaminaNotice, WhatsNewAnswer.On);
                Assert.AreEqual(WhatsNewAnswer.On, memory.AnswerOf(WhatsNewFeatures.StaminaNotice));
                Assert.AreEqual(3, AppConfig.Get<string[]>(ConfigWhatsNewMemory.AnswersKey, null).Length);
                // One value per answer: a stop at any moment leaves the whole answer or none of it.
                CollectionAssert.Contains(AppConfig.Get<string[]>(ConfigWhatsNewMemory.AnswersKey, null), "start-with-windows=never");

                var level = new Version(2, 6, 1);
                memory.SeenThrough = level;
                memory.TrayTold = level;
                Assert.AreEqual(level, new ConfigWhatsNewMemory().SeenThrough);
                Assert.AreEqual(level, new ConfigWhatsNewMemory().TrayTold);

                // The whole circle: the answers above, read back through the plan for a profile not yet shown the card.
                var lead = WhatsNewPlanner.Lead(Sample, new WhatsNewInputs
                {
                    Game = "genshin", Meets = _ => true, FeatureOn = f => f == WhatsNewFeatures.DayNotice,
                    Answered = f => memory.AnswerOf(f) != null, SeenThrough = null,
                });
                CollectionAssert.AreEqual(new[] { "day" }, lead.Rows.Select(r => r.Id).ToList());
            }
            finally
            {
                foreach (string k in keys) if (AppConfig.Has(k)) AppConfig.Remove(k);
            }
        }

        [TestMethod]
        public void Memory_AnswersParse_IsStrict()
        {
            string[] stored = { "stamina-notice=later", "nonsense", "start-with-windows=maybe", null, "stamina-notice=never", "=on" };
            Assert.AreEqual(WhatsNewAnswer.Never, ConfigWhatsNewMemory.Find(stored, "stamina-notice"), "the last answer wins");
            Assert.IsNull(ConfigWhatsNewMemory.Find(stored, "start-with-windows"), "an answer that cannot be read is no answer");
            Assert.IsNull(ConfigWhatsNewMemory.Find(stored, "day-notice"));
            Assert.IsNull(ConfigWhatsNewMemory.Find(stored, ""));
            Assert.IsNull(ConfigWhatsNewMemory.Find(null, "stamina-notice"));
            CollectionAssert.AreEqual(new[] { "nonsense", "start-with-windows=maybe", "=on", "stamina-notice=on" },
                ConfigWhatsNewMemory.With(stored, "stamina-notice", WhatsNewAnswer.On));
        }

        // ── One notice per start ────────────────────────────────────────

        [TestMethod]
        public void Start_OneNoticeAtMost_NewsFirst_TheTrayOnlyEverTheTray()
        {
            Assert.AreEqual(StartNoticeKind.NewsCard, StartNoticePlanner.Pick(newsWaiting: true, windowShown: true, trayToldNews: false));
            Assert.AreEqual(StartNoticeKind.NewsCard, StartNoticePlanner.Pick(newsWaiting: true, windowShown: true, trayToldNews: true));
            Assert.AreEqual(StartNoticeKind.NewsTray, StartNoticePlanner.Pick(newsWaiting: true, windowShown: false, trayToldNews: false));
            Assert.AreEqual(StartNoticeKind.Day, StartNoticePlanner.Pick(newsWaiting: true, windowShown: false, trayToldNews: true),
                "the tray said it once; the card waits on Start and the day's notice has the start");
            Assert.AreEqual(StartNoticeKind.Day, StartNoticePlanner.Pick(newsWaiting: false, windowShown: true, trayToldNews: false));
            Assert.AreEqual(StartNoticeKind.Day, StartNoticePlanner.Pick(newsWaiting: false, windowShown: false, trayToldNews: false));
        }

        [TestMethod]
        public void Day_OncePerGameDay_NeverOverAGame_AndNotOnAStartThatHadItsNotice()
        {
            const string today = "2026-11-11", yesterday = "2026-11-10";
            Assert.AreEqual(DayNoticeStep.Show, DayNoticeRule.Next(true, yesterday, today, null, otherWaits: false, mayInterrupt: true));
            Assert.AreEqual(DayNoticeStep.Show, DayNoticeRule.Next(true, null, today, null, false, true), "the first day ever");
            Assert.AreEqual(DayNoticeStep.Idle, DayNoticeRule.Next(true, today, today, null, false, true), "a second start the same day");
            Assert.AreEqual(DayNoticeStep.Idle, DayNoticeRule.Next(false, yesterday, today, null, false, true), "switched off");
            Assert.AreEqual(DayNoticeStep.Wait, DayNoticeRule.Next(true, yesterday, today, null, false, mayInterrupt: false),
                "subtitles run or a game is in front: later, never over it");
            Assert.AreEqual(DayNoticeStep.Wait, DayNoticeRule.Next(true, yesterday, today, null, otherWaits: true, mayInterrupt: true),
                "the new story's notice goes first");
            Assert.AreEqual(DayNoticeStep.StartSpent, DayNoticeRule.Next(true, yesterday, today, startSpentOn: today, false, true),
                "the news card was this start's notice");
            Assert.AreEqual(DayNoticeStep.Show, DayNoticeRule.Next(true, yesterday, today, startSpentOn: yesterday, false, true),
                "Kaption kept running into a new game day: that day has its own notice");
            Assert.AreEqual(DayNoticeStep.Idle, DayNoticeRule.Next(true, yesterday, null, null, false, true));
        }

        [TestMethod]
        public void Day_AClockSetBack_DoesNotRepeatADay()
        {
            const string a = "2026-11-10", b = "2026-11-11", c = "2026-11-12";
            // A was told, then B. The clock goes back to A: nothing, and nothing again when it reaches B.
            Assert.AreEqual(DayNoticeStep.Idle, DayNoticeRule.Next(true, lastDay: b, dayKey: a, null, false, true));
            Assert.AreEqual(DayNoticeStep.Idle, DayNoticeRule.Next(true, lastDay: b, dayKey: b, null, false, true));
            Assert.AreEqual(DayNoticeStep.Show, DayNoticeRule.Next(true, lastDay: b, dayKey: c, null, false, true), "the next real day has its notice");
            // The news card was this run's notice on B, nothing told before. The clock goes back to A: still spent.
            Assert.AreEqual(DayNoticeStep.StartSpent, DayNoticeRule.Next(true, lastDay: null, dayKey: a, startSpentOn: b, false, true));
            Assert.AreEqual(DayNoticeStep.StartSpent, DayNoticeRule.Next(true, lastDay: null, dayKey: b, startSpentOn: b, false, true));
            Assert.AreEqual(DayNoticeStep.Show, DayNoticeRule.Next(true, lastDay: null, dayKey: c, startSpentOn: b, false, true));
        }

        // ── What the notice of the day says ─────────────────────────────

        [TestMethod]
        public void Books_ForThePlayersCharacters_NamedWithWhoTheyAreFor()
        {
            FarmCardModel farm = FarmCardSamples.Model(FarmCardSamples.Wednesday, hasUid: true);
            Assert.AreEqual(FarmPlayerState.Rows, farm.Player);
            string line = DayNoticeContent.Books(farm, Fallback, En);
            StringAssert.StartsWith(line, "Books for your characters: ");
            foreach (FarmBook book in farm.Books.Where(b => b.Mine > 0).Take(DayNoticeContent.MaxSeries))
                StringAssert.Contains(line, book.Series + " (");
            Assert.IsTrue(farm.Rows.Any(r => line.Contains(r.Name)), "a character is named");
            Assert.IsFalse(farm.Books.Where(b => b.Mine == 0).Any(b => line.Contains(b.Series + " (")), "only the books somebody needs");
        }

        [TestMethod]
        public void Books_WithoutAUid_NameWhatIsOpen_AndSayHowToGetNames()
        {
            FarmCardModel farm = FarmCardSamples.Model(FarmCardSamples.Wednesday, hasUid: false);
            string line = DayNoticeContent.Books(farm, Fallback, En);
            StringAssert.StartsWith(line, "Talent books open today: ");
            foreach (FarmBook book in farm.Books) StringAssert.Contains(line, book.Series);
            StringAssert.EndsWith(line, "Save your UID in Kaption to see who they are for.");
            // A showcase that could not be read: the open books, without the invitation (the UID is there).
            string noRoster = DayNoticeContent.Books(FarmCardSamples.Model(FarmCardSamples.Wednesday, hasUid: true, emptyShowcase: true), Fallback, En);
            StringAssert.StartsWith(noRoster, "Talent books open today: ");
            Assert.IsFalse(noRoster.Contains("UID"));
        }

        [TestMethod]
        public void Books_OnSunday_SayEverythingIsOpen_OrWhoNeedsWhat()
        {
            Assert.AreEqual("Sunday: every talent book Domain is open.",
                DayNoticeContent.Books(FarmCardSamples.Model(FarmCardSamples.Sunday, hasUid: false), Fallback, En));
            StringAssert.StartsWith(DayNoticeContent.Books(FarmCardSamples.Model(FarmCardSamples.Sunday, hasUid: true), Fallback, En),
                "Books for your characters: ");
            Assert.IsNull(DayNoticeContent.Books(null, Fallback, En));
        }

        [TestMethod]
        public void Stamina_IsSaidOnlyWhenItMattersToday()
        {
            var now = new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
            DateTimeOffset dayEnds = now.AddHours(18);
            GameManifest genshin = GameRegistry.Current.FindByWireId("genshin");
            GameStamina resin = genshin.Stamina;
            // The Today card's own estimate: the notice and the card read one number.
            string Said(StaminaReading reading) =>
                DayNoticeContent.Stamina(TodayCardBuilder.Stamina(genshin, reading, null, now, "en-US"), now, dayEnds, Fallback, En);

            // Full while Kaption was closed: the notice that fell due overnight was dropped, so the day's notice says it.
            StringAssert.StartsWith(Said(new StaminaReading(resin.Max - 10, now.AddHours(-20), 0)), resin.Name + " is at the cap; it filled ");
            // Read longer ago than a fill from zero takes: at the cap, without a moment nobody can know.
            Assert.AreEqual(resin.Name + " has been at the cap for a while.", Said(new StaminaReading(5, now.AddDays(-3), 0)));
            // Fills before the game day ends: worth knowing now.
            StringAssert.StartsWith(Said(new StaminaReading(resin.Max - 30, now, resin.RegenSeconds)),
                $"{resin.Name}: about {resin.Max - 30}/{resin.Max}, at the cap ");
            // A day and more away: not today's business.
            Assert.IsNull(Said(new StaminaReading(0, now, 0)));
            // No reading, or a reading from the future (a clock that moved): nothing, never a guess.
            Assert.IsNull(Said(null));
            Assert.IsNull(Said(new StaminaReading(resin.Max, now.AddMinutes(10), 0)));
            Assert.IsNull(DayNoticeContent.Stamina(null, now, dayEnds, Fallback, En));
        }

        [TestMethod]
        public void Games_TheSelectedOne_AndThoseReadLately()
        {
            var now = new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
            var listed = GameRegistry.Current.Listed;
            var read = new Dictionary<string, DateTimeOffset>
            {
                ["genshin"] = now.AddDays(-2),                                   // played lately with the reader on
                ["wuwa"] = now.AddDays(-30),                                     // long ago
                ["zzz"] = now.AddMinutes(5),                                     // a clock that moved: not trusted
            };
            var games = DayNoticeContent.Games(listed, "starrail", id => read.TryGetValue(id, out var at) ? at : null, now);
            CollectionAssert.AreEqual(new[] { "starrail", "genshin" }, games.Select(g => g.Id).ToList(), "the selected game first");
            CollectionAssert.AreEqual(new[] { "genshin" }, DayNoticeContent.Games(listed, "Genshin", _ => null, now).Select(g => g.Id).ToList());
            Assert.AreEqual(0, DayNoticeContent.Games(listed, "nothing", _ => null, now).Count);
        }

        [TestMethod]
        public void Notice_SpeaksTheAppsLanguage_NotWindows()
        {
            // Seen on a real run: English Kaption on a Polish Windows said "Genshin Impact today, wtorek".
            Assert.AreEqual("Tuesday", DayNoticeContent.CultureFor("en-US").DateTimeFormat.GetDayName(DayOfWeek.Tuesday));
            Assert.AreEqual("wtorek", DayNoticeContent.CultureFor("pl-PL").DateTimeFormat.GetDayName(DayOfWeek.Tuesday));
            Assert.AreEqual("en-US", DayNoticeContent.CultureFor(null).Name, "anything else reads as English, like the interface");
            var text = DayNoticeBuilder.Build(new[] { new DayNoticeSection("genshin", "Genshin Impact", new[] { "A." }, true) },
                DayOfWeek.Tuesday, Fallback, DayNoticeContent.CultureFor("en-US"));
            Assert.AreEqual("Genshin Impact today, Tuesday", text.Title);
        }

        [TestMethod]
        public void Games_AGameReadLately_StillCounts_WithTheReaderSwitchedOff()
        {
            const string enabled = StaminaSettings.EnabledKey, reading = "Stamina.Reading.genshin", seen = "Stamina.Seen.genshin";
            foreach (string k in new[] { enabled, reading, seen }) if (AppConfig.Has(k)) AppConfig.Remove(k);
            try
            {
                var now = DateTimeOffset.UtcNow;
                Assert.IsNull(GI_Subtitles.Views.MainWindow.LastPlayedWithKaption("genshin"), "never read");
                StaminaSettings.SaveReading("genshin", new StaminaReading(120, now.AddDays(-2), 100));
                AppConfig.Set(enabled, false);
                DateTimeOffset? played = GI_Subtitles.Views.MainWindow.LastPlayedWithKaption("genshin");
                Assert.IsNotNull(played, "the reader is off, the reading is still the sign that the game is played");
                // Star Rail selected: Genshin is still one of the player's games, so its books still reach them.
                var games = DayNoticeContent.Games(GameRegistry.Current.Listed, "starrail", GI_Subtitles.Views.MainWindow.LastPlayedWithKaption, now);
                CollectionAssert.AreEqual(new[] { "starrail", "genshin" }, games.Select(g => g.Id).ToList());
                StaminaSettings.SaveSeen("genshin", now.AddHours(-1));
                Assert.IsTrue(GI_Subtitles.Views.MainWindow.LastPlayedWithKaption("genshin") > played, "a later sighting is the newer sign");
            }
            finally
            {
                foreach (string k in new[] { enabled, reading, seen }) if (AppConfig.Has(k)) AppConfig.Remove(k);
            }
        }

        [TestMethod]
        public void Notice_OneGame_TitleNamesItAndTheDay()
        {
            var text = DayNoticeBuilder.Build(new[]
            {
                new DayNoticeSection("genshin", "Genshin Impact", new[] { "Books for your characters: Gold (Zhongli, Xingqiu).", "Original Resin is at the cap; it filled at 06:10." }, true),
            }, DayOfWeek.Wednesday, Fallback, En);
            Assert.AreEqual("Genshin Impact today, Wednesday", text.Title);
            Assert.AreEqual("Books for your characters: Gold (Zhongli, Xingqiu). Original Resin is at the cap; it filled at 06:10.", text.Body);
            Assert.IsTrue(text.OpensCard);
        }

        [TestMethod]
        public void Notice_SeveralGames_EachOnItsLine_AndNothingMeansNoNotice()
        {
            var text = DayNoticeBuilder.Build(new[]
            {
                new DayNoticeSection("genshin", "Genshin Impact", new[] { "Talent books open today: Freedom, Gold." }, true),
                new DayNoticeSection("zzz", "Zenless Zone Zero", Array.Empty<string>(), false),
                new DayNoticeSection("starrail", "Honkai: Star Rail", new[] { " ", "Trailblaze Power is at the cap; it filled at 03:00." }, false),
            }, DayOfWeek.Friday, Fallback, En);
            Assert.AreEqual("Today in your games, Friday", text.Title);
            Assert.AreEqual("Genshin Impact: Talent books open today: Freedom, Gold.\nHonkai: Star Rail: Trailblaze Power is at the cap; it filled at 03:00.", text.Body);

            Assert.IsNull(DayNoticeBuilder.Build(Array.Empty<DayNoticeSection>(), DayOfWeek.Friday, Fallback, En));
            Assert.IsNull(DayNoticeBuilder.Build(new[] { new DayNoticeSection("zzz", "Zenless Zone Zero", new[] { "", null }, false) }, DayOfWeek.Friday, Fallback, En));
            Assert.IsFalse(DayNoticeBuilder.Build(new[] { new DayNoticeSection("starrail", "Honkai: Star Rail", new[] { "A." }, false) },
                DayOfWeek.Friday, Fallback, En).OpensCard);

            // A game whose sentence did not fit says nothing, so its card is not what a click opens.
            var dropped = DayNoticeBuilder.Build(new[]
            {
                new DayNoticeSection("starrail", "Honkai: Star Rail", new[] { new string('s', 90) + "." }, false),
                new DayNoticeSection("genshin", "Genshin Impact", new[] { new string('g', 180) + "." }, true),
            }, DayOfWeek.Friday, Fallback, En);
            Assert.IsFalse(dropped.Body.Contains("Genshin"));
            Assert.IsFalse(dropped.OpensCard, "the notice speaks of Star Rail only: the click opens Start");
        }

        [TestMethod]
        public void Notice_FitsWindowsLimits_InWholeSentences_EveryGameFirst()
        {
            string longLine = "Books for your characters: " + string.Join(", ", Enumerable.Range(0, 12).Select(i => $"Series{i} (Name{i})")) + ".";
            Assert.IsTrue(longLine.Length > 200 && longLine.Length < DayNoticeBuilder.MaxBody);
            var text = DayNoticeBuilder.Build(new[]
            {
                new DayNoticeSection("genshin", "Genshin Impact", new[] { longLine, "Original Resin is at the cap; it filled at 06:10." }, true),
                new DayNoticeSection("starrail", "Honkai: Star Rail", new[] { "Trailblaze Power is at the cap." }, false),
            }, DayOfWeek.Friday, Fallback, En);
            Assert.IsTrue(text.Body.Length <= DayNoticeBuilder.MaxBody, text.Body.Length.ToString());
            StringAssert.Contains(text.Body, "Series11 (Name11).", "the first sentence is whole");
            Assert.IsFalse(text.Body.Contains("Original Resin"), "the second sentence did not fit and is left to the card");
            Assert.IsFalse(text.Body.Contains("Trailblaze"), "nor did the other game's, whole");
            Assert.IsFalse(text.Body.EndsWith("…"));

            // Both games fit when the first sentence is short: the other game's first comes before this game's second.
            var both = DayNoticeBuilder.Build(new[]
            {
                new DayNoticeSection("genshin", "Genshin Impact", new[] { "Short.", new string('x', 220) + "." }, true),
                new DayNoticeSection("starrail", "Honkai: Star Rail", new[] { "Trailblaze Power is at the cap." }, false),
            }, DayOfWeek.Friday, Fallback, En);
            Assert.AreEqual("Genshin Impact: Short.\nHonkai: Star Rail: Trailblaze Power is at the cap.", both.Body);

            // One sentence longer than the limit on its own is cut, visibly.
            var cut = DayNoticeBuilder.Build(new[] { new DayNoticeSection("genshin", "Genshin Impact", new[] { new string('y', 400) }, true) },
                DayOfWeek.Friday, Fallback, En);
            Assert.AreEqual(DayNoticeBuilder.MaxBody, cut.Body.Length);
            StringAssert.EndsWith(cut.Body, "…");

            var title = DayNoticeBuilder.Build(new[] { new DayNoticeSection("x", new string('G', 80), new[] { "A." }, false) }, DayOfWeek.Friday, Fallback, En);
            Assert.AreEqual(DayNoticeBuilder.MaxTitle, title.Title.Length);
        }

        [TestMethod]
        public void Notice_ARealWednesday_InPolish_FitsAndNamesTheBooks()
        {
            RenderTestKit.RunSta(() =>
            {
                var pl = new CultureInfo("pl-PL");
                var strings = RenderTestKit.Strings("pl-PL");
                string L(string key, string fallback) => strings.Select(d => d[key] as string).LastOrDefault(s => s != null) ?? fallback;
                FarmCardModel farm = FarmCardSamples.Model(FarmCardSamples.Wednesday, hasUid: true);
                string books = DayNoticeContent.Books(farm, L, pl);
                var text = DayNoticeBuilder.Build(new[] { new DayNoticeSection("genshin", "Genshin Impact", new[] { books }, true) }, farm.Weekday, L, pl);
                Assert.AreEqual("Genshin Impact dziś, środa", text.Title);
                StringAssert.StartsWith(text.Body, "Książki dla Twoich postaci: ");
                Assert.IsTrue(text.Body.Length <= DayNoticeBuilder.MaxBody);
                // Without a UID: the open series, every one of them, still inside the limit.
                string open = DayNoticeContent.Books(FarmCardSamples.Model(FarmCardSamples.Wednesday, hasUid: false), L, pl);
                Assert.IsTrue(open.Length <= DayNoticeBuilder.MaxBody, open.Length.ToString());
            });
        }
    }
}
