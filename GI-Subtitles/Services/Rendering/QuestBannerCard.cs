using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>
    /// Overlay card that shows the current quest name when dialogue is part of a quest chain.
    /// Displayed as a small banner above the subtitle: "Quest: The Outlander Who Caught the Wind"
    /// Data comes from DialogueContextEngine's quest resolution (dialogId → talkId → questId → title).
    /// </summary>
    public class QuestBannerCard : IOverlayCard
    {
        public string CardId => "QuestBanner";
        public int Priority => 10; // Highest priority — appears above everything
        public bool IsEnabled { get; set; }

        private string _lastQuestTitle;
        private string _lastQuestType;
        private int _showFrames; // Keep showing for a few frames after quest context disappears

        public QuestBannerCard()
        {
            IsEnabled = Config.Get("ShowQuestBanner", true);
        }

        public bool ShouldShow(OverlayContext context)
        {
            if (!IsEnabled) return false;

            if (!string.IsNullOrEmpty(context.QuestTitle))
            {
                _lastQuestTitle = context.QuestTitle;
                _lastQuestType = context.QuestType;
                _showFrames = 15; // Keep visible for ~3 seconds after last quest dialogue (15 × 200ms)
                return true;
            }

            // Keep showing briefly after quest context disappears
            if (_showFrames > 0)
            {
                _showFrames--;
                return true;
            }

            return false;
        }

        public (string header, string content) GetDisplayText(OverlayContext context)
        {
            string type = FormatQuestType(_lastQuestType);
            string prefix = string.IsNullOrEmpty(type) ? "Quest" : type;
            return ($"{prefix}: {_lastQuestTitle}", null);
        }

        public void OnContextChanged(OverlayContext context) { }

        public void Reset()
        {
            _lastQuestTitle = null;
            _lastQuestType = null;
            _showFrames = 0;
        }

        private static string FormatQuestType(string type)
        {
            if (string.IsNullOrEmpty(type)) return "";
            switch (type.ToUpperInvariant())
            {
                case "AQ": return "Archon Quest";
                case "EQ": return "Event Quest";
                case "WQ": return "World Quest";
                case "LQ": return "Legend Quest";
                case "IQ": return "Commission";
                default: return "Quest";
            }
        }
    }
}
