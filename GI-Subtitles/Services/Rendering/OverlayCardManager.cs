using System.Collections.Generic;
using System.Linq;
using GI_Subtitles.Common;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>
    /// Manages all overlay cards, evaluating which should be visible each frame
    /// and collecting their display text in priority order.
    /// Cards are pluggable — add new card types by registering them here.
    /// </summary>
    public class OverlayCardManager
    {
        private readonly List<IOverlayCard> _cards = new List<IOverlayCard>();

        public OverlayCardManager()
        {
            // Register built-in cards in priority order
            _cards.Add(new QuestBannerCard());
            _cards.Add(new NpcInfoCard());
        }

        /// <summary>
        /// Register a custom card. Cards are evaluated in priority order (lower = first).
        /// </summary>
        public void RegisterCard(IOverlayCard card)
        {
            _cards.Add(card);
            _cards.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        /// <summary>
        /// Get all cards that should be visible given the current context.
        /// Returns cards sorted by priority (lowest first = top of screen).
        /// </summary>
        public List<CardDisplayInfo> GetVisibleCards(OverlayContext context)
        {
            var visible = new List<CardDisplayInfo>();

            foreach (var card in _cards)
            {
                if (card.ShouldShow(context))
                {
                    var (header, content) = card.GetDisplayText(context);
                    if (!string.IsNullOrEmpty(header) || !string.IsNullOrEmpty(content))
                    {
                        visible.Add(new CardDisplayInfo
                        {
                            CardId = card.CardId,
                            Priority = card.Priority,
                            Header = header,
                            Content = content
                        });
                    }
                }
            }

            return visible;
        }

        /// <summary>
        /// Notify all cards of context change.
        /// </summary>
        public void OnContextChanged(OverlayContext context)
        {
            foreach (var card in _cards)
                card.OnContextChanged(context);
        }

        /// <summary>
        /// Reset all cards (e.g., when OCR stops).
        /// </summary>
        public void ResetAll()
        {
            foreach (var card in _cards)
                card.Reset();
        }

        /// <summary>
        /// Get a specific card by ID for settings toggle.
        /// </summary>
        public IOverlayCard GetCard(string cardId)
        {
            return _cards.FirstOrDefault(c => c.CardId == cardId);
        }

        public IReadOnlyList<IOverlayCard> AllCards => _cards.AsReadOnly();
    }

    /// <summary>
    /// Display data for a visible card — ready for rendering.
    /// </summary>
    public struct CardDisplayInfo
    {
        public string CardId;
        public int Priority;
        public string Header;
        public string Content;
    }
}
