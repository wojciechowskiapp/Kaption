using System;
using System.Windows;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>
    /// Context data passed to overlay cards each frame.
    /// Contains everything a card needs to decide whether to show and what to display.
    /// </summary>
    public class OverlayContext
    {
        public string OcrText { get; set; }
        public string TranslatedContent { get; set; }
        public string MatchedEnglishKey { get; set; }
        public string DetectedNpcName { get; set; }
        public string HeaderText { get; set; }
        public bool HasTranslation { get; set; }
        public bool IsPartialMatch { get; set; }

        // Quest context (filled by DialogueContextEngine)
        public string QuestTitle { get; set; }
        public string QuestType { get; set; }

        // Match quality (filled by matcher)
        public MatchSource MatchSource { get; set; }
    }

    /// <summary>
    /// Where the translation match came from — used for confidence display.
    /// </summary>
    public enum MatchSource
    {
        None,
        HotCachePrediction,
        HotCacheNpc,
        SymSpellExact,
        SymSpellFuzzy,
        NGramMatch,
        FuzzyLevenshtein,
        CacheHit
    }

    /// <summary>
    /// A pluggable overlay card that can display contextual information
    /// above or below the main subtitle. Cards appear/disappear based on game context.
    /// </summary>
    public interface IOverlayCard
    {
        /// <summary>
        /// Unique identifier for this card type.
        /// </summary>
        string CardId { get; }

        /// <summary>
        /// Display priority (lower = higher on screen).
        /// Quest banner = 10, NPC info = 20, subtitle = 50, transcript = 90.
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// Whether this card is currently enabled by user settings.
        /// </summary>
        bool IsEnabled { get; set; }

        /// <summary>
        /// Evaluate whether this card should be visible given current context.
        /// Called every UI tick (~200ms). Must be fast.
        /// </summary>
        bool ShouldShow(OverlayContext context);

        /// <summary>
        /// Get the display text for the card. Called only when ShouldShow returns true.
        /// Returns (headerLine, contentLine) where either can be null.
        /// </summary>
        (string header, string content) GetDisplayText(OverlayContext context);

        /// <summary>
        /// Called when the card's context changes (e.g., new quest detected).
        /// Cards can cache state between calls.
        /// </summary>
        void OnContextChanged(OverlayContext context);

        /// <summary>
        /// Reset card state (e.g., when OCR stops or game changes).
        /// </summary>
        void Reset();
    }
}
