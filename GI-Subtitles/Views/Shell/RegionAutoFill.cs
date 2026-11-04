using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Validation;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>What the Start card says about the dialogue area Kaption tried to set by itself this session.</summary>
    public enum RegionAutoFillNote { None, Set, Failed }

    /// <summary>One watcher probe's verdict while the dialogue area is missing.</summary>
    public enum RegionAutoFillStep { Wait, Fill, GiveUp }

    /// <summary>
    /// The player set Kaption up before launching the game, so no dialogue area is saved and auto-start stops at the
    /// Region gate. When the game is in front, this fills the area once per session from the game's measured profile
    /// ratios (what setup saves when no dialogue is on screen); OCR on a splash or login screen would anchor on the
    /// wrong text. A saved area is never replaced. Not thread-safe: the watcher calls it on the UI thread.
    /// </summary>
    public sealed class RegionAutoFill
    {
        /// <summary>A client area smaller than this is a launcher, a splash or a window still opening.</summary>
        public const int MinClientWidth = 640, MinClientHeight = 360;

        /// <summary>The same client rectangle on this many probes in a row (about 3 s at the 1.5 s watch) before filling.</summary>
        public const int StableProbes = 3;

        /// <summary>Probes with the game in front and no usable, steady window before the card says it did not work.</summary>
        public const int MaxProbes = 12;

        public readonly record struct WindowRect(int X, int Y, int Width, int Height);

        private WindowRect? _last;
        private int _steady, _probes;

        /// <summary>Set or Failed: this session's one attempt is used up.</summary>
        public RegionAutoFillNote Outcome { get; private set; }

        /// <summary>The area Kaption saved by itself (this session or an earlier one), so the note goes once the player changes it.</summary>
        public string FilledRegion { get; private set; }

        /// <summary>Config key that keeps <see cref="FilledRegion"/> across restarts; a frame filled once never reads as hand-drawn.</summary>
        public const string FilledRegionKey = "RegionAutoFilled";

        /// <summary>An earlier session's filled area: the note survives the restart, this session may still try if the area goes.</summary>
        public void Restore(string filledRegion) => FilledRegion = string.IsNullOrEmpty(filledRegion) ? null : filledRegion;

        /// <summary>
        /// Only when auto-start waits on the dialogue area alone, the game window really is in front (the probe's own
        /// answer, not the "running counts" setting), no area is saved, nothing was decided yet this session, and no
        /// picker or translation is running.
        /// </summary>
        public static bool ShouldTry(bool regionSaved, bool blockedOnRegion, bool gameInFront, bool decided, bool busy) =>
            !regionSaved && blockedOnRegion && gameInFront && !decided && !busy;

        public bool Decided => Outcome != RegionAutoFillNote.None;

        public static bool IsUsable(in WindowRect w) => w.Width >= MinClientWidth && w.Height >= MinClientHeight;

        /// <summary>One probe while <see cref="ShouldTry"/> holds; <paramref name="window"/> is null when the window was not found.</summary>
        public RegionAutoFillStep Observe(WindowRect? window)
        {
            if (Decided) return RegionAutoFillStep.Wait;
            _probes++;
            if (window is WindowRect w && IsUsable(w))
            {
                _steady = _last == w ? _steady + 1 : 1;
                _last = w;
                if (_steady >= StableProbes) return RegionAutoFillStep.Fill;
            }
            else
            {
                _steady = 0;
                _last = null;
            }
            return _probes >= MaxProbes ? RegionAutoFillStep.GiveUp : RegionAutoFillStep.Wait;
        }

        /// <summary>The game left the front: a steady run has to start again, the probes already spent still count.</summary>
        public void Interrupt()
        {
            _steady = 0;
            _last = null;
        }

        /// <summary>
        /// Saves only when, on the UI thread after the lookup, still no area is saved: the player may have drawn one
        /// meanwhile (tray, Ctrl+Shift+R, setup).
        /// </summary>
        public static bool MaySave(bool regionSavedNow, string region) => !regionSavedNow && !string.IsNullOrEmpty(region);

        public void MarkSet(string region)
        {
            Outcome = RegionAutoFillNote.Set;
            FilledRegion = region;
        }

        public void MarkFailed() => Outcome = RegionAutoFillNote.Failed;

        /// <summary>The note follows the saved area: "set" while it is still the filled one, "failed" while none is saved.</summary>
        public RegionAutoFillNote NoteFor(string savedRegion, bool regionSaved)
        {
            if (regionSaved && FilledRegion != null && savedRegion == FilledRegion) return RegionAutoFillNote.Set;
            return Outcome == RegionAutoFillNote.Failed && !regionSaved ? RegionAutoFillNote.Failed : RegionAutoFillNote.None;
        }

        /// <summary>The dialogue area for this window from the profile's ratios, clamped to the desktop; null when off it.</summary>
        public static string RegionFor(GameRegionProfile profile, in WindowRect w, int desktopLeft, int desktopTop, int desktopWidth, int desktopHeight)
        {
            if (profile == null || !IsUsable(w)) return null;
            var (dialogue, _) = GameWindowDetector.CalculateFromRatios(profile, w.X, w.Y, w.Width, w.Height);
            if (!CaptureRegionValidator.TryParse(dialogue.Split(','), out int x, out int y, out int width, out int height)) return null;
            if (!CaptureRegionBounds.TryClamp(x, y, width, height, desktopLeft, desktopTop, desktopWidth, desktopHeight,
                    out int cx, out int cy, out int cw, out int ch))
                return null;
            return $"{cx},{cy},{cw},{ch}";
        }
    }
}
