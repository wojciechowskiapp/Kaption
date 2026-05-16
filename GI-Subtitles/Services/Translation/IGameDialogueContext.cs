using System;
using System.Collections.Generic;
using GI_Subtitles.Services.Security;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// Result of a dialogue-graph prediction. Returned from
    /// <see cref="IGameDialogueContext.GetSingleChainPrediction"/>,
    /// <see cref="IGameDialogueContext.GetPredictedAnswers"/>, and
    /// <see cref="IGameDialogueContext.GetNpcCachePredictions"/>.
    ///
    /// Moved out of the engine in the 2026-04-17 refactor that split
    /// DialogueContextEngine into an interface + strategy-driven base,
    /// so strategies and call sites alike can hold this shape without
    /// depending on a specific concrete engine class.
    /// </summary>
    public struct PredictionResult
    {
        /// <summary>Target-language translation. May be null when the
        /// translation dict has no entry for <see cref="EnText"/>.</summary>
        public string Translation;

        /// <summary>English source line (as stored in TextMapEN).</summary>
        public string EnText;
    }

    /// <summary>
    /// Game-agnostic public contract for the dialogue prediction engine.
    /// Consumers (MainWindow, SettingsWindow, AnswerTranslationService)
    /// talk to this interface — not to a concrete engine class — so that
    /// per-game implementations (Genshin, HSR, future titles) can be
    /// swapped in via <see cref="GameDialogueContextFactory"/>.
    /// </summary>
    public interface IGameDialogueContext
    {
        /// <summary>True once <see cref="Load"/> has successfully populated
        /// the dialogue graph and auxiliary indexes. Volatile: read safely
        /// from any thread.</summary>
        bool IsLoaded { get; }

        /// <summary>Total hot-cache hits since construction. Diagnostic only;
        /// surfaced on the Dashboard via <see cref="GetStats"/>. Increments
        /// are racy under contention — last-write-wins may drop counts.
        /// Safe for hit-rate ratios, unsafe as a precise accounting signal.</summary>
        int HotCacheHits { get; }

        /// <summary>Total hot-cache misses since construction. Racy under
        /// contention — see <see cref="HotCacheHits"/>. Do NOT drive behavior
        /// decisions from this value; diagnostic only.</summary>
        int HotCacheMisses { get; }

        /// <summary>True when the graph shows exactly one immediate next line
        /// from the active node — enables the chain-prediction UI path.</summary>
        bool HasSingleChainPrediction { get; }

        /// <summary>
        /// Load preprocessed dialogue graph files from <paramref name="dataDir"/>.
        /// Downloads / rebuilds via <see cref="DialogGraphDownloader"/> when the
        /// graph is missing (legacy fallback). Accepts encrypted .gisub or plain
        /// .json transparently via <paramref name="protectionHelper"/>.
        /// </summary>
        void Load(string dataDir, string textMapEnPath,
            IProgress<(int percent, string message)> progress = null,
            FileProtectionHelper protectionHelper = null);

        /// <summary>Fast-path hot cache lookup. Returns the translation for a
        /// known next-line prediction, or null when the input doesn't resolve.
        /// <paramref name="isPartialMatch"/> is true for typewriter-prefix hits
        /// — callers must NOT advance chain state on partials.</summary>
        string TryHotCacheMatch(string normalizedInput, out string matchedKey, out bool isPartialMatch);

        /// <summary>Resolve a dialogue node id from raw English text. Diagnostic
        /// / test-support path; runtime code goes through the regular pipeline.</summary>
        long FindNodeByText(string enText, string npcFilter = null);

        /// <summary>Explicitly set the active node and populate predictions from
        /// its successors. Used by tests and integrations with custom node
        /// resolution.</summary>
        void SetCurrentDialog(long dialogId, Dictionary<string, string> translationDict);

        /// <summary>Preload the NPC-wide hot cache tier as soon as the NPC name
        /// is detected via HSV color — well before OCR finishes the body line.</summary>
        void PreloadForNpc(string detectedNpcName, Dictionary<string, string> translationDict);

        /// <summary>Advance chain state after a confirmed full-line match.
        /// Updates <see cref="HasSingleChainPrediction"/> and the chain hot cache.</summary>
        void OnTextMatched(string matchedEnText, string detectedNpcName,
            Dictionary<string, string> translationDict);

        /// <summary>Clear all runtime chain/NPC state at conversation end.</summary>
        void Reset();

        /// <summary>Immediate next-line prediction when the graph has a single
        /// forward edge. Null when the next-count is not 1.</summary>
        PredictionResult? GetSingleChainPrediction();

        /// <summary>All immediate next-line predictions — used for player answer
        /// translation via <see cref="AnswerTranslationService"/>.</summary>
        List<PredictionResult> GetPredictedAnswers(Dictionary<string, string> translationDict);

        /// <summary>All current NPC-cache + chain-cache entries. Fallback source
        /// for answer matching when graph predictions don't cover the options.</summary>
        List<PredictionResult> GetNpcCachePredictions();

        /// <summary>(title, type) for the quest that owns the active dialog line,
        /// or null when no quest is associated.</summary>
        (string title, string type)? GetCurrentQuestInfo();

        /// <summary>Same as <see cref="GetCurrentQuestInfo"/> but the title is
        /// translated via <paramref name="translationDict"/> when available.</summary>
        string GetTranslatedQuestTitle(Dictionary<string, string> translationDict);

        /// <summary>Single-line diagnostic: hit rate + chain/NPC cache sizes.</summary>
        string GetStats();
    }
}
