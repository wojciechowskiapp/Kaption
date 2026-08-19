// ─────────────────────────────────────────────────────────────────────────────
//  DiagnosticBundleTests.cs
//  ---------------------------------------------------------------------------
//  End-to-end cover for the support log bundle. The redaction rules themselves
//  are tested in LogRedactorTests; what matters here is that the whole path
//  produces a readable zip. The failure modes that bite in practice are
//  structural — a log file locked by our own writer, a missing directory, one
//  collector throwing and taking the bundle down with it — not algorithmic.
//
//  Every test runs against a temp directory it creates and deletes, via
//  DiagnosticBundle.RootOverride. Nothing here reads or writes the real
//  %APPDATA%\Kaption, so the results do not depend on whether Kaption has ever
//  run on this machine and are identical on a clean CI runner.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using GI_Subtitles.Services.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class DiagnosticBundleTests
    {
        private string _root;

        [TestInitialize]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "kaption-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            DiagnosticBundle.RootOverride = _root;
        }

        [TestCleanup]
        public void TearDown()
        {
            DiagnosticBundle.RootOverride = null;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { /* a leftover temp dir is not worth failing the run */ }
        }

        private void WriteLog(string name, string content) =>
            File.WriteAllText(Path.Combine(_root, name), content);

        private static async Task<ZipArchive> BuildAsync(string note = null)
        {
            var result = await DiagnosticBundle.CreateAsync(note);
            Assert.IsNotNull(result, "No result returned.");
            Assert.IsTrue(File.Exists(result.FilePath), $"Bundle not written: {result.FilePath}");
            return ZipFile.OpenRead(result.FilePath);
        }

        private static string ReadEntry(ZipArchive zip, string name)
        {
            var entry = zip.GetEntry(name);
            Assert.IsNotNull(entry, $"Bundle has no '{name}'.");
            using (var reader = new StreamReader(entry.Open())) return reader.ReadToEnd();
        }

        // ── structure ──────────────────────────────────────────────────────

        [TestMethod]
        public async Task Create_ProducesReadableZipWithCoreArtifacts()
        {
            using (var zip = await BuildAsync())
            {
                var names = zip.Entries.Select(e => e.FullName).ToArray();

                CollectionAssert.Contains(names, "manifest.txt");
                CollectionAssert.Contains(names, "environment.txt");
                CollectionAssert.Contains(names, "state.txt");
                CollectionAssert.Contains(names, "user-note.txt");
                Assert.IsTrue(names.Any(n => n.StartsWith("logs/", StringComparison.Ordinal)),
                    "Bundle contained no logs folder at all.");
            }
        }

        [TestMethod]
        public async Task Create_ManifestCarriesASupportId()
        {
            var result = await DiagnosticBundle.CreateAsync(null);

            // The ID is what joins a zip in a bucket to a message in Discord,
            // so its shape is load-bearing rather than cosmetic.
            StringAssert.Matches(result.SupportId,
                new System.Text.RegularExpressions.Regex(@"^KAP-[0-9A-F]{4}-[0-9A-F]{4}$"));

            using (var zip = ZipFile.OpenRead(result.FilePath))
            {
                StringAssert.Contains(ReadEntry(zip, "manifest.txt"), result.SupportId);
            }
        }

        [TestMethod]
        public async Task Create_TwoBundles_GetDistinctSupportIds()
        {
            var first = await DiagnosticBundle.CreateAsync(null);
            var second = await DiagnosticBundle.CreateAsync(null);

            Assert.AreNotEqual(first.SupportId, second.SupportId,
                "Two reports sharing an ID would be indistinguishable in a support thread.");
        }

        // ── log collection ─────────────────────────────────────────────────

        [TestMethod]
        public async Task Create_CollectsTheKnownLogFiles()
        {
            WriteLog("app.log", "[INFO ] engine ready");
            WriteLog("app.log.1", "[INFO ] previous session");
            WriteLog("crash.log", "FATAL: something exploded");

            using (var zip = await BuildAsync())
            {
                StringAssert.Contains(ReadEntry(zip, "logs/app.log"), "engine ready");
                StringAssert.Contains(ReadEntry(zip, "logs/app.log.1"), "previous session");
                StringAssert.Contains(ReadEntry(zip, "logs/crash.log"), "something exploded");
            }
        }

        [TestMethod]
        public async Task Create_ReadsALogThatIsStillOpenForWriting()
        {
            // log4net holds app.log open for the life of the process with
            // immediateFlush, so the collector has to read through a shared
            // handle. This is the single most likely way the feature breaks in
            // the field, and it is why ReadTextShared passes FileShare.ReadWrite.
            string logPath = Path.Combine(_root, "app.log");
            using (var held = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                var writer = new StreamWriter(held) { AutoFlush = true };
                writer.WriteLine("[INFO ] written while the handle is still open");

                using (var zip = await BuildAsync())
                {
                    StringAssert.Contains(ReadEntry(zip, "logs/app.log"), "still open");
                }
            }
        }

        [TestMethod]
        public async Task Create_OversizedLog_IsTrimmedAndSaysSo()
        {
            // Silent truncation reads as "we captured everything" when we did
            // not, and sends the reader hunting for a gap we created ourselves.
            WriteLog("screenshot_log.txt", new string('x', 5 * 1024 * 1024));

            var result = await DiagnosticBundle.CreateAsync(null);
            using (var zip = ZipFile.OpenRead(result.FilePath))
            {
                StringAssert.Contains(ReadEntry(zip, "logs/screenshot_log.txt"), "[trimmed:");
            }
            Assert.IsTrue(result.Notes.Any(n => n.Contains("trimmed")),
                "Trimming happened but was not reported in the bundle notes.");
        }

        [TestMethod]
        public async Task Create_NoLogsAtAll_StillProducesAUsableBundle()
        {
            // A fresh install with no logs is itself a finding. Shipping an
            // empty folder would look like the collector broke.
            var result = await DiagnosticBundle.CreateAsync(null);

            using (var zip = ZipFile.OpenRead(result.FilePath))
            {
                StringAssert.Contains(ReadEntry(zip, "logs/README.txt"), "No log files");
            }
            Assert.IsTrue(result.Notes.Any(n => n.Contains("no log files")),
                "The absence of logs should be recorded, not passed over in silence.");
        }

        [TestMethod]
        public async Task Create_ExcludesTranscriptExports()
        {
            // Transcripts are user-initiated exports of game dialogue, not
            // diagnostics — they are deliberately not collected.
            Directory.CreateDirectory(Path.Combine(_root, "logs"));
            File.WriteAllText(Path.Combine(_root, "logs", "transcript_2026-08-17_101500.txt"), "dialogue");

            using (var zip = await BuildAsync())
            {
                Assert.IsFalse(zip.Entries.Any(e => e.FullName.Contains("transcript_")),
                    "A transcript export leaked into the bundle.");
            }
        }

        // ── content ────────────────────────────────────────────────────────

        [TestMethod]
        public async Task Create_UserNoteIsPreserved()
        {
            const string note = "Subtitles stopped appearing after I alt-tabbed back into the game.";

            using (var zip = await BuildAsync(note))
            {
                Assert.AreEqual(note, ReadEntry(zip, "user-note.txt").Trim());
            }
        }

        [TestMethod]
        public async Task Create_RedactsEverythingItWrites()
        {
            // The note is the one field a user can put anything into, so it is
            // the honest place to prove redaction covers generated content and
            // not just the log files.
            const string note = "my token is eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhIn0.c2ln and I am at C:\\Users\\Crisey\\Desktop";

            using (var zip = await BuildAsync(note))
            {
                string text = ReadEntry(zip, "user-note.txt");
                Assert.IsFalse(text.Contains("eyJhbGci"), "A token reached the bundle.");
                Assert.IsFalse(text.Contains("Crisey"), "A Windows username reached the bundle.");
            }
        }

        [TestMethod]
        public async Task Create_RedactsSecretsInsideCollectedLogs()
        {
            WriteLog("app.log", "[INFO ] auth ok for michal@gmail.com from C:\\Users\\Crisey\\AppData");

            using (var zip = await BuildAsync())
            {
                string text = ReadEntry(zip, "logs/app.log");
                Assert.IsFalse(text.Contains("Crisey"), "A username survived inside a collected log.");
                Assert.IsFalse(text.Contains("michal@"), "An email survived inside a collected log.");
                StringAssert.Contains(text, "@gmail.com", "The domain should be kept for triage.");
            }
        }

        // ── resilience ─────────────────────────────────────────────────────

        [TestMethod]
        public async Task Create_SurvivesAStateProviderThatThrows()
        {
            DiagnosticBundle.RegisterStateProvider(
                "ZZ deliberately broken probe", () => throw new InvalidOperationException("boom"));

            using (var zip = await BuildAsync())
            {
                // The bundle must still exist, and say what went wrong rather
                // than silently dropping the line.
                string text = ReadEntry(zip, "state.txt");
                StringAssert.Contains(text, "ZZ deliberately broken probe");
                StringAssert.Contains(text, "boom");
            }
        }

        [TestMethod]
        public async Task Create_ReportsRegisteredState()
        {
            DiagnosticBundle.RegisterStateProvider("AA test probe", () => "engine=Ready");

            using (var zip = await BuildAsync())
            {
                StringAssert.Contains(ReadEntry(zip, "state.txt"), "engine=Ready");
            }
        }
    }
}
