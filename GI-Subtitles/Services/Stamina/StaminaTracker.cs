using System;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Stamina
{
    /// <summary>What the tracker made of one value read off the screen.</summary>
    public enum StaminaVerdict
    {
        /// <summary>It is what the timer expected; nothing changes.</summary>
        AsExpected,
        /// <summary>A new value, believed: the timer now runs from it.</summary>
        Accepted,
        /// <summary>A surprising value seen too few times so far; it waits for the next looks.</summary>
        Waiting,
    }

    /// <summary>Why the timer last moved to a new reading.</summary>
    public enum StaminaChange
    {
        None,
        /// <summary>The first reading ever.</summary>
        First,
        /// <summary>Lower than the timer expected by more than a point: the player spent some.</summary>
        Spent,
        /// <summary>Higher than regeneration explains: a refill.</summary>
        Refilled,
        /// <summary>One point off the expectation: the same fill, with the moment within the interval corrected.</summary>
        Corrected,
    }

    /// <summary>
    /// Decides when a value read off the screen is believed. A value the timer already expects passes at once. Any
    /// other must come back on separate looks first: <c>confirmations</c> times when it is lower than expected (the
    /// player spent some), one point higher (the unknown moment within the interval) or the first ever,
    /// <c>refillConfirmations</c> times when it is higher than regeneration explains (a refill). Looks agree when they
    /// show the candidate's value, or the one point regeneration can add while it is being confirmed; anything else,
    /// a value going down or a second extra point, starts the count again. A point seen arriving between two close
    /// looks, once confirmed, also tells the timer where in the interval it is. Pure: the caller supplies the time.
    /// </summary>
    public sealed class StaminaTracker
    {
        private readonly GameStamina _stamina;
        private readonly int _confirmations, _refillConfirmations;
        private readonly TimeSpan _agreeWithin;

        // The candidate being confirmed: the value it started at, the value now, how many looks agreed, when the
        // last one was, and when a point was seen arriving (null when none was).
        private int _candidateBase, _candidateValue, _candidateLooks;
        private DateTimeOffset _candidateAt;
        private DateTimeOffset? _candidateTickAt;
        private int? _seenValue;
        private DateTimeOffset _seenAt;

        /// <summary>The reading the timer runs from; null before the first believed value.</summary>
        public StaminaReading Reading { get; private set; }

        /// <summary>Why <see cref="Reading"/> last changed (the answer to the last <see cref="StaminaVerdict.Accepted"/>).</summary>
        public StaminaChange LastChange { get; private set; }

        /// <summary>When a value that fits <see cref="Reading"/> was last on screen.</summary>
        public DateTimeOffset? LastSeenAt => _seenValue.HasValue ? _seenAt : null;

        /// <param name="agreeWithin">
        /// Looks further apart than this do not confirm each other, and a point counts as "seen arriving" only between
        /// looks closer than this.
        /// </param>
        public StaminaTracker(GameStamina stamina, int confirmations, int refillConfirmations, TimeSpan agreeWithin, StaminaReading known = null)
        {
            _stamina = stamina ?? throw new ArgumentNullException(nameof(stamina));
            _confirmations = Math.Max(1, confirmations);
            _refillConfirmations = Math.Max(_confirmations, refillConfirmations);
            _agreeWithin = agreeWithin;
            Reading = known;
        }

        public StaminaVerdict Offer(int value, DateTimeOffset now)
        {
            int? expected = Reading == null ? null : StaminaClock.ValueAt(_stamina, Reading, now);
            if (expected == value)
            {
                _candidateLooks = 0;
                Saw(value, now);
                return StaminaVerdict.AsExpected;
            }

            // Agrees with the candidate: its value again, or the single point regeneration can add meanwhile.
            bool recent = _candidateLooks > 0 && now - _candidateAt <= _agreeWithin;
            bool same = recent && value == _candidateValue;
            bool onePointMore = recent && value == _candidateValue + 1 && value == _candidateBase + 1 && value <= _stamina.Max;
            if (same || onePointMore)
            {
                _candidateLooks++;
                if (onePointMore) _candidateTickAt = now;
            }
            else
            {
                _candidateBase = value;
                _candidateLooks = 1;
                // One point more than the value on screen moments ago: a point arriving, if the next looks agree.
                bool arrived = _seenValue.HasValue && value == _seenValue.Value + 1 && value <= _stamina.Max && now - _seenAt <= _agreeWithin;
                _candidateTickAt = arrived ? now : null;
            }
            _candidateValue = value;
            _candidateAt = now;

            // Above what regeneration explains (one point of slack for the unknown moment within the interval): a refill.
            bool refill = expected.HasValue && value > expected.Value + 1;
            if (_candidateLooks < (refill ? _refillConfirmations : _confirmations)) return StaminaVerdict.Waiting;

            // One point either way is the unknown moment within the interval, not spending: nothing costs one point.
            LastChange = !expected.HasValue ? StaminaChange.First
                : refill ? StaminaChange.Refilled
                : value < expected.Value - 1 ? StaminaChange.Spent
                : StaminaChange.Corrected;
            double nextIn = _candidateTickAt is DateTimeOffset tick
                ? Math.Clamp(_stamina.RegenSeconds - (now - tick).TotalSeconds, 1, _stamina.RegenSeconds)
                : StaminaClock.UnknownPhase(_stamina);
            Reading = new StaminaReading(value, now, nextIn);
            _candidateLooks = 0;
            Saw(value, now);
            return StaminaVerdict.Accepted;
        }

        private void Saw(int value, DateTimeOffset now)
        {
            _seenValue = value;
            _seenAt = now;
        }
    }
}
