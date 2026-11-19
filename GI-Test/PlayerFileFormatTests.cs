// The JSON files shipped builds wrote and players keep on disk, pinned as this build reads and writes them: Config.json
// (from the 2.3.0 Newtonsoft writer to today's), overrides.<lang>.json, community.<lang>.json, community-queue.json,
// assist.json and the showcase cache. A player's settings or cached data failing to load after an update is the one
// real risk of a JSON library change. The samples are in Fixtures/player-files, written from each writer's code.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Community;
using GI_Subtitles.Services.Today;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AppConfig = GI_Subtitles.Core.Config.Config;

namespace GI_Test
{
    [TestClass]
    [DoNotParallelize] // the Config tests write the run's one settings file
    public class PlayerFileFormatTests
    {
        private string _dir;

        [TestInitialize]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "kaption-tests", "player-files-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void TearDown()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        internal static string Fixture(string name)
        {
            string root = RenderTestKit.RepoRoot() ?? throw new AssertInconclusiveException("Repository root not found.");
            return File.ReadAllText(Path.Combine(root, "GI-Test", "Fixtures", "player-files", name));
        }

        /// <summary>Line endings depend on the checkout and the writer's platform; the content is what is pinned.</summary>
        internal static string Lf(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');

        private string Temp(string name, string text, bool bom = false)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, text, new UTF8Encoding(bom));
            return path;
        }

        private static Dictionary<string, JsonNode> ReadConfig(string path)
        {
            var into = new Dictionary<string, JsonNode>();
            Assert.AreEqual(AppConfig.LoadOutcome.Loaded, AppConfig.ReadSettings(path, false, into, new object()));
            return into;
        }

        private static T Value<T>(Dictionary<string, JsonNode> settings, string key, T fallback = default) =>
            settings.TryGetValue(key, out var node) ? AppConfig.ValueOf(node, key, fallback) : fallback;

        // ── Config.json ──────────────────────────────────────────────────

        /// <summary>A Config.json the 2.3.0 Newtonsoft writer left (no migration has run on it) reads value for value.</summary>
        [TestMethod]
        public void Config_TheNewtonsoftEraFile_ReadsEveryValue()
        {
            var s = ReadConfig(Temp("Config.json", Fixture("config-2.3.0-newtonsoft.json")));

            Assert.AreEqual(16, s.Count);
            Assert.AreEqual("Genshin", Value<string>(s, "Game"));
            Assert.AreEqual("zh-CN", Value<string>(s, "UILang"));
            Assert.AreEqual("560,860,800,140", Value<string>(s, "Region"));
            CollectionAssert.AreEqual(new[] { 10, 20 }, Value<int[]>(s, "Pad"));
            Assert.AreEqual(28, Value<int>(s, "Size"));
            Assert.AreEqual(200, Value<int>(s, "OcrInterval"));
            Assert.AreEqual(1.0, Value<double>(s, "ForceOcrAfterSeconds"));
            Assert.IsTrue(Value<bool>(s, "UseSymSpell"));
            Assert.IsFalse(Value(s, "AutoShrinkText", true));
            Assert.AreEqual(1755600000L, Value<long>(s, "UpdateSkippedAtUnix"));
            Assert.AreEqual("2026-08-01T10:15:30.1234567Z", Value<string>(s, "OcrGpuQuarantineUtc"), "a date-like string stays a string");
            CollectionAssert.AreEqual(new[] { "today=yes", "fix=later" }, Value<string[]>(s, "WhatsNew.Answers"));
            Assert.AreEqual("Zażółć gęślą jaźń — „cytat” <b>&</b>", Value<string>(s, "OutputSample"));
            Assert.AreEqual(2.5, Value<JsonNode>(s, "FutureFeature")["levels"][1].GetValue<double>(), "a key this build does not know is kept whole");
            Assert.IsTrue(s.ContainsKey("Cleared") && s["Cleared"] == null, "null is kept as present (Config.Has is true)");
            Assert.AreEqual("fallback", Value(s, "Cleared", "fallback"), "and reads as the default");
        }

