using System.Drawing;

namespace GI_Subtitles.Services.Detection
{
    public enum FollowStep
    {
        /// <summary>The area belongs to this window: read as usual.</summary>
        Read,
        /// <summary>The window is moving or changing size: skip the read, place nothing yet.</summary>
        Hold,
        /// <summary>The window has been still long enough: place the block again.</summary>
        Place,
    }

    /// <summary>
    /// While translating in automatic mode: the capture tick hands over the game's client area (two Win32 calls, no
    /// allocation) and this says whether to read. A drag places nothing until the window has been still for
    /// <see cref="StillTicks"/> ticks, so the reader's shape changes once and not on every step. UI thread only.
    /// </summary>
    public sealed class DialogueAreaFollow
    {
        /// <summary>About half a second at the capture tick.</summary>
        public const int StillTicks = 5;

        private Rectangle? _placedFor, _candidate;
        private int _still;

        public bool IsPlacedFor(Rectangle client) => _placedFor == client;

        /// <param name="areaFitsClient">The saved area is the block on <paramref name="client"/> already (asked only
        /// when the client is not the one last placed for): last session's area on the same window, adopted as it is.</param>
        public FollowStep Observe(Rectangle client, bool areaFitsClient)
        {
            if (_placedFor == client || areaFitsClient)
            {
                MarkPlaced(client);
                return FollowStep.Read;
            }
            if (_candidate == client) _still++;
            else
            {
                _candidate = client;
                _still = 1;
            }
            return _still >= StillTicks ? FollowStep.Place : FollowStep.Hold;
        }

        /// <summary>The area now belongs to <paramref name="client"/> (placed, or left as it was because it could not be).</summary>
        public void MarkPlaced(Rectangle client)
        {
            _placedFor = client;
            _candidate = null;
            _still = 0;
        }

        /// <summary>The area was changed some other way (drawn, typed, a new game): the next tick looks again.</summary>
        public void Reset()
        {
            _placedFor = null;
            _candidate = null;
            _still = 0;
        }
    }
}
