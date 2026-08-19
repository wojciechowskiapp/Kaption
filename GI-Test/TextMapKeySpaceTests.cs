// ─────────────────────────────────────────────────────────────────────────────
//  TextMapKeySpaceTests.cs
//  ---------------------------------------------------------------------------
//  The publish pipeline joins the EN TextMap and the target-language TextMap
//  BY ID (tools/merge-textmap.cjs). Every game therefore has to have exactly
//  one EN key space end to end, and ZZZ is the game where that is easy to get
//  wrong: upstream ships a string-keyed TextMap
//  ("Main_Chat_Chapter01_3000024_01") while everything Kaption produces —
//  the dialogue graph, the bundle's textmap_en section, the file
//  translate_textmap_zzz.py writes — is keyed by numeric content hashes.
//
//  Feed merge-textmap.cjs the wrong one and zero ids overlap. Before the
//  guard below, it printed "REFUSING TO WRITE" *after* fs.writeFileSync, so
//  the empty pack was already on disk for publish-translation.sh to encrypt
//  and upload. These tests pin both halves: the refusal, and the fact that
//  nothing is written when it refuses.
//
//  Genshin and HSR ride on the same guard — a --en from a different patch
//  than --in is the same failure with a different cause.
//
//  These shell out to the real script, because the bug lived in the script's
//  statement order and only an end-to-end run can catch that.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace GI_Test
{
    [TestClass]
    public class TextMapKeySpaceTests
    {
        private const int EntryCount = 2000;   // comfortably over merge-textmap's 1000 floor

        private string _dir;
        private string _repoRoot;

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "TextMapKeySpace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _repoRoot = FindRepoRoot();
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best-effort */ }
        }

        // ── fixtures ─────────────────────────────────────────────────────

        private static string EnglishAt(int i) => $"Line number {i} spoken by somebody in a scene.";
        private static string PolishAt(int i) => $"Kwestia numer {i} wypowiedziana przez kogos w scenie.";

        /// <summary>What build-gamedata-zzz.cjs emits, and what
        /// translate_textmap_zzz.py is fed: numeric content-hash keys.</summary>
        private string WriteNumericKeyedEn()
        {
            var map = new Dictionary<string, string>();
            for (int i = 0; i < EntryCount; i++) map[(1_000_000 + i).ToString()] = EnglishAt(i);
            return Write("TextMapEN-numeric.json", map);
        }

        /// <summary>What translate_textmap_zzz.py writes: same ids, translated values.</summary>
        private string WriteNumericKeyedPl()
        {
            var map = new Dictionary<string, string>();
            for (int i = 0; i < EntryCount; i++) map[(1_000_000 + i).ToString()] = PolishAt(i);
            return Write("TextMapPL-numeric.json", map);
        }

        /// <summary>What ZenlessData ships. Same English text, different key space.</summary>
        private string WriteStringKeyedEn()
        {
            var map = new Dictionary<string, string>();
            for (int i = 0; i < EntryCount; i++)
                map[$"Main_Chat_Chapter01_{3_000_000 + i}_01"] = EnglishAt(i);
            return Write("TextMapEN-upstream.json", map);
        }

        private string Write(string name, Dictionary<string, string> map)
        {
            string p = Path.Combine(_dir, name);
            File.WriteAllText(p, JsonConvert.SerializeObject(map), new UTF8Encoding(false));
            return p;
        }

        // ── the regression ───────────────────────────────────────────────

        /// <summary>
        /// The happy path, so the failure test below can't pass just because
        /// the script is broken for everyone.
        /// </summary>
        [TestMethod]
        public void MergeTextMap_MatchingKeySpaces_WritesThePack()
        {
            string outPath = Path.Combine(_dir, "DictPL.json");
            var run = RunMerge(WriteNumericKeyedEn(), WriteNumericKeyedPl(), outPath);

            Assert.AreEqual(0, run.ExitCode,
                $"merge should succeed when both sides share a key space.\n{run.Output}");
            Assert.IsTrue(File.Exists(outPath), "merged pack must be written on success");

            var merged = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(outPath));
            Assert.AreEqual(EntryCount, merged.Count);
            // The published pack is keyed by ENGLISH TEXT — that's the whole
            // point of merging at publish time. The ids only have to agree
            // long enough to perform the join.
            Assert.AreEqual(PolishAt(7), merged[EnglishAt(7)]);
        }

        /// <summary>
        /// THE test. Hand the merge the string-keyed upstream ZZZ TextMap
        /// against a numeric-keyed PL file — the exact mistake Publish Studio
        /// Phase 7 used to be set up to make — and it must refuse AND leave
        /// nothing behind.
        /// </summary>
        [TestMethod]
        public void MergeTextMap_MismatchedKeySpaces_RefusesAndWritesNothing()
        {
            string outPath = Path.Combine(_dir, "DictPL.json");
            var run = RunMerge(WriteStringKeyedEn(), WriteNumericKeyedPl(), outPath);

            Assert.AreNotEqual(0, run.ExitCode,
                "a zero-overlap join must fail the publish, not produce an empty pack");
            Assert.IsFalse(File.Exists(outPath),
                "REFUSING TO WRITE must actually refuse to write — the check used to run " +
                "after fs.writeFileSync, leaving the empty pack on disk for the next step");

            // The diagnostic is the only thing standing between an operator and
            // an afternoon of confusion, so pin its substance.
            StringAssert.Contains(run.Output, "REFUSING TO WRITE");
            StringAssert.Contains(run.Output, "key space");
            StringAssert.Contains(run.Output, "Main_Chat_Chapter01_3000000_01",
                "the failure must show sample keys from both sides so the mismatch is visible");
        }

        /// <summary>
        /// Same guard, the Genshin/HSR-shaped cause: EN and target from
        /// different patches, so the hash ids have been reassigned.
        /// </summary>
        [TestMethod]
        public void MergeTextMap_IdsFromDifferentPatches_RefusesAndWritesNothing()
        {
            var shifted = new Dictionary<string, string>();
            for (int i = 0; i < EntryCount; i++) shifted[(9_000_000 + i).ToString()] = PolishAt(i);

            string outPath = Path.Combine(_dir, "DictPL.json");
            var run = RunMerge(WriteNumericKeyedEn(), Write("TextMapPL-shifted.json", shifted), outPath);

            Assert.AreNotEqual(0, run.ExitCode);
            Assert.IsFalse(File.Exists(outPath));
        }

        // ── plumbing ─────────────────────────────────────────────────────

        private sealed class RunOutcome
        {
            public int ExitCode;
            public string Output = "";
        }

        private RunOutcome RunMerge(string enPath, string inPath, string outPath)
        {
            string script = Path.Combine(_repoRoot, "tools", "merge-textmap.cjs");
            Assert.IsTrue(File.Exists(script), $"merge-textmap.cjs not found at {script}");

            var psi = new ProcessStartInfo("node")
            {
                WorkingDirectory = _repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in new[] { script, "--en", enPath, "--in", inPath, "--out", outPath })
                psi.ArgumentList.Add(a);

            Process proc;
            try
            {
                proc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"node is not on PATH, so the publish tools can't be exercised: {ex.Message}");
                throw;
            }

            using (proc)
            {
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                return new RunOutcome { ExitCode = proc.ExitCode, Output = stdout + stderr };
            }
        }

        /// <summary>
        /// Walk up from the test assembly until we find the repo (identified by
        /// tools/merge-textmap.cjs). Beats hard-coding a path that breaks the
        /// moment someone clones elsewhere.
        /// </summary>
        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tools", "merge-textmap.cjs")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            Assert.Inconclusive("Could not locate the repo root from the test output directory.");
            return null;
        }
    }
}