        /// <summary>Re-saving a file this build only read changes nothing: values it never set are written as they were read.</summary>
        [TestMethod]
        public void Config_ResavingAnOldFile_WritesItBackAsItWas()
        {
            string text = Fixture("config-2.3.0-newtonsoft.json");
            var s = ReadConfig(Temp("Config.json", text));
            Assert.AreEqual(Lf(text), Lf(AppConfig.ToText(s)));
        }

        /// <summary>What Set writes: two-space indents, one array item a line, Polish and &lt;&gt;&amp; unescaped, a whole double without ".0".</summary>
        [TestMethod]
        public void Config_TheWriter_WritesTheseExactLines()
        {
            var values = new (string Key, object Value)[]
            {
                ("PlayerFileFormatTests.Int", 28),
                ("PlayerFileFormatTests.Double", 1.0),
                ("PlayerFileFormatTests.Fraction", 2.5),
                ("PlayerFileFormatTests.Long", 1755600000L),
                ("PlayerFileFormatTests.Bool", true),
                ("PlayerFileFormatTests.Text", "Zażółć gęślą jaźń — „cytat” <b>&</b> \"q\" \\ \t"),
                ("PlayerFileFormatTests.Array", new[] { "today=yes", "fix=later" }),
                ("PlayerFileFormatTests.Pad", new[] { 10, 20 }),
            };
            try
            {
                AppConfig.SetMany(values.Select(v => new KeyValuePair<string, object>(v.Key, v.Value)));
                var onDisk = ReadConfig(AppConfig.SettingsFilePath);
                string mine = AppConfig.ToText(values.Select(v => new KeyValuePair<string, JsonNode>(v.Key, onDisk[v.Key])));
                Assert.AreEqual(Lf("""
                    {
                      "PlayerFileFormatTests.Int": 28,
                      "PlayerFileFormatTests.Double": 1,
                      "PlayerFileFormatTests.Fraction": 2.5,
                      "PlayerFileFormatTests.Long": 1755600000,
                      "PlayerFileFormatTests.Bool": true,
                      "PlayerFileFormatTests.Text": "Zażółć gęślą jaźń — „cytat” <b>&</b> \"q\" \\ \t",
                      "PlayerFileFormatTests.Array": [
                        "today=yes",
                        "fix=later"
                      ],
                      "PlayerFileFormatTests.Pad": [
                        10,
                        20
                      ]
                    }
                    """), Lf(mine));
            }
            finally
            {
                AppConfig.SetMany(values.Select(v => new KeyValuePair<string, object>(v.Key, null)));
            }
        }

        /// <summary>
        /// A player who rolls back gets 2.6.2's reader on the file this build wrote. That reader is frozen below as it
        /// shipped (Config.cs and JsonDefaults.cs at the v2.6.2 tag); every value must come back the same.
        /// </summary>
        [TestMethod]
        public void Config_WhatThisBuildWrites_TheLastReleasesReaderReads()
        {
            var values = new KeyValuePair<string, object>[]
            {
                new("PlayerFileFormatTests.Game", "StarRail"),
                new("PlayerFileFormatTests.Size", 31),
                new("PlayerFileFormatTests.Seconds", 1.5),
                new("PlayerFileFormatTests.Unix", 1763280000L),
                new("PlayerFileFormatTests.On", true),
                new("PlayerFileFormatTests.Text", "Dziękuję — „cytat” <b>&</b>"),
                new("PlayerFileFormatTests.Answers", new[] { "a=1", "ż=2" }),
                new("PlayerFileFormatTests.Pad", new[] { 4, 8 }),
            };
            try
            {
                AppConfig.SetMany(values);
                var old = Release262ConfigReader.Load(File.ReadAllText(AppConfig.SettingsFilePath));
                Assert.AreEqual("StarRail", Release262ConfigReader.Get<string>(old, "PlayerFileFormatTests.Game"));
                Assert.AreEqual(31, Release262ConfigReader.Get<int>(old, "PlayerFileFormatTests.Size"));
                Assert.AreEqual(1.5, Release262ConfigReader.Get<double>(old, "PlayerFileFormatTests.Seconds"));
                Assert.AreEqual(1763280000L, Release262ConfigReader.Get<long>(old, "PlayerFileFormatTests.Unix"));
                Assert.IsTrue(Release262ConfigReader.Get<bool>(old, "PlayerFileFormatTests.On"));
                Assert.AreEqual("Dziękuję — „cytat” <b>&</b>", Release262ConfigReader.Get<string>(old, "PlayerFileFormatTests.Text"));
                CollectionAssert.AreEqual(new[] { "a=1", "ż=2" }, Release262ConfigReader.Get<string[]>(old, "PlayerFileFormatTests.Answers"));
                CollectionAssert.AreEqual(new[] { 4, 8 }, Release262ConfigReader.Get<int[]>(old, "PlayerFileFormatTests.Pad"));
            }
            finally
            {
                AppConfig.SetMany(values.Select(v => new KeyValuePair<string, object>(v.Key, null)));
            }
        }

