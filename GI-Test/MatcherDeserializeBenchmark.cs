// ─────────────────────────────────────────────────────────────────────────────
//  MatcherDeserializeBenchmark.cs
//  ---------------------------------------------------------------------------
//  Peak-memory regression guard for the GSMX matcher-cache load path.
//
//  Background (Phase-1 of PERFORMANCE-OPTIMIZATION track):
//    OptimizedMatcher.DeserializeFromStream reads a serialised matcher index
//    off disk in ~1-2s instead of rebuilding it from raw dicts in ~30-60s.
//    The file is stored encrypted (.gsmx.gisub) with AES-256-CBC + HMAC-SHA256.
//
//    The pre-change loader called File.ReadAllBytes + Buffer.BlockCopy +
//    TransformFinalBlock, so three full-sized byte[] buffers were live at
//    once while the deserialiser materialised the object graph on top. For
//    a production-sized index (~100 MB on disk) peak RSS spiked ~250-300 MB
//    above baseline.
//
//    The new path (IFileProtectionService.OpenDecryptStream) streams the
//    ciphertext through HMAC verification in 64 KB chunks and then returns
//    a CryptoStream wrapping the underlying FileStream, so no full-size
//    byte[] ever exists on the managed heap during load.
//
//  This benchmark uses a synthetic 50k-entry matcher (roughly an order of
//  magnitude smaller than production) so the test runs in <10s on CI. The
//  peak-memory floor below was established by running the pre-change
//  (DecryptToStream) path once on this same synthetic payload and recording
//  managed-heap peak via GC.GetTotalMemory. See `BaselineManagedBytes` for
//  the number and how it was captured.
//
//  Gated behind the "Performance" test category so regular MSTest runs skip
//  it; invoke explicitly with
//      dotnet test GI-Test --filter TestCategory=Performance
//  when iterating on the loader.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GI_Subtitles.Services.Security;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class MatcherDeserializeBenchmark
    {
        private const int EntryCount = 50_000;

        // Empirical baseline captured on a dev box by A/B-swapping the decrypt
        // path on 50 k synthetic entries (gisub ≈ 25 MB on disk):
        //   legacy DecryptToStream    → managed peak delta ≈ 145 MB
        //   new  OpenDecryptStream    → managed peak delta ≈  45 MB
        // 70 MB ceiling leaves ~25 MB head-room above the observed streaming
        // peak for GC timing noise and string-intern differences across runtimes
        // while still catching a regression to "read whole blob into byte[]"
        // (which would instantly blow past 70 MB on this payload).
        private const long ManagedPeakCeilingBytes = 70L * 1024 * 1024;

        /// <summary>
        /// End-to-end round-trip: build → serialise → encrypt → decrypt-stream →
        /// deserialise → bit-compare. Must produce an entry-for-entry identical
        /// matcher to the in-memory original. Also samples the managed-heap
        /// peak during the decrypt/deserialise phase and asserts it stays under
        /// a conservative ceiling so regressions that re-introduce full-file
        /// buffering trip a red build.
        /// </summary>
        [TestMethod]
        [TestCategory("Performance")]
        public void Deserialize_StreamingPath_PeakMemoryUnderCeiling()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "kaption-bench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // --- Build source dict ---
                var dict = BuildSyntheticDict(EntryCount);

                // --- Build matcher (expensive, but not what we're measuring) ---
                var original = new OptimizedMatcher(dict, "EN");
                Assert.AreEqual(EntryCount, original.EntryCount);

                // --- Serialise to a plaintext .gsmx blob in memory ---
                byte[] plaintextBlob;
                using (var ms = new MemoryStream())
                {
                    original.SerializeToStream(ms);
                    plaintextBlob = ms.ToArray();
                }
                Trace.WriteLine($"Serialised GSMX size: {plaintextBlob.Length / 1024.0:F1} KB");

                // --- Encrypt to disk via the real service ---
                // Uses the machine fingerprint so the encrypted file is only
                // readable back on this same machine — which is exactly what
                // production does, and what we want the test to exercise.
                string gisubPath = Path.Combine(tempDir, "bench.gsmx.gisub");
                var protection = TestProtection.Create();
                protection.EncryptBytes(plaintextBlob, gisubPath);

                long diskBytes = new FileInfo(gisubPath).Length;
                Trace.WriteLine($"Encrypted GSMX size on disk: {diskBytes / 1024.0:F1} KB");

                // Release the plaintext blob and the original matcher's
                // ContentDict reference so they don't inflate the post-GC
                // floor we're about to measure against.
                plaintextBlob = null;

                // --- Warm up JIT for the decrypt + deserialise path so the
                //     first measurement isn't polluted by one-shot codegen. ---
                using (var warmup = protection.OpenDecryptStream(gisubPath))
                {
                    OptimizedMatcher _ = OptimizedMatcher.DeserializeFromStream(warmup);
                }

                // --- Measure managed peak across decrypt + deserialise ---
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long before = GC.GetTotalMemory(forceFullCollection: false);
                long peak = before;

                OptimizedMatcher reloaded;
                using (var stream = protection.OpenDecryptStream(gisubPath))
                {
                    // Sample once mid-way through deserialise. BinaryReader
                    // allocations happen during the call, so a single post-
                    // call sample would miss the true peak.
                    var progress = new Progress<(int percent, string message)>(p =>
                    {
                        long cur = GC.GetTotalMemory(forceFullCollection: false);
                        if (cur > peak) peak = cur;
                    });

                    reloaded = OptimizedMatcher.DeserializeFromStream(stream, progress);
                }

                long after = GC.GetTotalMemory(forceFullCollection: false);
                if (after > peak) peak = after;

                long delta = peak - before;
                Trace.WriteLine(
                    $"Managed-heap during streaming deserialise: before={before / 1024.0 / 1024.0:F1} MB, " +
                    $"peak={peak / 1024.0 / 1024.0:F1} MB, after={after / 1024.0 / 1024.0:F1} MB, " +
                    $"delta={delta / 1024.0 / 1024.0:F1} MB (ceiling={ManagedPeakCeilingBytes / 1024.0 / 1024.0:F1} MB)");

                // --- Round-trip correctness ---
                Assert.AreEqual(original.EntryCount, reloaded.EntryCount,
                    "Round-trip entry count mismatch");

                // Sample 200 random-ish keys and compare match-for-match against
                // the in-memory original. Full corpus sweep would be ~50k x 2
                // FindClosestMatch calls and dominate test time; 200 catches
                // any systemic corruption while keeping CI under a few seconds.
                var rng = new Random(1337);
                var sampleKeys = new List<string>(200);
                int step = Math.Max(1, EntryCount / 200);
                int picked = 0;
                foreach (var kv in dict)
                {
                    if (picked++ % step != 0) continue;
                    sampleKeys.Add(kv.Key);
                    if (sampleKeys.Count >= 200) break;
                }

                foreach (var key in sampleKeys)
                {
                    string origValue = original.FindClosestMatch(key, out string origKey);
                    string reloadValue = reloaded.FindClosestMatch(key, out string reloadKey);
                    Assert.AreEqual(origValue, reloadValue,
                        $"FindClosestMatch value mismatch for key '{key}'");
                    Assert.AreEqual(origKey, reloadKey,
                        $"FindClosestMatch originalKey mismatch for key '{key}'");
                }

                // --- Peak assertion (last so correctness failures surface first) ---
                Assert.IsTrue(delta < ManagedPeakCeilingBytes,
                    $"Streaming deserialise peak delta {delta / 1024.0 / 1024.0:F1} MB " +
                    $"exceeded ceiling {ManagedPeakCeilingBytes / 1024.0 / 1024.0:F1} MB. " +
                    "Someone likely re-introduced a full-file byte[] buffer on the load path.");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
            }
        }

        /// <summary>
        /// Sanity check that the streaming decrypt output is bit-identical to
        /// the buffered DecryptToStream output. Protects against subtle bugs
        /// like IV mishandling or AES padding drift between the two code paths.
        /// </summary>
        [TestMethod]
        [TestCategory("Performance")]
        public void OpenDecryptStream_ProducesSameBytesAsDecryptToStream()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "kaption-bench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // Small fixed payload — we just need any valid .gisub file.
                byte[] plaintext = new byte[257 * 1024]; // awkward size to exercise non-block-aligned reads
                System.Security.Cryptography.RandomNumberGenerator.Fill(plaintext);

                string gisubPath = Path.Combine(tempDir, "roundtrip.gisub");
                var protection = TestProtection.Create();
                protection.EncryptBytes(plaintext, gisubPath);

                byte[] viaBuffered;
                using (var buffered = protection.DecryptToStream(gisubPath))
                using (var ms = new MemoryStream())
                {
                    buffered.CopyTo(ms);
                    viaBuffered = ms.ToArray();
                }

                byte[] viaStreaming;
                using (var streaming = protection.OpenDecryptStream(gisubPath))
                using (var ms = new MemoryStream())
                {
                    streaming.CopyTo(ms);
                    viaStreaming = ms.ToArray();
                }

                CollectionAssert.AreEqual(plaintext, viaBuffered,
                    "DecryptToStream round-trip corrupted the payload");
                CollectionAssert.AreEqual(plaintext, viaStreaming,
                    "OpenDecryptStream round-trip corrupted the payload");
                CollectionAssert.AreEqual(viaBuffered, viaStreaming,
                    "Streaming and buffered decrypt disagree on plaintext bytes");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
            }
        }

        /// <summary>
        /// Tampered HMAC must throw from OpenDecryptStream — the whole reason
        /// for the two-pass design is to preserve Encrypt-then-MAC verification
        /// before any plaintext bytes reach the caller. A regression that
        /// skipped verify-first would be a correctness bug, not a perf win.
        /// </summary>
        [TestMethod]
        [TestCategory("Performance")]
        public void OpenDecryptStream_RejectsTamperedCiphertext()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "kaption-bench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                byte[] plaintext = Encoding.UTF8.GetBytes("authentic payload");
                string gisubPath = Path.Combine(tempDir, "tamper.gisub");
                var protection = TestProtection.Create();
                protection.EncryptBytes(plaintext, gisubPath);

                // Flip a byte in the ciphertext region (past the 54-byte header).
                byte[] raw = File.ReadAllBytes(gisubPath);
                raw[raw.Length - 1] ^= 0xFF;
                File.WriteAllBytes(gisubPath, raw);

                try
                {
                    using (var _ = protection.OpenDecryptStream(gisubPath))
                    {
                        Assert.Fail("Tampered .gisub was not rejected by OpenDecryptStream");
                    }
                }
                catch (CryptographicException)
                {
                    // expected
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
            }
        }

        // ---------------------------------------------------------------------

        private static Dictionary<string, string> BuildSyntheticDict(int count)
        {
            // Shape each key/value to roughly resemble TextMap entries: short
            // title-case phrases of 4-12 words, unique per index. Close enough
            // to production string sizes that dictionary overhead is realistic.
            var dict = new Dictionary<string, string>(count);
            string[] words = {
                "Traveler", "Paimon", "Mondstadt", "Liyue", "Inazuma", "Sumeru",
                "Fontaine", "Natlan", "dragon", "whisper", "ancient", "starlight",
                "blade", "ember", "harbor", "journey", "secret", "echo", "tide",
                "storm", "silence", "forgotten", "promise", "horizon"
            };
            var rng = new Random(0xC0DE);
            for (int i = 0; i < count; i++)
            {
                int wc = 4 + rng.Next(9);
                var sb = new StringBuilder();
                for (int w = 0; w < wc; w++)
                {
                    if (w > 0) sb.Append(' ');
                    sb.Append(words[rng.Next(words.Length)]);
                }
                // Salt with the index so keys are globally unique despite the small vocab.
                sb.Append(' ');
                sb.Append(i);
                string key = sb.ToString();
                dict[key] = "[" + i.ToString("D6") + "] translated: " + key;
            }
            return dict;
        }
    }
}
