using System.Drawing;

namespace GI_Subtitles.Services.Detection
{
    /// <summary>What a lookup of the game's own window found (process match only, never a title alone).</summary>
    public enum GameWindowState { NotFound, Minimised, Found }

    public enum WatchStep { Wait, Ready, GiveUp }

    /// <summary>
    /// While translation is off, the 1.5 s auto-start watch looks up the game's window. Once one usable client area has
    /// held for <see cref="StableProbes"/> probes it is Ready: the area is decided for it, and again whenever another
    /// client holds that long (the window moved or changed size). A minimised game waits without using up the try; a
    /// game that never shows a usable window gives up once, so the Start card can offer the picker. UI thread only.
    /// </summary>
    public sealed class DialogueAreaWatch
    {
        /// <summary>About 3 s at the 1.5 s watch: a window still opening or switching to full screen settles first.</summary>
        public const int StableProbes = 3;

        /// <summary>Probes with the game in front and no usable, steady window before the card says it did not work.</summary>
        public const int MaxProbes = 12;

        private Rectangle? _last, _decidedFor;
        private int _steady, _probes;
        private bool _everDecided;

        /// <summary>This session's try gave up: no usable window was ever seen.</summary>
        public bool GaveUp { get; private set; }

        public WatchStep Observe(GameWindowState state, Rectangle client)
        {
            if (state == GameWindowState.Minimised)
            {
                Interrupt();
                return WatchStep.Wait;
            }
            _probes++;
            if (state == GameWindowState.Found && DialogueAreaPlacement.IsUsable(client))
            {
                _steady = _last == client ? _steady + 1 : 1;
                _last = client;
                if (_steady >= StableProbes && _decidedFor != client) return WatchStep.Ready;
            }
            else
            {
                Interrupt();
            }
            if (!_everDecided && !GaveUp && _probes >= MaxProbes)
            {
                GaveUp = true;
                return WatchStep.GiveUp;
            }
            return WatchStep.Wait;
        }

        /// <summary>The area was decided for this client (placed, kept or widened).</summary>
        public void MarkDecided(Rectangle client)
        {
            _decidedFor = client;
            _everDecided = true;
        }

        /// <summary>
        /// The block could not be placed on this client (mostly off the screen): it is not asked again until the window
        /// changes, and the probes still count toward giving up, so the Start card can offer the picker.
        /// </summary>
        public void MarkUnplaceable(Rectangle client) => _decidedFor = client;

        /// <summary>The game left the front: a steady run has to start again.</summary>
        public void Interrupt()
        {
            _steady = 0;
            _last = null;
        }

        /// <summary>The area changed some other way: the next steady window is decided again.</summary>
        public void Forget() => _decidedFor = null;
    }
}
