using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Views.Shell;

namespace GI_Subtitles.Services.Guess
{
    /// <summary>Why a ranking notice may not show right now.</summary>
    public enum GuessGateResult
    {
        Show,
        Disabled,
        ShownToday,
        Translating,
        GameInFront,
        WindowsBusy,
    }

    /// <summary>
    /// When a ranking notice may show (R4 section 2.2). Checked at delivery, not at poll time. The decision is pure; the
    /// two Windows questions (what is in front, may an app notify now) sit in the thin shims below it.
    /// </summary>
    public static class GuessNoticeGate
    {
        /// <summary>SHQueryUserNotificationState: the one state in which Windows accepts a notification.</summary>
        public const int QunsAcceptsNotifications = 5;

        /// <summary>The value the shim reports when Windows would not answer: treated as busy.</summary>
        public const int QunsUnknown = 0;

        public static GuessGateResult Check(bool enabled, string lastShownDay, string today, bool translating, bool gameInFront, int quns)
        {
            if (!enabled) return GuessGateResult.Disabled;
            if (!string.IsNullOrEmpty(lastShownDay) && string.Equals(lastShownDay, today, StringComparison.Ordinal)) return GuessGateResult.ShownToday;
            if (translating) return GuessGateResult.Translating;
            if (gameInFront) return GuessGateResult.GameInFront;
            // Busy (2), a full-screen Direct3D app (3), presentation mode (4), quiet time (6), an app's own full screen (7),
            // and a failed call: all wait. Windows would otherwise file the toast away where its click may not work.
            if (quns != QunsAcceptsNotifications) return GuessGateResult.WindowsBusy;
            return GuessGateResult.Show;
        }

        public static bool CanShow(bool enabled, string lastShownDay, string today, bool translating, bool gameInFront, int quns) =>
            Check(enabled, lastShownDay, today, translating, gameInFront, quns) == GuessGateResult.Show;

        /// <summary>True when the image name (no ".exe") is one of the listed games' executables.</summary>
        public static bool IsGameImage(string imageName, IEnumerable<IReadOnlyCollection<string>> processNames) =>
            !string.IsNullOrEmpty(imageName) && processNames != null && processNames.Any(names => names != null && GameProbe.Matches(imageName, names));

        // ── Windows shims ────────────────────────────────────────────────

        /// <summary>Any listed game's process owns the foreground window. A generic Unreal executable counts too: the cost is a wait.</summary>
        public static bool AnyGameInFront()
        {
            try
            {
                string image = GameProbe.ForegroundImageName();
                return IsGameImage(image, GameRegistry.Current.Profiles.Select(p => (IReadOnlyCollection<string>)(p.ProcessNames ?? Array.Empty<string>())));
            }
            catch
            {
                return true; // could not tell: wait rather than risk showing over a game
            }
        }

        /// <summary>The QUERY_USER_NOTIFICATION_STATE value, or <see cref="QunsUnknown"/> when the call fails.</summary>
        public static int QueryNotificationState()
        {
            try
            {
                return SHQueryUserNotificationState(out int state) == 0 ? state : QunsUnknown;
            }
            catch
            {
                return QunsUnknown;
            }
        }

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);
    }
}
