using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>
    /// Optional overlay card that shows detected NPC name when HSV color detection finds one.
    /// Appears as a subtle line: "Speaking: Katheryne"
    /// Helps players remember who they're talking to, especially after long breaks.
    /// </summary>
    public class NpcInfoCard : IOverlayCard
    {
        public string CardId => "NpcInfo";
        public int Priority => 20; // Below quest banner, above subtitle
        public bool IsEnabled { get; set; }

        private string _lastNpcName;
        private int _showFrames;

        public NpcInfoCard()
        {
            IsEnabled = Config.Get("ShowNpcInfo", false); // Off by default (user said "not that useful")
        }

        public bool ShouldShow(OverlayContext context)
        {
            if (!IsEnabled) return false;

            if (!string.IsNullOrEmpty(context.DetectedNpcName))
            {
                if (context.DetectedNpcName != _lastNpcName)
                {
                    _lastNpcName = context.DetectedNpcName;
                    _showFrames = 25; // Show for ~5 seconds on NPC change
                }
                return _showFrames > 0;
            }

            if (_showFrames > 0)
            {
                _showFrames--;
                return true;
            }

            return false;
        }

        public (string header, string content) GetDisplayText(OverlayContext context)
        {
            return (_lastNpcName, null);
        }

        public void OnContextChanged(OverlayContext context)
        {
            if (!string.IsNullOrEmpty(context.DetectedNpcName) &&
                context.DetectedNpcName != _lastNpcName)
            {
                _showFrames = 25;
            }
        }

        public void Reset()
        {
            _lastNpcName = null;
            _showFrames = 0;
        }
    }
}
