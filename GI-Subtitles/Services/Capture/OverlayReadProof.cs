using System;

namespace GI_Subtitles.Services.Capture
{
    /// <summary>
    /// What the app holds about the read check (<see cref="OverlayReadCheck"/>): whether the subtitle panel is proven
    /// out of the frames, for which reader and which setup of it, and when the next check is due. The panel stops being
    /// blacked out of a read only while <see cref="Holds"/> says so for the reader that read is made with; everything
    /// that could make an old proof wrong (a setting, a start, another reader, a reader set up again, the app reading
    /// its own line) takes it away here, in one place. Pure: the window and the reader are the caller's.
    /// </summary>
    public sealed class OverlayReadProof
    {
        /// <summary>How long after a check that proved nothing the next one is tried; doubles up to the cap.</summary>
        public const int RetryMs = 10_000, RetryCapMs = 120_000;

        private object _reader;
        private long _epoch;
        /// <summary>Moves whenever a proof is taken away: a check that began before that is void.</summary>
        private long _generation;
        /// <summary>The app read its own line under a proof: no check is believed again before the next start.</summary>
        private bool _tripped;

        public OverlayReadState State { get; private set; } = OverlayReadState.Unknown;
        /// <summary>The settings ask for the overlay to be kept out of the app's reads (over the text, recordings off).</summary>
        public bool Wanted { get; private set; }
        /// <summary>When the next check is due; <see cref="long.MaxValue"/> when none is.</summary>
        public long DueAtMs { get; private set; } = long.MaxValue;
        public int ChecksWithoutProof { get; private set; }
        /// <summary>The next check is the first since translation started.</summary>
        public bool FirstSinceStart { get; private set; }

        /// <summary>What the settings say now. True when that changed: then nothing proven before counts.</summary>
        public bool SettingsSay(bool wanted, long nowMs)
        {
            if (wanted == Wanted) return false;
            Wanted = wanted;
            _tripped = false;
            Forget(nowMs);
            return true;
        }

        /// <summary>
        /// Takes the proof away (another reader, a reader set up again). A new check is due at once while the settings
        /// still ask for one, unless the app has read its own line since the last start.
        /// </summary>
        public void Forget(long nowMs)
        {
            _reader = null;
            _generation++;
            if (_tripped) return;
            State = OverlayReadState.Unknown;
            ChecksWithoutProof = 0;
            DueAtMs = Wanted ? nowMs : long.MaxValue;
        }

        public void TranslationStarted(long nowMs)
        {
            if (!Wanted) return;
            _tripped = false;
            Forget(nowMs);
            FirstSinceStart = true;
        }

        public bool Due(long nowMs) => Wanted && nowMs >= DueAtMs;

        /// <summary>A check begins. Nothing is due while it runs; the ticket goes back to <see cref="End"/>.</summary>
        public long Begin()
        {
            DueAtMs = long.MaxValue;
            return _generation;
        }

        /// <summary>
        /// The check ended with <paramref name="state"/>, made through <paramref name="reader"/> in its setup
        /// <paramref name="epoch"/>. False: the proof was taken away while the check ran, so its answer is about a
        /// state that is gone.
        /// </summary>
        public bool End(long ticket, OverlayReadState state, object reader, long epoch, long nowMs)
        {
            if (ticket != _generation || !Wanted) return false;
            FirstSinceStart = false;
            State = state;
            switch (state)
            {
                case OverlayReadState.OutOfFrame:
                    ChecksWithoutProof = 0;
                    _reader = reader;
                    _epoch = epoch;
                    break;
                case OverlayReadState.InFrame:
                    // The reader does see the overlay: asking again every few seconds would only hold the capture tick.
                    // A start, another reader or a changed setting asks again.
                    break;
                default:
                    long wait = Math.Min(RetryCapMs, (long)RetryMs << Math.Min(ChecksWithoutProof, 8));
                    ChecksWithoutProof++;
                    DueAtMs = nowMs + wait;
                    break;
            }
            return true;
        }

        /// <summary>
        /// Whether a read through <paramref name="reader"/>, in its setup <paramref name="epoch"/>, is proven free of
        /// the panel: only the very reader the check looked through, and only until it is set up again.
        /// </summary>
        public bool Holds(object reader, long epoch) =>
            State == OverlayReadState.OutOfFrame && reader != null && ReferenceEquals(reader, _reader) && epoch == _epoch;

        /// <summary>
        /// The app read its own line (<see cref="SelfReadTripwire"/>): the reader sees the overlay after all, whatever
        /// a check said, so none is run again before the next start or a changed setting.
        /// </summary>
        public void Tripped()
        {
            _tripped = true;
            State = OverlayReadState.InFrame;
            _reader = null;
            _generation++;
            DueAtMs = long.MaxValue;
        }
    }
}
