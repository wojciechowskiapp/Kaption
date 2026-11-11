using System;
using System.Globalization;
using GI_Subtitles.Services.Automation;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>What the Start dashboard's state card says; one state at a time, in priority order.</summary>
    public enum StartCardKind
    {
        Stopped,
        Translating,
        WaitingForGame,
        Downloading,
        BuildingIndex,
        StartingReader,
        ReaderFailed,
        /// <summary>ONNX Runtime itself failed to load; .NET caches that for the process, so only a restart helps.</summary>
        ReaderUnavailable,
        Offline,
        DeviceLimit,
        PackMissing,
        /// <summary>A beta game whose first pack the server does not list yet: nothing to download, it is on the way.</summary>
        PackComing,
        /// <summary>Auto-start is on, but a Start gate other than the game itself is closed: say which, offer the fix.</summary>
        AutoStartBlocked,
        /// <summary>Another game was picked and "Later" chosen: the loaded data is still the old game's until the restart.</summary>
        GameSwitchPending,
        /// <summary>Translating, but every frame is black: the game looks like exclusive full screen.</summary>
        SessionFullScreen,
        /// <summary>Translating and reading lines, none of which the pack knows: the game's language, or the area.</summary>
        SessionNoMatch,
        /// <summary>Translating on a new install with nothing dialogue-like read yet.</summary>
        SessionNoDialogue,
        /// <summary>The first line this install has ever shown: said once.</summary>
        FirstLineWorks,
    }

    /// <summary>The card's single action (None hides the button).</summary>
    public enum StartCardAction { None, Start, Stop, StartAnyway, Retry, RetryReader, RestartApp, OpenAccount, OpenPacks, ResolveGate, OpenBorderlessGuide, OpenLanguageGuide, CheckArea }

    public enum ReaderState { Loading, Ready, Failed, Unavailable }

    /// <summary>Everything the card depends on, gathered from the window and services on each refresh.</summary>
    public sealed class StartCardInputs
    {
        public bool Translating { get; init; }
        public int LinesThisSession { get; init; }
        public bool Downloading { get; init; }
        public double? DownloadFraction { get; init; }
        public long? DownloadBytesTotal { get; init; }
        public bool IndexBuilding { get; init; }
        public int IndexSeconds { get; init; }
        public ReaderState Reader { get; init; }
        /// <summary>Windows error code behind <see cref="ReaderState.Unavailable"/>, e.g. 0x8007045A; shown in the card.</summary>
        public string ReaderErrorCode { get; init; }
        public bool ReaderOnCpu { get; init; }
        public bool Offline { get; init; }
        public bool PackOnDisk { get; init; }
        public bool PackMissing { get; init; }
        /// <summary>With <see cref="PackMissing"/>: the game is beta and the server lists no pack for it yet.</summary>
        public bool PackComing { get; init; }
        public bool DeviceLimit { get; init; }
        /// <summary>Auto-start is armed and the game is not on screen yet.</summary>
        public bool WaitingForGame { get; init; }
        /// <summary>The selected game's name, for "Uruchom Genshin Impact, a Kaption wystartuje sam".</summary>
        public string GameName { get; init; }
        /// <summary>Auto-start is on and this gate (not the game) would stop it; None when nothing does.</summary>
        public StartGate AutoStartBlockedBy { get; init; }
        /// <summary>Config["Game"] names another game than the one this process loaded.</summary>
        public bool GameSwitchPending { get; init; }
        /// <summary>With <see cref="GameSwitchPending"/>: the display name of the game whose data is still loaded.</summary>
        public string LoadedGameName { get; init; }
        /// <summary>This session's automatic dialogue area: still the saved one (Set), or it could not be found (Failed).</summary>
        public RegionAutoFillNote RegionAutoFill { get; init; }
        /// <summary>Why no line shows while translating (SessionCheckModel); None when lines show or nothing is known.</summary>
        public SessionCheckKind SessionCheck { get; init; }
        /// <summary>This run of translation showed the install's first line ever.</summary>
        public bool FirstLineShown { get; init; }
    }

    public sealed class StartCardState
    {
        public StartCardKind Kind { get; init; }
        public StartCardAction Action { get; init; }
        /// <summary>Offline while a pack is on disk: translation still works, the card says so in a note.</summary>
        public bool ShowOfflineNote { get; init; }
        public bool ShowCpuNote { get; init; }
        public bool ShowProgress { get; init; }
        public double Progress { get; init; }
        /// <summary>The closed gate behind <see cref="StartCardKind.AutoStartBlocked"/>.</summary>
        public StartGate Gate { get; init; }
        /// <summary>Kaption saved the dialogue area by itself this session: the card says so, so a wrong frame gets fixed.</summary>
        public bool ShowRegionAutoSetNote { get; init; }
        /// <summary>With the Region gate: the automatic try did not work, the card says so and offers the picker.</summary>
        public bool RegionAutoFailed { get; init; }
    }

    public static class StartCardModel
    {
        /// <summary>The brand gradient belongs to starting and stopping translation; every other action is an outline.</summary>
        public static bool IsGradientAction(StartCardAction action) => action is StartCardAction.Start or StartCardAction.Stop;

        /// <summary>
        /// A black picture and unmatched lines both also fit a dialogue area in the wrong place, so those two states
        /// offer the area under their first fix (where "Change game" sits otherwise).
        /// </summary>
        public static bool OffersAreaAsSecondFix(StartCardKind kind) => kind is StartCardKind.SessionFullScreen or StartCardKind.SessionNoMatch;

        /// <summary>The problems that have a card state of their own below the Translating branch.</summary>
        private static bool HasKnownProblem(StartCardInputs i) =>
            i.GameSwitchPending || (i.Offline && !i.PackOnDisk) || i.Reader is ReaderState.Failed or ReaderState.Unavailable || i.PackMissing;

        /// <summary>The session check a card state shows, None for every other state.</summary>
        public static SessionCheckKind SessionCheckOf(StartCardKind kind) => kind switch
        {
            StartCardKind.SessionFullScreen => SessionCheckKind.FullScreen,
            StartCardKind.SessionNoMatch => SessionCheckKind.NoMatch,
            StartCardKind.SessionNoDialogue => SessionCheckKind.NoDialogue,
            _ => SessionCheckKind.None,
        };

        /// <summary>The states shown while translation runs: plain, the session check's three, the first-line confirmation.</summary>
        public static bool IsTranslating(StartCardKind kind) => kind is StartCardKind.Translating or StartCardKind.SessionFullScreen
            or StartCardKind.SessionNoMatch or StartCardKind.SessionNoDialogue or StartCardKind.FirstLineWorks;

        /// <summary>
        /// Blocking problems first (device limit, nothing to translate with), then work in progress, then the steady
        /// states. Running translation wins over background work: the user's session matters more than a download.
        /// </summary>
        public static StartCardState Compute(StartCardInputs i)
        {
            bool cpuNote = i.ReaderOnCpu && i.Reader == ReaderState.Ready;
            bool offlineNote = i.Offline && i.PackOnDisk;
            bool areaNote = i.RegionAutoFill == RegionAutoFillNote.Set;
            if (i.DeviceLimit)
                return new StartCardState { Kind = StartCardKind.DeviceLimit, Action = StartCardAction.OpenAccount };
            if (i.Translating)
            {
                // Below every blocking problem, above plain "Translating": the session check, then the one-time confirmation.
                // With a problem the card already knows (it reads "Translating" then, as before) no check is laid on top:
                // advice about the game's language under a reader that failed would be wrong.
                SessionCheckKind sessionCheck = HasKnownProblem(i) ? SessionCheckKind.None : i.SessionCheck;
                (StartCardKind kind, StartCardAction action) = sessionCheck switch
                {
                    SessionCheckKind.FullScreen => (StartCardKind.SessionFullScreen, StartCardAction.OpenBorderlessGuide),
                    SessionCheckKind.NoMatch => (StartCardKind.SessionNoMatch, StartCardAction.OpenLanguageGuide),
                    SessionCheckKind.NoDialogue => (StartCardKind.SessionNoDialogue, StartCardAction.CheckArea),
                    _ => (i.FirstLineShown ? StartCardKind.FirstLineWorks : StartCardKind.Translating, StartCardAction.Stop),
                };
                // The area note would sit under a sentence that already talks about the area.
                bool check = sessionCheck != SessionCheckKind.None;
                return new StartCardState { Kind = kind, Action = action, ShowOfflineNote = offlineNote, ShowCpuNote = cpuNote, ShowRegionAutoSetNote = areaNote && !check };
            }
            // Before downloads and gates: whatever they say is about data this process will not use.
            if (i.GameSwitchPending)
                return new StartCardState { Kind = StartCardKind.GameSwitchPending, Action = StartCardAction.RestartApp };
            if (i.Offline && !i.PackOnDisk)
                return new StartCardState { Kind = StartCardKind.Offline, Action = StartCardAction.Retry };
            if (i.Downloading)
                return new StartCardState
                {
                    Kind = StartCardKind.Downloading,
                    ShowProgress = i.DownloadFraction.HasValue,
                    Progress = Math.Clamp(i.DownloadFraction ?? 0, 0, 1),
                };
            if (i.Reader == ReaderState.Unavailable)
                return new StartCardState { Kind = StartCardKind.ReaderUnavailable, Action = StartCardAction.RestartApp };
            if (i.Reader == ReaderState.Failed)
                return new StartCardState { Kind = StartCardKind.ReaderFailed, Action = StartCardAction.RetryReader };
            if (i.PackMissing && i.PackComing)
                return new StartCardState { Kind = StartCardKind.PackComing, Action = StartCardAction.None, ShowOfflineNote = offlineNote };
            if (i.PackMissing)
                return new StartCardState { Kind = StartCardKind.PackMissing, Action = StartCardAction.OpenPacks, ShowOfflineNote = offlineNote };
            if (i.IndexBuilding)
                return new StartCardState { Kind = StartCardKind.BuildingIndex, ShowOfflineNote = offlineNote };
            if (i.Reader == ReaderState.Loading)
                return new StartCardState { Kind = StartCardKind.StartingReader, ShowOfflineNote = offlineNote };
            // Auto-start would wait for more than the game: say what, before "Czekam na grę" or "gotowe do startu" mislead.
            if (i.AutoStartBlockedBy is not (StartGate.None or StartGate.GameRunning))
                return new StartCardState
                {
                    Kind = StartCardKind.AutoStartBlocked, Action = StartCardAction.ResolveGate, Gate = i.AutoStartBlockedBy,
                    ShowOfflineNote = offlineNote, ShowCpuNote = cpuNote, ShowRegionAutoSetNote = areaNote,
                    RegionAutoFailed = i.AutoStartBlockedBy == StartGate.Region && i.RegionAutoFill == RegionAutoFillNote.Failed,
                };
            if (i.WaitingForGame)
                return new StartCardState { Kind = StartCardKind.WaitingForGame, Action = StartCardAction.StartAnyway, ShowOfflineNote = offlineNote, ShowCpuNote = cpuNote, ShowRegionAutoSetNote = areaNote };
            return new StartCardState { Kind = StartCardKind.Stopped, Action = StartCardAction.Start, ShowOfflineNote = offlineNote, ShowCpuNote = cpuNote, ShowRegionAutoSetNote = areaNote };
        }
    }

    public enum PackFreshness { Unknown, UpToDate, UpdateAvailable, NotInstalled }

    public static class PackStatus
    {
        /// <summary>
        /// Pack versions look like "7.1.0-r5": a game version and a revision. Newer game version or revision on the
        /// server = update. Anything unparseable compares as text so a re-publish still shows up as different.
        /// </summary>
        public static PackFreshness Compare(string installed, string latest)
        {
            if (string.IsNullOrWhiteSpace(installed)) return PackFreshness.NotInstalled;
            if (string.IsNullOrWhiteSpace(latest)) return PackFreshness.Unknown;
            if (TryParse(installed, out var a) && TryParse(latest, out var b))
                return a.CompareTo(b) >= 0 ? PackFreshness.UpToDate : PackFreshness.UpdateAvailable;
            return string.Equals(installed.Trim(), latest.Trim(), StringComparison.OrdinalIgnoreCase)
                ? PackFreshness.UpToDate : PackFreshness.UpdateAvailable;
        }

        private static bool TryParse(string s, out (Version game, int rev) v)
        {
            v = default;
            string t = s.Trim();
            int dash = t.IndexOf("-r", StringComparison.OrdinalIgnoreCase);
            string head = dash >= 0 ? t.Substring(0, dash) : t;
            int rev = 0;
            if (dash >= 0 && !int.TryParse(t.Substring(dash + 2), NumberStyles.None, CultureInfo.InvariantCulture, out rev)) return false;
            if (!Version.TryParse(head.Contains('.') ? head : head + ".0", out var parsed)) return false;
            // "7.1" and "7.1.0" are the same release; Version ranks a missing part below 0.
            var game = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
            v = (game, rev);
            return true;
        }

        private static int CompareTo(this (Version game, int rev) a, (Version game, int rev) b)
        {
            int c = a.game.CompareTo(b.game);
            return c != 0 ? c : a.rev.CompareTo(b.rev);
        }

        /// <summary>"96 MB" / "1,2 GB" in the UI culture.</summary>
        public static string FormatSize(long bytes, CultureInfo culture)
        {
            if (bytes <= 0) return "0 MB";
            double mb = bytes / (1024.0 * 1024.0);
            if (mb >= 1024) return (mb / 1024).ToString(mb >= 10 * 1024 ? "0" : "0.0", culture) + " GB";
            return mb.ToString(mb >= 10 ? "0" : "0.0", culture) + " MB";
        }
    }

    public static class Plural
    {
        /// <summary>
        /// Picks "one|few|many" from a resource value. Polish: 1 → one; 2-4 (not 12-14) → few; else many. English
        /// and anything else: 1 → one, else few. The chosen form is formatted with the count.
        /// </summary>
        public static string Format(string forms, long n, CultureInfo culture)
        {
            if (string.IsNullOrEmpty(forms)) return n.ToString("N0", culture);
            string[] f = forms.Split('|');
            string pick = f.Length switch
            {
                1 => f[0],
                2 => n == 1 ? f[0] : f[1],
                _ => culture.TwoLetterISOLanguageName == "pl" ? f[PolishIndex(n)] : (n == 1 ? f[0] : f[1]),
            };
            return string.Format(culture, pick, n);
        }

        public static int PolishIndex(long n)
        {
            if (n == 1) return 0;
            long d = Math.Abs(n) % 10, h = Math.Abs(n) % 100;
            return d >= 2 && d <= 4 && (h < 12 || h > 14) ? 1 : 2;
        }
    }

    public static class ReleaseNoteTitle
    {
        /// <summary>The site's own wording (landing/src/data/release-seo.ts): "Genshin Impact 7.1 po polsku: …".</summary>
        public static string Build(string gameName, string gameVersion, string kind, string tagline,
                                   string newVersionFormat, string newVersionNoTaglineFormat, string revisionFormat)
        {
            bool isNew = string.Equals(kind, "new_version", StringComparison.OrdinalIgnoreCase);
            if (isNew && !string.IsNullOrWhiteSpace(tagline))
                return string.Format(CultureInfo.CurrentCulture, newVersionFormat, gameName, gameVersion, tagline.Trim());
            return string.Format(CultureInfo.CurrentCulture, isNew ? newVersionNoTaglineFormat : revisionFormat, gameName, gameVersion);
        }
    }
}
