using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Games;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Parity anchor for the game manifests: every public property of the games recorded in
    /// GI-Test/Fixtures/game-profiles.golden.json (Genshin, Star Rail, ZZZ, written from the hand-coded profiles before
    /// they moved to games/*.json), the generic fallback and the lookup results. Additive: a game added later is not
    /// recorded and passes, as long as it comes after the recorded ones. KAPTION_UPDATE_GOLDEN=1 records what is new
    /// (a property, a probe) and refuses to rewrite a recorded value; change one by hand, on purpose.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class GameProfileGoldenTests
    {
        private const string FixtureName = "game-profiles.golden.json";

        // Inputs whose resolution must not move: canonical tags, casing, padding, aliases, display names and unknowns.
        private static readonly string[] ResolveProbes =
        {
            "Genshin", "genshin", "GENSHIN", " Genshin ", "Genshin Impact",
            "StarRail", "starrail", "Star Rail", "HSR", "hsr", "Honkai: Star Rail",
            "ZZZ", "zzz", " ZZZ ", "Zenless", "zenless", "ZenlessZoneZero", "Zenless Zone Zero", "Zenless-Zone-Zero",
            "zenless zone zero", "NotAGame", "Genshin1TypoFromConfig", "", "   ", null,
        };

        private static readonly string[] DisplayNameProbes =
        {
            "Genshin", "starrail", "ZZZ", "Zenless", " Genshin ", "hsr", "NotAGame", "", null,
        };

        private static readonly JsonSerializerOptions Indented = new JsonSerializerOptions { WriteIndented = true };

        [TestMethod]
        public void Profiles_MatchTheGolden()
        {
            string path = GoldenPath();
            bool update = Environment.GetEnvironmentVariable("KAPTION_UPDATE_GOLDEN") == "1";
            if (!File.Exists(path))
            {
                if (!update) Assert.Fail($"{path} is missing; run once with KAPTION_UPDATE_GOLDEN=1 on a known-good tree.");
                Write(path, Capture(GameRegionProfile.RegisteredGameIds.ToArray()));
                Assert.Inconclusive($"Golden written: {path}");
            }

            JsonObject golden = ReadGolden(path);
            if (update)
            {
                var changed = new List<string>();
                JsonNode merged = AddNew(golden, Capture(RecordedGameIds(golden)), "$", changed);
                if (changed.Count > 0)
                    Assert.Fail("KAPTION_UPDATE_GOLDEN only records what is new. These recorded values changed; edit the golden by hand if that is intended:\n"
                                + string.Join("\n", changed));
                Write(path, merged);
                Assert.Inconclusive($"Golden updated with new entries only: {path}");
            }

            string difference = Difference(golden);
            Assert.IsNull(difference, "Game profiles drifted from the golden: " + difference);
        }

        [TestMethod]
        public void Golden_LetsANewGameAfterTheRecordedOnes_AndCatchesOneBeforeThem()
        {
            JsonObject golden = ReadGolden(GoldenPath());

            var later = Fixture();
            later.Order = GameManifestLoader.LoadEmbedded().Max(m => m.Order) + 1; // where the next real game goes
            using (GameRegistry.Use(GameRegistry.FromManifests(GameManifestLoader.LoadEmbedded().Append(later))))
            {
                Assert.AreEqual("FixtureGame", GameRegionProfile.RegisteredGameIds.Last());
                Assert.IsNull(Difference(golden), "a new game changes nothing recorded");
            }

            var first = Fixture();
            first.Order = 0;
            using (GameRegistry.Use(GameRegistry.FromManifests(GameManifestLoader.LoadEmbedded().Append(first))))
                StringAssert.StartsWith(Difference(golden), "$.registeredGameIds[0]", "the recorded games must stay first, in order");

            var manifests = GameManifestLoader.LoadEmbedded();
            manifests.First(m => m.Id == "starrail").Name += " (renamed)";
            using (GameRegistry.Use(GameRegistry.FromManifests(manifests)))
                StringAssert.StartsWith(Difference(golden), "$.profiles[1].DisplayName", "a recorded value that moves is still caught");
        }

        [TestMethod]
        public void UpdateMode_AddsNewEntries_AndRefusesChangedOnes()
        {
            var golden = (JsonObject)JsonNode.Parse("{\"a\":1,\"list\":[{\"x\":1}],\"names\":[\"Genshin\"]}");
            var actual = (JsonObject)JsonNode.Parse("{\"a\":1,\"list\":[{\"x\":1,\"y\":true}],\"names\":[\"Genshin\"],\"b\":2}");
            var changed = new List<string>();
            JsonNode merged = AddNew(golden, actual, "$", changed);
            Assert.AreEqual(0, changed.Count);
            Assert.AreEqual("{\"a\":1,\"list\":[{\"x\":1,\"y\":true}],\"names\":[\"Genshin\"],\"b\":2}", merged.ToJsonString());

            actual["a"] = 5;
            ((JsonArray)actual["names"])[0] = "StarRail";
            AddNew(golden, actual, "$", changed);
            CollectionAssert.AreEqual(new[] { "$.a: recorded 1, now 5", "$.names: recorded [\"Genshin\"], now [\"StarRail\"]" }, changed);
        }

        private static string GoldenPath()
        {
            string root = RenderTestKit.RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            return Path.Combine(root, "GI-Test", "Fixtures", FixtureName);
        }

        private static JsonObject ReadGolden(string path)
        {
            Assert.IsTrue(File.Exists(path), $"{path} is missing; run once with KAPTION_UPDATE_GOLDEN=1 on a known-good tree.");
            return JsonNode.Parse(File.ReadAllText(path)).AsObject();
        }

        private static void Write(string path, JsonNode node) =>
            File.WriteAllText(path, node.ToJsonString(Indented).Replace("\r\n", "\n") + "\n");

        /// <summary>The games the golden records, in its order.</summary>
        private static string[] RecordedGameIds(JsonObject golden) =>
            golden["registeredGameIds"].AsArray().Select(n => (string)n).ToArray();

        /// <summary>The first difference between the golden and the registry now, or null.</summary>
        private static string Difference(JsonObject golden) =>
            FirstDifference(golden, Capture(RecordedGameIds(golden)), "$");

        private static GameManifest Fixture()
        {
            string root = RenderTestKit.RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            return GameManifestLoader.Parse(File.ReadAllText(Path.Combine(root, "GI-Test", "Fixtures", "games", "fixturegame.json")));
        }

        /// <summary>
        /// The recorded games' profiles (by GameId, in the golden's order), the registry's first ids, the fallbacks and
        /// the probes. Games registered after the recorded ones are not captured.
        /// </summary>
        internal static JsonObject Capture(IReadOnlyList<string> gameIds)
        {
            var registered = GameRegionProfile.RegisteredProfiles.ToList();
            var profiles = new JsonArray();
            foreach (string id in gameIds)
            {
                var profile = registered.FirstOrDefault(r => string.Equals(r.GameId, id, StringComparison.Ordinal));
                profiles.Add(profile == null ? null : Dump(profile));
            }

            var resolve = new JsonObject();
            foreach (string input in ResolveProbes)
            {
                var hit = GameRegionProfile.Get(input);
                resolve[Key(input)] = new JsonObject
                {
                    ["gameId"] = hit.GameId,
                    ["registered"] = GameRegionProfile.RegisteredProfiles.Any(r => ReferenceEquals(r, hit)),
                };
            }

            var names = new JsonObject();
            foreach (string input in DisplayNameProbes) names[Key(input)] = GameRegionProfile.DisplayNameOf(input);

            return new JsonObject
            {
                ["registeredGameIds"] = new JsonArray(GameRegionProfile.RegisteredGameIds.Take(gameIds.Count).Select(id => (JsonNode)id).ToArray()),
                ["profiles"] = profiles,
                ["fallback"] = Dump(GameRegionProfile.Get("NotAGame")),
                ["fallbackForNull"] = Dump(GameRegionProfile.Get(null)),
                ["resolve"] = resolve,
                ["displayNameOf"] = names,
            };
        }

        private static string Key(string input) => input == null ? "(null)" : "[" + input + "]";

        private static JsonObject Dump(GameRegionProfile profile)
        {
            var o = new JsonObject();
            foreach (var prop in typeof(GameRegionProfile).GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.MetadataToken))
                o[prop.Name] = JsonSerializer.SerializeToNode(prop.GetValue(profile), prop.PropertyType);
            return o;
        }

        /// <summary>
        /// <paramref name="expected"/> plus what only <paramref name="actual"/> has, in actual's order; a value both have
        /// but that differs is listed in <paramref name="changed"/> and kept as recorded.
        /// </summary>
        internal static JsonNode AddNew(JsonNode expected, JsonNode actual, string path, List<string> changed)
        {
            if (expected is JsonObject eo && actual is JsonObject ao)
            {
                var merged = new JsonObject();
                foreach (var kv in ao)
                    merged[kv.Key] = eo.ContainsKey(kv.Key) ? AddNew(eo[kv.Key], kv.Value, $"{path}.{kv.Key}", changed) : kv.Value?.DeepClone();
                foreach (var kv in eo)
                    if (!ao.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value?.DeepClone(); // kept: the comparison reports it missing
                return merged;
            }
            if (expected is JsonArray ea && actual is JsonArray aa && ea.Count == aa.Count && ea.All(e => e is JsonObject))
            {
                var merged = new JsonArray();
                for (int i = 0; i < ea.Count; i++) merged.Add(AddNew(ea[i], aa[i], $"{path}[{i}]", changed));
                return merged;
            }
            if (!JsonNode.DeepEquals(expected, actual)) changed.Add($"{path}: recorded {Show(expected)}, now {Show(actual)}");
            return expected?.DeepClone();
        }

        private static string FirstDifference(JsonNode expected, JsonNode actual, string path)
        {
            if (expected == null || actual == null)
                return expected == null && actual == null ? null : $"{path}: expected {Show(expected)}, got {Show(actual)}";
            if (expected is JsonObject eo && actual is JsonObject ao)
            {
                foreach (var kv in eo)
                {
                    if (!ao.ContainsKey(kv.Key)) return $"{path}.{kv.Key}: missing";
                    string d = FirstDifference(kv.Value, ao[kv.Key], $"{path}.{kv.Key}");
                    if (d != null) return d;
                }
                var extra = ao.Select(kv => kv.Key).FirstOrDefault(k => !eo.ContainsKey(k));
                return extra == null ? null : $"{path}.{extra}: not in the golden";
            }
            if (expected is JsonArray ea && actual is JsonArray aa)
            {
                if (ea.Count != aa.Count) return $"{path}: {ea.Count} items expected, got {aa.Count}";
                for (int i = 0; i < ea.Count; i++)
                {
                    string d = FirstDifference(ea[i], aa[i], $"{path}[{i}]");
                    if (d != null) return d;
                }
                return null;
            }
            return JsonNode.DeepEquals(expected, actual) ? null : $"{path}: expected {Show(expected)}, got {Show(actual)}";
        }

        private static string Show(JsonNode node) => node == null ? "null" : node.ToJsonString();
    }
}
