using System;
using System.Collections.Generic;
using System.Linq;
using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>What the Start page's spotlight card knows at launch: plain values, so the rules test without a window.</summary>
    public sealed class SpotlightInputs
    {
        public bool SetupCompleted { get; init; }
        /// <summary>The setup window ran in this process: the player just saw everything there is.</summary>
        public bool OnboardingRanThisLaunch { get; init; }
        /// <summary>The 2.5 card is up; one once-card at a time, and that one wins.</summary>
        public bool Intro25Visible { get; init; }
        /// <summary>The live listing (/api/app/supported-versions) has a wuwa/pl pack.</summary>
        public bool WuWaPolishListed { get; init; }
        /// <summary>PackAvailability.IsComing("WuWa", "pl"): the server's own list still says "on the way".</summary>
        public bool WuWaComing { get; init; }
        public string WuWaGameVersion { get; init; }
        public bool WuWaSelected { get; init; }
        public bool SeenWuWa { get; init; }
        public bool SeenGuess { get; init; }
        /// <summary>Launches on which each row was already on screen.</summary>
        public int ShowsWuWa { get; init; }
        public int ShowsGuess { get; init; }
        /// <summary>The live listing has an endfield/pl pack (the server lists only packs every player can get).</summary>
        public bool EndfieldPolishListed { get; init; }
        /// <summary>PackAvailability.IsComing("Endfield", "pl"): the server's own list still says "on the way".</summary>
        public bool EndfieldComing { get; init; }
        public string EndfieldGameVersion { get; init; }
        /// <summary>The player has Endfield selected: they know the game, so the row never shows.</summary>
        public bool EndfieldSelected { get; init; }
        public bool SeenEndfield { get; init; }
        public int ShowsEndfield { get; init; }
    }

    public enum SpotlightWuWaAction { None, Switch, OpenGamePage }

    public sealed record SpotlightRows(bool WuWa, SpotlightWuWaAction WuWaAction, string WuWaVersion, bool Guess)
    {
        /// <summary>The Endfield row (always a "switch to the game" action); init-only so the older four-value form still reads.</summary>
        public bool Endfield { get; init; }
        public string EndfieldVersion { get; init; }
        public bool Any => WuWa || Endfield || Guess;
        public static readonly SpotlightRows None = new(false, SpotlightWuWaAction.None, null, false);
    }

    /// <summary>
    /// The "Nowość w Kaption" card on Start (R4 section 3.2): up to three rows, each retired on its own by its action,
    /// its close button, or after <see cref="MaxShows"/> launches on screen. The WuWa and Endfield rows claim Polish
    /// subtitles only while the live listing backs it.
    /// </summary>
    public static class SpotlightRules
    {
        public const int MaxShows = 3;

        public const string SeenWuWaKey = "Ui.Seen.WuWaPolish";
        public const string SeenGuessKey = "Ui.Seen.GuessGame";
        public const string ShowsWuWaKey = "Ui.Spotlight.Shows.WuWaPolish";
        public const string ShowsGuessKey = "Ui.Spotlight.Shows.GuessGame";
        public const string SeenEndfieldKey = "Ui.Seen.EndfieldPolish";
        public const string ShowsEndfieldKey = "Ui.Spotlight.Shows.EndfieldPolish";

        public static SpotlightRows Rows(SpotlightInputs i)
        {
            if (i == null || !i.SetupCompleted || i.OnboardingRanThisLaunch || i.Intro25Visible) return SpotlightRows.None;
            bool wuwa = i.WuWaPolishListed && !i.WuWaComing && !i.SeenWuWa && i.ShowsWuWa < MaxShows;
            bool guess = !i.SeenGuess && i.ShowsGuess < MaxShows;
            var action = !wuwa ? SpotlightWuWaAction.None : i.WuWaSelected ? SpotlightWuWaAction.OpenGamePage : SpotlightWuWaAction.Switch;
            // Never to a player who has the game selected, and a launch that finds it selected retires the row for good.
            bool endfield = i.EndfieldPolishListed && !i.EndfieldComing && !i.EndfieldSelected && !i.SeenEndfield && i.ShowsEndfield < MaxShows;
            return new SpotlightRows(wuwa, action, wuwa ? i.WuWaGameVersion : null, guess)
            {
                Endfield = endfield,
                EndfieldVersion = endfield ? i.EndfieldGameVersion : null,
            };
        }

        /// <summary>The listing has a Polish pack for this game (wire id); its game version ("3.7"), or null.</summary>
        public static bool HasPolishPack(IEnumerable<LatestPack> packs, string wireId, out string gameVersion)
        {
            var pack = packs?.FirstOrDefault(p => string.Equals(p.Game, wireId, StringComparison.OrdinalIgnoreCase)
                                                && string.Equals(p.Language, "pl", StringComparison.OrdinalIgnoreCase));
            gameVersion = pack == null ? null : ShortVersion(pack.GameVersion);
            return pack != null;
        }

        public static bool HasWuWaPolish(IEnumerable<LatestPack> packs, out string gameVersion) => HasPolishPack(packs, "wuwa", out gameVersion);

        public static bool HasEndfieldPolish(IEnumerable<LatestPack> packs, out string gameVersion) => HasPolishPack(packs, "endfield", out gameVersion);

        /// <summary>"3.7.0" reads "3.7"; anything else as given.</summary>
        public static string ShortVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            string[] parts = v.Trim().Split('.');
            return parts.Length == 3 && parts[2] == "0" ? parts[0] + "." + parts[1] : v.Trim();
        }

        // ── Config ───────────────────────────────────────────────────────

        public static SpotlightInputs FromConfig(SpotlightInputs live) => new()
        {
            SetupCompleted = live.SetupCompleted,
            OnboardingRanThisLaunch = live.OnboardingRanThisLaunch,
            Intro25Visible = live.Intro25Visible,
            WuWaPolishListed = live.WuWaPolishListed,
            WuWaComing = live.WuWaComing,
            WuWaGameVersion = live.WuWaGameVersion,
            WuWaSelected = live.WuWaSelected,
            SeenWuWa = Config.Get(SeenWuWaKey, false),
            SeenGuess = Config.Get(SeenGuessKey, false),
            ShowsWuWa = Config.Get(ShowsWuWaKey, 0),
            ShowsGuess = Config.Get(ShowsGuessKey, 0),
            EndfieldPolishListed = live.EndfieldPolishListed,
            EndfieldComing = live.EndfieldComing,
            EndfieldGameVersion = live.EndfieldGameVersion,
            EndfieldSelected = live.EndfieldSelected,
            SeenEndfield = Config.Get(SeenEndfieldKey, false),
            ShowsEndfield = Config.Get(ShowsEndfieldKey, 0),
        };

        public static void MarkSeen(bool wuwa, bool guess, bool endfield = false)
        {
            if (wuwa) Config.Set(SeenWuWaKey, true);
            if (guess) Config.Set(SeenGuessKey, true);
            if (endfield) Config.Set(SeenEndfieldKey, true);
        }

        /// <summary>One more launch on screen for each row shown (the caller counts once per process).</summary>
        public static void CountShown(SpotlightRows rows)
        {
            if (rows.WuWa) Config.Set(ShowsWuWaKey, Config.Get(ShowsWuWaKey, 0) + 1);
            if (rows.Guess) Config.Set(ShowsGuessKey, Config.Get(ShowsGuessKey, 0) + 1);
            if (rows.Endfield) Config.Set(ShowsEndfieldKey, Config.Get(ShowsEndfieldKey, 0) + 1);
        }

        /// <summary>Top margin of each row: 14 under another visible row, 0 for the first one on the card.</summary>
        public static (double Endfield, double Guess) RowGaps(bool wuwa, bool endfield, bool guess) =>
            (wuwa && endfield ? 14 : 0, (wuwa || endfield) && guess ? 14 : 0);
    }
}
