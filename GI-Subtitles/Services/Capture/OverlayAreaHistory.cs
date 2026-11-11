using System;
using System.Collections.Generic;
using System.Drawing;

namespace GI_Subtitles.Services.Capture
{
    /// <summary>
    /// Where Kaption's own panels were at the reads of this capture tick and of the tick before. A frame can be one
    /// composition older than the layout, so a read masks these places as well as where the panels are now
    /// (<see cref="OverlayMask"/>). One tick makes several reads (dialogue, answers, the stamina strip, which copies
    /// the frame the tick already holds); the caller says when a tick begins, and everything seen within it is kept
    /// for the tick's later reads, however long they take. A tick's places stop counting once they are older than
    /// the freshness given here: no frame shows them any more, and masking them would only black out game text.
    /// </summary>
    public sealed class OverlayAreaHistory
    {
        private readonly int _freshMs;
        private List<Rectangle> _thisTick = new();
        private List<Rectangle> _lastTick = new();
        private long _thisTickAt = long.MinValue;
        private long _lastTickAt = long.MinValue;

        /// <param name="freshMs">How long after a tick's last read its places can still be in a frame.</param>
        public OverlayAreaHistory(int freshMs)
        {
            _freshMs = Math.Max(0, freshMs);
        }

        /// <summary>A new capture tick begins: this tick's places become "the tick before", and older ones are forgotten.</summary>
        public void NextTick()
        {
            (_lastTick, _thisTick) = (_thisTick, _lastTick);
            _lastTickAt = _thisTickAt;
            _thisTick.Clear();
            _thisTickAt = long.MinValue;
        }

        /// <summary>
        /// Notes the areas of a read at <paramref name="atMs"/> and returns the earlier ones its frame may still show:
        /// this tick's earlier reads, and the tick before while that is fresh.
        /// </summary>
        public IReadOnlyList<Rectangle> Observe(IReadOnlyList<Rectangle> now, long atMs)
        {
            var before = Join(Fresh(_lastTickAt, atMs) ? _lastTick : null, _thisTick);
            _thisTickAt = atMs;
            if (now != null)
                for (int i = 0; i < now.Count; i++)
                    if (!_thisTick.Contains(now[i])) _thisTick.Add(now[i]);
            return before;
        }

        /// <summary>
        /// For a read outside the capture tick (Ctrl+Q, the dialogue-area search, the UID read): the places of the
        /// last two ticks, each only while it is fresh at <paramref name="atMs"/>. Notes nothing.
        /// </summary>
        public IReadOnlyList<Rectangle> Recent(long atMs) =>
            Join(Fresh(_lastTickAt, atMs) ? _lastTick : null, Fresh(_thisTickAt, atMs) ? _thisTick : null);

        /// <summary>
        /// Everything a one-off read has to black out: where the panels are <paramref name="now"/>, plus where the last
        /// ticks saw them while that is fresh (<see cref="Recent"/>). Notes nothing.
        /// </summary>
        public IReadOnlyList<Rectangle> WithRecent(IReadOnlyList<Rectangle> now, long atMs)
        {
            var recent = Recent(atMs);
            if (recent.Count == 0) return now ?? Array.Empty<Rectangle>();
            var all = new List<Rectangle>((now?.Count ?? 0) + recent.Count);
            if (now != null) all.AddRange(now);
            for (int i = 0; i < recent.Count; i++)
                if (!all.Contains(recent[i])) all.Add(recent[i]);
            return all;
        }

        /// <summary>A tick read at <paramref name="tickAt"/> still counts at <paramref name="atMs"/>; a clock that went backwards does not.</summary>
        private bool Fresh(long tickAt, long atMs) => tickAt != long.MinValue && atMs >= tickAt && atMs - tickAt <= _freshMs;

        private static IReadOnlyList<Rectangle> Join(List<Rectangle> first, List<Rectangle> second)
        {
            int count = (first?.Count ?? 0) + (second?.Count ?? 0);
            if (count == 0) return Array.Empty<Rectangle>();
            var joined = new List<Rectangle>(count);
            if (first != null) joined.AddRange(first);
            if (second != null) joined.AddRange(second);
            return joined;
        }
    }
}
