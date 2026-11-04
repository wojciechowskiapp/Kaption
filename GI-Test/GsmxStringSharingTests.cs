using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The GSMX matcher cache loads next to the pack dictionary (contentDict), which stays alive all
    /// session. With sharing on, the matcher reuses the dictionary's key and value instances instead of
    /// a second copy. Only reference identity may change: content, order and matching stay the same.
    /// </summary>
    [TestClass]
    public class GsmxStringSharingTests
    {
        private static readonly string LongLine =
            "But disposing of Vache's reagents isn't enough to solve the medical supply shortage. Many of my patients " +
            "can't be seen promptly, and my stock of medicine is running low. Żółć, gęślą jaźń — and then some more.";

        private static Dictionary<string, string> SampleCorpus() => new Dictionary<string, string>
        {
            ["Yes, you."] = "Tak, ty.",
            ["Yes, you did! Why are you denying it? It was right before I went on that rant about the moody factory owner!"] =
                "Tak, zrobiłeś to! Dlaczego się wypierasz? To było tuż przed moją tyradą o ponurym właścicielu fabryki!",
            ["Yes, you are right."] = "Tak, masz rację.",
            ["Might I ask what brings you two here?"] = "Czy mogę spytać, co was tutaj sprowadza?",
            ["Might I ask what this is?"] = "Czy mogę zapytać, co to jest?",
            ["I agree."] = "Zgadzam się.",
            ["I agree. It might also help your case if you stopped challenging people to duels all the time."] =
                "Zgadzam się. Twojej sprawie mogłoby też pomóc, gdybyś przestał cały czas wyzywać ludzi na pojedynki.",
            ["Over here.\nQuick!"] = "Tutaj.\nSzybko!",
            ["Terho"] = "Terho",
            ["Oh..."] = "Och...",
            ["Max HP"] = "",
            [LongLine] = "Ale usunięcie odczynników Vache nie wystarczy, by rozwiązać problem braku leków. " +
                         "Wielu moich pacjentów nie może zostać przyjętych na czas, a moje zapasy się kończą. Źdźbło.",
        };

        private static readonly string[] Probes =
        {
            "Yes, you.",
            "Yes, you did! Why are",
            "Yes, you did! Why are you denying it? It was right before I went on that rant about the moody factory owner!",
            "Might I ask",
            "Might I ask what brings you two here",
            "I agree.",
            "I agree. It might also help your case if you stopped",
            "l agree. It might a1so help your case if you stopped challenging people to duels all the time.",
            "Over here.\nQuick!",
            "Terho",
            "Oh...",
            "Max HP",
            LongLine,
            LongLine.Substring(0, 90),
            "Something that is not in the pack at all.",
        };

        [TestMethod]
        public void Sharing_ReusesTheDictionarysInstances_ForEveryEntry()
        {
            Dictionary<string, string> dict = SampleCorpus();
            byte[] payload = Serialize(Copy(dict));

            using OptimizedMatcher shared = Deserialize(payload, dict);
            var lookup = dict.GetAlternateLookup<ReadOnlySpan<char>>();
            Assert.AreEqual(dict.Count, shared.EntryCount);
            for (int slot = 0; slot < shared.EntryCount; slot++)
            {
                string key = shared.OriginalKeyAt(slot);
                Assert.IsTrue(lookup.TryGetValue(key.AsSpan(), out string dictKey, out string dictValue), key);
                Assert.AreSame(dictKey, key, $"key of slot {slot}");
                Assert.AreSame(dictValue, shared.ValueAt(slot), $"value of slot {slot}");
            }
        }

        [TestMethod]
        public void Sharing_Off_KeepsFreshStrings()
        {
            Dictionary<string, string> dict = SampleCorpus();
            byte[] payload = Serialize(Copy(dict));

            using OptimizedMatcher plain = Deserialize(payload, null);
            var lookup = dict.GetAlternateLookup<ReadOnlySpan<char>>();
            for (int slot = 0; slot < plain.EntryCount; slot++)
            {
                string key = plain.OriginalKeyAt(slot);
                lookup.TryGetValue(key.AsSpan(), out string dictKey, out _);
                if (key.Length > 0)
                    Assert.AreNotSame(dictKey, key, "without sharing the cache holds its own copy");
            }
        }

        [TestMethod]
        public void Sharing_KeepsTheCachesValue_WhenThePackChangedIt()
        {
            Dictionary<string, string> cached = SampleCorpus();
            byte[] payload = Serialize(Copy(cached));

            // Same key set, one value edited since the cache was written (count-only cache validation lets it through).
            Dictionary<string, string> live = SampleCorpus();
            live["I agree."] = "Też tak uważam.";

            using OptimizedMatcher shared = Deserialize(payload, live);
            int slot = SlotOf(shared, "I agree.");
            Assert.AreEqual("Zgadzam się.", shared.ValueAt(slot), "the cache's own text must survive");
            Assert.AreNotSame(live["I agree."], shared.ValueAt(slot));
            var lookup = live.GetAlternateLookup<ReadOnlySpan<char>>();
            lookup.TryGetValue("I agree.".AsSpan(), out string liveKey, out _);
            Assert.AreSame(liveKey, shared.OriginalKeyAt(slot), "the key is still shared");
        }

        [TestMethod]
        public void Sharing_WithAComparerWithoutSpanLookups_SharesValuesOnly()
        {
            var dict = new Dictionary<string, string>(SampleCorpus(), new OrdinalWithoutSpans());
            byte[] payload = Serialize(Copy(SampleCorpus()));

            using OptimizedMatcher shared = Deserialize(payload, dict);
            int slot = SlotOf(shared, "Might I ask what this is?");
            Assert.AreSame(dict["Might I ask what this is?"], shared.ValueAt(slot));
            using OptimizedMatcher plain = Deserialize(payload, null);
            AssertMatchesIdentical(plain, shared);
        }

        [TestMethod]
        public void Matching_IsIdentical_WithSharingOnAndOff()
        {
            Dictionary<string, string> dict = SampleCorpus();
            byte[] payload = Serialize(Copy(dict));

            using OptimizedMatcher plain = Deserialize(payload, null);
            using OptimizedMatcher shared = Deserialize(payload, dict);
            Assert.AreEqual(plain.EntryCount, shared.EntryCount);
            for (int slot = 0; slot < plain.EntryCount; slot++)
            {
                Assert.AreEqual(plain.OriginalKeyAt(slot), shared.OriginalKeyAt(slot), $"order of slot {slot}");
                Assert.AreEqual(plain.ValueAt(slot), shared.ValueAt(slot), $"value of slot {slot}");
            }
            AssertMatchesIdentical(plain, shared);
        }

        /// <summary>
        /// Retained managed heap of a 50k-entry cache load, with and without sharing, while the pack
        /// dictionary is alive (as contentDict is). Prints the numbers for the session record.
        /// </summary>
        [TestMethod]
        [DoNotParallelize]
        public void Sharing_DropsTheDuplicateStrings_On50kEntries()
        {
            const int Entries = 50_000;
            var dict = new Dictionary<string, string>(Entries);
            long stringBytes = 0;
            for (int i = 0; i < Entries; i++)
            {
                string key = $"Line {i}: the traveler asked the guard at gate {i % 97} about the road to the harbour.";
                string value = $"Linia {i}: podróżnik zapytał strażnika przy bramie {i % 97} o drogę do portu, żółtą ścieżką.";
                dict[key] = value;
                stringBytes += StringSize(key) + StringSize(value);
            }
            byte[] payload = Serialize(Copy(dict));

            (long plainRetained, long plainAllocated, double plainMs, OptimizedMatcher plain) = Measure(payload, null);
            (long sharedRetained, long sharedAllocated, double sharedMs, OptimizedMatcher shared) = Measure(payload, dict);
            try
            {
                long saved = plainRetained - sharedRetained;
                Console.WriteLine($"{Entries:N0} entries, payload {payload.Length / 1024.0 / 1024.0:F1} MB: " +
                                  $"retained {plainRetained / 1048576.0:F1} MB without sharing, " +
                                  $"{sharedRetained / 1048576.0:F1} MB with it (saved {saved / 1048576.0:F1} MB; " +
                                  $"key+value strings {stringBytes / 1048576.0:F1} MB). Allocated during load " +
                                  $"{plainAllocated / 1048576.0:F1} MB vs {sharedAllocated / 1048576.0:F1} MB; " +
                                  $"load {plainMs:F0} ms vs {sharedMs:F0} ms.");
                Assert.IsTrue(saved >= stringBytes * 8 / 10,
                    $"sharing saved {saved:N0} B of an expected ~{stringBytes:N0} B");
                Assert.IsTrue(sharedAllocated < plainAllocated, "a hit must not allocate the strings it reuses");
            }
            finally
            {
                plain.Dispose();
                shared.Dispose();
                GC.KeepAlive(dict);
            }
        }

        private static (long retained, long allocated, double ms, OptimizedMatcher matcher) Measure(
            byte[] payload, Dictionary<string, string> share)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long before = GC.GetTotalMemory(forceFullCollection: true);
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            OptimizedMatcher matcher = Deserialize(payload, share);
            sw.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            long after = GC.GetTotalMemory(forceFullCollection: true);
            return (after - before, allocated, sw.Elapsed.TotalMilliseconds, matcher);
        }

        // UTF-16 string object on x64: 8 B header + 8 B method table + 4 B length + 2 B per char + terminator, 8-aligned.
        private static long StringSize(string s) => (22 + 2L * s.Length + 7) & ~7L;

        private static void AssertMatchesIdentical(OptimizedMatcher expected, OptimizedMatcher actual)
        {
            foreach (string probe in Probes)
            {
                string e = expected.FindClosestMatch(probe, out string eKey, out bool ePartial, out bool eAmbiguous);
                string a = actual.FindClosestMatch(probe, out string aKey, out bool aPartial, out bool aAmbiguous);
                Assert.AreEqual(e, a, probe);
                Assert.AreEqual(eKey, aKey, probe);
                Assert.AreEqual(ePartial, aPartial, probe);
                Assert.AreEqual(eAmbiguous, aAmbiguous, probe);

                bool eExact = expected.TryGetExactValue(probe, out string eValue);
                bool aExact = actual.TryGetExactValue(probe, out string aValue);
                Assert.AreEqual(eExact, aExact, probe);
                Assert.AreEqual(eValue, aValue, probe);
            }
        }

        private static int SlotOf(OptimizedMatcher matcher, string key)
        {
            for (int slot = 0; slot < matcher.EntryCount; slot++)
                if (matcher.OriginalKeyAt(slot) == key) return slot;
            Assert.Fail($"'{key}' is not in the matcher");
            return -1;
        }

        // Fresh string instances with the same text, so identity proves where a string came from.
        private static Dictionary<string, string> Copy(Dictionary<string, string> source)
        {
            var copy = new Dictionary<string, string>(source.Count);
            foreach (var kv in source)
                copy[new string(kv.Key.AsSpan())] = new string(kv.Value.AsSpan());
            return copy;
        }

        private static byte[] Serialize(Dictionary<string, string> corpus)
        {
            using var matcher = new OptimizedMatcher(corpus, "EN");
            using var ms = new MemoryStream();
            matcher.SerializeToStream(ms);
            return ms.ToArray();
        }

        private static OptimizedMatcher Deserialize(byte[] payload, Dictionary<string, string> share)
        {
            using var ms = new MemoryStream(payload, writable: false);
            return OptimizedMatcher.DeserializeFromStream(ms, null, share);
        }

        private sealed class OrdinalWithoutSpans : IEqualityComparer<string>
        {
            public bool Equals(string x, string y) => string.Equals(x, y, StringComparison.Ordinal);
            public int GetHashCode(string obj) => StringComparer.Ordinal.GetHashCode(obj);
        }
    }
}