        /// <summary>
        /// Config.json is hand-editable: comments, trailing commas and a BOM read; a quoted number reads; a quoted bool,
        /// a fraction for a whole number and a number for text read as the default; key names are case-sensitive.
        /// </summary>
        [TestMethod]
        public void Config_HandEditedFile_ReadsAsPinned()
        {
            string json = """
                {
                  // edited in Notepad
                  "Game": "ZZZ",
                  "game": "Genshin",
                  "Size": "30",
                  "OcrInterval": 120.0,
                  "MaxOverlayWidth": 999.6,
                  "UseSymSpell": "true",
                  "AutoShrinkText": 1,
                  "Region": 5,
                  "MaxOverlayHeight": null,
                  "Pad": [ 3, ],
                  /* trailing comma below */
                  "Debug": true,
                }
                """;
            var s = ReadConfig(Temp("Config.json", json, bom: true));
            Assert.AreEqual("ZZZ", Value<string>(s, "Game"), "keys are case-sensitive: \"game\" is another key");
            Assert.AreEqual("Genshin", Value<string>(s, "game"));
            Assert.AreEqual(30, Value<int>(s, "Size"), "a quoted number reads");
            Assert.AreEqual(-1, Value(s, "OcrInterval", -1), "120.0 is not an int: the default");
            Assert.AreEqual(-1, Value(s, "MaxOverlayWidth", -1), "nor is 999.6 (no rounding)");
            Assert.IsFalse(Value(s, "UseSymSpell", false), "a quoted bool reads as the default");
            Assert.IsFalse(Value(s, "AutoShrinkText", false), "so does 1 for a bool");
            Assert.IsNull(Value<string>(s, "Region"), "a number for text reads as the default");
            Assert.IsTrue(s.ContainsKey("MaxOverlayHeight"));
            Assert.AreEqual(7, Value(s, "MaxOverlayHeight", 7), "null reads as the default, absent too");
            CollectionAssert.AreEqual(new[] { 3 }, Value<int[]>(s, "Pad"));
            Assert.IsTrue(Value<bool>(s, "Debug"));
        }

        /// <summary>Config.json as 2.6.2 shipped its reader: GI-Subtitles/Core/Config/Config.cs and Common/JsonDefaults.cs at v2.6.2.</summary>
        internal static class Release262ConfigReader
        {
            private static readonly JsonDocumentOptions ParseOptions = new()
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };

            private static readonly JsonSerializerOptions ReadOptions = new()
            {
                PropertyNameCaseInsensitive = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
            };

            public static JsonObject Load(string json) => (JsonObject)JsonNode.Parse(json, documentOptions: ParseOptions);

