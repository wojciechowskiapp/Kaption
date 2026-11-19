using System;
using System.Drawing;

namespace GI_Subtitles.Services.Hints
{
    /// <summary>
    /// The pointer and the card, from one look to the next. <see cref="Armed"/>: the pointer has been seen away from
    /// this card, so being on it now is a visit. <see cref="Resting"/>: a visible pointer lies still on the card.
    /// <see cref="StripOpen"/>: the three buttons are up and can be clicked. The window starts from the default
    /// value whenever a card appears, moves or is replaced.
    /// </summary>
    public readonly record struct HintPointerState(bool Armed, bool StripOpen, bool Resting, Point RestAt, long? RestSinceMs, long? OutsideSinceMs)
    {
        /// <summary>The player is at the card: its clock waits and a pinned sheet stays at full strength.</summary>
        public bool Holds => StripOpen || Resting;

        /// <summary>Away from the card with nothing open.</summary>
        internal static HintPointerState Away => new(true, false, false, default, null, null);
    }

    /// <summary>
    /// When the card's buttons exist (.plan/research/s68/HINTS-AND-MORE-TRANSLATION.md, 3.7). The card itself never
    /// takes a click; the strip is a second small window that can, so it is there only on purpose: a visible pointer
    /// came to the card and rested on it. A card that appears under a parked pointer opens nothing until the pointer
    /// leaves and comes back, and a hidden pointer (the game in mouse-look) opens nothing and closes the strip at
    /// once, so no click meant for the game ever lands on it. Pure; the window looks every <see cref="TickMs"/>.
    /// </summary>
    public static class HintInteractivity
    {
        public const int TickMs = 200;
        /// <summary>How long the pointer lies still on the card before the strip opens.</summary>
        public const int RestMs = 400;
        /// <summary>How long the pointer is away from the card and the strip before the strip closes.</summary>
        public const int LeaveMs = 600;
        /// <summary>A hand on a mouse is never quite still: moving less than this many DIPs is still resting.</summary>
        public const double RestSlopDip = 4;

        /// <param name="card">The card's bounds, in the pointer's coordinates (screen pixels).</param>
        /// <param name="strip">The strip's bounds; only looked at while it is open.</param>
        /// <param name="nowMs">A steady clock (Environment.TickCount64).</param>
        /// <param name="slopPx"><see cref="RestSlopDip"/> in pixels of the card's monitor.</param>
        public static HintPointerState Next(HintPointerState state, bool pointerVisible, Point pointer, Rectangle card, Rectangle strip,
            long nowMs, int slopPx)
        {
            // Unarmed again: a pointer that comes back into view on the card did not travel to it.
            if (!pointerVisible) return default;

            if (card.Contains(pointer))
            {
                // The card came up under the pointer, or the pointer came into view on it: not a visit.
                if (!state.Armed) return default;
                bool still = state.RestSinceMs.HasValue
                    && Math.Abs(pointer.X - state.RestAt.X) <= slopPx && Math.Abs(pointer.Y - state.RestAt.Y) <= slopPx;
                // A pointer on its way across starts a new rest at every look and so never finishes one.
                Point restAt = still ? state.RestAt : pointer;
                long restSince = still ? state.RestSinceMs.Value : nowMs;
                bool resting = nowMs - restSince >= RestMs;
                return new HintPointerState(true, state.StripOpen || resting, resting, restAt, restSince, null);
            }

            if (!state.StripOpen || strip.Contains(pointer)) return HintPointerState.Away with { StripOpen = state.StripOpen };

            long outsideSince = state.OutsideSinceMs ?? nowMs;
            return nowMs - outsideSince >= LeaveMs ? HintPointerState.Away : HintPointerState.Away with { StripOpen = true, OutsideSinceMs = outsideSince };
        }
    }
}
