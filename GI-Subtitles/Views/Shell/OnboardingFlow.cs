using System;
using System.Collections.Generic;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Validation;

namespace GI_Subtitles.Views.Shell
{
    public enum OnboardingStep { Welcome = 0, Game = 1, SignIn = 2, Prepare = 3, Done = 4 }

    /// <summary>FirstRun replaces the EULA → sign-in → key → wizard chain; Rerun is "Uruchom kreator" from Help.</summary>
    public enum OnboardingMode { FirstRun, Rerun }

    /// <summary>
    /// The setup window's steps and their rules, without UI: Witaj → Gra → Zaloguj → Przygotowuję → Gotowe. The
    /// user sees the product read their own screen (Gra) before the sign-in wall; "Gotowe" opens as soon as the pack
    /// is on disk, the index builds afterwards.
    /// </summary>
    public sealed class OnboardingFlow
    {
        public static readonly IReadOnlyList<OnboardingStep> Steps =
            new[] { OnboardingStep.Welcome, OnboardingStep.Game, OnboardingStep.SignIn, OnboardingStep.Prepare, OnboardingStep.Done };

        public OnboardingFlow(OnboardingMode mode) { Mode = mode; }

        public OnboardingMode Mode { get; }
        public OnboardingStep Step { get; private set; } = OnboardingStep.Welcome;
        public bool EulaAccepted { get; set; }
        public string Game { get; set; }
        public bool RegionSet { get; set; }
        public bool SignedIn { get; set; }
        public bool KeyReady { get; set; }
        /// <summary>The pack sync finished (downloaded, already current, or not available for this plan).</summary>
        public bool PackSettled { get; set; }
        /// <summary>The key fetch failed for good (sign-in expired, device unknown): only Quit or Retry remain.</summary>
        public bool KeyFailed { get; set; }

        public int Index => (int)Step;
        public bool IsFirst => Step == OnboardingStep.Welcome;
        public bool IsLast => Step == OnboardingStep.Done;

        public bool CanAdvance => Step switch
        {
            OnboardingStep.Welcome => EulaAccepted,
            OnboardingStep.Game => !string.IsNullOrEmpty(Game),
            OnboardingStep.SignIn => SignedIn,
            OnboardingStep.Prepare => KeyReady && PackSettled,
            _ => true,
        };

        /// <summary>Back is offered on Gra and Zaloguj; preparing and done only move forward.</summary>
        public bool CanGoBack => Step is OnboardingStep.Game or OnboardingStep.SignIn;

        public bool Advance()
        {
            if (!CanAdvance || IsLast) return false;
            Step++;
            return true;
        }

        public bool Back()
        {
            if (!CanGoBack) return false;
            Step--;
            return true;
        }

        /// <summary>Jump straight to a step that is already reachable (a click on a finished step in the rail).</summary>
        public bool GoTo(OnboardingStep target)
        {
            if (target >= Step || Step > OnboardingStep.SignIn) return false;
            Step = target;
            return true;
        }

        /// <summary>
        /// What closing the window means for a first run, as the old chain's exit codes: 2 legal terms not accepted,
        /// 1 not signed in, 3 no file key; 0 = setup is far enough to start the app.
        /// </summary>
        public int ExitCodeIfClosed
        {
            get
            {
                if (Mode == OnboardingMode.Rerun) return 0;
                if (!EulaAccepted) return 2;
                if (!SignedIn) return 1;
                if (!KeyReady) return 3;
                return 0;
            }
        }

        /// <summary>
        /// Finding the game in step 2 detects the dialogue area only when none is saved; a saved one (drawn by hand, or
        /// set on an earlier run) is replaced only when the user clicks "Wykryj automatycznie".
        /// </summary>
        public static bool ShouldAutoDetectRegion(bool savedRegionValid, bool userAsked) => userAsked || !savedRegionValid;

        /// <summary>A first run is due when no setup was ever completed or dismissed (2.3 and 2.4 users have one).</summary>
        public static bool IsFirstRunNeeded(bool setupCompleted, bool setupDismissed) => !setupCompleted && !setupDismissed;

        public static bool IsFirstRunNeeded() =>
            IsFirstRunNeeded(Config.Get("SetupCompleted", false), Config.Get("SetupDismissed", false));
    }

    /// <summary>Drawing the dialogue area by hand, before or after the main window exists.</summary>
    public static class RegionPicker
    {
        /// <summary>"x,y,w,h" in physical pixels clamped to the desktop, or null when cancelled.</summary>
        public static string Pick(Func<System.Windows.Rect, bool> overlapPredicate = null)
        {
            var rect = Screenshot.Screenshot.GetRegion(overlapPredicate);
            if (rect.IsEmpty || double.IsInfinity(rect.Width) || double.IsNaN(rect.Width) || rect.Width < 1 || rect.Height < 1) return null;
            var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
            if (!CaptureRegionBounds.TryClamp((int)Math.Round(rect.X), (int)Math.Round(rect.Y), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height),
                    desktop.Left, desktop.Top, desktop.Width, desktop.Height, out int x, out int y, out int w, out int h))
                return null;
            return $"{x},{y},{w},{h}";
        }
    }
}
