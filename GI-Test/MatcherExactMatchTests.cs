using System;
using System.Collections.Generic;
using System.IO;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class MatcherExactMatchTests
    {
        [DataTestMethod]
        [DataRow("abcdef", "abcdefg")]
        [DataRow("abcdefgh", "abcdefg")]
        public void SymSpell_CompleteLineWinsOverEitherPrefix(string distractor, string input)
        {
            var keys = new[] { distractor, input };
            var index = new SymSpellIndex(keys, keys, keys);

            Assert.IsTrue(index.TryFindMatch(input, out int slot, out int distance));
            Assert.AreEqual(1, slot, "A prefix is not a better match than the complete input.");
            Assert.AreEqual(0, distance);
        }

        [TestMethod]
        public void SymSpell_ExactHitAvoidsFuzzyCandidateAllocations()
        {
            const string input = "thewindispickingupagain";
            var keys = new[] { input, input + "tomorrow", "thewindhasfinallysettled" };
            var index = new SymSpellIndex(keys, keys, keys);
            for (int i = 0; i < 20; i++) index.TryFindMatch(input, out _, out _);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) index.TryFindMatch(input, out _, out _);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Console.WriteLine($"SymSpell exact hit: {allocated / 1000.0:F0} bytes/call");
            Assert.AreEqual(0L, allocated, "An exact posting-list hit needs no candidate sets or deletion strings.");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Ngram_CompleteLineWinsOverLongerPrefix_InHeapAndMmap(bool mmap)
        {
            // CN disables SymSpell, exercising the n-gram path independently.
            var corpus = new Dictionary<string, string>
            {
                ["欢迎来到蒙德城我们一起去冒险吧"] = "longer",
                ["欢迎来到蒙德城"] = "exact",
            };
            using var stream = new MemoryStream();
            if (mmap)
            {
                MatcherBlobWriter.Write(corpus, null,
                    new MatcherBlobSchema.MatcherMeta { Game = "genshin", Language = "pl" }, stream);
                stream.Position = 0;
            }
            using var matcher = mmap
                ? OptimizedMatcher.LoadFromReaderMmap(MatcherBlobReader.LoadFromStream(stream), "CN")
                : new OptimizedMatcher(corpus, "CN");

            Assert.AreEqual("exact", matcher.FindClosestMatch("欢迎来到蒙德城", out string key));
            Assert.AreEqual("欢迎来到蒙德城", key);
        }

        [TestMethod]
        public void HeaderSeparated_OnlyLineBreaks_ReturnsEmpty()
        {
            using var matcher = new OptimizedMatcher(new Dictionary<string, string>(), "EN");
            var result = matcher.FindMatchWithHeaderSeparated("\n\n", out string key);
            Assert.AreEqual("", result.Content);
            Assert.AreEqual("", key);
        }
    }
}
