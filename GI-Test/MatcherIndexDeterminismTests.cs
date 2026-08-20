// ─────────────────────────────────────────────────────────────────────────────
//  MatcherIndexDeterminismTests.cs
//  ---------------------------------------------------------------------------
//  The matcher resolves ties by "first candidate wins", so the posting lists in
//  the SymSpell delete index and the n-gram index have to be in a canonical
//  order. If they are not, the same recording replayed in two processes picks
//  different dictionary entries.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Reflection;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class MatcherIndexDeterminismTests
    {
        // Many keys per 7-char prefix, so delete-variant buckets are wide enough
        // to span several Parallel.ForEach partitions.
        private const int PrefixCount = 240;
        private const int PerPrefix = 120;

        private static string[] BuildNormalizedKeys()
        {
            var keys = new string[PrefixCount * PerPrefix];
            int k = 0;
            for (int p = 0; p < PrefixCount; p++)
            {
                string prefix = "ab" + p.ToString("D5");
                for (int i = 0; i < PerPrefix; i++)
                {
                    keys[k++] = prefix + i.ToString("D6");
                }
            }
            return keys;
        }

        private static Dictionary<long, int[]> DeleteIndexOf(SymSpellIndex index)
        {
            var field = typeof(SymSpellIndex).GetField(
                "_deleteIndex", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "SymSpellIndex._deleteIndex was renamed.");
            return (Dictionary<long, int[]>)field.GetValue(index);
        }

        [TestMethod]
        public void SymSpellDeleteIndex_PostingListsAreAscending()
        {
            var keys = BuildNormalizedKeys();
            var index = new SymSpellIndex(keys, keys, keys);

            int widest = 0;
            foreach (var kvp in DeleteIndexOf(index))
            {
                int[] posting = kvp.Value;
                if (posting.Length > widest) widest = posting.Length;

                for (int i = 1; i < posting.Length; i++)
                {
                    Assert.IsTrue(posting[i - 1] < posting[i],
                        $"Delete-index bucket 0x{kvp.Key:X} is not ascending at position {i}: " +
                        $"{posting[i - 1]} then {posting[i]}. Parallel partitions were merged in " +
                        "thread-completion order, so the frozen array differs per process and " +
                        "Stage 0 tie-breaks differently on every launch.");
                }
            }

            Assert.IsTrue(widest > 1,
                $"Test corpus produced no multi-entry buckets (widest = {widest}); it cannot " +
                "detect a merge-order problem.");
        }

        [TestMethod]
        public void SymSpellDeleteIndex_TwoBuildsAgreeElementWise()
        {
            var keys = BuildNormalizedKeys();

            var first = DeleteIndexOf(new SymSpellIndex(keys, keys, keys));
            var second = DeleteIndexOf(new SymSpellIndex(keys, keys, keys));

            Assert.AreEqual(first.Count, second.Count, "Bucket count diverged between builds.");

            foreach (var kvp in first)
            {
                Assert.IsTrue(second.TryGetValue(kvp.Key, out int[] other),
                    $"Bucket 0x{kvp.Key:X} missing from the second build.");
                CollectionAssert.AreEqual(kvp.Value, other,
                    $"Bucket 0x{kvp.Key:X} differs element-wise between two builds in one process.");
            }
        }

        [TestMethod]
        public void NgramIndex_PostingListsAreAscending()
        {
            var dict = new Dictionary<string, string>(4000, StringComparer.Ordinal);
            for (int i = 0; i < 4000; i++) dict["shared phrase body " + i.ToString("D5")] = "PL:" + i;

            using (var matcher = new OptimizedMatcher(dict, "EN"))
            {
                var field = typeof(OptimizedMatcher).GetField(
                    "_ngramIndex", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(field, "OptimizedMatcher._ngramIndex was renamed.");

                var ngramIndex =
                    (System.Collections.Frozen.FrozenDictionary<long, int[]>)field.GetValue(matcher);

                int widest = 0;
                foreach (var kvp in ngramIndex)
                {
                    int[] posting = kvp.Value;
                    if (posting.Length > widest) widest = posting.Length;

                    for (int i = 1; i < posting.Length; i++)
                    {
                        Assert.IsTrue(posting[i - 1] < posting[i],
                            $"N-gram bucket 0x{kvp.Key:X} is not ascending at position {i}.");
                    }
                }

                Assert.IsTrue(widest > 1, "Test corpus produced no multi-entry n-gram buckets.");
            }
        }

        [TestMethod]
        public void EntriesArray_FollowsSourceDictionaryOrder()
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < 500; i++) dict["Line number " + i.ToString("D4") + " of dialogue."] = "PL:" + i;

            using (var matcher = new OptimizedMatcher(dict, "EN"))
            {
                var field = typeof(OptimizedMatcher).GetField(
                    "_entries", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(field, "OptimizedMatcher._entries was renamed.");

                Array entries = (Array)field.GetValue(matcher);
                Assert.AreEqual(dict.Count, entries.Length);

                FieldInfo origKeyField = null;
                int slot = 0;
                foreach (var kvp in dict)
                {
                    object entry = entries.GetValue(slot);
                    if (origKeyField == null)
                    {
                        origKeyField = entry.GetType().GetField(
                            "OriginalKey", BindingFlags.Instance | BindingFlags.Public);
                        Assert.IsNotNull(origKeyField, "Entry.OriginalKey was renamed.");
                    }

                    Assert.AreEqual(kvp.Key, (string)origKeyField.GetValue(entry),
                        $"Slot {slot} does not follow source-dictionary order — sorting the " +
                        "posting lists cannot make the matcher deterministic if slot ids drift.");
                    slot++;
                }
            }
        }
    }
}