            public static T Get<T>(JsonObject settings, string key, T defaultValue = default)
            {
                if (settings.TryGetPropertyValue(key, out var token) && token != null)
                {
                    try { return token.Deserialize<T>(ReadOptions); }
                    catch (Exception) { }
                }
                return defaultValue;
            }
        }

        // ── overrides.<lang>.json ────────────────────────────────────────

        [TestMethod]
        public void Overrides_ReadTheShapeTheAppWrites_AndWriteItBackUnchanged()
        {
            string text = Fixture("overrides.pl.json");
            var fixes = LayerFiles.ReadUserFixes(Temp("overrides.pl.json", text), out var state);
            Assert.AreEqual(LayerFileState.Loaded, state);
            Assert.AreEqual(2, fixes.Count);
            var paimon = fixes["Let's go, Paimon!"];
            Assert.AreEqual("Chodźmy, Paimon!", paimon.Text);
            Assert.AreEqual("Idziemy, Paimon!", paimon.Was);
            Assert.AreEqual("Traveler", paimon.Speaker);
            Assert.AreEqual("brzmi naturalniej <i>&</i> krócej", paimon.Note);
            Assert.AreEqual(new DateTimeOffset(2026, 11, 15, 19, 4, 5, TimeSpan.Zero), paimon.SavedAt);
            Assert.AreEqual("", fixes["Ehe?"].Speaker, "an optional member that is absent reads as empty");

            string back = Path.Combine(_dir, "back.json");
            LayerFiles.WriteUserFixes(back, "genshin", "pl", fixes.Values);
            Assert.AreEqual(Lf(text), Lf(File.ReadAllText(back)));
        }

        /// <summary>A hand edit: comments, trailing commas, an offset time, unknown and wrongly cased members, a bare array.</summary>
        [TestMethod]
        public void Overrides_HandEditedFile_ReadsAsPinned()
        {
            string json = """
                {
                  // mine
                  "overrides": [
                    { "en": "A", "text": "Ą", "savedAt": "2026-11-15T21:04:05+02:00", "extra": [1, 2], },
                    { "EN": "B", "text": "skipped: en is case-sensitive" },
                    { "en": "C", "text": 5 },
                    { "en": "D", "text": "D1", "speaker": null, "savedAt": "yesterday" },
                    { "en": "D", "text": "D2" },
                  ],
                }
                """;
            var fixes = LayerFiles.ReadUserFixes(Temp("o.json", json), out var state);
            Assert.AreEqual(LayerFileState.Loaded, state);
            CollectionAssert.AreEquivalent(new[] { "A", "D" }, fixes.Keys.ToArray());
            Assert.AreEqual(new DateTimeOffset(2026, 11, 15, 19, 4, 5, TimeSpan.Zero), fixes["A"].SavedAt, "the offset is honoured");
            Assert.AreEqual(TimeSpan.Zero, fixes["A"].SavedAt.Offset, "and the time is kept in UTC");
            Assert.AreEqual("D2", fixes["D"].Text, "a repeated line: the last one wins");
            Assert.AreEqual(DateTimeOffset.MinValue, fixes["D"].SavedAt, "an unreadable or missing time is MinValue");

            var bare = LayerFiles.ReadUserFixes(Temp("bare.json", """[ { "en": "X", "text": "Y" } ]"""), out _);
            Assert.AreEqual("Y", bare["X"].Text, "a bare array is read too");
        }

        // ── community.<lang>.json ────────────────────────────────────────

        [TestMethod]
        public void Community_ReadTheShapeTheAppWrites_AndWriteItBackUnchanged()
        {
            string text = Fixture("community.pl.json");
            var entries = LayerFiles.ReadCommunity(Temp("community.pl.json", text), out string since, out var state);
            Assert.AreEqual(LayerFileState.Loaded, state);
            Assert.AreEqual("2026-11-15 20:01:02", since, "the server's time is kept verbatim, never reformatted");
            Assert.AreEqual("Zaczekaj.", entries["Hold on."].Target);
            Assert.AreEqual("2026-11-14T10:00:00Z", entries["Thank you."].AcceptedAt);

            string back = Path.Combine(_dir, "back.json");
            LayerFiles.WriteCommunity(back, "genshin", "pl", since, entries.Values);
            Assert.AreEqual(Lf(text), Lf(File.ReadAllText(back)));
        }

        // ── community-queue.json ─────────────────────────────────────────

        [TestMethod]
        public void Queue_ReadsTheShapeTheAppWrites_AndWritesItBackTheSameWay()
        {
            string text = Fixture("community-queue.json");
            string path = Temp("community-queue.json", text);
            var queue = new SuggestionQueue(path, () => new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero));
            var items = queue.Items;
            Assert.AreEqual(2, items.Count);
            var first = items[0];
            Assert.AreEqual("0f8e5b8c2a7d4c1e9b3a6d5f4e2c1b0a", first.Id);
            Assert.AreEqual("Zaczekaj.", first.SuggestedText);
            Assert.AreEqual("Where are we going?", first.ContextPrev);
            Assert.AreEqual(2, first.Attempts);
            Assert.AreEqual(new DateTimeOffset(2026, 11, 15, 19, 4, 0, TimeSpan.Zero), first.NextAttemptAt);
            Assert.AreEqual("offline", first.LastError);
            Assert.AreEqual("", items[1].Speaker);

            // Moving both retries writes the file; only those two times change.
            var later = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
            queue.DeferAll(later);
            string expected = text
                .Replace("\"nextAttemptAt\": \"2026-11-15T19:04:00.0000000+00:00\"", "\"nextAttemptAt\": \"2026-11-16T09:00:00.0000000+00:00\"")
                .Replace("\"nextAttemptAt\": \"2026-11-16T07:30:00.0000000+00:00\"", "\"nextAttemptAt\": \"2026-11-16T09:00:00.0000000+00:00\"");
            Assert.AreEqual(Lf(expected), Lf(File.ReadAllText(path)));
        }

        [TestMethod]
        public void Queue_HandEditedFile_ReadsAsPinned()
        {
            string json = """
                { "items": [
                  { "game": "genshin", "lang": "pl", "enText": "A", "suggestedText": "Ą", "createdAt": "2026-11-16T07:00:00Z", "attempts": "3" },
                  { "game": "genshin", "lang": "pl", "enText": "B", "suggestedText": "Ę", "createdAt": "2026-11-16T07:00:00Z", "attempts": 2.0 },
                  { "game": "genshin", "lang": "pl", "enText": "C", "suggestedText": "Ć" },
                ] }
                """;
            var queue = new SuggestionQueue(Temp("q.json", json), () => new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero));
            var items = queue.Items;
            CollectionAssert.AreEqual(new[] { "A", "B" }, items.Select(i => i.EnText).ToArray(), "an item without createdAt is dropped");
            Assert.AreEqual(0, items[0].Attempts, "a quoted count reads as 0");
            Assert.AreEqual(0, items[1].Attempts, "so does 2.0");
            Assert.AreEqual(items[0].CreatedAt, items[0].NextAttemptAt, "a missing next attempt is the creation time");
            Assert.AreEqual(32, items[0].Id.Length, "a missing id gets a new one");
        }

        // ── assist.json ──────────────────────────────────────────────────

        [TestMethod]
        public void Assist_ReadsTheShapeTheAppWrites_AndWritesItTheSameWay()
        {
            string path = Temp("assist.json", Fixture("assist.json"));
            var store = new AssistStore(_ => path, TimeSpan.Zero);
            var readings = store.Readings("Genshin");
            Assert.AreEqual(2, readings.Count);
            Assert.AreEqual(new AssistStoredReading("local", "genshin.resin", "160/200", "Żywica", new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero)), readings[0]);
            Assert.AreEqual("a1b2c3", readings[1].Account);
            var health = store.Health("Genshin");
            Assert.AreEqual(new AssistReaderHealth("genshin.resin", new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero), 0), health[0]);
            Assert.AreEqual(new AssistReaderHealth("genshin.stamina", null, 3), health[1]);

            // A write keeps the file's own style: camelCase, two-space indents, non-ASCII escaped (its options differ from
            // the other files'), every time in UTC with its offset.
            store.Record("Genshin", "local", "genshin.stamina", "120/240", "Paliwo „x”", new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.FromHours(1)));
            string written = Lf(File.ReadAllText(path));
            StringAssert.Contains(written, "{\n  \"version\": 1,\n  \"salt\": \"c2FsdC1mb3ItdGVzdHM=\",\n  \"accounts\": {\n    \"local\": {\n");
            StringAssert.Contains(written, "\"screen\": \"\\u017Bywica\",");
            StringAssert.Contains(written, "\"screen\": \"Paliwo \\u201Ex\\u201D\",");
            StringAssert.Contains(written, "\"seenUtc\": \"2026-11-16T08:30:00+00:00\"");
            StringAssert.Contains(written, "\"lastSeenUtc\": \"2026-11-16T08:30:00+00:00\",\n      \"sessionsSinceSeen\": 0");

            var again = new AssistStore(_ => path, TimeSpan.Zero).Readings("Genshin");
            Assert.AreEqual(3, again.Count);
            Assert.AreEqual("Paliwo „x”", again.Single(r => r.ReaderId == "genshin.stamina").Screen);
        }

        /// <summary>assist.json's reader is strict: wrongly cased names read as nothing, and a comment makes the file read as empty.</summary>
        [TestMethod]
        public void Assist_HandEditedFile_ReadsAsPinned()
        {
            string cased = Temp("cased.json", """{ "Version": 1, "Accounts": { "local": { "r": { "Value": "1", "SeenUtc": "2026-11-16T08:00:00Z" } } } }""");
            Assert.AreEqual(0, new AssistStore(_ => cased, TimeSpan.Zero).Readings("Genshin").Count);

            string commented = Temp("commented.json", "// mine\n" + Fixture("assist.json"));
            Assert.AreEqual(0, new AssistStore(_ => commented, TimeSpan.Zero).Readings("Genshin").Count);

            string later = Temp("later.json", Fixture("assist.json").Replace("\"version\": 1", "\"version\": 2"));
            Assert.AreEqual(0, new AssistStore(_ => later, TimeSpan.Zero).Readings("Genshin").Count, "a later version reads as empty");
        }

        // ── the showcase cache (a cached API answer) ─────────────────────

        [TestMethod]
        public void Showcase_ReadsTheCacheTheAppWrites_AndWritesItTheSameWay()
        {
            string text = Fixture("showcase.json");
            var roster = ShowcaseRoster.FromCache(text, "700000001");
            Assert.IsNotNull(roster);
            Assert.AreEqual("2026-11-16", roster.Day);
            Assert.AreEqual(new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero), roster.ReadAt);
            Assert.AreEqual(2, roster.Characters.Count);
            Assert.AreEqual(10, roster.Characters[0].Talents[0].Level);
            Assert.IsNull(roster.Characters[1].Icon, "only https images are kept");

            Assert.AreEqual(Lf(text).Replace("\"http://insecure.example/x.png\"", "null"), roster.ToCacheJson());
        }

        [TestMethod]
        public void Showcase_HandEditedCache_ReadsAsPinned()
        {
            string text = Fixture("showcase.json");
            Assert.IsNotNull(ShowcaseRoster.FromCache(text.Replace("\"day\"", "\"extra\":[1],\"day\""), "700000001"), "an unknown member is ignored");
            Assert.IsNull(ShowcaseRoster.FromCache(text.Replace("\"level\":90", "\"level\":\"90\""), "700000001"), "a quoted number drops the cache");
            Assert.IsNull(ShowcaseRoster.FromCache(text.Replace("\"uid\"", "\"Uid\""), "700000001"), "names are case-sensitive");
            Assert.IsNull(ShowcaseRoster.FromCache("// c\n" + text, "700000001"), "a comment drops the cache");
        }
    }
}
