using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Security;
using GI_Subtitles.Services.Translation.Strategies;
using Newtonsoft.Json;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// Template-method + injected-strategy base for the dialogue prediction
    /// engine. The public behaviour is identical to the pre-refactor
    /// <c>DialogueContextEngine</c> for Genshin — algorithms, thresholds,
    /// disambiguation priorities, and cache invariants were carried over
    /// verbatim in the 2026-04-17 refactor. Per-game subclasses override
    /// only what actually differs (quest-type codes, next-edge resolution,
    /// name normalisation) via the virtual hooks or the DI'd strategy set.
    /// </summary>
    public abstract class DialogueContextBase : IGameDialogueContext, IDialogueGraphAccessor
    {
        // --- Graph data (loaded once at startup) ---
        // All ids use long to handle game values that exceed Int32.MaxValue.

        private Dictionary<long, DialogNode> _dialogGraph;
        private Dictionary<string, List<long>> _textToDialogIds;
        private Dictionary<string, string> _npcNames;
        private Dictionary<long, TalkNode> _talkIndex;
        // Post phase-3: a slim residual map that carries ONLY the hashes still
        // read at runtime through IDialogueGraphAccessor.TryGetTextMapValue.
        // The full ~90–110 MB TextMapEN dictionary is materialised as a local
        // inside LoadCore, consumed to populate DialogNode.EnText +
        // QuestInfoEntry.QuestTitle + _npcNames, then released to the GC. What
        // survives here is typically just quest-title entries (title hash was
        // inlined on the quest entry anyway, but we keep the slim map so
        // interface implementations that take an arbitrary hash keep working).
        private Dictionary<string, string> _textMapEN;

        // NPC-wide index: npcRoleId -> list of dialogIds (for idle talk prediction).
        private Dictionary<string, List<long>> _npcToDialogIds;

        // Reverse NPC name lookup: normalized display name -> roleId(s).
        // Enables pre-loading hot cache when NPC name is detected via HSV color.
        private Dictionary<string, List<string>> _npcNameToRoleIds;

        // Reverse index: dialogId -> talkId (built during Load for O(1) talk lookup).
        private Dictionary<long, long> _dialogToTalkId;

        // Quest info loaded from QuestInfo.json.
        private Dictionary<long, QuestInfoEntry> _questInfo;

        // Track which NPCs are already loaded in the current conversation.
        private readonly HashSet<string> _loadedNpcs = new HashSet<string>();

        // --- Runtime state (updated during gameplay) ---

        private long _activeDialogId;
        private long[] _immediateNextIds = Array.Empty<long>(); // direct nextDialogs of active node

        // Two-tier hot cache: chain cache (precise, from nextDialogs) checked first,
        // NPC cache (broad, from PreloadForNpc) as fallback.
        private readonly Dictionary<string, HotCacheEntry> _chainCache; // nextDialogs predictions
        private readonly Dictionary<string, HotCacheEntry> _npcCache;   // NPC-wide dialogue lines
        private readonly List<string> _contextHistory;
        private const int MAX_CONTEXT_HISTORY = 10;
        private const int MAX_CHAIN_CACHE = 50;
        // NPC cache accumulates across all conversation participants and
        // orphan-body-match learnings within a single dialogue. Cleared on
        // Reset (subtitle fade-out = dialogue end). Typical size during a
        // single talk: 200-1000 entries.
        //
        // HARD CAP added 2026-04-24: Reset() only fires on subtitle-overlay
        // fade-out. In long gameplay sessions spanning many quests without
        // a full overlay dismissal, _npcCache kept growing unbounded and
        // user reported RSS climbing to ~1.8 GB (each HotCacheEntry is 3
        // string refs + 8 bytes; but LearnOrphanBodyMatch in a noisy OCR
        // session can fire hundreds of times per dialogue). When the cap is
        // hit we Clear() the entire cache — rebuilds from the next OCR tick
        // via PopulateHotCache + PreloadForNpc. Cost of rebuild is tiny
        // (cache is predictions, not ground truth); cost of unbounded growth
        // was 50-200 MB of retained strings + dict overhead per play session.
        private const int MAX_NPC_CACHE = 3000;

        private readonly object _lock = new object();

        // --- Strategies ---

        private readonly INextNodeResolver _nextResolver;
        private readonly IQuestBannerFormatter _questFmt;
        private readonly INpcNameNormalizer _nameNorm;
        private readonly INpcRoleDisambiguator _disambig;

        /// <summary>Access the next-node strategy directly (rare — prefer the
        /// <see cref="GetNextNodeIds"/> virtual hook). Exposed for subclasses
        /// that need to swap behaviour inside their own overrides.</summary>
        protected INextNodeResolver NextResolver => _nextResolver;

        /// <summary>Access the quest-banner strategy directly.</summary>
        protected IQuestBannerFormatter QuestFormatter => _questFmt;

        /// <summary>Access the NPC-name normaliser directly.</summary>
        protected INpcNameNormalizer NameNormalizer => _nameNorm;

        /// <summary>Access the NPC-role disambiguator directly.</summary>
        protected INpcRoleDisambiguator Disambiguator => _disambig;

        // --- IGameDialogueContext surface ---

        /// <summary>
        /// True once <see cref="Load"/> has stamped the load parameters, OR
        /// once the underlying graph has fully materialised. This field
        /// deliberately flips the moment <see cref="Load"/> returns so that
        /// call-site gates (<c>ContextEngine?.IsLoaded == true</c>) continue
        /// to pass through to the real methods — each of which calls
        /// <see cref="EnsureLoadedCore"/> internally and materialises the
        /// graph on the first hit.
        ///
        /// Tests and diagnostics that need to know the underlying graph is
        /// really resident use <see cref="IsFullyLoaded"/>.
        /// </summary>
        public volatile bool IsLoaded;

        bool IGameDialogueContext.IsLoaded => IsLoaded;

        // Lazy-load state. Kept separate from IsLoaded so call-site gates
        // continue to flow through to hot-path methods. Flips to true only
        // when the underlying graph, NPC names, talk index, quest info, and
        // reverse indexes have all been populated in memory.
        private volatile bool _isFullyLoaded;

        /// <summary>
        /// True when the full graph + auxiliary indexes have been
        /// materialised in memory. False while the engine is in the
        /// prepared-but-not-yet-loaded lazy state. Mainly consumed by tests
        /// that want to assert deferral was honoured.
        /// </summary>
        public bool IsFullyLoaded => _isFullyLoaded;

        // Captured Load() parameters; consumed by EnsureLoadedCore on the
        // first hot-path call. Zero allocations until then — the big
        // structures (_dialogGraph, _textMapEN, _textToDialogIds, etc.)
        // stay null and cost nothing.
        private string _pendingDataDir;
        private string _pendingTextMapEnPath;
        private FileProtectionHelper _pendingProtectionHelper;

        // LazyInitializer sentinel for the materialisation step. One object
        // allocation, zero state until first use. ExecutionAndPublication
        // semantics: exactly one thread runs LoadCore, all others wait.
        // Sentinel lifted by LazyInitializer. Null until the first
        // successful materialisation, which atomically publishes the
        // instance via Interlocked.CompareExchange inside the BCL.
        private object _loadSentinel;
        // The bool+lock pair required by the
        // EnsureInitialized(ref T, ref bool, ref object, Func<T>) overload
        // — neither may be readonly (both are passed by ref).
        private bool _loadInitialized;
        private object _loadInitLock = new object();

        /// <summary>Total hot-cache hits. Volatile counter — not thread-safe
        /// down to the last increment, but diagnostics only.</summary>
        public int HotCacheHits;

        int IGameDialogueContext.HotCacheHits => HotCacheHits;

        /// <summary>Total hot-cache misses.</summary>
        public int HotCacheMisses;

        int IGameDialogueContext.HotCacheMisses => HotCacheMisses;

        /// <summary>True when the graph shows exactly 1 immediate next line.
        /// Uses the graph structure (not cache size) so it's reliable even
        /// with accumulated cache entries.</summary>
        public bool HasSingleChainPrediction => _immediateNextIds.Length == 1;

        // --- Virtual hooks (template-method seams) ---

        /// <summary>Forward-edge lookup. Default: dispatches to
        /// <see cref="INextNodeResolver"/>. Override to bypass the strategy.</summary>
        protected virtual long[] GetNextNodeIds(long nodeId)
            => _nextResolver.Resolve(nodeId, this);

        /// <summary>Quest banner formatting. Default: dispatches to
        /// <see cref="IQuestBannerFormatter"/>.</summary>
        protected virtual (string title, string type)? ResolveQuestBanner(long questId)
            => _questFmt.Format(questId, this);

        /// <summary>Full-name normalisation used by reverse index keys.</summary>
        protected virtual string NormalizeNpcName(string raw)
            => _nameNorm.NormalizeFull(raw);

        /// <summary>First-name extraction used by preload lookups.</summary>
        protected virtual string ExtractNpcFirstName(string raw)
            => _nameNorm.ExtractFirstName(raw);

        /// <summary>Candidate disambiguation for shared-content-hash nodes.</summary>
        protected virtual long DisambiguateNode(List<long> candidates, string npcFilter)
            => _disambig.Disambiguate(candidates, npcFilter, this);

        /// <summary>Chain-cache prefix-match threshold when the graph shows
        /// exactly one forward edge. Any input shorter than this is NOT
        /// matched as a prefix — preserves the session-13 tuning that cured
        /// confident-wrong translations on typewriter openings like "I'm…".</summary>
        protected virtual int ChainPrefixThresholdSingle => 8;

        /// <summary>Chain-cache prefix-match threshold when the graph shows
        /// multiple forward edges. Higher than the single-edge case because
        /// branches share more leading text.</summary>
        protected virtual int ChainPrefixThresholdMulti => 12;

        /// <summary>
        /// Optional bundle-game gate. When non-null and a bundle-meta file is
        /// present on disk, <see cref="Load"/> refuses to proceed if the
        /// bundle's declared game doesn't match (case-insensitive). Null =
        /// accept any bundle (useful for tests / forward compat).
        /// </summary>
        protected virtual string ExpectedBundleGame => null;

        // --- Construction ---

        /// <summary>
        /// Construct the base with optional strategy overrides. Passing null
        /// for any slot wires in the shipping default, which reproduces the
        /// pre-refactor behaviour exactly.
        /// </summary>
        protected DialogueContextBase(
            INextNodeResolver nextResolver = null,
            IQuestBannerFormatter questFmt = null,
            INpcNameNormalizer nameNorm = null,
            INpcRoleDisambiguator disambig = null)
        {
            _nextResolver = nextResolver ?? new GraphNextResolver();
            _questFmt = questFmt ?? new DefaultQuestBannerFormatter();
            _nameNorm = nameNorm ?? new TrimNameNormalizer();
            _disambig = disambig ?? new NpcNameDisambiguator();

            _chainCache = new Dictionary<string, HotCacheEntry>(MAX_CHAIN_CACHE, StringComparer.OrdinalIgnoreCase);
            _npcCache = new Dictionary<string, HotCacheEntry>(StringComparer.OrdinalIgnoreCase);
            _contextHistory = new List<string>(MAX_CONTEXT_HISTORY);
            IsLoaded = false;
        }

        // --- Data structures (private to engine internals) ---

        private struct HotCacheEntry
        {
            public string Translation;
            public long DialogId;
            public string OriginalEnText;
        }

        private struct QuestInfoEntry
        {
            public ulong TitleHash;
            public string QuestType;
            /// <summary>English title pre-resolved from <see cref="TitleHash"/>
            /// against TextMapEN at load time. Null when unresolved. See
            /// <see cref="DialogNode.EnText"/> for the rationale behind
            /// inlining the resolved text on the owning record.</summary>
            public string QuestTitle;
        }

        // --- IDialogueGraphAccessor implementation ---

        long IDialogueGraphAccessor.ActiveDialogId => _activeDialogId;

        bool IDialogueGraphAccessor.TryGetNode(long id, out DialogNode node)
        {
            if (_dialogGraph != null && _dialogGraph.TryGetValue(id, out node))
                return true;
            node = default;
            return false;
        }

        bool IDialogueGraphAccessor.TryGetTalkNode(long id, out TalkNode node)
        {
            if (_talkIndex != null && _talkIndex.TryGetValue(id, out node))
                return true;
            node = default;
            return false;
        }

        bool IDialogueGraphAccessor.TryGetNpcName(string roleId, out string displayName)
        {
            if (!string.IsNullOrEmpty(roleId) && _npcNames != null && _npcNames.TryGetValue(roleId, out displayName))
                return true;
            displayName = null;
            return false;
        }

        bool IDialogueGraphAccessor.TryGetQuestInfo(long questId, out (ulong TitleHash, string QuestType) info)
        {
            if (_questInfo != null && _questInfo.TryGetValue(questId, out var entry))
            {
                info = (entry.TitleHash, entry.QuestType);
                return true;
            }
            info = default;
            return false;
        }

        bool IDialogueGraphAccessor.TryGetTextMapValue(string hashStr, out string text)
        {
            if (!string.IsNullOrEmpty(hashStr) && _textMapEN != null && _textMapEN.TryGetValue(hashStr, out text))
                return true;
            text = null;
            return false;
        }

        // --- Load ---

        /// <summary>
        /// Prepare the engine for on-demand loading. This call does NOT
        /// materialise the ~200k dialog nodes / ~88k text entries / ~60 MB
        /// TextMapEN that make up the dialogue graph. It only validates the
        /// BundleMeta sidecar, stamps the caller's parameters, and flips
        /// <see cref="IsLoaded"/> so callers' <c>IsLoaded == true</c> gates
        /// flow through to the hot-path methods. The real load happens
        /// inside those hot-path methods on first use, via
        /// <see cref="EnsureLoadedCore"/>.
        ///
        /// RAM payoff: when the user boots Kaption but never actually
        /// triggers OCR against dialogue (stays in menus, browses settings,
        /// closes the app during setup), none of the graph structures are
        /// ever allocated. On a 1 GB-free cold boot this recovers 80-150 MB
        /// that the old eager-load burned unconditionally.
        ///
        /// Blocking cost on first OCR match: 1-3 s on SSD to parse graph +
        /// TextMapEN. That cost lives on the background OCR tick, never on
        /// the UI thread.
        /// </summary>
        public void Load(string dataDir, string textMapEnPath,
            IProgress<(int percent, string message)> progress = null,
            FileProtectionHelper protectionHelper = null)
        {
            try
            {
                // Forward-compat: optional BundleMeta file lets us gate on
                // per-game bundle content. v1 bundles (current shipping
                // format) don't emit this file, so a missing meta is a
                // valid "accept" path. See GamedataSyncService for the
                // bundle_version source. This is the ONE check we do
                // eagerly — it's a sub-KB file and it determines whether
                // we're willing to load *anything* for this game at all.
                if (!ValidateBundleMeta(dataDir, protectionHelper))
                {
                    IsLoaded = false;
                    _isFullyLoaded = false;
                    return;
                }

                // Stamp the parameters. The real LoadCore() runs inside
                // EnsureLoadedCore() on the first hot-path call.
                _pendingDataDir = dataDir;
                _pendingTextMapEnPath = textMapEnPath;
                _pendingProtectionHelper = protectionHelper;

                // Reset the LazyInitializer triplet so a re-Load (e.g.,
                // user switches Game in SettingsWindow) actually rebuilds
                // the graph instead of serving the stale one. All three
                // fields must reset together — leaving _loadInitialized
                // true would make LazyInitializer skip the factory on the
                // next EnsureLoadedCore call.
                _loadSentinel = null;
                _loadInitialized = false;
                _isFullyLoaded = false;

                // Flip the public gate. Hot-path call sites gated on
                // `IsLoaded == true` now pass through; their internal
                // EnsureLoadedCore() call materialises the graph on demand.
                IsLoaded = true;

                progress?.Report((100, "Dialogue engine ready (deferred load)"));
                Logger.Log.Info("DialogueContext prepared for lazy load — graph materialises on first hot-path call.");
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Failed to prepare dialogue engine: {ex.Message}");
                IsLoaded = false;
                _isFullyLoaded = false;
            }
        }

        /// <summary>
        /// Force eager materialisation — test hook. Production code MUST
        /// NOT call this; it exists so integration tests like
        /// <c>DialoguePredictionTests</c> can continue to call
        /// <c>engine.Load(...)</c> followed by <c>engine.ForceLoadForTests()</c>
        /// instead of having to fabricate a first-hot-path call to kick off
        /// the lazy init. Also useful in the rare case a deployment wants
        /// to fall back to the pre-lazy behaviour (warm cache on startup).
        /// </summary>
        public void ForceLoadForTests()
        {
            EnsureLoadedCore();
        }

        /// <summary>
        /// Materialise the dialogue graph + auxiliary indexes on demand.
        /// Thread-safe: uses <see cref="LazyInitializer.EnsureInitialized{T}"/>
        /// semantics so exactly one thread runs the load and all others
        /// block on the same sentinel. Called from every hot-path method
        /// (TryHotCacheMatch, OnTextMatched, PreloadForNpc, …) as the first
        /// statement.
        /// </summary>
        /// <returns>True when the graph is resident and hot-path logic can
        /// proceed. False when <see cref="Load"/> was never called, the
        /// BundleMeta check failed, or the underlying file parse threw.</returns>
        private bool EnsureLoadedCore()
        {
            if (_isFullyLoaded)
                return true;
            if (!IsLoaded)
                return false; // Load() never called / BundleMeta rejected
            if (_pendingDataDir == null)
                return false; // defensive — mismatch between IsLoaded and param state

            LazyInitializer.EnsureInitialized(ref _loadSentinel,
                ref _loadInitialized, ref _loadInitLock,
                () =>
                {
                    LoadCore(_pendingDataDir, _pendingTextMapEnPath, _pendingProtectionHelper);
                    return new object();
                });

            return _isFullyLoaded;
        }

        /// <summary>
        /// The real load. Moved out of <see cref="Load"/> in the lazy-init
        /// refactor so it can be deferred to first hot-path use. Runs
        /// exactly once per engine instance (enforced by the sentinel in
        /// <see cref="EnsureLoadedCore"/>). Progress reporting was dropped
        /// here because there's no UI observer on the hot-path tick —
        /// logging stays for diagnostics.
        /// </summary>
        private void LoadCore(string dataDir, string textMapEnPath, FileProtectionHelper protectionHelper)
        {
            var sw = Stopwatch.StartNew();
            GI_Subtitles.Core.Runtime.RamDiag.LogCheckpoint("DialogueContext before parse");
            // Full TextMapEN lives as a LOCAL for the duration of LoadCore
            // only. It's consumed by the graph/NPC/quest loaders to inline
            // resolved text onto each record (DialogNode.EnText,
            // QuestInfoEntry.QuestTitle, _npcNames values) and then deliberately
            // released by going out of scope so the GC can reclaim the
            // 90–110 MB backing Dictionary<string,string>.
            Dictionary<string, string> fullTextMap;
            try
            {
                // Load TextMapEN for hash resolution (public file, always plaintext).
                //
                // Pre-round-1: JsonConvert.DeserializeObject<Dictionary<string,string>>(
                //   reader.ReadToEnd()) materialised the full ~60 MB JSON payload
                // as a UTF-16 string (~120 MB heap) plus a JObject DOM (~200-400
                // MB of JProperty/JValue allocations) before building the final
                // dictionary. On a 1 GB-free cold boot that pushed peak commit
                // briefly past 2 GB.
                //
                // Post-round-1: stream through VoiceContentHelper's shared
                // Utf8JsonReader loop. Peak RAM during parse is O(final-dict +
                // one 64 KiB ArrayPool buffer) — ~90-110 MB instead of ~300-500.
                //
                // Error tolerance is preserved (silently skip non-string values,
                // tolerate comments + trailing commas); see
                // ReadFlatStringDictionaryFromJson for the exact rules.
                if (File.Exists(textMapEnPath))
                {
                    using (var stream = File.OpenRead(textMapEnPath))
                    {
                        fullTextMap = GI_Subtitles.Common.VoiceContentHelper
                            .StreamFlatJsonDictionary(
                                stream,
                                flattenWrappedObjects: false,
                                entryCapacityHint: 0)
                            ?? new Dictionary<string, string>();
                    }
                }
                else
                {
                    fullTextMap = new Dictionary<string, string>();
                }

                // If graph doesn't exist, download and build it.
                var graphPath = Path.Combine(dataDir, "DialogGraph.json");
                bool graphExists = protectionHelper != null
                    ? DialogGraphDownloader.GraphExists(dataDir, protectionHelper)
                    : File.Exists(graphPath);

                if (!graphExists)
                {
                    try
                    {
                        DialogGraphDownloader.DownloadAndBuild(dataDir, textMapEnPath,
                            progress: null, protectionHelper);
                    }
                    catch (Exception dlEx)
                    {
                        Logger.Log.Error($"Failed to download dialogue graph: {dlEx.Message}");
                        return;
                    }
                }

                // Re-check after potential build.
                graphExists = protectionHelper != null
                    ? protectionHelper.FileExists(graphPath)
                    : File.Exists(graphPath);
                if (!graphExists)
                {
                    Logger.Log.Warn("DialogGraph.json not found — prediction disabled");
                    return;
                }

                LoadDialogGraph(graphPath, fullTextMap, protectionHelper);

                var npcPath = Path.Combine(dataDir, "NpcNames.json");
                bool npcExists = protectionHelper != null
                    ? protectionHelper.FileExists(npcPath) : File.Exists(npcPath);
                if (npcExists)
                    LoadNpcNames(npcPath, fullTextMap, protectionHelper);

                var talkPath = Path.Combine(dataDir, "TalkIndex.json");
                bool talkExists = protectionHelper != null
                    ? protectionHelper.FileExists(talkPath) : File.Exists(talkPath);
                if (talkExists)
                    LoadTalkIndex(talkPath, protectionHelper);

                var questPath = Path.Combine(dataDir, "QuestInfo.json");
                bool questExists = protectionHelper != null
                    ? protectionHelper.FileExists(questPath) : File.Exists(questPath);
                if (questExists)
                    LoadQuestInfo(questPath, fullTextMap, protectionHelper);

                // Reverse indexes read the already-inlined DialogNode.EnText,
                // so they don't need the full text map any more. Build order
                // is load all the records → inline text onto each → build
                // indexes → then drop the full map.
                BuildTextToDialogIndex();
                BuildNpcNameReverseIndex();
                BuildDialogToTalkIndex();

                // All consumers of the full TextMapEN have run. Build a slim
                // residual for TryGetTextMapValue callers (quest titles are
                // inlined on QuestInfoEntry already; this map exists for the
                // long tail — namely whatever DefaultQuestBannerFormatter
                // still goes through until it's migrated to the inline title).
                // Keying by quest title hashes only keeps resident ~few-KB of
                // strings instead of ~90–110 MB.
                _textMapEN = BuildSlimTextMap(fullTextMap);

                // Belt-and-braces: explicit null so even if the next line
                // tripped we don't accidentally keep a reference through an
                // exception path. `fullTextMap` is the only root to the big
                // Dictionary<string,string>; clearing it lets gen-2 collection
                // reclaim the ~100 MB of string data in one go.
                // ReSharper disable once RedundantAssignment
                fullTextMap = null;

                _isFullyLoaded = true;
                sw.Stop();
                Logger.Log.Info($"DialogueContext lazy-init: loaded {_dialogGraph?.Count ?? 0} nodes, " +
                               $"{_textToDialogIds?.Count ?? 0} text entries in {sw.ElapsedMilliseconds} ms " +
                               $"(residual textmap: {_textMapEN?.Count ?? 0} entries)");
                GI_Subtitles.Core.Runtime.RamDiag.LogCheckpoint("DialogueContext after parse");
                // Do NOT call AggressiveReclaim here. Post-lazy-init this runs on
                // the FIRST hot-path call (PreloadForNpc / TryHotCacheMatch in the
                // OCR tick), i.e. on the UI thread. A blocking Gen2 + LOH-compact
                // stalls the UI for hundreds of ms right when the user is waiting
                // for a subtitle. The background watchdog in RamDiag now picks up
                // the post-load garbage via natural GC notifications — async,
                // non-blocking, same RSS outcome.
            }
            catch (Exception ex)
            {
                sw.Stop();
                Logger.Log.Error($"Failed to load dialogue graph on-demand after {sw.ElapsedMilliseconds} ms: {ex.Message}");
                _isFullyLoaded = false;
            }
        }

        /// <summary>
        /// Construct the post-load residual text-map covering only hashes
        /// that are still looked up through
        /// <see cref="IDialogueGraphAccessor.TryGetTextMapValue"/> at runtime.
        /// In practice this is the set of quest-title hashes — the old
        /// <c>DefaultQuestBannerFormatter</c> route reaches them through the
        /// accessor, and we preserve that behaviour to avoid churning the
        /// strategy interface.
        ///
        /// Dialog-node content hashes are deliberately excluded: every caller
        /// that needed them has been migrated to read <c>DialogNode.EnText</c>
        /// directly (see <c>TryAddToCache</c>, <c>GetSingleChainPrediction</c>,
        /// <c>GetPredictedAnswers</c>, <c>PopulateHotCache</c>). Keeping ~200k
        /// content strings here would defeat the whole phase-3 RAM win.
        /// </summary>
        private Dictionary<string, string> BuildSlimTextMap(Dictionary<string, string> fullMap)
        {
            // Ordinal comparer matches the original JSON-backed map. Hash strings
            // are bag-of-digits, so culture folding never mattered.
            var slim = new Dictionary<string, string>(
                _questInfo?.Count ?? 0, StringComparer.Ordinal);
            if (fullMap == null || _questInfo == null) return slim;

            foreach (var kv in _questInfo)
            {
                ulong titleHash = kv.Value.TitleHash;
                if (titleHash == 0) continue;
                string key = titleHash.ToString();
                if (slim.ContainsKey(key)) continue;
                if (fullMap.TryGetValue(key, out var title) && !string.IsNullOrEmpty(title))
                    slim[key] = title;
            }
            return slim;
        }

        /// <summary>
        /// Check an optional <c>BundleMeta</c> sidecar for bundle_version +
        /// extension.game. Returns false only when a meta is present AND the
        /// declared game contradicts <see cref="ExpectedBundleGame"/>.
        /// Missing meta = v1 bundle = accept.
        /// </summary>
        private bool ValidateBundleMeta(string dataDir, FileProtectionHelper protectionHelper)
        {
            if (string.IsNullOrEmpty(ExpectedBundleGame))
                return true;

            string metaPath = Path.Combine(dataDir, "BundleMeta.json");
            bool metaExists = protectionHelper != null
                ? protectionHelper.FileExists(metaPath)
                : File.Exists(metaPath);
            if (!metaExists)
                return true; // v1 bundles have no meta — tolerate.

            try
            {
                // Sub-KB sidecar file — a scoped streaming read by
                // Utf8JsonReader is both strictly cheaper and eliminates
                // this file's last JObject DOM allocation. Extracts
                // `extension.game` without materialising the other
                // sidecar fields (bundle_version, schema hints).
                string bundleGame = ReadBundleMetaGame(metaPath, protectionHelper);
                if (string.IsNullOrEmpty(bundleGame))
                    return true; // extension missing = v1 bundle — tolerate.

                if (!string.Equals(bundleGame, ExpectedBundleGame, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log.Error(
                        $"BundleMeta game mismatch: expected \"{ExpectedBundleGame}\" " +
                        $"but bundle declares \"{bundleGame}\". Refusing to load.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                // Parse failure when a game is expected → fail-closed.
                // A corrupt BundleMeta often co-occurs with a half-written
                // split file set (process killed mid-install). Loading
                // partial data into the prediction engine silently
                // degrades OCR matching; refusing to load forces a
                // clean re-sync on the next launch, which is cheap.
                Logger.Log.Error($"BundleMeta parse failed ({ex.Message}); refusing to load because ExpectedBundleGame is set.");
                return false;
            }
        }

        /// <summary>
        /// Streaming lookup of <c>extension.game</c> from the BundleMeta
        /// sidecar. Returns null when the file is missing the extension
        /// object or the game string — caller treats that as "v1 bundle,
        /// tolerate". Any parse exception bubbles up to
        /// <see cref="ValidateBundleMeta"/>'s fail-closed catch.
        ///
        /// Uses <see cref="System.Text.Json.Utf8JsonReader"/> rather than a
        /// Newtonsoft JObject DOM — the sidecar is small (sub-KB), but
        /// avoiding a JObject allocation here eliminates a measurable
        /// Newtonsoft warm-up cost on cold boots where BundleMeta.json is
        /// the only JObject site hit before the big TextMapEN parse.
        /// </summary>
        private static string ReadBundleMetaGame(string metaPath, FileProtectionHelper protectionHelper)
        {
            // Accept encrypted sidecar files too — same call shape as the
            // other LoadCore loaders. The plain-file path uses a direct
            // FileStream so the read never buffers the whole payload into
            // memory before the reader starts consuming bytes.
            using (var stream = protectionHelper != null
                ? protectionHelper.OpenForReading(metaPath)
                : (Stream)File.OpenRead(metaPath))
            {
                // Sidecar is always tiny (<1 KB), so read it once into a
                // stack-friendly buffer and run the STJ reader over the
                // span. Zero ArrayPool traffic.
                byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(4096);
                try
                {
                    int total = 0;
                    int read;
                    while ((read = stream.Read(rented, total, rented.Length - total)) > 0)
                    {
                        total += read;
                        if (total == rented.Length)
                        {
                            // Grow if the sidecar is unexpectedly large — rare,
                            // but defensive against future schema additions.
                            byte[] bigger = System.Buffers.ArrayPool<byte>.Shared.Rent(rented.Length * 2);
                            Buffer.BlockCopy(rented, 0, bigger, 0, total);
                            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                            rented = bigger;
                        }
                    }

                    var reader = new System.Text.Json.Utf8JsonReader(
                        new ReadOnlySpan<byte>(rented, 0, total),
                        isFinalBlock: true,
                        state: default);
                    return ExtractExtensionGame(ref reader);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        /// <summary>
        /// Walk a pre-buffered Utf8JsonReader looking for the top-level
        /// <c>extension</c> object's <c>game</c> string. Returns null if
        /// either property is missing or has the wrong shape.
        /// </summary>
        private static string ExtractExtensionGame(ref System.Text.Json.Utf8JsonReader reader)
        {
            if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.StartObject)
                return null;

            int depth = 1;
            while (reader.Read())
            {
                if (reader.TokenType == System.Text.Json.JsonTokenType.EndObject)
                {
                    depth--;
                    if (depth == 0) return null;
                    continue;
                }

                if (reader.TokenType == System.Text.Json.JsonTokenType.PropertyName && depth == 1)
                {
                    bool isExtension = reader.ValueTextEquals("extension");
                    if (!reader.Read()) return null;

                    if (isExtension && reader.TokenType == System.Text.Json.JsonTokenType.StartObject)
                    {
                        // Inside `extension` — look for `game` string.
                        int extDepth = 1;
                        while (reader.Read())
                        {
                            if (reader.TokenType == System.Text.Json.JsonTokenType.EndObject)
                            {
                                extDepth--;
                                if (extDepth == 0) return null;
                                continue;
                            }
                            if (reader.TokenType == System.Text.Json.JsonTokenType.StartObject ||
                                reader.TokenType == System.Text.Json.JsonTokenType.StartArray)
                            {
                                reader.TrySkip();
                                continue;
                            }
                            if (reader.TokenType == System.Text.Json.JsonTokenType.PropertyName && extDepth == 1)
                            {
                                bool isGame = reader.ValueTextEquals("game");
                                if (!reader.Read()) return null;
                                if (isGame && reader.TokenType == System.Text.Json.JsonTokenType.String)
                                    return reader.GetString();
                                if (reader.TokenType == System.Text.Json.JsonTokenType.StartObject ||
                                    reader.TokenType == System.Text.Json.JsonTokenType.StartArray)
                                    reader.TrySkip();
                            }
                        }
                        return null;
                    }
                    else if (reader.TokenType == System.Text.Json.JsonTokenType.StartObject ||
                             reader.TokenType == System.Text.Json.JsonTokenType.StartArray)
                    {
                        reader.TrySkip();
                    }
                    // Scalars on non-extension keys: already consumed by Read() above.
                }
            }
            return null;
        }

        // --- Hot cache matching ---

        /// <summary>
        /// Check the hot cache for a predicted match.
        /// isPartialMatch: true for prefix matches (typewriter effect) —
        /// caller should NOT call OnTextMatched for partial matches.
        /// </summary>
        public string TryHotCacheMatch(string normalizedInput, out string matchedKey, out bool isPartialMatch)
        {
            matchedKey = "";
            isPartialMatch = false;
            if (!EnsureLoadedCore())
                return null;
            if (_chainCache.Count == 0 && _npcCache.Count == 0)
                return null;

            lock (_lock)
            {
                // === TIER 1: Chain cache (highest priority — nextDialogs predictions) ===
                // Checked with exact + fuzzy + prefix matching

                // Exact match in chain cache
                if (_chainCache.TryGetValue(normalizedInput, out var chainEntry))
                {
                    matchedKey = chainEntry.OriginalEnText;
                    HotCacheHits++;
                    return chainEntry.Translation;
                }

                // Fuzzy match in chain cache (OCR errors: l↔1, O↔0)
                int fuzzyThreshold = normalizedInput.Length >= 20 ? 3 : 2;
                string bestMatch = null;
                int bestDist = fuzzyThreshold;
                HotCacheEntry bestEntry = default;

                foreach (var kvp in _chainCache)
                {
                    int shorter = Math.Min(normalizedInput.Length, kvp.Key.Length);
                    int longer = Math.Max(normalizedInput.Length, kvp.Key.Length);
                    if (longer - shorter > shorter / 5 + 1)
                        continue;

                    int dist = QuickEditDistance(normalizedInput, kvp.Key, bestDist);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestMatch = kvp.Key;
                        bestEntry = kvp.Value;
                        if (dist == 0) break;
                    }
                }

                if (bestMatch != null)
                {
                    matchedKey = bestEntry.OriginalEnText;
                    HotCacheHits++;
                    return bestEntry.Translation;
                }

                // Prefix match in chain cache (typewriter effect).
                //
                // Thresholds were previously 3 (single next) / 8 (multi). A 3-char prefix
                // is NOT actually unambiguous even with a single known next-node — many
                // dialogue openings share the same first few words ("I'm...", "I've...",
                // "It's...", "Let...", "The...") and committing to a prefix match at that
                // point during the typewriter animation produced confident-wrong translations.
                // Raised to 8 / 12 to require enough characters for real disambiguation.
                int chainMinPrefix = _immediateNextIds.Length == 1
                    ? ChainPrefixThresholdSingle
                    : ChainPrefixThresholdMulti;
                if (normalizedInput.Length >= chainMinPrefix)
                {
                    foreach (var kvp in _chainCache)
                    {
                        if (kvp.Key.Length > normalizedInput.Length &&
                            kvp.Key.StartsWith(normalizedInput, StringComparison.Ordinal))
                        {
                            matchedKey = kvp.Value.OriginalEnText;
                            isPartialMatch = true;
                            HotCacheHits++;
                            return kvp.Value.Translation;
                        }
                    }
                }

                // === TIER 2: NPC cache (fallback — all NPC dialogue lines) ===
                // Exact + unambiguous prefix only (NO fuzzy).

                if (_npcCache.TryGetValue(normalizedInput, out var npcEntry))
                {
                    matchedKey = npcEntry.OriginalEnText;
                    HotCacheHits++;
                    return npcEntry.Translation;
                }

                // Prefix match in NPC cache — only if EXACTLY ONE entry matches.
                // Multiple matches = ambiguous prefix → wait for more text.
                if (normalizedInput.Length >= 10)
                {
                    HotCacheEntry? singleMatch = null;
                    string singleMatchKey = null;
                    int matchCount = 0;

                    foreach (var kvp in _npcCache)
                    {
                        if (kvp.Key.Length > normalizedInput.Length &&
                            kvp.Key.StartsWith(normalizedInput, StringComparison.Ordinal))
                        {
                            matchCount++;
                            if (matchCount == 1)
                            {
                                singleMatch = kvp.Value;
                                singleMatchKey = kvp.Value.OriginalEnText;
                            }
                            else
                            {
                                break; // ambiguous, stop
                            }
                        }
                    }

                    if (matchCount == 1 && singleMatch.HasValue)
                    {
                        matchedKey = singleMatchKey;
                        isPartialMatch = true;
                        HotCacheHits++;
                        return singleMatch.Value.Translation;
                    }
                }

                // === TIER 3: NPC-scoped distance match ===
                //
                // Two length regimes, one loop:
                //
                //   (a) OCR text ≈ cached length (within ±10 chars) — full
                //       Levenshtein. Handles OCR glyph errors on complete
                //       lines (HSR's normal shape, Genshin's end-of-
                //       typewriter state).
                //
                //   (b) OCR text << cached length — prefix-aligned
                //       Levenshtein: compare OCR against cached[0..ocrLen]
                //       instead of the full cached line. Genshin's
                //       typewriter emits correct partial prefixes; without
                //       this branch the old full-string Levenshtein saw the
                //       69-char length gap and short-circuited via
                //       QuickEditDistance's `Math.Abs(sLen - tLen) >=
                //       maxDist` early-return. Tier 3 was effectively dead
                //       for every mid-typewriter tick on Genshin.
                //
                // Prefix-aligned uses a tighter distance threshold (OCR
                // noise dominates — no "partial typewriter" slack in an
                // equal-length comparison) and reports isPartialMatch=true
                // so the caller's OnTextMatched skips chain advancement
                // until a full-length match confirms the line.
                if (normalizedInput.Length >= 8 && _npcCache.Count > 0)
                {
                    string npcBestKey = null;
                    HotCacheEntry npcBestEntry = default;
                    bool npcBestIsPrefix = false;
                    int fullThreshold   = Math.Max(3, normalizedInput.Length / 5);
                    int prefixThreshold = Math.Max(2, normalizedInput.Length / 8);
                    int npcBestDist = Math.Max(fullThreshold, prefixThreshold) + 1;

                    foreach (var kvp in _npcCache)
                    {
                        int dist;
                        bool isPrefixMode;
                        if (kvp.Key.Length > normalizedInput.Length + 10)
                        {
                            // Prefix-aligned: trim cached to OCR length.
                            string cachedPrefix = kvp.Key.Substring(0, normalizedInput.Length);
                            dist = QuickEditDistance(normalizedInput, cachedPrefix, prefixThreshold + 1);
                            if (dist > prefixThreshold) continue;
                            isPrefixMode = true;
                        }
                        else
                        {
                            dist = QuickEditDistance(normalizedInput, kvp.Key, fullThreshold + 1);
                            if (dist > fullThreshold) continue;
                            isPrefixMode = false;
                        }

                        if (dist < npcBestDist)
                        {
                            npcBestDist = dist;
                            npcBestKey = kvp.Key;
                            npcBestEntry = kvp.Value;
                            npcBestIsPrefix = isPrefixMode;
                            if (dist == 0) break;
                        }
                    }

                    if (npcBestKey != null)
                    {
                        matchedKey = npcBestEntry.OriginalEnText;
                        // Prefix-mode hits are "we think this is what the
                        // typewriter is typing" — don't advance chain state
                        // until a subsequent full-length confirms. Same
                        // semantic as Tier 2's prefix-match path above.
                        isPartialMatch = npcBestIsPrefix;
                        HotCacheHits++;
                        return npcBestEntry.Translation;
                    }
                }
            }

            HotCacheMisses++;
            return null;
        }

        // --- Node resolution + state updates ---

        /// <summary>
        /// Resolve a dialogue node id from a raw English line. Uses the reverse
        /// text-to-dialog index built at load time, then runs the same NPC-aware
        /// disambiguation <see cref="OnTextMatched"/> uses when multiple nodes share
        /// the same content hash.
        /// </summary>
        public long FindNodeByText(string enText, string npcFilter = null)
        {
            if (string.IsNullOrEmpty(enText) || !EnsureLoadedCore() || _textToDialogIds == null)
                return 0;

            string normalized = OptimizedMatcher.NormalizeInput(enText, true);
            if (string.IsNullOrEmpty(normalized))
                return 0;

            lock (_lock)
            {
                if (!_textToDialogIds.TryGetValue(normalized, out var ids) || ids == null || ids.Count == 0)
                    return 0;

                return DisambiguateNode(ids, npcFilter);
            }
        }

        /// <summary>
        /// Explicitly set the active dialogue node and populate the hot cache
        /// from its immediate and deep-lookahead successors.
        /// </summary>
        public void SetCurrentDialog(long dialogId, Dictionary<string, string> translationDict)
        {
            if (dialogId == 0 || !EnsureLoadedCore() || _dialogGraph == null)
                return;

            lock (_lock)
            {
                if (!_dialogGraph.TryGetValue(dialogId, out var node))
                    return;

                _activeDialogId = dialogId;
                // Route through GetNextNodeIds so subclasses that override
                // edge resolution (voice-linked chains, externally-stored
                // next-refs) see the same edges OnTextMatched would. Keeping
                // both assignment sites consistent is the whole point of
                // the strategy slot — never read NextDialogIds directly.
                _immediateNextIds = GetNextNodeIds(dialogId) ?? Array.Empty<long>();

                PopulateHotCache(dialogId, translationDict, depth: 5);
            }
        }

        /// <summary>
        /// Pre-load the hot cache with ALL dialogue lines for a detected NPC.
        /// Call this as soon as NPC name is detected via HSV color — BEFORE
        /// any text matching. This ensures the hot cache is ready when the
        /// first dialogue line arrives.
        /// </summary>
        public void PreloadForNpc(string detectedNpcName, Dictionary<string, string> translationDict)
        {
            if (string.IsNullOrEmpty(detectedNpcName) || !EnsureLoadedCore() ||
                _npcNameToRoleIds == null || _npcToDialogIds == null)
                return;

            // Extract just the NPC name (before role/title text) via strategy.
            string normName = ExtractNpcFirstName(detectedNpcName);
            if (string.IsNullOrEmpty(normName))
                return;

            lock (_lock)
            {
                // Skip if this NPC's entries are already loaded.
                if (_loadedNpcs.Contains(normName))
                    return;

                _loadedNpcs.Add(normName);

                // Only reset chain state if we're NOT in an active conversation.
                if (_chainCache.Count == 0)
                {
                    _activeDialogId = 0;
                    _immediateNextIds = Array.Empty<long>();
                }

                if (!_npcNameToRoleIds.TryGetValue(normName, out var roleIds))
                    return;

                // Load ALL dialogue lines for this NPC into the NPC cache.
                // The two-tier system keeps this safe: NPC cache uses exact + prefix
                // only (no fuzzy), so wrong matches are avoided.
                int loaded = 0;
                foreach (string roleId in roleIds)
                {
                    if (!_npcToDialogIds.TryGetValue(roleId, out var dialogIds))
                        continue;
                    foreach (long dialogId in dialogIds)
                    {
                        if (!_dialogGraph.TryGetValue(dialogId, out var node)) continue;
                        loaded += TryAddToCache(_npcCache, dialogId, node, translationDict);
                    }
                }

                if (loaded > 0)
                    Logger.Log.Debug($"Pre-loaded {loaded} first-lines for NPC \"{normName}\"");
            }
        }

        /// <summary>
        /// Insert a body-match result into <see cref="_npcCache"/> keyed by the
        /// normalized OCR form. Used when a successful matcher lookup can't be
        /// tied back to a dialogue-graph node (hash drift, missing TextMapEN
        /// entry, OCR misread that body-match corrected to a legitimate line
        /// not in the bundled graph). Callers hold <see cref="_lock"/>.
        /// </summary>
        private void LearnOrphanBodyMatch(string normalized, string enText,
            Dictionary<string, string> translationDict)
        {
            if (string.IsNullOrEmpty(normalized) || string.IsNullOrEmpty(enText))
                return;
            if (translationDict == null || _npcCache.ContainsKey(normalized))
                return;
            if (!translationDict.TryGetValue(enText, out var pl) || string.IsNullOrEmpty(pl))
                return;

            // Cap guard — see MAX_NPC_CACHE comment. Flush when we hit the
            // ceiling rather than growing without bound.
            if (_npcCache.Count >= MAX_NPC_CACHE)
            {
                try { Common.Logger.Log.Info($"[HotCache] _npcCache reached {MAX_NPC_CACHE} entries — flushing to prevent unbounded growth."); }
                catch { /* swallow */ }
                _npcCache.Clear();
            }

            _npcCache[normalized] = new HotCacheEntry
            {
                Translation = pl,
                DialogId = 0, // no graph node backs this entry
                OriginalEnText = enText,
            };
        }

        private int TryAddToCache(Dictionary<string, HotCacheEntry> cache, long dialogId,
            DialogNode node, Dictionary<string, string> translationDict)
        {
            // Post phase-3: read the pre-resolved EnText off the node instead
            // of round-tripping through the full _textMapEN dictionary
            // (~200k hash→string lookups avoided over the app lifetime).
            string enText = node.EnText;
            if (string.IsNullOrEmpty(enText)) return 0;

            string normalized = OptimizedMatcher.NormalizeInput(enText, true);
            if (string.IsNullOrEmpty(normalized) || cache.ContainsKey(normalized)) return 0;

            string translation = null;
            if (translationDict != null && translationDict.TryGetValue(enText, out var pl))
                translation = pl;

            if (string.IsNullOrEmpty(translation)) return 0;

            // Cap guard for _npcCache (chain cache has its own guard in
            // PopulateHotCache). When an NPC preload would push us over
            // MAX_NPC_CACHE, stop adding — we prefer partial cache coverage
            // over unbounded growth. Chain cache stays precise; NPC cache
            // is a broader fallback and partial coverage is acceptable.
            if (ReferenceEquals(cache, _npcCache) && cache.Count >= MAX_NPC_CACHE)
            {
                return 0;
            }

            cache[normalized] = new HotCacheEntry
            {
                Translation = translation,
                DialogId = dialogId,
                OriginalEnText = enText,
            };
            return 1;
        }

        /// <summary>
        /// Called after a successful match to update dialogue context
        /// and predict next lines for the hot cache.
        /// </summary>
        public void OnTextMatched(string matchedEnText, string detectedNpcName,
            Dictionary<string, string> translationDict)
        {
            if (!EnsureLoadedCore() || _textToDialogIds == null)
                return;

            lock (_lock)
            {
                _contextHistory.Add(matchedEnText);
                if (_contextHistory.Count > MAX_CONTEXT_HISTORY)
                    _contextHistory.RemoveAt(0);

                string normalized = OptimizedMatcher.NormalizeInput(matchedEnText, true);

                // Remove the just-matched entry from chain cache (already used).
                _chainCache.Remove(normalized);

                if (!_textToDialogIds.TryGetValue(normalized, out var candidateIds))
                {
                    // Two reasons we land here:
                    //   (a) OCR misread — text not in dialogue graph. DON'T reset chain
                    //       state; existing predictions survive, next correctly-read
                    //       line re-syncs via disambiguation.
                    //   (b) Cross-version hash drift — user's TextMapEN snapshot doesn't
                    //       contain the contentHash the shipped DialogGraph bundle
                    //       references for this line, so BuildTextToDialogIndex skipped
                    //       it (no EN text → no index entry). Body match still succeeded
                    //       via the pre-merged {EN:PL} translationDict, which is keyed
                    //       by English text directly and doesn't care about hashes.
                    //
                    // For (b), opportunistically learn the line into _npcCache so the
                    // next OCR tick on the same text hits the hot cache. Keyed by
                    // normalized form, same as graph-sourced entries — Tier 2 of
                    // TryHotCacheMatch picks it up identically. Synthetic DialogId=0
                    // is harmless (nothing dereferences it; _dialogGraph[0] returns
                    // false and the entry is never used as a chain-prediction anchor).
                    LearnOrphanBodyMatch(normalized, matchedEnText, translationDict);
                    return;
                }

                long bestDialogId = DisambiguateNode(candidateIds, detectedNpcName);
                if (bestDialogId == 0)
                    return;

                _activeDialogId = bestDialogId;

                // Store immediate next IDs for instant prediction — honours
                // the next-node strategy so subclasses can reshape the graph
                // without re-implementing the state machine.
                _immediateNextIds = GetNextNodeIds(bestDialogId) ?? Array.Empty<long>();

                // Deep lookahead: predict 5 steps ahead. Accumulate — don't
                // clear existing predictions. Branching dialogues (player
                // choices) land all branches in the cache.
                PopulateHotCache(bestDialogId, translationDict, depth: 5);
            }
        }

        public void Reset()
        {
            // Reset touches only the runtime caches (always allocated),
            // never the deferred graph structures. Safe to call in the
            // unloaded state — deliberately does NOT trigger lazy init.
            int evictedEntries;
            lock (_lock)
            {
                evictedEntries = _chainCache.Count + _npcCache.Count;
                _activeDialogId = 0;
                _immediateNextIds = Array.Empty<long>();
                _chainCache.Clear();
                _npcCache.Clear();
                _loadedNpcs.Clear();
                _contextHistory.Clear();
            }

            // Dialogue end is a NATURAL boundary where we've just freed the
            // hot-cache entries (potentially thousands of HotCacheEntry strings
            // + dict-slot overhead). Ask Windows to trim the working set so
            // committed-but-idle pages return to standby — otherwise Task
            // Manager shows RSS "stuck" at peak even though the managed heap
            // shrunk. ~1 ms P/Invoke; Windows soft-faults pages back on next
            // access if they're needed again.
            //
            // The RamDiag.StartBackgroundMonitor watchdog relies on
            // GC.RegisterForFullGCNotification which historically required
            // Server GC + Concurrent=false to fire — we run Workstation GC
            // with Concurrent=true, so that path may never activate. This
            // Reset-boundary trim is the belt-and-braces that guarantees at
            // least one trim per dialogue cycle regardless of GC notification
            // semantics.
            //
            // NOT calling AggressiveReclaim — that does Gen2 + LOH compact +
            // trim and blocks 300-800 ms. Reset fires on the WPF dispatcher
            // thread (subtitle fade-out Completed handler); a 300+ ms stall
            // there is user-visible (session-35 lesson, see memory
            // feedback_no_aggressive_reclaim_on_ui_path.md).
            if (evictedEntries >= 200)
            {
                try { Core.Runtime.RamDiag.TrimWorkingSet(); }
                catch { /* swallow — trim is best-effort */ }
            }
        }

        // --- Predictions ---

        /// <summary>
        /// Get the immediate next dialogue prediction from the chain.
        /// Returns the translation for the single next dialog in the graph.
        /// </summary>
        public PredictionResult? GetSingleChainPrediction()
        {
            // Cheap guard before touching the lock: _immediateNextIds is
            // only ever non-empty after OnTextMatched has run, which
            // already triggered EnsureLoadedCore. Skip the lazy-init round
            // trip when the graph is clearly idle.
            if (_immediateNextIds.Length != 1) return null;
            if (!_isFullyLoaded) return null;

            lock (_lock)
            {
                if (_immediateNextIds.Length != 1) return null;
                long nextId = _immediateNextIds[0];

                if (_dialogGraph == null || !_dialogGraph.TryGetValue(nextId, out var nextNode)) return null;

                // Inline EnText — see TryAddToCache comment on the phase-3 drop.
                string enText = nextNode.EnText;
                if (string.IsNullOrEmpty(enText)) return null;

                // Look up in chain cache first, then try translation dict.
                string normalized = OptimizedMatcher.NormalizeInput(enText, true);
                if (!string.IsNullOrEmpty(normalized) && _chainCache.TryGetValue(normalized, out var entry))
                {
                    return new PredictionResult
                    {
                        Translation = entry.Translation,
                        EnText = entry.OriginalEnText,
                    };
                }

                return null;
            }
        }

        /// <summary>
        /// Get predicted player answer choices from graph NextDialogIds.
        /// Returns only immediate next nodes — used for instant display.
        /// </summary>
        public List<PredictionResult> GetPredictedAnswers(Dictionary<string, string> translationDict)
        {
            var results = new List<PredictionResult>();
            // Same reasoning as GetSingleChainPrediction: the answer path
            // only has work to do once chain state has been advanced, and
            // chain advancement (OnTextMatched) already paid the lazy-init
            // cost. No need to force materialisation here.
            //
            // Post phase-3: the _textMapEN == null guard was removed — the
            // full map is deliberately dropped after load and nodes carry
            // their own resolved EnText. A non-null _dialogGraph is enough.
            if (!_isFullyLoaded || _dialogGraph == null)
                return results;

            lock (_lock)
            {
                foreach (long nextId in _immediateNextIds)
                {
                    if (!_dialogGraph.TryGetValue(nextId, out var nextNode))
                        continue;

                    string enText = nextNode.EnText;
                    if (string.IsNullOrEmpty(enText))
                        continue;

                    string translation = null;
                    if (translationDict != null && translationDict.TryGetValue(enText, out var pl))
                        translation = pl;

                    results.Add(new PredictionResult { EnText = enText, Translation = translation });
                }
            }

            return results;
        }

        /// <summary>
        /// Get all NPC cache entries as prediction candidates for fuzzy answer matching.
        /// Used when graph NextDialogIds don't cover answer choices (shop NPCs, idle talk).
        /// </summary>
        public List<PredictionResult> GetNpcCachePredictions()
        {
            var results = new List<PredictionResult>();
            // Cache contents are populated by the hot-path calls that
            // already went through EnsureLoadedCore. If nothing's in the
            // caches, skipping lazy init saves us a wasted traversal.
            if (!_isFullyLoaded) return results;

            lock (_lock)
            {
                foreach (var entry in _npcCache.Values)
                {
                    if (!string.IsNullOrEmpty(entry.OriginalEnText) &&
                        !string.IsNullOrEmpty(entry.Translation))
                    {
                        results.Add(new PredictionResult
                        {
                            EnText = entry.OriginalEnText,
                            Translation = entry.Translation,
                        });
                    }
                }
                foreach (var entry in _chainCache.Values)
                {
                    if (!string.IsNullOrEmpty(entry.OriginalEnText) &&
                        !string.IsNullOrEmpty(entry.Translation))
                    {
                        results.Add(new PredictionResult
                        {
                            EnText = entry.OriginalEnText,
                            Translation = entry.Translation,
                        });
                    }
                }
            }

            return results;
        }

        public string GetStats()
        {
            int total = HotCacheHits + HotCacheMisses;
            double hitRate = total > 0 ? (double)HotCacheHits / total * 100 : 0;
            return $"{HotCacheHits}/{total} hits ({hitRate:F0}%), chain:{_chainCache.Count} npc:{_npcCache.Count}";
        }

        // --- Quest banner ---

        /// <summary>
        /// Get quest information for the currently active dialogue.
        /// Returns (questTitle, questType) or null if no quest is associated.
        /// </summary>
        public (string title, string type)? GetCurrentQuestInfo()
        {
            // _activeDialogId is only ever set by OnTextMatched /
            // SetCurrentDialog — both of those already paid the lazy-init
            // cost. If it's still 0 we have no work to do anyway.
            if (_activeDialogId == 0 || !_isFullyLoaded ||
                _dialogToTalkId == null || _talkIndex == null)
                return null;

            lock (_lock)
            {
                if (!_dialogToTalkId.TryGetValue(_activeDialogId, out long talkId))
                    return null;

                if (!_talkIndex.TryGetValue(talkId, out var talk) || talk.QuestId == 0)
                    return null;

                // Delegate banner formatting to the strategy so per-game
                // quest-type-code mappings (AQ / WQ / IQ / …) can be swapped.
                return ResolveQuestBanner(talk.QuestId);
            }
        }

        /// <summary>
        /// Get translated quest title using the translation dictionary.
        /// </summary>
        public string GetTranslatedQuestTitle(Dictionary<string, string> translationDict)
        {
            var questInfo = GetCurrentQuestInfo();
            if (questInfo == null || translationDict == null) return null;

            string enTitle = questInfo.Value.title;
            if (translationDict.TryGetValue(enTitle, out var translated))
                return translated;

            return enTitle; // Fallback to English title.
        }

        // --- Private file loaders ---

        /// <summary>
        /// Read a signed 64-bit identifier (dialog id, talk id, quest id).
        /// These stay within Int64 range — only TextMap hashes go wider.
        /// </summary>
        private static long ReadLong(JsonTextReader jr)
        {
            if (jr.Value is long l) return l;
            if (jr.Value is int i) return i;
            if (jr.Value is string s && long.TryParse(s, out var parsed))
                return parsed;
            return Convert.ToInt64(jr.Value);
        }

        /// <summary>
        /// Read an unsigned 64-bit TextMap hash. HSR uses xxhash64 and
        /// ~half of produced values exceed Int64.MaxValue — our Node
        /// builder emits them as JSON strings (quoted) to preserve the
        /// full 64-bit range through JSON.parse in the build tool and
        /// Newtonsoft here.
        /// </summary>
        private static ulong ReadUInt64(JsonTextReader jr)
        {
            if (jr.Value is ulong ul) return ul;
            if (jr.Value is long l) return unchecked((ulong)l);
            if (jr.Value is int i) return unchecked((ulong)(long)i);
            if (jr.Value is string s && ulong.TryParse(s, out var parsed))
                return parsed;
            return Convert.ToUInt64(jr.Value);
        }

        private void LoadDialogGraph(string path,
            Dictionary<string, string> fullTextMap,
            FileProtectionHelper protectionHelper = null)
        {
            _dialogGraph = new Dictionary<long, DialogNode>();
            // String-table for RoleType and RoleId dedupe. The dialog graph
            // has ~200k nodes spanning only ~5-10 distinct RoleType values
            // ("NPC"/"PLAYER"/"BLACK_SCREEN"/…) and ~1-5k distinct RoleId
            // values (one per NPC). Newtonsoft emits a fresh string for each
            // scalar it reads, so without deduping we'd allocate ~200k
            // RoleType strings and ~200k RoleId strings — ~5-8 MB of
            // redundant heap that a tiny interning map eliminates. Keyed by
            // StringComparer.Ordinal for predictable behaviour with game
            // ids that have no cultural folding concerns.
            var roleTypeIntern = new Dictionary<string, string>(StringComparer.Ordinal);
            var roleIdIntern = new Dictionary<string, string>(StringComparer.Ordinal);

            using (var stream = protectionHelper != null
                ? protectionHelper.OpenForReading(path)
                : (Stream)File.OpenRead(path))
            using (var reader = new StreamReader(stream))
            using (var jsonReader = new JsonTextReader(reader))
            {
                jsonReader.Read(); // StartObject
                while (jsonReader.Read())
                {
                    if (jsonReader.TokenType == JsonToken.PropertyName)
                    {
                        string idStr = (string)jsonReader.Value;
                        if (!long.TryParse(idStr, out long dialogId))
                        {
                            jsonReader.Skip();
                            continue;
                        }

                        jsonReader.Read(); // StartObject
                        var node = new DialogNode();
                        var nextList = new List<long>();

                        while (jsonReader.Read() && jsonReader.TokenType != JsonToken.EndObject)
                        {
                            if (jsonReader.TokenType != JsonToken.PropertyName) continue;
                            string prop = (string)jsonReader.Value;
                            jsonReader.Read();

                            switch (prop)
                            {
                                case "h":
                                    node.ContentHash = ReadUInt64(jsonReader);
                                    break;
                                case "nh":
                                    node.NameHash = ReadUInt64(jsonReader);
                                    break;
                                case "n":
                                    if (jsonReader.TokenType == JsonToken.StartArray)
                                    {
                                        while (jsonReader.Read() && jsonReader.TokenType != JsonToken.EndArray)
                                        {
                                            if (jsonReader.Value != null)
                                                nextList.Add(ReadLong(jsonReader));
                                        }
                                    }
                                    break;
                                case "rt":
                                    {
                                        string rt = (string)jsonReader.Value;
                                        if (rt != null)
                                        {
                                            if (!roleTypeIntern.TryGetValue(rt, out var canonical))
                                            {
                                                canonical = rt;
                                                roleTypeIntern[rt] = canonical;
                                            }
                                            node.RoleType = canonical;
                                        }
                                    }
                                    break;
                                case "ri":
                                    {
                                        string ri = jsonReader.Value?.ToString();
                                        if (ri != null)
                                        {
                                            if (!roleIdIntern.TryGetValue(ri, out var canonical))
                                            {
                                                canonical = ri;
                                                roleIdIntern[ri] = canonical;
                                            }
                                            node.RoleId = canonical;
                                        }
                                    }
                                    break;
                                default:
                                    jsonReader.Skip();
                                    break;
                            }
                        }

                        node.NextDialogIds = nextList.Count > 0 ? nextList.ToArray() : Array.Empty<long>();

                        // Inline the English body text at load time so we can
                        // drop the full TextMapEN dictionary at the end of
                        // LoadCore. A missing hash → EnText stays null, which
                        // is indistinguishable at the call sites from the
                        // pre-refactor "_textMapEN.TryGetValue returned false"
                        // branch (every caller already guards with
                        // string.IsNullOrEmpty).
                        if (node.ContentHash != 0 && fullTextMap != null &&
                            fullTextMap.TryGetValue(node.ContentHash.ToString(), out var enText))
                        {
                            node.EnText = enText;
                        }

                        _dialogGraph[dialogId] = node;
                    }
                }
            }
            Logger.Log.Info($"Loaded {_dialogGraph.Count:N0} dialog nodes");
        }

        private void LoadNpcNames(string path,
            Dictionary<string, string> fullTextMap,
            FileProtectionHelper protectionHelper = null)
        {
            _npcNames = new Dictionary<string, string>();
            using (var stream = protectionHelper != null
                ? protectionHelper.OpenForReading(path)
                : (Stream)File.OpenRead(path))
            using (var reader = new StreamReader(stream))
            using (var jsonReader = new JsonTextReader(reader))
            {
                jsonReader.Read(); // StartObject
                while (jsonReader.Read())
                {
                    if (jsonReader.TokenType == JsonToken.PropertyName)
                    {
                        string npcId = (string)jsonReader.Value;
                        jsonReader.Read();
                        ulong nameHash = ReadUInt64(jsonReader);
                        // Name resolution against the full TextMap local —
                        // happens once at load time; only the resolved name
                        // string survives in _npcNames afterwards.
                        string name = fullTextMap != null && fullTextMap.TryGetValue(nameHash.ToString(), out var n)
                            ? n : "";
                        if (!string.IsNullOrEmpty(name))
                            _npcNames[npcId] = name;
                    }
                }
            }
        }

        private void LoadTalkIndex(string path, FileProtectionHelper protectionHelper = null)
        {
            _talkIndex = new Dictionary<long, TalkNode>();
            using (var stream = protectionHelper != null
                ? protectionHelper.OpenForReading(path)
                : (Stream)File.OpenRead(path))
            using (var reader = new StreamReader(stream))
            using (var jsonReader = new JsonTextReader(reader))
            {
                jsonReader.Read(); // StartObject
                while (jsonReader.Read())
                {
                    if (jsonReader.TokenType == JsonToken.PropertyName)
                    {
                        string idStr = (string)jsonReader.Value;
                        if (!long.TryParse(idStr, out long talkId))
                        {
                            jsonReader.Skip();
                            continue;
                        }

                        jsonReader.Read(); // StartObject
                        var node = new TalkNode();
                        var nextList = new List<long>();

                        while (jsonReader.Read() && jsonReader.TokenType != JsonToken.EndObject)
                        {
                            if (jsonReader.TokenType != JsonToken.PropertyName) continue;
                            string prop = (string)jsonReader.Value;
                            jsonReader.Read();

                            switch (prop)
                            {
                                case "init":
                                    node.InitDialog = ReadLong(jsonReader);
                                    break;
                                case "quest":
                                    node.QuestId = ReadLong(jsonReader);
                                    break;
                                case "next":
                                    if (jsonReader.TokenType == JsonToken.StartArray)
                                    {
                                        while (jsonReader.Read() && jsonReader.TokenType != JsonToken.EndArray)
                                        {
                                            if (jsonReader.Value != null)
                                                nextList.Add(ReadLong(jsonReader));
                                        }
                                    }
                                    break;
                                default:
                                    jsonReader.Skip();
                                    break;
                            }
                        }

                        node.NextTalks = nextList.Count > 0 ? nextList.ToArray() : Array.Empty<long>();
                        _talkIndex[talkId] = node;
                    }
                }
            }
        }

        private void LoadQuestInfo(string path,
            Dictionary<string, string> fullTextMap,
            FileProtectionHelper protectionHelper = null)
        {
            _questInfo = new Dictionary<long, QuestInfoEntry>();
            using (var stream = protectionHelper != null
                ? protectionHelper.OpenForReading(path)
                : (Stream)File.OpenRead(path))
            using (var reader = new StreamReader(stream))
            using (var jsonReader = new JsonTextReader(reader))
            {
                jsonReader.Read(); // StartObject
                while (jsonReader.Read())
                {
                    if (jsonReader.TokenType == JsonToken.PropertyName)
                    {
                        string idStr = (string)jsonReader.Value;
                        if (!long.TryParse(idStr, out long questId))
                        {
                            jsonReader.Skip();
                            continue;
                        }

                        jsonReader.Read(); // StartObject
                        ulong titleHash = 0;
                        string questType = "";

                        while (jsonReader.Read() && jsonReader.TokenType != JsonToken.EndObject)
                        {
                            if (jsonReader.TokenType != JsonToken.PropertyName) continue;
                            string prop = (string)jsonReader.Value;
                            jsonReader.Read();

                            switch (prop)
                            {
                                case "title":
                                    titleHash = ReadUInt64(jsonReader);
                                    break;
                                case "type":
                                    questType = (string)jsonReader.Value ?? "";
                                    break;
                                default:
                                    jsonReader.Skip();
                                    break;
                            }
                        }

                        if (titleHash != 0)
                        {
                            // Inline the quest title at load time — same motive
                            // as DialogNode.EnText: lets LoadCore drop the full
                            // TextMapEN dictionary once every resolvable hash
                            // has been copied onto the owning record.
                            string title = fullTextMap != null &&
                                fullTextMap.TryGetValue(titleHash.ToString(), out var t)
                                ? t : null;
                            _questInfo[questId] = new QuestInfoEntry
                            {
                                TitleHash = titleHash,
                                QuestType = questType,
                                QuestTitle = title,
                            };
                        }
                    }
                }
            }
            Logger.Log.Info($"Loaded {_questInfo.Count:N0} quest info entries");
        }

        private void BuildNpcNameReverseIndex()
        {
            _npcNameToRoleIds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (_npcNames == null) return;

            foreach (var kv in _npcNames)
            {
                string roleId = kv.Key;
                string fullName = kv.Value;
                if (string.IsNullOrEmpty(fullName)) continue;

                // Index by first name via strategy (most reliable for matching
                // OCR-detected names — OCR often loses the role/title suffix).
                string normFirst = ExtractNpcFirstName(fullName);
                if (string.IsNullOrEmpty(normFirst)) continue;

                if (!_npcNameToRoleIds.TryGetValue(normFirst, out var list))
                {
                    list = new List<string>();
                    _npcNameToRoleIds[normFirst] = list;
                }
                if (!list.Contains(roleId))
                    list.Add(roleId);
            }
            Logger.Log.Info($"Built NPC name reverse index: {_npcNameToRoleIds.Count} unique names");
        }

        private void BuildDialogToTalkIndex()
        {
            _dialogToTalkId = new Dictionary<long, long>();
            if (_talkIndex == null || _dialogGraph == null) return;

            foreach (var talkKv in _talkIndex)
            {
                long talkId = talkKv.Key;
                long current = talkKv.Value.InitDialog;
                var visited = new HashSet<long>();

                while (current != 0 && visited.Add(current))
                {
                    _dialogToTalkId[current] = talkId;
                    if (_dialogGraph.TryGetValue(current, out var node) && node.NextDialogIds.Length > 0)
                        current = node.NextDialogIds[0];
                    else
                        break;
                }
            }
        }

        private void BuildTextToDialogIndex()
        {
            _textToDialogIds = new Dictionary<string, List<long>>(
                _dialogGraph?.Count ?? 0, StringComparer.OrdinalIgnoreCase);
            _npcToDialogIds = new Dictionary<string, List<long>>();

            if (_dialogGraph == null)
                return;

            foreach (var kv in _dialogGraph)
            {
                // Read the inline EnText populated by LoadDialogGraph. Same
                // semantics as the pre-phase-3 _textMapEN lookup: missing
                // resolution (cross-version drift / stripped line) → skip.
                string enText = kv.Value.EnText;
                if (string.IsNullOrEmpty(enText)) continue;

                string normalized = OptimizedMatcher.NormalizeInput(enText, true);
                if (string.IsNullOrEmpty(normalized)) continue;

                if (!_textToDialogIds.TryGetValue(normalized, out var list))
                {
                    list = new List<long>(1);
                    _textToDialogIds[normalized] = list;
                }
                list.Add(kv.Key);

                // NPC-wide index for idle talk prediction.
                string npcId = kv.Value.RoleId;
                if (!string.IsNullOrEmpty(npcId))
                {
                    if (!_npcToDialogIds.TryGetValue(npcId, out var npcList))
                    {
                        npcList = new List<long>();
                        _npcToDialogIds[npcId] = npcList;
                    }
                    npcList.Add(kv.Key);
                }
            }
        }

        // --- Hot cache population ---

        private void PopulateHotCache(long dialogId, Dictionary<string, string> translationDict, int depth)
        {
            if (depth <= 0 || !_dialogGraph.TryGetValue(dialogId, out var node))
                return;

            long[] nextIds = GetNextNodeIds(dialogId) ?? Array.Empty<long>();
            foreach (long nextId in nextIds)
            {
                if (_chainCache.Count >= MAX_CHAIN_CACHE)
                    return;

                if (!_dialogGraph.TryGetValue(nextId, out var nextNode))
                    continue;

                // Inline EnText — see TryAddToCache comment on the phase-3 drop.
                string enText = nextNode.EnText;
                if (string.IsNullOrEmpty(enText))
                    continue;

                string normalized = OptimizedMatcher.NormalizeInput(enText, true);
                if (string.IsNullOrEmpty(normalized) || _chainCache.ContainsKey(normalized))
                    continue;

                string translation = null;
                if (translationDict != null && translationDict.TryGetValue(enText, out var pl))
                    translation = pl;

                if (!string.IsNullOrEmpty(translation))
                {
                    _chainCache[normalized] = new HotCacheEntry
                    {
                        Translation = translation,
                        DialogId = nextId,
                        OriginalEnText = enText,
                    };
                }

                PopulateHotCache(nextId, translationDict, depth - 1);
            }

            // Talk transitions: end of chain → load next talk's initial dialog.
            if (nextIds.Length == 0 && _dialogToTalkId != null && _talkIndex != null)
            {
                if (_dialogToTalkId.TryGetValue(dialogId, out long talkId) &&
                    _talkIndex.TryGetValue(talkId, out var talk))
                {
                    foreach (long nextTalkId in talk.NextTalks)
                    {
                        if (_talkIndex.TryGetValue(nextTalkId, out var nextTalk))
                        {
                            PopulateHotCache(nextTalk.InitDialog, translationDict, 1);
                        }
                    }
                }
            }
        }

        // --- Edit distance utility ---

        private static int QuickEditDistance(string s, string t, int maxDist)
        {
            int sLen = s.Length;
            int tLen = t.Length;
            if (Math.Abs(sLen - tLen) >= maxDist) return maxDist;
            if (sLen == 0) return tLen;
            if (tLen == 0) return sLen;

            if (sLen > tLen)
            {
                var tmp = s; s = t; t = tmp;
                var tmpLen = sLen; sLen = tLen; tLen = tmpLen;
            }

            var prev = new int[sLen + 1];
            var curr = new int[sLen + 1];

            for (int i = 0; i <= sLen; i++) prev[i] = i;

            for (int j = 1; j <= tLen; j++)
            {
                curr[0] = j;
                int minInRow = j;
                for (int i = 1; i <= sLen; i++)
                {
                    int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    int d1 = curr[i - 1] + 1;
                    int d2 = prev[i] + 1;
                    int d3 = prev[i - 1] + cost;
                    int d = d1 < d2 ? d1 : d2;
                    if (d3 < d) d = d3;
                    curr[i] = d;
                    if (d < minInRow) minInRow = d;
                }
                if (minInRow >= maxDist) return maxDist;
                var swap = prev; prev = curr; curr = swap;
            }

            return prev[sLen];
        }
    }
}
