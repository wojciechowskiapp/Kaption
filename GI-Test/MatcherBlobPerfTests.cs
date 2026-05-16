// ─────────────────────────────────────────────────────────────────────────────
//  MatcherBlobPerfTests.cs
//  ---------------------------------------------------------------------------
//  Opt-in perf smoke (runs as a normal unit test but with loose asserts so
//  CI on slow hosts doesn't flake). Tracks 1000 warm lookups through
//  FST + ZSTD decompress on a 50k-entry corpus.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class MatcherBlobPerfTests
    {
        [TestMethod]
        public void EndToEnd_50k_Lookups_UnderBudget()
        {
            const int Count = 50_000;
            var corpus = new Dictionary<string, string>(Count, StringComparer.Ordinal);
            var rand = new Random(777);
            var keys = new List<string>(Count);

            for (int i = 0; i < Count; i++)
            {
                var sb = new StringBuilder(12);
                for (int k = 0; k < 12; k++) sb.Append((char)('a' + rand.Next(26)));
                string key = sb.ToString() + "_" + i.ToString("D6");
                corpus[key] = $"Witaj podróżniku, tekst polski numer {i}.";
                keys.Add(key);
            }

            var ms = new MemoryStream();
            var meta = new MatcherBlobSchema.MatcherMeta
            {
                CorpusVersion = "perf",
                Game = "genshin",
                Language = "pl",
            };

            var buildSw = Stopwatch.StartNew();
            MatcherBlobWriter.Write(corpus, trainedDictionary: null, meta: meta, output: ms);
            buildSw.Stop();
            long blobSize = ms.Length;
            ms.Position = 0;

            var loadSw = Stopwatch.StartNew();
            using (var reader = MatcherBlobReader.LoadFromStream(ms))
            {
                loadSw.Stop();

                // Warm.
                for (int i = 0; i < 100; i++)
                    reader.TryGetValue(keys[i % keys.Count], out _);

                var lookupSw = Stopwatch.StartNew();
                int hits = 0;
                for (int i = 0; i < 1000; i++)
                {
                    if (reader.TryGetValue(keys[(i * 37) % keys.Count], out _)) hits++;
                }
                lookupSw.Stop();

                Trace.WriteLine(
                    $"[perf] build={buildSw.ElapsedMilliseconds}ms " +
                    $"load={loadSw.ElapsedMilliseconds}ms " +
                    $"1000x lookup={lookupSw.ElapsedMilliseconds}ms " +
                    $"hits={hits} " +
                    $"blob={blobSize / 1024} KB");

                Assert.AreEqual(1000, hits, "Every sampled key should round-trip.");
                // 50 ms budget for 1000 lookups on the target hardware class.
                // Some CI hosts are much slower, so stick to a loose 500 ms
                // ceiling; regressions will still surface.
                Assert.IsTrue(lookupSw.ElapsedMilliseconds < 500,
                    $"1000 lookups took {lookupSw.ElapsedMilliseconds} ms — beyond 500 ms ceiling.");

                // Save the numbers somewhere durable so we can read them in
                // the agent report. vstest swallows Trace.WriteLine; write
                // to the temp dir instead.
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "kmx-perf.txt"),
                    $"build={buildSw.ElapsedMilliseconds}ms " +
                    $"load={loadSw.ElapsedMilliseconds}ms " +
                    $"1000xLookup={lookupSw.ElapsedMilliseconds}ms " +
                    $"blob={blobSize} bytes " +
                    $"entries={Count}");
            }
        }
    }
}
