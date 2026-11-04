using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Today;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The site/desktop contract for "talents worth levelling today": tools/fixtures/talent-farm-cases.json, run here
    /// through TalentFarmPlanner and ServerDay and on the site through today-talents.ts (today-talents-fixture.test.mjs).
    /// </summary>
    [TestClass]
    public class TalentFarmPlannerTests
    {
        private static JsonElement Fixture()
        {
            string root = RenderTestKit.RepoRoot() ?? throw new AssertInconclusiveException("Repository root not found.");
            string path = Path.Combine(root, "tools", "fixtures", "talent-farm-cases.json");
            Assert.IsTrue(File.Exists(path), "the shared fixture is checked in");
            return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
        }

        internal static IReadOnlyList<TalentDomain> Domains(JsonElement fx) => fx.GetProperty("domains").EnumerateArray().Select(d =>
            new TalentDomain(d.GetProperty("name").GetString(), d.GetProperty("city").GetInt32(),
                d.GetProperty("days").EnumerateArray().Select(x => x.GetInt32()).ToList(),
                d.GetProperty("items").EnumerateArray().Select(i => new DomainItem(i.GetProperty("id").GetInt32(), i.GetProperty("name").GetString(), i.GetProperty("rarity").GetInt32())).ToList(),
                d.GetProperty("since").ValueKind == JsonValueKind.String ? d.GetProperty("since").GetString() : null)).ToList();

        private static List<string> Strings(JsonElement a) => a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString()).ToList() : new List<string>();

        [TestMethod]
        public void BookDomains_MatchTheFixture()
        {
            var fx = Fixture();
            var domains = Domains(fx);
            int n = 0;
            foreach (var c in fx.GetProperty("bookCases").EnumerateArray())
            {
                var mats = c.GetProperty("materials").EnumerateArray().Select(m => new MaterialRef(m.GetProperty("name").GetString(), m.GetProperty("image").GetString())).ToList();
                CollectionAssert.AreEqual(Strings(c.GetProperty("expect")), TalentFarmPlanner.BookDomains(mats, domains).Select(d => d.Name).ToList(), c.GetProperty("name").GetString());
                n++;
            }
            Assert.IsTrue(n >= 5);
        }

        [TestMethod]
        public void PriorityKinds_MatchTheFixture()
        {
            foreach (var c in Fixture().GetProperty("priorityCases").EnumerateArray())
            {
                var kinds = c.GetProperty("skills").EnumerateArray().Select(s => s.GetProperty("kind").GetString()).ToList();
                var positions = c.GetProperty("positions").ValueKind == JsonValueKind.Array
                    ? c.GetProperty("positions").EnumerateArray().Select(x => x.GetInt32()).ToList() : null;
                CollectionAssert.AreEqual(Strings(c.GetProperty("expect")), TalentFarmPlanner.PriorityKinds(kinds, positions).ToList(), c.GetProperty("name").GetString());
            }
        }

        [TestMethod]
        public void BookFor_MatchesTheFixture()
        {
            foreach (var c in Fixture().GetProperty("bookForCases").EnumerateArray())
            {
                var e = c.GetProperty("expect");
                Assert.AreEqual(e.ValueKind == JsonValueKind.Null ? null : e.GetString(),
                    TalentFarmPlanner.BookFor(Strings(c.GetProperty("books")), c.GetProperty("target").GetInt32()));
            }
        }

        [TestMethod]
        public void Plan_MatchesTheFixture()
        {
            var plan = Fixture().GetProperty("plan");
            var catalog = plan.GetProperty("catalog").EnumerateArray().Select(e => new TalentEntry(e.GetProperty("id").GetString(), e.GetProperty("name").GetString(),
                e.GetProperty("icon").ValueKind == JsonValueKind.String ? e.GetProperty("icon").GetString() : null,
                Strings(e.GetProperty("order")), Strings(e.GetProperty("books")))).ToList();
            var chars = plan.GetProperty("chars").EnumerateArray().Select(c => new KeptCharacter(c.GetProperty("id").GetString(), c.GetProperty("level").GetInt32(),
                c.GetProperty("talents").EnumerateArray().Select(t => new KeptTalent(t.GetProperty("kind").GetString(), t.GetProperty("short").GetString(),
                    t.GetProperty("level").GetInt32(), t.GetProperty("bonus").GetInt32())).ToList())).ToList();
            int cases = 0;
            foreach (var c in plan.GetProperty("cases").EnumerateArray())
            {
                string name = c.GetProperty("name").GetString();
                var got = TalentFarmPlanner.Plan(chars, catalog, new HashSet<string>(Strings(c.GetProperty("open"))));
                var want = c.GetProperty("expect").EnumerateArray().ToList();
                Assert.AreEqual(want.Count, got.Count, name + ": rows");
                for (int i = 0; i < want.Count; i++)
                {
                    var w = want[i];
                    var r = got[i];
                    Assert.AreEqual(w.GetProperty("id").GetString(), r.Id, $"{name} row {i}");
                    Assert.AreEqual(Nullable(w.GetProperty("best")), r.Best, $"{name} {r.Id} best");
                    Assert.AreEqual(w.GetProperty("level").GetInt32(), r.Level);
                    var ws = w.GetProperty("steps").EnumerateArray().ToList();
                    Assert.AreEqual(ws.Count, r.Steps.Count, $"{name} {r.Id} steps");
                    for (int k = 0; k < ws.Count; k++)
                    {
                        var s = r.Steps[k];
                        string at = $"{name} {r.Id} step {k}";
                        Assert.AreEqual(ws[k].GetProperty("kind").GetString(), s.Kind, at);
                        Assert.AreEqual(ws[k].GetProperty("short").GetString(), s.Short, at);
                        Assert.AreEqual(ws[k].GetProperty("level").GetInt32(), s.Level, at);
                        Assert.AreEqual(ws[k].GetProperty("target").GetInt32(), s.Target, at);
                        Assert.AreEqual(ws[k].GetProperty("bonus").GetInt32(), s.Bonus, at);
                        Assert.AreEqual(ws[k].GetProperty("tier").GetInt32(), s.Tier, at);
                        Assert.AreEqual(ws[k].GetProperty("count").GetInt32(), s.Count, at);
                        Assert.AreEqual(ws[k].GetProperty("crown").GetBoolean(), s.Crown, at);
                        Assert.AreEqual(Nullable(ws[k].GetProperty("priority")), s.Priority, at);
                        Assert.AreEqual(ws[k].GetProperty("domain").GetString(), s.Domain, at);
                    }
                }
                cases++;
            }
            Assert.IsTrue(cases >= 4);
        }

        [TestMethod]
        public void ServerDay_MatchesTheFixture()
        {
            foreach (var c in Fixture().GetProperty("dayCases").EnumerateArray())
            {
                var now = DateTimeOffset.Parse(c.GetProperty("now").GetString());
                var server = ServerDay.Parse(c.GetProperty("server").GetString());
                Assert.AreEqual(c.GetProperty("offset").GetInt32(), ServerDay.OffsetOf(server));
                var d = ServerDay.At(now, server);
                var e = c.GetProperty("expect");
                string at = $"{c.GetProperty("now").GetString()} {c.GetProperty("server").GetString()}";
                Assert.AreEqual(e.GetProperty("weekday").GetInt32(), (int)d.Weekday, at);
                Assert.AreEqual(DateTimeOffset.Parse(e.GetProperty("start").GetString()), d.Start, at);
                Assert.AreEqual(DateTimeOffset.Parse(e.GetProperty("reset").GetString()), d.Reset, at);
            }
        }

        [TestMethod]
        public void ServerDay_KeyIsTheServersDate()
        {
            // 02:59 UTC is 03:59 in Europe: still Tuesday's game day.
            Assert.AreEqual("2026-11-03", ServerDay.At(DateTimeOffset.Parse("2026-11-04T02:59:00Z"), FarmServer.Europe).Key(FarmServer.Europe));
            Assert.AreEqual("2026-11-04", ServerDay.At(DateTimeOffset.Parse("2026-11-04T03:00:00Z"), FarmServer.Europe).Key(FarmServer.Europe));
            Assert.AreEqual("2026-11-04", ServerDay.At(DateTimeOffset.Parse("2026-11-03T20:00:00Z"), FarmServer.Asia).Key(FarmServer.Asia));
            Assert.AreEqual(FarmServer.Europe, ServerDay.Parse("mars"));
            Assert.AreEqual("america", ServerDay.Id(FarmServer.America));
        }

        private static int? Nullable(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt32();
    }
}
