using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Services.Capture
{
    /// <summary>What Windows said when the overlay asked to stay out of screen capture.</summary>
    public enum WindowsHideAnswer
    {
        /// <summary>Not asked: the overlay window is not up yet, or the player wants the subtitles in recordings.</summary>
        NotAsked,
        /// <summary>Windows keeps the overlay out of screen capture.</summary>
        Hidden,
        /// <summary>Windows refused, so the overlay is in the desktop picture whatever the setting says.</summary>
        Refused,
        /// <summary>The player wants the overlay in recordings, but Windows did not undo an earlier hiding.</summary>
        StillHidden,
    }

    /// <summary>What a recording or a stream of the screen shows, for the line under the setting.</summary>
    public enum CaptureStatus
    {
        /// <summary>The overlay window is not up yet: nothing is known, say nothing.</summary>
        Unknown,
        /// <summary>The setting is on: screen capture shows the subtitles.</summary>
        Shown,
        /// <summary>The setting is off and Windows hides the overlay.</summary>
        Hidden,
        /// <summary>The setting is off, but Windows refused to hide the overlay on this PC.</summary>
        ShownWindowsRefused,
        /// <summary>The setting is on, but Windows did not undo the hiding: recordings still miss the subtitles until a restart.</summary>
        HiddenWindowsRefused,
    }

    /// <summary>
    /// "Show subtitles in recordings and streams": whether the subtitle overlay asks Windows to keep it out of screen
    /// capture, and when the app has to black the overlay out of the frames it reads itself so it never reads its own
    /// Polish line. Pure decisions; the Win32 call and the mask live in MainWindow.
    /// </summary>
    public static class CaptureVisibility
    {
        public const string ShowInRecordingsKey = "ShowInRecordings";

        /// <summary>
        /// What an install without the key gets. Off keeps what each PC does today; whether it ships on is the owner's
        /// call (.plan/in-progress/RECORDING-MODE.md has the replay numbers and what is still untested on Windows 11).
        /// </summary>
        public const bool DefaultShowInRecordings = false;

        /// <summary>
        /// How long the app keeps masking after Windows agreed to hide the overlay: the hiding starts with the next
        /// composed frame, and a frame read in between still shows the overlay. A few frames at any refresh rate.
        /// </summary>
        public const int HideSettleMs = 500;

        /// <summary>The saved choice; an absent key means <see cref="DefaultShowInRecordings"/> (nothing is written for it).</summary>
        public static bool ShowInRecordings =>
            Resolve(Config.Has(ShowInRecordingsKey) ? Config.Get(ShowInRecordingsKey, DefaultShowInRecordings) : null);

        public static bool Resolve(bool? saved) => saved ?? DefaultShowInRecordings;

        /// <summary>The overlay asks Windows to hide it only while the player does not want it in recordings.</summary>
        public static bool AsksWindowsToHide(bool showInRecordings) => !showInRecordings;

        /// <summary>
        /// Whether the overlay can be in a frame the app reads, and so must be blacked out of it: always when recordings
        /// show it, when Windows refused to hide it, and on a capture backend that does not honour the hiding.
        /// </summary>
        public static bool MasksOverlay(bool showInRecordings, WindowsHideAnswer answer, bool backendHonoursHiding) =>
            showInRecordings || answer != WindowsHideAnswer.Hidden || !backendHonoursHiding;

        /// <summary>
        /// What is known after the overlay asked to be in screen capture again. Windows refusing matters only when the
        /// overlay was hidden before: then it still is. A window that was never hidden is in the picture either way.
        /// </summary>
        public static WindowsHideAnswer AfterAskingToShow(bool windowsAccepted, WindowsHideAnswer before) =>
            !windowsAccepted && before is WindowsHideAnswer.Hidden or WindowsHideAnswer.StillHidden
                ? WindowsHideAnswer.StillHidden
                : WindowsHideAnswer.NotAsked;

        public static CaptureStatus Status(bool showInRecordings, WindowsHideAnswer answer)
        {
            if (showInRecordings)
                return answer == WindowsHideAnswer.StillHidden ? CaptureStatus.HiddenWindowsRefused : CaptureStatus.Shown;
            return answer switch
            {
                WindowsHideAnswer.Hidden => CaptureStatus.Hidden,
                WindowsHideAnswer.Refused => CaptureStatus.ShownWindowsRefused,
                _ => CaptureStatus.Unknown,
            };
        }

        /// <summary>The string key of the sentence for a status; null when there is nothing honest to say.</summary>
        public static string StatusKey(CaptureStatus status) => status switch
        {
            CaptureStatus.Shown => "look_Recording_Status_Shown",
            CaptureStatus.Hidden => "look_Recording_Status_Hidden",
            CaptureStatus.ShownWindowsRefused => "look_Recording_Status_Refused",
            CaptureStatus.HiddenWindowsRefused => "look_Recording_Status_StillHidden",
            _ => null,
        };

        /// <summary>The English sentence, used when the dictionary has no entry.</summary>
        public static string StatusFallback(CaptureStatus status) => status switch
        {
            CaptureStatus.Shown => "Visible to screen capture. OBS game capture shows only the game: use display capture.",
            CaptureStatus.Hidden => "Hidden from recordings, streams and screenshots on this PC.",
            CaptureStatus.ShownWindowsRefused => "Windows can't hide the subtitles on this PC, so screen capture still shows them.",
            CaptureStatus.HiddenWindowsRefused => "Windows didn't let the subtitles back into screen capture. Restart Kaption to show them.",
            _ => null,
        };
    }
}
