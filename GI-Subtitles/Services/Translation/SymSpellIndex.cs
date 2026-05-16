using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// SymSpell-based fast text matching for OCR error correction.
    /// Uses precalculated delete variants to find matches within edit distance 2 in ~5μs.
    ///
    /// Algorithm: For each dictionary entry, generate all possible character deletions
    /// (up to maxEditDistance) of the first prefixLength characters. At query time,
    /// generate deletions of the input prefix and look up candidates. Verify candidates
    /// with actual Levenshtein distance.
    ///
    /// English-only. Designed as a fast pre-filter before the n-gram system.
    /// Can be enabled/disabled via Config without affecting other matching.
    /// </summary>
    public class SymSpellIndex
    {
        // FNV-1a constants (same as OptimizedMatcher for consistency)
        private const ulong FNV_OFFSET_BASIS = 14695981039346656037UL;
        private const ulong FNV_PRIME = 1099511628211UL;

        private readonly int _maxEditDistance;
        private readonly int _prefixLength;

        // Delete variant hash → list of entry indices (frozen after build)
        private readonly Dictionary<long, int[]> _deleteIndex;

        // Entry data (parallel arrays for cache locality)
        private readonly string[] _normalizedKeys;
        private readonly string[] _originalKeys;
        private readonly string[] _values;
        private readonly int _entryCount;

        public bool IsReady { get; private set; }

        /// <summary>
        /// Build a SymSpell index from the dictionary.
        /// </summary>
        /// <param name="normalizedKeys">Pre-normalized dictionary keys (same normalization as OptimizedMatcher)</param>
        /// <param name="originalKeys">Original (non-normalized) keys for lookup</param>
        /// <param name="values">Translation values</param>
        /// <param name="maxEditDistance">Max edit distance for matching (default 2)</param>
        /// <param name="prefixLength">Prefix length to index (default 7)</param>
        public SymSpellIndex(
            string[] normalizedKeys,
            string[] originalKeys,
            string[] values,
            int maxEditDistance = 2,
            int prefixLength = 7)
        {
            _maxEditDistance = maxEditDistance;
            _prefixLength = prefixLength;
            _normalizedKeys = normalizedKeys;
            _originalKeys = originalKeys;
            _values = values;
            _entryCount = normalizedKeys.Length;

            _deleteIndex = BuildIndex(null);
            IsReady = true;
        }

        /// <summary>
        /// Build a SymSpell index with progress reporting.
        /// </summary>
        public SymSpellIndex(
            string[] normalizedKeys,
            string[] originalKeys,
            string[] values,
            IProgress<(int percent, string message)> progress,
            int maxEditDistance = 2,
            int prefixLength = 7)
        {
            _maxEditDistance = maxEditDistance;
            _prefixLength = prefixLength;
            _normalizedKeys = normalizedKeys;
            _originalKeys = originalKeys;
            _values = values;
            _entryCount = normalizedKeys.Length;

            _deleteIndex = BuildIndex(progress);
            IsReady = true;
        }

        private Dictionary<long, int[]> BuildIndex(IProgress<(int percent, string message)> progress)
        {
            // Empty-dictionary guard: on a fresh install the TextMap JSONs
            // haven't been downloaded yet, so the caller hands us zero
            // entries. Partitioner.Create(0, 0) throws
            // ArgumentOutOfRangeException on `toExclusive` — that used to
            // bubble all the way up to the Download-path try/catch and
            // leave the matcher unusable for the rest of the session. A
            // zero-entry SymSpell is trivially "nothing matches anything",
            // which is the correct behavior until the dictionary arrives.
            if (_entryCount <= 0)
            {
                progress?.Report((100, "SymSpell: no entries, skipping index build."));
                return new Dictionary<long, int[]>(capacity: 0);
            }

            // Phase 1: Generate delete variants in parallel using thread-local dictionaries.
            // Each thread builds its own partial index, then we merge at the end.
            // This is ~3-4x faster than sequential on a 4+ core CPU.
            int processed = 0;
            var partialIndexes = new ConcurrentBag<Dictionary<long, List<int>>>();

            Parallel.ForEach(
                Partitioner.Create(0, _entryCount),
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                () => new Dictionary<long, List<int>>(4096), // thread-local
                (range, state, localIndex) =>
                {
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        string key = _normalizedKeys[i];
                        if (string.IsNullOrEmpty(key)) continue;

                        int prefixLen = Math.Min(key.Length, _prefixLength);

                        var deletes = new HashSet<long>();
                        GenerateDeletes(key, 0, prefixLen, _maxEditDistance, deletes);
                        deletes.Add(HashSpan(key, 0, prefixLen));

                        foreach (long hash in deletes)
                        {
                            if (!localIndex.TryGetValue(hash, out var list))
                            {
                                list = new List<int>(1);
                                localIndex[hash] = list;
                            }
                            list.Add(i);
                        }
                    }

                    if (progress != null)
                    {
                        int done = Interlocked.Add(ref processed, range.Item2 - range.Item1);
                        int pct = (int)(50 + (done * 20.0 / _entryCount));
                        progress.Report((pct, $"Building SymSpell index... {done:N0}/{_entryCount:N0}"));
                    }

                    return localIndex;
                },
                localIndex => partialIndexes.Add(localIndex)
            );

            // Phase 2: Merge partial indexes from all threads
            progress?.Report((70, "Merging SymSpell index partitions..."));
            var tempIndex = new Dictionary<long, List<int>>(_entryCount * 2);
            foreach (var partial in partialIndexes)
            {
                foreach (var kvp in partial)
                {
                    if (tempIndex.TryGetValue(kvp.Key, out var existing))
                    {
                        existing.AddRange(kvp.Value);
                    }
                    else
                    {
                        tempIndex[kvp.Key] = kvp.Value;
                    }
                }
            }

            // Phase 3: Freeze lists into arrays for read performance
            var frozenIndex = new Dictionary<long, int[]>(tempIndex.Count);
            foreach (var kvp in tempIndex)
            {
                frozenIndex[kvp.Key] = kvp.Value.ToArray();
            }

            progress?.Report((75, $"SymSpell index ready ({frozenIndex.Count:N0} buckets)"));
            return frozenIndex;
        }

        /// <summary>
        /// Recursively generate all delete variants by removing characters from positions [start..end).
        /// </summary>
        private void GenerateDeletes(string word, int start, int end, int remainingDistance, HashSet<long> results)
        {
            if (remainingDistance == 0 || end - start <= 0) return;

            int len = end - start;
            if (len <= 0) return;

            // For each position, create a delete variant by skipping that character
            // We represent the variant by its hash (no string allocation)
            for (int i = start; i < end; i++)
            {
                // Hash the string with character at position i removed
                long hash = HashWithSkip(word, start, end, i);
                if (results.Add(hash) && remainingDistance > 1 && len > 1)
                {
                    // Recurse for deeper deletions
                    // Create the actual shortened string for recursive deletion
                    // (we need the chars to generate further deletes)
                    GenerateDeletesFromVariant(word, start, end, i, remainingDistance - 1, results);
                }
            }
        }

        /// <summary>
        /// Generate further deletes from a variant that already has one character removed.
        /// </summary>
        private void GenerateDeletesFromVariant(string word, int start, int end, int skipPos, int remainingDistance, HashSet<long> results)
        {
            if (remainingDistance == 0) return;

            // Generate deletes by skipping a second character (in addition to skipPos)
            for (int i = start; i < end; i++)
            {
                if (i == skipPos) continue;

                long hash = HashWithTwoSkips(word, start, end, skipPos, i);
                results.Add(hash);
                // For maxEditDistance=2, we don't need to go deeper
            }
        }

        /// <summary>
        /// Hash a substring [start..end) with one character position skipped.
        /// </summary>
        private static long HashWithSkip(string s, int start, int end, int skipPos)
        {
            ulong hash = FNV_OFFSET_BASIS;
            for (int i = start; i < end; i++)
            {
                if (i == skipPos) continue;
                hash ^= s[i];
                hash *= FNV_PRIME;
            }
            return (long)hash;
        }

        /// <summary>
        /// Hash a substring [start..end) with two character positions skipped.
        /// </summary>
        private static long HashWithTwoSkips(string s, int start, int end, int skip1, int skip2)
        {
            ulong hash = FNV_OFFSET_BASIS;
            for (int i = start; i < end; i++)
            {
                if (i == skip1 || i == skip2) continue;
                hash ^= s[i];
                hash *= FNV_PRIME;
            }
            return (long)hash;
        }

        /// <summary>
        /// Hash a substring [start..start+length).
        /// </summary>
        private static long HashSpan(string s, int start, int length)
        {
            ulong hash = FNV_OFFSET_BASIS;
            int end = start + length;
            for (int i = start; i < end; i++)
            {
                hash ^= s[i];
                hash *= FNV_PRIME;
            }
            return (long)hash;
        }

        /// <summary>
        /// Try to find a match within maxEditDistance.
        /// Returns true if a match was found, with the entry index.
        ///
        /// This method is thread-safe (read-only after construction).
        /// </summary>
        /// <param name="normalizedInput">Pre-normalized input text</param>
        /// <param name="entryIndex">Index into the entry arrays if found</param>
        /// <param name="editDistance">Actual edit distance of the match</param>
        /// <returns>True if match found within maxEditDistance</returns>
        public bool TryFindMatch(string normalizedInput, out int entryIndex, out int editDistance)
        {
            entryIndex = -1;
            editDistance = int.MaxValue;

            if (string.IsNullOrEmpty(normalizedInput) || !IsReady)
                return false;

            int inputPrefixLen = Math.Min(normalizedInput.Length, _prefixLength);

            // Generate delete variants of the input prefix
            var candidates = new HashSet<int>();

            // Check exact prefix hash first (distance 0)
            long exactHash = HashSpan(normalizedInput, 0, inputPrefixLen);
            if (_deleteIndex.TryGetValue(exactHash, out int[] exactEntries))
            {
                foreach (int idx in exactEntries)
                    candidates.Add(idx);
            }

            // Generate input deletes and look up
            var inputDeletes = new HashSet<long>();
            GenerateDeletes(normalizedInput, 0, inputPrefixLen, _maxEditDistance, inputDeletes);

            foreach (long deleteHash in inputDeletes)
            {
                if (_deleteIndex.TryGetValue(deleteHash, out int[] entries))
                {
                    foreach (int idx in entries)
                        candidates.Add(idx);
                }
            }

            if (candidates.Count == 0)
                return false;

            // Verify candidates with actual Levenshtein distance
            int bestDistance = _maxEditDistance + 1;
            int bestIndex = -1;

            foreach (int idx in candidates)
            {
                string candidateKey = _normalizedKeys[idx];

                // Length filter: if lengths differ by more than maxEditDistance, skip
                int lenDiff = Math.Abs(normalizedInput.Length - candidateKey.Length);
                if (lenDiff > _maxEditDistance) continue;

                // For prefix matching: compare input against the candidate's prefix
                int compareLen = Math.Min(normalizedInput.Length, candidateKey.Length);
                int dist;

                if (normalizedInput.Length <= candidateKey.Length)
                {
                    // Input is shorter or equal — check if it's a prefix match
                    if (candidateKey.StartsWith(normalizedInput, StringComparison.Ordinal))
                    {
                        dist = 0;
                    }
                    else
                    {
                        // Compare input against the same-length prefix of the candidate
                        dist = LevenshteinDistance(
                            normalizedInput, 0, normalizedInput.Length,
                            candidateKey, 0, normalizedInput.Length,
                            bestDistance);
                    }
                }
                else
                {
                    // Input is longer — check reverse containment
                    if (normalizedInput.StartsWith(candidateKey, StringComparison.Ordinal))
                    {
                        dist = 0;
                    }
                    else
                    {
                        dist = LevenshteinDistance(
                            normalizedInput, 0, candidateKey.Length,
                            candidateKey, 0, candidateKey.Length,
                            bestDistance);
                    }
                }

                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestIndex = idx;
                    if (dist == 0) break; // Perfect match
                }
            }

            if (bestIndex >= 0 && bestDistance <= _maxEditDistance)
            {
                entryIndex = bestIndex;
                editDistance = bestDistance;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Private constructor for deserialization. Sets all readonly fields directly
        /// from pre-built data without running the expensive index build.
        /// </summary>
        private SymSpellIndex(
            int maxEditDistance,
            int prefixLength,
            Dictionary<long, int[]> deleteIndex,
            string[] normalizedKeys,
            string[] originalKeys,
            string[] values)
        {
            _maxEditDistance = maxEditDistance;
            _prefixLength = prefixLength;
            _deleteIndex = deleteIndex;
            _normalizedKeys = normalizedKeys;
            _originalKeys = originalKeys;
            _values = values;
            _entryCount = normalizedKeys.Length;
            IsReady = true;
        }

        /// <summary>
        /// Serialize only the SymSpell delete index to a BinaryWriter.
        /// String arrays (normalizedKeys, originalKeys, values) are NOT serialized here
        /// because they are shared with OptimizedMatcher entries and serialized there.
        /// </summary>
        internal void SerializeIndex(BinaryWriter writer)
        {
            writer.Write(_maxEditDistance);
            writer.Write(_prefixLength);
            writer.Write(_deleteIndex.Count);

            foreach (var kvp in _deleteIndex)
            {
                writer.Write(kvp.Key);
                int[] arr = kvp.Value;
                writer.Write(arr.Length);
                for (int i = 0; i < arr.Length; i++)
                {
                    writer.Write(arr[i]);
                }
            }
        }

        /// <summary>
        /// Deserialize a SymSpell index from a BinaryReader.
        /// String arrays are provided externally (shared with OptimizedMatcher entries).
        /// </summary>
        internal static SymSpellIndex DeserializeIndex(
            BinaryReader reader,
            string[] normalizedKeys,
            string[] originalKeys,
            string[] values)
        {
            int maxEditDistance = reader.ReadInt32();
            int prefixLength = reader.ReadInt32();
            int bucketCount = reader.ReadInt32();

            var deleteIndex = new Dictionary<long, int[]>(bucketCount);
            for (int b = 0; b < bucketCount; b++)
            {
                long hash = reader.ReadInt64();
                int arrLen = reader.ReadInt32();
                int[] arr = new int[arrLen];
                for (int i = 0; i < arrLen; i++)
                {
                    arr[i] = reader.ReadInt32();
                }
                deleteIndex[hash] = arr;
            }

            return new SymSpellIndex(maxEditDistance, prefixLength, deleteIndex,
                normalizedKeys, originalKeys, values);
        }

        /// <summary>
        /// Get the original key for an entry index.
        /// </summary>
        public string GetOriginalKey(int entryIndex) => _originalKeys[entryIndex];

        /// <summary>
        /// Get the translation value for an entry index.
        /// </summary>
        public string GetValue(int entryIndex) => _values[entryIndex];

        /// <summary>
        /// Minimal Levenshtein distance for verification.
        /// Uses stackalloc for speed, with early termination.
        /// </summary>
        private static int LevenshteinDistance(
            string s1, int s1Start, int s1End,
            string s2, int s2Start, int s2End,
            int threshold)
        {
            int len1 = s1End - s1Start;
            int len2 = s2End - s2Start;

            if (len1 == 0) return len2;
            if (len2 == 0) return len1;
            if (Math.Abs(len1 - len2) > threshold) return threshold + 1;

            // Ensure s1 is the shorter string
            if (len1 > len2)
            {
                var ts = s1; s1 = s2; s2 = ts;
                var tStart = s1Start; s1Start = s2Start; s2Start = tStart;
                var tEnd = s1End; s1End = s2End; s2End = tEnd;
                var tLen = len1; len1 = len2; len2 = tLen;
            }

            Span<int> prev = len1 < 256 ? stackalloc int[len1 + 1] : new int[len1 + 1];
            Span<int> curr = len1 < 256 ? stackalloc int[len1 + 1] : new int[len1 + 1];

            for (int i = 0; i <= len1; i++) prev[i] = i;

            for (int j = 1; j <= len2; j++)
            {
                curr[0] = j;
                int minInRow = j;
                char c2 = s2[s2Start + j - 1];

                for (int i = 1; i <= len1; i++)
                {
                    int cost = (s1[s1Start + i - 1] == c2) ? 0 : 1;
                    int d1 = curr[i - 1] + 1;
                    int d2 = prev[i] + 1;
                    int d3 = prev[i - 1] + cost;
                    int d = d1 < d2 ? d1 : d2;
                    if (d3 < d) d = d3;
                    curr[i] = d;
                    if (d < minInRow) minInRow = d;
                }

                if (minInRow > threshold) return threshold + 1;
                var tmp = prev; prev = curr; curr = tmp;
            }

            return prev[len1];
        }
    }
}
