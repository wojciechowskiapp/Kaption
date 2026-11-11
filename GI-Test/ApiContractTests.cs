// The app's fixtures of the Worker API against contracts/api/*.schema.json, the schemas the Worker's own answers are
// checked against (backend/test-workers): a sample that drifts from the real answer fails here, not in production.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GI_Subtitles.Services.Codes;
using GI_Subtitles.Services.Network;
using Json.Schema;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class ApiContractTests
    {
        private const string Codes = "app-codes.schema.json";
        private const string Overlay = "community-overlay.schema.json";
        private const string LicenseFiles = "license-files.schema.json";

        // One build per schema: every schema registers its $id, and a second build of the same id is not needed.
        private static readonly ConcurrentDictionary<string, JsonSchema> Schemas = new();

        private static string Root() =>
            RenderTestKit.RepoRoot() ?? throw new AssertInconclusiveException("GI-Subtitles.sln not found above the test binaries");

        private static string ContractsDir() => Path.Combine(Root(), "contracts", "api");

        private static string Fixture(string name) => File.ReadAllText(Path.Combine(Root(), "GI-Test", "Fixtures", "api", name));

        private static JsonSchema Schema(string file) =>
            Schemas.GetOrAdd(file, f => JsonSchema.FromText(File.ReadAllText(Path.Combine(ContractsDir(), f))));

        /// <summary>Fails with every error of the body, each with its place in the body, so one run shows the whole drift.</summary>
        private static void AssertMatches(string schemaFile, string json, string label)
        {
            using var doc = JsonDocument.Parse(json);
            var result = Schema(schemaFile).Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
            if (result.IsValid) return;
            var lines = (result.Details ?? new List<EvaluationResults>())
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors.Select(e => $"  {d.InstanceLocation} ({d.EvaluationPath}): {e.Key}: {e.Value}"));
            Assert.Fail($"{label} does not match contracts/api/{schemaFile}:\n{string.Join("\n", lines)}");
        }

        [TestMethod]
        public void TheAppsContractsAreTheOnesCheckedHere()
        {
            using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsDir(), "index.json")));
            var mine = index.RootElement.GetProperty("contracts").EnumerateArray()
                .Where(c => c.GetProperty("consumers").EnumerateArray().Any(x => x.GetString() == "desktop"))
                .Select(c => c.GetProperty("schema").GetString()).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(new[] { Codes, Overlay, LicenseFiles }, mine, "a new desktop contract needs its fixtures checked here");
            foreach (string file in mine)
            {
                using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsDir(), file)));
                Assert.AreEqual("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString(), file);
                Assert.IsNotNull(Schema(file), file);
            }
        }

        [TestMethod]
        public void CodesSamples_MatchTheContract_AndParse()
        {
            AssertMatches(Codes, CodesSamples.Genshin, "CodesSamples.Genshin");
            AssertMatches(Codes, CodesSamples.NeverChecked, "CodesSamples.NeverChecked");
            Assert.AreEqual(2, RedeemCodeList.Parse(CodesSamples.Genshin, "genshin").Active.Count);
            Assert.IsFalse(RedeemCodeList.Parse(CodesSamples.NeverChecked, "zzz").Checked);
        }

        [TestMethod]
        public void CommunityOverlayFixture_MatchesTheContract_AndParses()
        {
            string json = Fixture("community-overlay.sample.json");
            AssertMatches(Overlay, json, "Fixtures/api/community-overlay.sample.json");
            using var doc = JsonDocument.Parse(json);
            var page = KaptionApiClient.ParseOverlay(doc.RootElement.Clone(), requestedFull: false);
            Assert.IsFalse(page.Full);
            Assert.AreEqual("2026-11-09T11:58:00.000Z", page.Until);
            CollectionAssert.AreEqual(new[] { "Powinniśmy ruszać, zanim się ściemni.", "Dzięki za pomoc, Podróżniku.", "" },
                page.Entries.Select(e => e.Target).ToArray(), "two lines and one removal");
        }

        [TestMethod]
        public void LicenseFilesFixture_MatchesTheContract_AndParses()
        {
            string json = Fixture("license-files.sample.json");
            AssertMatches(LicenseFiles, json, "Fixtures/api/license-files.sample.json");
            var items = JsonSerializer.Deserialize<ApiListEnvelope<FileMetadata>>(json).Items;
            Assert.AreEqual(2, items.Count);
            Assert.AreEqual("7.2.0-r2", items[0].Version);
            Assert.AreEqual(103_809_040, items[0].Size);
            Assert.AreEqual(64, items[0].ContentSha256?.Length, "pack_sha256 read");
            Assert.IsNull(items[1].ContentSha256, "a pack without a content hash");
        }

        [TestMethod]
        public void TheCheckBites()
        {
            const string drifted = """{"game":"genshin","checkedAt":"yesterday","active":[],"expired":[],"program":null,"extra":1}""";
            var e = Assert.ThrowsExactly<AssertFailedException>(() => AssertMatches(Codes, drifted, "drifted"));
            StringAssert.Contains(e.Message, "/checkedAt");
            StringAssert.Contains(e.Message, "extra");
        }

        [TestMethod]
        public void ABadTimestampAloneFails()
        {
            // Times are patterns in the contracts (enforced); format validation is on too, should one use "format".
            const string badTime = """{"game":"genshin","checkedAt":"yesterday","active":[],"expired":[],"program":null}""";
            var e = Assert.ThrowsExactly<AssertFailedException>(() => AssertMatches(Codes, badTime, "bad time"));
            StringAssert.Contains(e.Message, "/checkedAt");
        }
    }
}
