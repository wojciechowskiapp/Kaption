using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Common;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Models;
using GI_Subtitles.Services.Security;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// Optimized text matcher for translation lookup.
    ///
    /// Matching pipeline:
    ///   1. SymSpell fast path (edit distance &lt;= 2, ~5us) -- catches exact/near-exact matches with OCR errors
    ///   2. N-gram candidate selection + OCR-weighted Levenshtein (~1ms) -- handles prefix/substring matching
    ///
    /// SymSpell and OCR-weighted distance are modular and can be disabled via Config.
    ///
    /// Supports binary serialization (GSMX format) to skip the expensive index build on
    /// subsequent launches. Typical cold build: 30-60s. Deserialization: &lt;2s.
    /// </summary>
    public class OptimizedMatcher : IDisposable
    {
        // Binary serialization format magic header and version
        private static readonly byte[] GSMX_MAGIC = { 0x47, 0x53, 0x4D, 0x58 }; // "GSMX"
        private const byte GSMX_VERSION = 1;

        // Heap-resident mode (legacy, GSMX-backed or first-time build):
        // every entry's normKey/origKey/value is materialised as a
        // managed string. Non-null <=> `_reader == null`.
        private readonly Entry[] _entries;
        // N-gram → slot-id buckets. Stored as int[] rather than List<int>
        // so we don't pay the ~40 B/bucket List header overhead + the
        // typical 2× over-allocation of the backing array. On a 488k-entry
        // corpus with ~400k unique 4-grams that adds up to ≈12–20 MB of
        // pure housekeeping we can avoid. The builder accumulates into
        // List<int> then freezes to int[] at hand-off.
        //
        // Net8 migration (2026-04-23): swapped Dictionary<long,int[]> for
        // FrozenDictionary<long,int[]>. The outer map is built once at
        // load and read-only forever — lookup-dominated. Frozen form uses
        // a perfect-ish hash probe that's ~40-70% faster than Dictionary
        // on cold probes, with identical TryGetValue / Count / foreach API
        // so the GSMX serializer needs no changes. Construction cost on
        // 400k buckets is ~40 ms — one-shot, during load.
        private readonly FrozenDictionary<long, int[]> _ngramIndex;
        private readonly int[] _shortKeysIndices;

        // Lazily built OriginalKey → slot-id index for the rare
        // header-exact-lookup path (<see cref="FindMatchWithHeaderSeparated"/>).
        // The previous design kept a full <c>Dictionary&lt;string,string&gt;</c>
        // duplicate (ContentDict) resident for the life of the matcher —
        // ~23 MB of hash-table overhead on a 488k-entry corpus, paid even
        // by sessions that never dispatched a multi-line OCR result. We
        // now pay that cost only on the first TryGetExactValue hit, and
        // the slot-id map is ~30% smaller than the string→string equivalent
        // (4-byte int payload vs. 8-byte string reference) because values
        // are resolved through <see cref="_entries"/> directly.
        //
        // Thread-safe: built under a sync block; subsequent readers see a
        // fully-populated snapshot via the volatile publish on the field.
        //
        // Net8 migration: FrozenDictionary<string,int> with StringComparer.Ordinal
        // replaces the old Dictionary<string,int>. On 488k string keys, the
        // frozen form saves ~5-10 MB (no Entry.next chain, compact _keys[]/
        // _values[] arrays) and gives ~40% faster lookups on the
        // header-exact-match fast path. Built once on first hit; subsequent
        // calls do the zero-copy ref load.
        private volatile FrozenDictionary<string, int> _origKeyToSlot;
        private readonly object _origKeyToSlotLock = new object();

        // Mmap-backed mode (Phase 2, .kmx.gisub v3): `_reader` owns the
        // FST + compressed value pool; the matcher keeps ONLY the
        // normalized-key + original-key string arrays resident (≈40 MB
        // each on a 488k corpus — tolerable), and fetches values on
        // demand from the mmap'd blob. `_entries`/`ContentDict` are null
        // in this mode — call sites that touch them go through the
        // MmapMode helpers so the hot path branches cleanly.
        private readonly MatcherBlobReader _reader;
        private readonly string[] _normKeys;
        private readonly string[] _origKeys;
        private readonly int[] _keyLengths;

        // SymSpell fast-path index (EN only, null if disabled)
        private readonly SymSpellIndex _symSpellIndex;
        private readonly bool _useOcrWeightedDistance;

        public bool Loaded = false;
        public bool isEng = false;

        private readonly int _ngramSize;
        private int _disposed;

        /// <summary>
        /// True when the matcher reads keys/values through a
        /// MatcherBlobReader (mmap path) instead of a resident
        /// <see cref="Entry"/>[] table. Exposed for tests/diagnostics.
        /// </summary>
        public bool IsMmapBacked => _reader != null;

        // FNV-1a hash constants for fast n-gram hashing
        private const ulong FNV_OFFSET_BASIS = 14695981039346656037UL;
        private const ulong FNV_PRIME = 1099511628211UL;

        private struct Entry
        {
            public string NormalizedKey;
            public string OriginalKey;
            public string Value;
            public int Length;
        }

        // Fast FNV-1a n-gram hash, span-native. Net8 migration (2026-04-23):
        // swapped (string, int start, int length) for (ROSpan<char>). Call sites
        // pass `s.AsSpan(i, ngramSize)` — zero-alloc, bounds-check elided by JIT
        // on PGO-warmed paths, and the length-driven for-loop vectorizes better
        // with Span than with indexed-string access.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetNgramHash(ReadOnlySpan<char> s)
        {
            ulong hash = FNV_OFFSET_BASIS;
            for (int i = 0; i < s.Length; i++)
            {
                hash ^= s[i];
                hash *= FNV_PRIME;
            }
            return (long)hash;
        }

        /// <summary>
        /// Number of keys in the matcher's internal entry table. Exposed so
        /// callers can sanity-check a freshly-loaded cache against the live
        /// dictionary — a cache serialised against an empty dict will load
        /// cleanly but have zero entries, and downstream FindClosestMatch
        /// calls will silently return "". The cache-load path in
        /// SettingsWindow rejects caches whose EntryCount drops far below
        /// the live dict's count so that specific stale-state doesn't make
        /// it to users a second time.
        /// </summary>
        public int EntryCount =>
            _reader != null ? _reader.EntryCount : (_entries?.Length ?? 0);

        /// <summary>
        /// Private constructor for deserialization. Sets all readonly fields directly
        /// from pre-built data without running the expensive index build.
        /// </summary>
        private OptimizedMatcher(
            Entry[] entries,
            Dictionary<long, int[]> ngramIndex,
            int[] shortKeysIndices,
            SymSpellIndex symSpellIndex,
            bool isEngFlag,
            int ngramSize)
        {
            _entries = entries;
            _ngramIndex = ngramIndex.ToFrozenDictionary();
            _shortKeysIndices = shortKeysIndices;
            _symSpellIndex = symSpellIndex;
            _useOcrWeightedDistance = Config.Get<bool>("OcrWeightedDistance", true);
            isEng = isEngFlag;
            _ngramSize = ngramSize;
            Loaded = true;
        }

        public OptimizedMatcher(Dictionary<string, string> voiceContentDict, string inputLanguage)
        {
            isEng = inputLanguage == "EN";
            if (voiceContentDict == null) voiceContentDict = new Dictionary<string, string>();
            _useOcrWeightedDistance = Config.Get<bool>("OcrWeightedDistance", true);

            // EN: 4-gram is crucial for performance (reduces candidates)
            // CN: 2-gram is sufficient
            _ngramSize = isEng ? 4 : 2;

            int count = voiceContentDict.Count;

            // Empty-dict short-circuit. Fresh installs hit this before any
            // TextMap has been downloaded — there's nothing to match against
            // yet. Construct a load-bearing-nothing matcher: empty entries,
            // empty indexes, Loaded=true so callers don't treat it as broken.
            // Otherwise Partitioner.Create(0, 0) downstream crashes the
            // whole Load path.
            if (count == 0)
            {
                _entries = Array.Empty<Entry>();
                _shortKeysIndices = Array.Empty<int>();
                _ngramIndex = FrozenDictionary<long, int[]>.Empty;
                _symSpellIndex = null;
                Loaded = true;
                return;
            }

            _entries = new Entry[count];
            var shortKeysList = new List<int>();

            // Phase 1: Build entries array
            int index = 0;
            foreach (var kvp in voiceContentDict)
            {
                string normKey = NormalizeInput(kvp.Key, isEng);

                _entries[index] = new Entry
                {
                    NormalizedKey = normKey,
                    OriginalKey = kvp.Key,
                    Value = kvp.Value,
                    Length = normKey.Length
                };

                if (normKey.Length < _ngramSize)
                {
                    shortKeysList.Add(index);
                }
                index++;
            }
            _shortKeysIndices = shortKeysList.ToArray();

            // Phase 2: Build N-gram and SymSpell indexes in parallel
            bool buildSymSpell = isEng && Config.Get<bool>("UseSymSpell", true);

            var ngramTask = Task.Run(() => BuildNgramIndexFromEntries(_entries, _ngramSize, _shortKeysIndices));
            var symSpellTask = buildSymSpell
                ? Task.Run(() => BuildSymSpellFromEntries(null))
                : Task.FromResult<SymSpellIndex>(null);

            Task.WaitAll(ngramTask, symSpellTask);
            _ngramIndex = ngramTask.Result.ToFrozenDictionary();
            _symSpellIndex = symSpellTask.Result;

            Loaded = true;
        }

        /// <summary>
        /// Constructs an OptimizedMatcher with progress reporting.
        /// Progress ranges from 50% to 98% during index construction.
        /// N-gram and SymSpell indexes are built in parallel for faster startup.
        /// </summary>
        public OptimizedMatcher(Dictionary<string, string> voiceContentDict, string inputLanguage,
            IProgress<(int percent, string message)> progress)
        {
            isEng = inputLanguage == "EN";
            if (voiceContentDict == null) voiceContentDict = new Dictionary<string, string>();
            _useOcrWeightedDistance = Config.Get<bool>("OcrWeightedDistance", true);
            _ngramSize = isEng ? 4 : 2;

            int count = voiceContentDict.Count;

            // Empty-dict short-circuit — same rationale as the progress-free
            // constructor above. Skip all index construction and return a
            // trivially-empty matcher so the Load path doesn't crash when
            // the source JSONs haven't been downloaded yet.
            if (count == 0)
            {
                _entries = Array.Empty<Entry>();
                _shortKeysIndices = Array.Empty<int>();
                _ngramIndex = FrozenDictionary<long, int[]>.Empty;
                _symSpellIndex = null;
                progress?.Report((100, "No dictionary entries yet — index skipped."));
                Loaded = true;
                return;
            }

            _entries = new Entry[count];
            var shortKeysList = new List<int>();

            // Phase 1: Build entries array (50-65%)
            int index = 0;
            foreach (var kvp in voiceContentDict)
            {
                string normKey = NormalizeInput(kvp.Key, isEng);
                _entries[index] = new Entry
                {
                    NormalizedKey = normKey,
                    OriginalKey = kvp.Key,
                    Value = kvp.Value,
                    Length = normKey.Length
                };

                if (normKey.Length < _ngramSize)
                {
                    shortKeysList.Add(index);
                }
                index++;

                // Report progress every 10000 entries (50% to 65%)
                if (progress != null && index % 10000 == 0)
                {
                    int pct = (int)(50 + (index * 15.0 / count));
                    progress.Report((pct, $"Building entries... {index:N0}/{count:N0}"));
                }
            }
            _shortKeysIndices = shortKeysList.ToArray();

            progress?.Report((65, "Building search indexes in parallel..."));

            // Phase 2: Build N-gram and SymSpell indexes in parallel (65-95%)
            bool buildSymSpell = isEng && Config.Get<bool>("UseSymSpell", true);

            var ngramProgress = new Progress<(int percent, string message)>(p =>
            {
                // N-gram progress maps to 65-80%
                progress?.Report(p);
            });

            var ngramTask = Task.Run(() =>
            {
                var result = BuildNgramIndexFromEntries(_entries, _ngramSize, _shortKeysIndices);
                progress?.Report((80, $"N-gram index ready ({result.Count:N0} buckets)"));
                return result;
            });

            var symSpellTask = buildSymSpell
                ? Task.Run(() =>
                {
                    progress?.Report((65, "Building SymSpell fast-match index..."));
                    var result = BuildSymSpellFromEntries(progress);
                    progress?.Report((90, "SymSpell index ready"));
                    return result;
                })
                : Task.FromResult<SymSpellIndex>(null);

            Task.WaitAll(ngramTask, symSpellTask);
            _ngramIndex = ngramTask.Result.ToFrozenDictionary();
            _symSpellIndex = symSpellTask.Result;

            Loaded = true;
            progress?.Report((98, "Search index ready"));
        }

        /// <summary>
        /// Build the N-gram index from a pre-populated entries array.
        /// Extracted as a static method to enable parallel execution with SymSpell build.
        /// </summary>
        private static Dictionary<long, int[]> BuildNgramIndexFromEntries(
            Entry[] entries, int ngramSize, int[] shortKeysExclude)
        {
            int count = entries.Length;
            // Accumulate into List<int> buckets while we're still growing,
            // then freeze to int[] for the returned index. Keeping the
            // returned map as int[]-valued drops ~40 B of List header per
            // bucket plus whatever slack the List backing array was
            // over-allocated with — on a 488k corpus that totals ~12-20 MB
            // of housekeeping the hot path would otherwise carry forever.
            var working = new Dictionary<long, List<int>>(count * 4);

            for (int idx = 0; idx < count; idx++)
            {
                string normKey = entries[idx].NormalizedKey;
                if (normKey.Length < ngramSize)
                {
                    // Already tracked in shortKeysIndices, skip n-gram indexing
                    continue;
                }

                var distinctHashes = new HashSet<long>();
                var keySpan = normKey.AsSpan();
                for (int i = 0; i <= normKey.Length - ngramSize; i++)
                {
                    long hash = GetNgramHash(keySpan.Slice(i, ngramSize));
                    if (distinctHashes.Add(hash))
                    {
                        if (!working.TryGetValue(hash, out var list))
                        {
                            list = new List<int>();
                            working[hash] = list;
                        }
                        list.Add(idx);
                    }
                }
            }

            return FreezeNgramIndex(working);
        }

        /// <summary>
        /// Convert the <c>List&lt;int&gt;</c>-valued working map into the
        /// frozen <c>int[]</c>-valued map that the hot path queries. Walk
        /// the working dictionary once, calling <see cref="List{T}.ToArray"/>
        /// on each bucket — that allocates exactly-sized arrays and releases
        /// the List + its over-allocated backing buffer to GC at the next
        /// cycle.
        /// </summary>
        private static Dictionary<long, int[]> FreezeNgramIndex(
            Dictionary<long, List<int>> working)
        {
            var frozen = new Dictionary<long, int[]>(working.Count);
            foreach (var kvp in working)
            {
                frozen[kvp.Key] = kvp.Value.ToArray();
            }
            return frozen;
        }

        /// <summary>
        /// Build SymSpell index from the already-constructed _entries array.
        /// </summary>
        private SymSpellIndex BuildSymSpellFromEntries(IProgress<(int percent, string message)> progress)
        {
            int count = _entries.Length;
            var normKeys = new string[count];
            var origKeys = new string[count];
            var values = new string[count];

            for (int i = 0; i < count; i++)
            {
                normKeys[i] = _entries[i].NormalizedKey;
                origKeys[i] = _entries[i].OriginalKey;
                values[i] = _entries[i].Value;
            }

            if (progress != null)
                return new SymSpellIndex(normKeys, origKeys, values, progress);
            else
                return new SymSpellIndex(normKeys, origKeys, values);
        }

        // --- Binary Serialization (GSMX format) ---

        /// <summary>
        /// Serialize the entire matcher state to a binary stream.
        /// Format: GSMX magic (4 bytes) + version (1 byte) + all index data.
        /// The caller is responsible for encrypting the output stream.
        /// </summary>
        public void SerializeToStream(Stream output)
        {
            using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
            {
                // Header
                writer.Write(GSMX_MAGIC);
                writer.Write(GSMX_VERSION);

                // Core flags
                writer.Write(isEng);
                writer.Write(_ngramSize);

                // Entries
                int count = _entries.Length;
                writer.Write(count);
                for (int i = 0; i < count; i++)
                {
                    writer.Write(_entries[i].NormalizedKey ?? "");
                    writer.Write(_entries[i].OriginalKey ?? "");
                    writer.Write(_entries[i].Value ?? "");
                }

                // Short keys indices
                writer.Write(_shortKeysIndices.Length);
                for (int i = 0; i < _shortKeysIndices.Length; i++)
                {
                    writer.Write(_shortKeysIndices[i]);
                }

                // N-gram index. Wire format is length-prefix + int items,
                // unchanged from the List<int>-valued era — the on-disk
                // layout is identical whether the runtime holds int[] or
                // List<int>, so existing GSMX caches still deserialise.
                writer.Write(_ngramIndex.Count);
                foreach (var kvp in _ngramIndex)
                {
                    writer.Write(kvp.Key);
                    int[] ids = kvp.Value;
                    writer.Write(ids.Length);
                    for (int i = 0; i < ids.Length; i++)
                    {
                        writer.Write(ids[i]);
                    }
                }

                // SymSpell index
                bool hasSymSpell = _symSpellIndex != null && _symSpellIndex.IsReady;
                writer.Write(hasSymSpell);
                if (hasSymSpell)
                {
                    _symSpellIndex.SerializeIndex(writer);
                }
            }
        }

        /// <summary>
        /// Deserialize a matcher from a binary GSMX stream.
        /// This is dramatically faster than rebuilding from the dictionary (~1-2s vs 30-60s).
        /// Falls back gracefully: caller should catch exceptions and rebuild if needed.
        ///
        /// Streaming contract: this method reads the input stream strictly forward, one
        /// record at a time, so the caller may pass a non-seekable <see cref="CryptoStream"/>
        /// directly off disk. The only per-entry allocation is the <see cref="Entry"/>
        /// struct plus its three strings; no intermediate List/byte[] buffers the full
        /// payload. Peak memory during load is ~sizeof(_entries) + ~sizeof(_ngramIndex)
        /// + ~sizeof(_symSpellIndex) — i.e. only the final object graph, not 2-3x of it.
        /// </summary>
        /// <param name="input">Stream containing GSMX binary data (decrypted)</param>
        /// <param name="progress">Optional progress reporter (maps to 50-98%)</param>
        /// <returns>A fully initialized OptimizedMatcher ready for queries</returns>
        public static OptimizedMatcher DeserializeFromStream(
            Stream input,
            IProgress<(int percent, string message)> progress = null)
        {
            using (var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true))
            {
                // Validate header
                byte[] magic = reader.ReadBytes(4);
                if (magic.Length < 4 ||
                    magic[0] != GSMX_MAGIC[0] || magic[1] != GSMX_MAGIC[1] ||
                    magic[2] != GSMX_MAGIC[2] || magic[3] != GSMX_MAGIC[3])
                {
                    throw new InvalidDataException("Invalid GSMX file: missing magic header");
                }

                byte version = reader.ReadByte();
                if (version != GSMX_VERSION)
                {
                    throw new InvalidDataException(
                        $"Unsupported GSMX version {version} (expected {GSMX_VERSION})");
                }

                progress?.Report((50, "Loading pre-built search index..."));

                // Core flags
                bool isEngFlag = reader.ReadBoolean();
                int ngramSize = reader.ReadInt32();

                // Entries — pre-size everything off the count header. No intermediate
                // List<Entry>; write directly into the final pre-allocated Entry[].
                int entryCount = reader.ReadInt32();
                var entries = new Entry[entryCount];

                // Previously built a Dictionary<string,string>(entryCount) here to
                // back FindMatchWithHeaderSeparated's exact-lookup path. On a 488k
                // entry corpus that was ~23 MB of hash-table overhead held for the
                // life of the matcher, whether or not the session ever exercised
                // the multi-line-header code path. The matcher now lazy-builds a
                // slim origKey→slot Dictionary<string,int> on first header lookup
                // (see _origKeyToSlot / EnsureOrigKeyToSlot) so the common case
                // pays nothing.

                progress?.Report((55, $"Loading {entryCount:N0} entries..."));

                for (int i = 0; i < entryCount; i++)
                {
                    string normKey = reader.ReadString();
                    string origKey = reader.ReadString();
                    string value = reader.ReadString();

                    entries[i] = new Entry
                    {
                        NormalizedKey = normKey,
                        OriginalKey = origKey,
                        Value = value,
                        Length = normKey.Length
                    };

                    if (progress != null && i % 50000 == 0 && i > 0)
                    {
                        int pct = (int)(55 + (i * 10.0 / entryCount));
                        progress.Report((pct, $"Loading entries... {i:N0}/{entryCount:N0}"));
                    }
                }

                progress?.Report((65, "Loading short key index..."));

                // Short keys indices — exact-sized array, no List.
                int shortKeysCount = reader.ReadInt32();
                int[] shortKeysIndices = new int[shortKeysCount];
                for (int i = 0; i < shortKeysCount; i++)
                {
                    shortKeysIndices[i] = reader.ReadInt32();
                }

                progress?.Report((70, "Loading n-gram index..."));

                // N-gram index — pre-size the outer dict, and each bucket
                // is an exact-sized int[] read directly off the stream.
                // Skipping the intermediate List<int> saves ~40 B of header
                // overhead per bucket and avoids the 2× over-allocation
                // doubling pattern that List uses during construction.
                int ngramBucketCount = reader.ReadInt32();
                var ngramIndex = new Dictionary<long, int[]>(ngramBucketCount);
                for (int b = 0; b < ngramBucketCount; b++)
                {
                    long hash = reader.ReadInt64();
                    int listCount = reader.ReadInt32();
                    int[] ids = new int[listCount];
                    for (int i = 0; i < listCount; i++)
                    {
                        ids[i] = reader.ReadInt32();
                    }
                    ngramIndex[hash] = ids;

                    if (progress != null && b % 100000 == 0 && b > 0)
                    {
                        int pct = (int)(70 + (b * 15.0 / ngramBucketCount));
                        progress.Report((pct, $"Loading n-gram index... {b:N0}/{ngramBucketCount:N0}"));
                    }
                }

                progress?.Report((85, "Loading SymSpell index..."));

                // SymSpell index.
                //
                // Previously we allocated three separate string[entryCount] reference
                // arrays here (normKeys, origKeys, values) purely to hand them to
                // SymSpellIndex.DeserializeIndex. Those arrays are pure duplication —
                // every slot references the same string already held by entries[i].
                // On a 500k-entry corpus that's an extra ~12 MB of transient GC pressure
                // for zero value. We now extract the views in one pass and reuse the
                // three slim arrays (still 3 × 8 B × count = the irreducible minimum
                // the SymSpellIndex API requires).
                bool hasSymSpell = reader.ReadBoolean();
                SymSpellIndex symSpellIndex = null;
                if (hasSymSpell)
                {
                    var normKeys = new string[entryCount];
                    var origKeys = new string[entryCount];
                    var values = new string[entryCount];
                    for (int i = 0; i < entryCount; i++)
                    {
                        ref readonly var e = ref entries[i];
                        normKeys[i] = e.NormalizedKey;
                        origKeys[i] = e.OriginalKey;
                        values[i] = e.Value;
                    }

                    symSpellIndex = SymSpellIndex.DeserializeIndex(
                        reader, normKeys, origKeys, values);
                }

                progress?.Report((95, "Finalizing search index..."));

                var matcher = new OptimizedMatcher(
                    entries, ngramIndex, shortKeysIndices,
                    symSpellIndex, isEngFlag, ngramSize);

                progress?.Report((98, "Search index loaded from cache"));
                return matcher;
            }
        }

        // --- KMX blob serialization (new zero-heap-tax format) ---------------

        /// <summary>
        /// Serialize the matcher's EN → PL corpus to the new KMX blob format
        /// (FST + fixed-width entries + ZSTD-compressed value pool).
        /// The legacy GSMX serializer (<see cref="SerializeToStream"/>) stays
        /// in place as a fallback; call sites pick whichever format they want
        /// until GSMX is removed in a follow-up.
        /// </summary>
        /// <param name="output">Destination stream (must be writable + seekable).</param>
        /// <param name="meta">Corpus metadata (game, language, corpus version, etc.).</param>
        /// <param name="trainedDictionary">
        /// Pre-trained ZSTD dictionary, or null / zero-length for no dictionary.
        /// See <see cref="ZstdDictionaryTrainer"/> to produce one.
        /// </param>
        public void Save(Stream output,
            MatcherBlobSchema.MatcherMeta meta,
            byte[] trainedDictionary = null,
            int compressionLevel = 19)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (meta == null) throw new ArgumentNullException(nameof(meta));

            // Previously Save() read from a persistent ContentDict field. That
            // field was retired as part of the Phase 3 RAM audit — it duplicated
            // ~23 MB of Dictionary<string,string> overhead for the whole session
            // when the only surviving caller was this rarely-used migration
            // writer. We now synthesise a read-only view over _entries[] just
            // for the duration of the write (no hash-table allocation — the
            // wrapper enumerates entries lazily and exposes key→value via
            // a one-shot lookup map built at the start of Write).
            //
            // Correctness: MatcherBlobWriter.Write enforces unique keys, so the
            // wrapper's indexer behaviour mirrors the old dict (last-writer-wins
            // wouldn't matter because Entry[] already holds a single row per
            // origKey by construction).
            IReadOnlyDictionary<string, string> corpus = BuildCorpusViewForSave();
            MatcherBlobWriter.Write(
                corpus: corpus,
                trainedDictionary: trainedDictionary,
                meta: meta,
                output: output,
                compressionLevel: compressionLevel);
        }

        /// <summary>
        /// Build a one-shot read-only dictionary view over the matcher's
        /// <see cref="_entries"/> array for <see cref="MatcherBlobWriter.Write"/>.
        /// This is only called from <see cref="Save"/>, which in the shipping
        /// path is itself only called during the opportunistic GSMX→v3 blob
        /// migration. For the mmap-backed matcher we have no resident
        /// <see cref="_entries"/> array, so we return an empty view —
        /// migration must target a heap-resident matcher, which is the
        /// call shape in SettingsWindow.TryWriteMatcherBlobV3.
        /// </summary>
        private IReadOnlyDictionary<string, string> BuildCorpusViewForSave()
        {
            if (_entries == null || _entries.Length == 0)
                return new Dictionary<string, string>(0);

            // Materialise a real dict here instead of faking an enumerator-
            // only view. MatcherBlobWriter.Write does a `corpus[key]` indexer
            // call in its value-compression loop, and IReadOnlyDictionary
            // doesn't let us implement a fast string-keyed indexer without
            // either a hash table or an O(n) scan. A transient 488k-entry
            // Dictionary allocation is fine: the writer disposes it the
            // moment Save() returns, and Save() runs at most once per
            // matcher rebuild (and never during steady-state OCR).
            var view = new Dictionary<string, string>(_entries.Length);
            for (int i = 0; i < _entries.Length; i++)
            {
                ref readonly var e = ref _entries[i];
                if (e.OriginalKey != null)
                    view[e.OriginalKey] = e.Value ?? "";
            }
            return view;
        }

        /// <summary>
        /// Build an <see cref="OptimizedMatcher"/> backed by a KMX matcher blob.
        ///
        /// The returned matcher has the full 3-stage pipeline wired up:
        ///   * Stage 0 (SymSpell) — built in-memory from the keys the FST holds,
        ///     because SymSpell's delete-prefix index is not serialisable
        ///     round-trip through the blob today. (Adding it is a follow-up.)
        ///   * Stage 1 (N-gram) — built the same way the in-memory constructor
        ///     does today; we pull keys from the FST and their PL values from
        ///     the decompressor.
        ///   * Stage 2 (OCR-weighted Levenshtein) — unchanged, runs over the
        ///     reconstituted entries.
        ///
        /// In the steady state (after OptimizedMatcher is fully blob-native)
        /// this path will stop materialising the Entry[] and pull keys from
        /// the FST + values from the decompressor lazily. For the first
        /// landing we keep the Entry[] shape so no downstream call site
        /// needs to change.
        /// </summary>
        public static OptimizedMatcher LoadFromBlob(Stream blob, string inputLanguage,
            IProgress<(int percent, string message)> progress = null)
        {
            if (blob == null) throw new ArgumentNullException(nameof(blob));
            // Until the hot path reads from the blob lazily (follow-up:
            // FstKeyIndex + ZstdValueDecoder plumbed into FindClosestMatch),
            // we fully materialise entries[] + contentDict + SymSpell. Once
            // that's done we no longer need the reader — dispose it so the
            // 86 MB blob byte[], the Lucene FST graph, the ZSTD decoder,
            // and the trained dict all become GC-eligible. Skipping this
            // step is what made UseMatcherBlob=true a 1–2 GB RAM regression
            // vs. GSMX on a 488k-entry corpus.
            using (var reader = MatcherBlobReader.LoadFromStream(blob))
            {
                var matcher = LoadFromReader(reader, inputLanguage, progress);
                // Hint Gen2 that the reader's backing bytes are dead. Without
                // this the promoted byte[] can sit in Gen2 for minutes, keeping
                // RSS high even though nothing references it any more.
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                return matcher;
            }
        }

        /// <summary>
        /// Variant of <see cref="LoadFromBlob(Stream, string, IProgress{ValueTuple{int, string}})"/>
        /// that takes a pre-constructed reader (e.g. one mmap'd against a
        /// decrypted container). **Caller owns the reader's lifetime.** For
        /// today's materialising path the caller can Dispose the reader
        /// immediately after this method returns (we copy everything into
        /// Entry[] + Dictionary + SymSpell). The mmap-backed lazy path
        /// currently in development will need the reader kept alive —
        /// that's a call-site contract change when we land it.
        /// </summary>
        public static OptimizedMatcher LoadFromReader(MatcherBlobReader reader, string inputLanguage,
            IProgress<(int percent, string message)> progress = null)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            bool isEngFlag = inputLanguage == "EN";
            int ngramSize = isEngFlag ? 4 : 2;
            int entryCount = reader.EntryCount;

            progress?.Report((50, $"Reconstituting {entryCount:N0} entries from blob..."));

            var entries = new Entry[entryCount];
            var shortKeysList = new List<int>();

            // Walk FST in ordinal order — this hands us every (key, slotId) pair.
            // We decompress the value at the same time so both structures end
            // up populated in a single pass.
            //
            // No contentDict is built here — the header-exact-lookup path now
            // lazy-builds its own slim Dictionary<string,int> on first call
            // (see _origKeyToSlot). On a 488k-entry corpus that's ~23 MB of
            // session-lifetime savings vs. materialising a full
            // Dictionary<string,string> duplicate up front.
            foreach (var kv in reader.EnumerateKeys())
            {
                int slot = kv.Value;
                if (slot < 0 || slot >= entryCount) continue;

                var entryRow = reader.GetEntry(slot);
                string value = "";
                if (entryRow.PlaintextLength > 0)
                {
                    byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent((int)entryRow.PlaintextLength);
                    try
                    {
                        int n = reader.DecodeValue(slot, rented.AsSpan(0, (int)entryRow.PlaintextLength));
                        value = Encoding.UTF8.GetString(rented, 0, n);
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                    }
                }

                string key = kv.Key;
                string normKey = NormalizeInput(key, isEngFlag);
                entries[slot] = new Entry
                {
                    NormalizedKey = normKey,
                    OriginalKey = key,
                    Value = value,
                    Length = normKey.Length,
                };
                if (normKey.Length < ngramSize) shortKeysList.Add(slot);

                if (progress != null && slot % 50000 == 0 && slot > 0)
                {
                    int pct = (int)(50 + (slot * 30.0 / entryCount));
                    progress.Report((pct, $"Reconstituting entries... {slot:N0}/{entryCount:N0}"));
                }
            }

            int[] shortKeysIndices = shortKeysList.ToArray();

            progress?.Report((85, "Building in-memory search indexes..."));

            var ngramIndex = BuildNgramIndexFromEntries(entries, ngramSize, shortKeysIndices);

            SymSpellIndex symSpellIndex = null;
            if (isEngFlag && Config.Get<bool>("UseSymSpell", true) && entryCount > 0)
            {
                var normKeys = new string[entryCount];
                var origKeys = new string[entryCount];
                var values = new string[entryCount];
                for (int i = 0; i < entryCount; i++)
                {
                    normKeys[i] = entries[i].NormalizedKey ?? "";
                    origKeys[i] = entries[i].OriginalKey ?? "";
                    values[i] = entries[i].Value ?? "";
                }
                symSpellIndex = new SymSpellIndex(normKeys, origKeys, values);
            }

            progress?.Report((98, "Matcher ready"));

            return new OptimizedMatcher(entries, ngramIndex, shortKeysIndices,
                symSpellIndex, isEngFlag, ngramSize);
        }

        // --- Mmap-backed load path (Phase 2: RAM-preserving) -----------------

        /// <summary>
        /// Private constructor for the mmap-backed matcher. Takes ownership
        /// of the <see cref="MatcherBlobReader"/> — disposing the matcher
        /// tears down the reader (and its mmap view / decryptor).
        /// </summary>
        private OptimizedMatcher(
            MatcherBlobReader reader,
            string[] normKeys,
            string[] origKeys,
            int[] keyLengths,
            Dictionary<long, int[]> ngramIndex,
            int[] shortKeysIndices,
            SymSpellIndex symSpellIndex,
            bool isEngFlag,
            int ngramSize)
        {
            _reader = reader;
            _normKeys = normKeys;
            _origKeys = origKeys;
            _keyLengths = keyLengths;
            _ngramIndex = ngramIndex.ToFrozenDictionary();
            _shortKeysIndices = shortKeysIndices;
            _symSpellIndex = symSpellIndex;
            _useOcrWeightedDistance = Config.Get<bool>("OcrWeightedDistance", true);
            isEng = isEngFlag;
            _ngramSize = ngramSize;
            Loaded = true;
        }

        /// <summary>
        /// Build an <see cref="OptimizedMatcher"/> whose corpus lives in a
        /// memory-mapped, AES-CTR-decrypted v3 .gisub container. Only the
        /// keys (normalized + original) remain heap-resident — values are
        /// fetched on demand by decrypting + ZSTD-decompressing the
        /// winning candidate's bytes out of the mmap'd blob.
        ///
        /// Steady-state heap footprint on a 488k-entry corpus is roughly:
        ///   * normKeys[488k] + origKeys[488k]       ≈ 80 MB   (strings)
        ///   * _keyLengths[488k]                       ≈ 2 MB
        ///   * n-gram index                            ≈ 30 MB
        ///   * SymSpell delete index                   ≈ 100 MB
        /// vs. the materialised LoadFromReader path which also holds:
        ///   * values[488k] + ContentDict              ≈ 80-120 MB
        /// ... which is the ~200-400 MB RSS win the spec is chasing.
        ///
        /// The returned matcher OWNS the decryptor. Dispose the matcher to
        /// release the mmap view. Failure inside this method disposes the
        /// decryptor before throwing so the caller never has to.
        /// </summary>
        public static OptimizedMatcher LoadFromMmap(
            IMmapDecryptor decryptor,
            string inputLanguage,
            IProgress<(int percent, string message)> progress = null)
        {
            if (decryptor == null) throw new ArgumentNullException(nameof(decryptor));

            MatcherBlobReader reader;
            try
            {
                reader = MatcherBlobReader.LoadFromMmapDecryptor(decryptor);
            }
            catch
            {
                // LoadFromMmapDecryptor owns decryptor on failure — already disposed.
                throw;
            }

            try
            {
                return LoadFromReaderMmap(reader, inputLanguage, progress);
            }
            catch
            {
                try { reader.Dispose(); } catch { /* best-effort */ }
                throw;
            }
        }

        /// <summary>
        /// Build a mmap-backed matcher directly from an already-open
        /// <see cref="MatcherBlobReader"/>. Used by tests that can build a
        /// plaintext-backed reader without the v3 encryption layer. The
        /// returned matcher takes ownership of the reader.
        /// </summary>
        public static OptimizedMatcher LoadFromReaderMmap(
            MatcherBlobReader reader,
            string inputLanguage,
            IProgress<(int percent, string message)> progress = null)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            bool isEngFlag = inputLanguage == "EN";
            int ngramSize = isEngFlag ? 4 : 2;
            int entryCount = reader.EntryCount;

            if (entryCount == 0)
            {
                // Trivially-empty matcher — still owns the reader so callers
                // don't accidentally leave the mmap view open.
                return new OptimizedMatcher(
                    reader,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<int>(),
                    new Dictionary<long, int[]>(0),
                    Array.Empty<int>(),
                    symSpellIndex: null,
                    isEngFlag,
                    ngramSize);
            }

            progress?.Report((50, $"Indexing {entryCount:N0} keys (mmap)..."));

            var normKeys = new string[entryCount];
            var origKeys = new string[entryCount];
            var keyLengths = new int[entryCount];
            var shortKeysList = new List<int>();

            // Single pass over the FST: capture origKey + derived normKey
            // per slot. Values are left in the blob — we never decode them
            // during index construction. This is the big RSS saving.
            int seen = 0;
            foreach (var kv in reader.EnumerateKeys())
            {
                int slot = kv.Value;
                if (slot < 0 || slot >= entryCount) continue;

                string key = kv.Key;
                string normKey = NormalizeInput(key, isEngFlag);
                origKeys[slot] = key;
                normKeys[slot] = normKey;
                keyLengths[slot] = normKey.Length;
                if (normKey.Length < ngramSize) shortKeysList.Add(slot);

                seen++;
                if (progress != null && seen % 50000 == 0)
                {
                    int pct = (int)(50 + (seen * 25.0 / entryCount));
                    progress.Report((pct, $"Indexing keys... {seen:N0}/{entryCount:N0}"));
                }
            }

            int[] shortKeysIndices = shortKeysList.ToArray();

            progress?.Report((80, "Building in-memory n-gram + SymSpell indexes..."));

            // N-gram + SymSpell only reference normKeys — no value strings
            // involved. We reuse the shared static builder from the
            // heap-resident path so there's exactly one implementation.
            var ngramIndex = BuildNgramIndexFromNormKeys(normKeys, keyLengths, ngramSize, shortKeysIndices);

            SymSpellIndex symSpellIndex = null;
            if (isEngFlag && Config.Get<bool>("UseSymSpell", true))
            {
                // SymSpell needs origKeys + values arrays because its public
                // GetOriginalKey/GetValue accessors dereference them. We
                // pass `normKeys` + `origKeys` resident arrays and an
                // empty-string array as the values placeholder — the
                // matcher's FindClosestMatch translates SymSpell's slot id
                // back into a value via the mmap reader, so SymSpell's
                // own values[] array is effectively unused. This avoids
                // allocating a second 60 MB values[] string array just to
                // satisfy SymSpell's ctor signature.
                var emptyValues = new string[entryCount];
                // `null` string cells are legal — SymSpellIndex.GetValue is
                // only ever invoked for paths that aren't taken in mmap
                // mode; we keep the array itself so the 8 B/slot reference
                // overhead isn't NullReferenceException-triggering.
                symSpellIndex = new SymSpellIndex(normKeys, origKeys, emptyValues);
            }

            progress?.Report((98, "Matcher ready (mmap-backed)"));

            return new OptimizedMatcher(reader, normKeys, origKeys, keyLengths,
                ngramIndex, shortKeysIndices, symSpellIndex, isEngFlag, ngramSize);
        }

        /// <summary>
        /// Mmap-mode variant of <see cref="BuildNgramIndexFromEntries"/>.
        /// Reads from parallel <paramref name="normKeys"/> + <paramref name="keyLengths"/>
        /// arrays so we don't have to rehydrate an Entry[] just to build
        /// the n-gram bloom. Same freeze-on-return shape as the Entry[]
        /// builder — callers receive int[] buckets so the hot path doesn't
        /// carry List<int> headers.
        /// </summary>
        private static Dictionary<long, int[]> BuildNgramIndexFromNormKeys(
            string[] normKeys, int[] keyLengths, int ngramSize, int[] shortKeysExclude)
        {
            int count = normKeys.Length;
            var working = new Dictionary<long, List<int>>(count * 4);

            for (int idx = 0; idx < count; idx++)
            {
                string normKey = normKeys[idx];
                if (normKey == null) continue;
                int len = keyLengths[idx];
                if (len < ngramSize) continue; // already tracked in shortKeysIndices

                var distinctHashes = new HashSet<long>();
                var keySpan = normKey.AsSpan();
                for (int i = 0; i <= len - ngramSize; i++)
                {
                    long hash = GetNgramHash(keySpan.Slice(i, ngramSize));
                    if (distinctHashes.Add(hash))
                    {
                        if (!working.TryGetValue(hash, out var list))
                        {
                            list = new List<int>();
                            working[hash] = list;
                        }
                        list.Add(idx);
                    }
                }
            }

            return FreezeNgramIndex(working);
        }

        /// <summary>
        /// Decode the value for <paramref name="slot"/> from the mmap-backed
        /// blob. Scratch buffer is rented from the shared pool so the
        /// steady-state allocation is one <see cref="string"/> per lookup
        /// (same as the legacy in-memory path).
        /// </summary>
        private string FetchValueMmap(int slot)
        {
            if (_reader == null || slot < 0 || slot >= _reader.EntryCount) return "";
            var entry = _reader.GetEntry(slot);
            int plain = (int)entry.PlaintextLength;
            if (plain <= 0) return string.Empty;

            byte[] rented = ArrayPool<byte>.Shared.Rent(plain);
            try
            {
                int n = _reader.DecodeValue(slot, rented.AsSpan(0, plain));
                return Encoding.UTF8.GetString(rented, 0, n);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        /// <summary>
        /// Look up an exact original key (as the dictionary would store it)
        /// and return its value. Used by <see cref="FindMatchWithHeaderSeparated"/>
        /// to resolve header lines that already come in as the exact corpus
        /// key. Works in both mmap and heap-resident modes.
        ///
        /// In heap-resident mode the origKey→slot index is built lazily on
        /// the first call and reused for the rest of the session (see
        /// <see cref="_origKeyToSlot"/>). That defers a ~17 MB allocation
        /// from load-time until it's actually needed, and skips it entirely
        /// for sessions that never hit a multi-line OCR result.
        /// </summary>
        private bool TryGetExactValue(string origKey, out string value)
        {
            if (string.IsNullOrEmpty(origKey)) { value = null; return false; }

            if (_reader != null)
            {
                // Mmap mode: use the FST + zstd decoder directly.
                return _reader.TryGetValue(origKey, out value);
            }

            // Heap-resident mode: lazy origKey → slot index, then dereference.
            var map = _origKeyToSlot;
            if (map == null) map = EnsureOrigKeyToSlot();
            if (map != null && map.TryGetValue(origKey, out int slot)
                && slot >= 0 && slot < (_entries?.Length ?? 0))
            {
                value = _entries[slot].Value;
                return value != null;
            }

            value = null;
            return false;
        }

        /// <summary>
        /// Build the <see cref="_origKeyToSlot"/> map the first time it's
        /// needed. Double-checked lock guards against concurrent builds on
        /// a cold matcher — the first caller pays the one-shot cost, the
        /// rest spin briefly on the lock and then read the published map.
        /// Returns null when there's no heap-resident entries array (mmap
        /// mode callers already took the early return above).
        /// </summary>
        private FrozenDictionary<string, int> EnsureOrigKeyToSlot()
        {
            if (_entries == null) return null;

            lock (_origKeyToSlotLock)
            {
                var existing = _origKeyToSlot;
                if (existing != null) return existing;

                // Build mutably first, then freeze: FrozenDictionary has no
                // incremental-add API. On 488k keys the Dictionary build takes
                // ~30-50 ms; freezing another ~40-150 ms. Still one-shot on
                // the first header-exact-match call.
                var builder = new Dictionary<string, int>(_entries.Length, StringComparer.Ordinal);
                for (int i = 0; i < _entries.Length; i++)
                {
                    string k = _entries[i].OriginalKey;
                    if (k != null)
                    {
                        // Last-writer-wins on the astronomically-rare duplicate
                        // case. MatcherBlobWriter enforces uniqueness at build
                        // time so this is defensive only.
                        builder[k] = i;
                    }
                }

                var frozen = builder.ToFrozenDictionary(StringComparer.Ordinal);

                // Volatile publish. Readers that saw a null _origKeyToSlot
                // above either observe the fully-populated map we publish
                // here, or take the lock and observe the same.
                _origKeyToSlot = frozen;
                return frozen;
            }
        }

        /// <summary>
        /// Accessor: normalized key for slot id. Branches once on mode.
        /// JIT inlines in both modes because the branch predicate is a
        /// readonly field set at construction time.
        /// </summary>
        private string GetNormKey(int slot)
        {
            return _reader != null ? _normKeys[slot] : _entries[slot].NormalizedKey;
        }

        private int GetKeyLength(int slot)
        {
            return _reader != null ? _keyLengths[slot] : _entries[slot].Length;
        }

        private string GetOriginalKey(int slot)
        {
            return _reader != null ? _origKeys[slot] : _entries[slot].OriginalKey;
        }

        /// <summary>
        /// Resolve the winning slot's PL value. Legacy mode reads the
        /// pre-materialised <see cref="Entry.Value"/>; mmap mode pays one
        /// ZSTD decompress + UTF8 decode (≈10 μs amortised).
        /// </summary>
        private string GetValue(int slot)
        {
            return _reader != null ? FetchValueMmap(slot) : _entries[slot].Value;
        }

        public string FindClosestMatch(string input, out string Key)
        {
            string normInput = NormalizeInput(input, isEng);

            if (string.IsNullOrEmpty(normInput))
            {
                Key = "";
                return "";
            }

            // --- Stage 0: SymSpell Fast Path (EN only, edit distance <= 2) ---
            // Catches exact matches with 1-2 OCR character errors in ~5us.
            // In mmap mode SymSpellIndex.GetValue returns a null sentinel
            // (the matcher holds the live values[] behind the reader), so
            // we pull the value via FetchValueMmap whenever that happens.
            if (_symSpellIndex != null && _symSpellIndex.IsReady)
            {
                if (_symSpellIndex.TryFindMatch(normInput, out int symIdx, out int symDist))
                {
                    // SymSpell found a match -- verify it's reasonable
                    string symKey = _symSpellIndex.GetOriginalKey(symIdx);
                    string symValue = _symSpellIndex.GetValue(symIdx);
                    if (string.IsNullOrEmpty(symValue) && _reader != null)
                    {
                        // Mmap mode: values[] array inside SymSpell is empty
                        // by design — go fetch the real value from the blob.
                        symValue = FetchValueMmap(symIdx);
                    }

                    // For exact/near-exact matches (distance 0-2), return immediately
                    if (symDist <= 2 && !string.IsNullOrEmpty(symValue))
                    {
                        Key = symKey;
                        return symValue;
                    }
                }
            }

            int inputLen = normInput.Length;
            HashSet<int> candidates = new HashSet<int>();

            // --- Stage 1: Candidate Selection (N-gram Pruning) ---

            if (inputLen < _ngramSize)
            {
                foreach (var id in _shortKeysIndices) candidates.Add(id);
            }
            else
            {
                int maxCandidatesPerGram = 2000;
                bool foundRareGram = false;
                int step = inputLen > 50 ? 2 : 1;

                // Net8 Span hot path: slice the normalized input once. Per OCR
                // tick the n-gram loop runs ~inputLen/step times; sharing the
                // same ROSpan<char> across iterations avoids any repeated
                // AsSpan() allocation (which is zero-alloc anyway but keeps
                // the IL tighter for PGO).
                var inputSpan = normInput.AsSpan();
                for (int i = 0; i <= inputLen - _ngramSize; i += step)
                {
                    long hash = GetNgramHash(inputSpan.Slice(i, _ngramSize));
                    if (_ngramIndex.TryGetValue(hash, out var ids))
                    {
                        if (ids.Length > maxCandidatesPerGram) continue;
                        foundRareGram = true;
                        foreach (var id in ids)
                        {
                            candidates.Add(id);
                        }
                    }
                }

                if (!foundRareGram)
                {
                    for (int i = 0; i <= inputLen - _ngramSize; i += step)
                    {
                        long hash = GetNgramHash(inputSpan.Slice(i, _ngramSize));
                        if (_ngramIndex.TryGetValue(hash, out var ids))
                        {
                            foreach (var id in ids) candidates.Add(id);
                        }
                    }
                }

                if (inputLen < 10)
                {
                    foreach (var id in _shortKeysIndices) candidates.Add(id);
                }
            }

            if (candidates.Count == 0)
            {
                Key = "";
                return "";
            }

            // --- Stage 2: Exact Distance Calculation ---

            int globalBestDistance = int.MaxValue;
            float globalBestWeightedDist = float.MaxValue;
            int bestIndex = -1;

            // Snapshot the mode-specific arrays into locals once per call.
            // In mmap mode the JIT only has to hoist the null-check on
            // `_reader` once — after that the hot loop is a straight
            // array index, same as the legacy Entry[] path.
            bool mmap = _reader != null;
            string[] normKeysArr = _normKeys;
            int[] keyLensArr = _keyLengths;
            Entry[] entriesArr = _entries;

            foreach (int id in candidates)
            {
                string normKey;
                int keyLen;
                if (mmap)
                {
                    normKey = normKeysArr[id];
                    keyLen = keyLensArr[id];
                }
                else
                {
                    ref readonly var e = ref entriesArr[id];
                    normKey = e.NormalizedKey;
                    keyLen = e.Length;
                }

                int currentDistance;

                // Prefix matching (handles typewriter effect -- OCR captures partial sentence)
                if (keyLen >= inputLen)
                {
                    if (normKey.StartsWith(normInput, StringComparison.Ordinal))
                    {
                        currentDistance = 0;
                    }
                    else if (inputLen > 10 && keyLen <= 150 && normKey.Contains(normInput))
                    {
                        currentDistance = 0;
                    }
                    else
                    {
                        ReadOnlySpan<char> keySpan = normKey.AsSpan().Slice(0, inputLen);

                        if (_useOcrWeightedDistance && isEng)
                        {
                            // OCR-weighted distance: common OCR confusions cost less
                            float weightedDist = OcrWeightedDistance.Calculate(
                                normInput.AsSpan(), keySpan, globalBestWeightedDist);
                            currentDistance = (int)Math.Ceiling(weightedDist);

                            if (weightedDist < globalBestWeightedDist)
                            {
                                globalBestWeightedDist = weightedDist;
                            }
                        }
                        else
                        {
                            currentDistance = CalculateLevenshteinDistance(normInput.AsSpan(), keySpan, globalBestDistance);
                        }
                    }
                }
                else
                {
                    if ((inputLen - keyLen) > globalBestDistance) continue;

                    if (normInput.StartsWith(normKey, StringComparison.Ordinal))
                    {
                        currentDistance = 0;
                    }
                    else
                    {
                        if (_useOcrWeightedDistance && isEng)
                        {
                            float weightedDist = OcrWeightedDistance.Calculate(
                                normInput.AsSpan(), normKey.AsSpan(), globalBestWeightedDist);
                            currentDistance = (int)Math.Ceiling(weightedDist);

                            if (weightedDist < globalBestWeightedDist)
                            {
                                globalBestWeightedDist = weightedDist;
                            }
                        }
                        else
                        {
                            currentDistance = CalculateLevenshteinDistance(normInput.AsSpan(), normKey.AsSpan(), globalBestDistance);
                        }
                    }
                }

                if (currentDistance < globalBestDistance)
                {
                    globalBestDistance = currentDistance;
                    bestIndex = id;
                    if (currentDistance == 0) break;
                }
            }

            // --- Stage 3: Verification ---

            if (bestIndex != -1)
            {
                // Dynamic threshold: English needs more tolerance due to OCR noise
                // OCR-weighted distance produces lower values for common confusions,
                // so the threshold can be slightly tighter
                double threshold;
                if (_useOcrWeightedDistance && isEng)
                {
                    // With weighted distance, use the float value for more precise threshold check
                    double weightedThreshold = Math.Max(4, inputLen * 0.35);
                    if (globalBestWeightedDist <= weightedThreshold)
                    {
                        Key = GetOriginalKey(bestIndex);
                        return GetValue(bestIndex);
                    }
                }

                threshold = isEng ? Math.Max(5, inputLen * 0.4) : Math.Max(2, inputLen * 0.4);
                if (globalBestDistance <= threshold)
                {
                    Key = GetOriginalKey(bestIndex);
                    return GetValue(bestIndex);
                }
            }

            Key = "";
            return "";
        }

        private static int CalculateLevenshteinDistance(ReadOnlySpan<char> source, ReadOnlySpan<char> target, int threshold)
        {
            int sourceLen = source.Length;
            int targetLen = target.Length;

            if (sourceLen == 0) return targetLen;
            if (targetLen == 0) return sourceLen;
            if (Math.Abs(sourceLen - targetLen) > threshold) return threshold + 1;

            if (sourceLen > targetLen)
            {
                var temp = source; source = target; target = temp;
                var tempLen = sourceLen; sourceLen = targetLen; targetLen = tempLen;
            }

            Span<int> prev = sourceLen < 512 ? stackalloc int[sourceLen + 1] : new int[sourceLen + 1];
            Span<int> curr = sourceLen < 512 ? stackalloc int[sourceLen + 1] : new int[sourceLen + 1];

            for (int i = 0; i <= sourceLen; i++) prev[i] = i;

            for (int j = 1; j <= targetLen; j++)
            {
                curr[0] = j;
                int minInRow = j;
                char targetChar = target[j - 1];

                for (int i = 1; i <= sourceLen; i++)
                {
                    int cost = (source[i - 1] == targetChar) ? 0 : 1;
                    int d1 = curr[i - 1] + 1;
                    int d2 = prev[i] + 1;
                    int d3 = prev[i - 1] + cost;

                    int dist = (d1 < d2) ? d1 : d2;
                    if (d3 < dist) dist = d3;

                    curr[i] = dist;
                    if (dist < minInRow) minInRow = dist;
                }

                if (minInRow > threshold) return threshold + 1;
                var tempRow = prev; prev = curr; curr = tempRow;
            }

            return prev[sourceLen];
        }

        public static string NormalizeInput(string input, bool isEng)
        {
            if (string.IsNullOrEmpty(input)) return "";

            // Net8 migration (2026-04-23): swapped `char[] + new string(char[], 0, idx)`
            // for stackalloc + new string(ReadOnlySpan<char>). Zero heap alloc on
            // the scratch buffer path. Per OCR tick this kills thousands of tiny
            // char[] allocations during the candidate-normalize loop (~1-2 ms
            // of GC pressure). Input length is capped at 512 to stay on the
            // stack; longer inputs fall back to a pooled buffer.
            int len = input.Length;
            char[] rented = len > 512 ? System.Buffers.ArrayPool<char>.Shared.Rent(len) : null;
            Span<char> result = rented != null ? rented.AsSpan(0, len) : stackalloc char[len];

            try
            {
                int idx = 0;
                if (!isEng)
                {
                    // CN: Remove whitespace only
                    for (int i = 0; i < len; i++)
                    {
                        char c = input[i];
                        if (!char.IsWhiteSpace(c)) result[idx++] = c;
                    }
                }
                else
                {
                    // EN: Aggressive Normalization (Letters & Digits only, Lowercase)
                    for (int i = 0; i < len; i++)
                    {
                        char c = input[i];
                        if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                        {
                            result[idx++] = c;
                        }
                        else if (c >= 'A' && c <= 'Z')
                        {
                            result[idx++] = (char)(c + 32);
                        }
                    }
                }

                // idx == 0 short-circuit returns a shared empty string.
                if (idx == 0) return string.Empty;

                // new string(ReadOnlySpan<char>) — single allocation for the
                // final immutable string. No intermediate char[].
                return new string(result.Slice(0, idx));
            }
            finally
            {
                if (rented != null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }
        }

        public MatchResult FindMatchWithHeaderSeparated(string ocrText, out string key)
        {
            key = "";
            var result = new MatchResult { Header = "", Content = "" };

            if (string.IsNullOrEmpty(ocrText)) return result;

            string[] lines = ocrText.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 1)
            {
                result.Content = FindClosestMatch(lines[0], out key);
                return result;
            }

            int maxLength = 0;
            int maxIndex = 0;
            if (!isEng)
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Length > maxLength * 1.3)
                    {
                        maxLength = lines[i].Length;
                        maxIndex = i;
                    }
                }
                if (IsTitleCase(lines[maxIndex]) && IsEnglishLine(lines[maxIndex]) && maxIndex < lines.Length - 1)
                {
                    maxIndex++;
                }
            }
            else
            {
                if (lines[1].Length > lines[0].Length * 2)
                {
                    maxIndex = 1;
                }
            }
            List<string> headers = new List<string>();
            for (int i = 0; i < maxIndex; i++) headers.Add(lines[i]);

            string bodyText = string.Join(" ", lines.Skip(maxIndex));

            string headerMatch = "";
            foreach (string header in headers)
            {
                if (TryGetExactValue(header, out string headerValue)
                    && !string.IsNullOrEmpty(headerValue)
                    && !headerValue.Contains("test"))
                {
                    if (!string.IsNullOrEmpty(headerMatch)) headerMatch += " ";
                    headerMatch += headerValue;
                }
            }
            string bodyMatch = FindClosestMatch(bodyText, out string bodyKey);
            if (string.IsNullOrEmpty(bodyMatch) && maxIndex > 1)
            {
                bodyMatch = FindClosestMatch(string.Join(" ", lines.Skip(1)), out bodyKey);
            }
            key = bodyKey;
            result.Content = bodyMatch;
            result.Header = headerMatch;
            return result;
        }

        private static bool IsTitleCase(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (char.IsLetter(text[0]) && !char.IsUpper(text[0])) return false;
            return true;
        }

        private static bool IsEnglishLine(string text)
        {
            int engCount = 0;
            int len = 0;
            foreach (char c in text)
            {
                if (char.IsLetter(c))
                {
                    len++;
                    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) engCount++;
                }
            }
            return len > 0 && ((double)engCount / len) > 0.8;
        }

        /// <summary>
        /// Release the mmap view / ZSTD decompressor / file handle owned
        /// by this matcher. A no-op for heap-resident matchers (GSMX /
        /// plain-ctor path). Safe to call multiple times; idempotent.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _reader?.Dispose(); } catch { /* best-effort */ }
            GC.SuppressFinalize(this);
        }
    }
}
