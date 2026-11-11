using System.Threading;
using GI_Subtitles.Services.Translation;
using OpenCvSharp;

namespace GI_Subtitles.Services.Detection
{
    /// <summary>What one read of the dialogue area came to, for the Start card's session check.</summary>
    public enum ReadOutcome
    {
        /// <summary>Nothing was read.</summary>
        NoText,
        /// <summary>Text that says nothing either way: HUD numbers, a label, a miss that was already counted.</summary>
        Ignored,
        /// <summary>The pack knows the text but it is not dialogue (map, menu, item): the game is in English.</summary>
        PackHit,
        /// <summary>A line was found and painted.</summary>
        Matched,
        /// <summary>A speaker and a sentence, and the pack has no such line.</summary>
        Unmatched,
    }

    /// <summary>The evidence at one moment; a value, so the card's model is tested without a window.</summary>
    public readonly record struct SessionEvidenceSnapshot(
        int DialogueReads, int MatchedReads, int BlackFrames, int PackHits, int UnmatchedRun,
        double InFrontSeconds, double BlackSeconds, bool SawPicture, double PictureSeconds = 0, int TextReads = 0);

    /// <summary>
    /// What the reading loop saw since translation started: counters only, written with Interlocked on the capture
    /// path (no allocation, no logging) and read by the Start card once a second.
    /// </summary>
    public sealed class SessionEvidence
    {
        /// <summary>A longer pause between two frames (another window in front, sleep) is not time with the game in front.</summary>
        public const long MaxFrameGapMs = 2000;

        private const long NoFrame = long.MinValue;

        private int _dialogueReads, _matchedReads, _blackFrames, _packHits, _unmatchedRun, _sawPicture, _textReads;
        private long _lastFrameMs = NoFrame, _inFrontMs, _blackRunMs, _pictureRunMs;
        private string _lastUnmatched;

        /// <summary>Translation started, or the dialogue area changed: what was seen before no longer counts.</summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _dialogueReads, 0);
            Interlocked.Exchange(ref _matchedReads, 0);
            Interlocked.Exchange(ref _blackFrames, 0);
            Interlocked.Exchange(ref _packHits, 0);
            Interlocked.Exchange(ref _unmatchedRun, 0);
            Interlocked.Exchange(ref _sawPicture, 0);
            Interlocked.Exchange(ref _textReads, 0);
            Interlocked.Exchange(ref _lastFrameMs, NoFrame);
            Interlocked.Exchange(ref _inFrontMs, 0);
            Interlocked.Exchange(ref _blackRunMs, 0);
            Interlocked.Exchange(ref _pictureRunMs, 0);
            Volatile.Write(ref _lastUnmatched, null);
        }

        /// <summary>One captured frame while the game is in front. <paramref name="nowMs"/> is Environment.TickCount64.</summary>
        public void OnGameFrame(bool black, long nowMs)
        {
            long previous = Interlocked.Exchange(ref _lastFrameMs, nowMs);
            long elapsed = previous == NoFrame ? 0 : nowMs - previous;
            if (elapsed < 0 || elapsed > MaxFrameGapMs)
            {
                // Nobody watched in between: a run is unbroken frames, so both start over with this frame.
                elapsed = 0;
                Interlocked.Exchange(ref _blackRunMs, 0);
                Interlocked.Exchange(ref _pictureRunMs, 0);
            }
            if (elapsed > 0) Interlocked.Add(ref _inFrontMs, elapsed);
            if (black)
            {
                Interlocked.Increment(ref _blackFrames);
                if (elapsed > 0) Interlocked.Add(ref _blackRunMs, elapsed);
                Interlocked.Exchange(ref _pictureRunMs, 0);
            }
            else
            {
                Volatile.Write(ref _sawPicture, 1);
                if (elapsed > 0) Interlocked.Add(ref _pictureRunMs, elapsed);
                Interlocked.Exchange(ref _blackRunMs, 0);
            }
        }

        /// <summary>One finished read. <paramref name="text"/> is kept by reference, to count a repeated miss once.</summary>
        public void OnRead(ReadOutcome outcome, string text)
        {
            // Any text at all, a repeated line or a HUD number included, shows that the picture is not black.
            if (outcome != ReadOutcome.NoText) Interlocked.Increment(ref _textReads);
            switch (outcome)
            {
                case ReadOutcome.Matched:
                    Interlocked.Increment(ref _dialogueReads);
                    Interlocked.Increment(ref _matchedReads);
                    Interlocked.Exchange(ref _unmatchedRun, 0);
                    Volatile.Write(ref _lastUnmatched, null);
                    break;
                case ReadOutcome.PackHit:
                    Interlocked.Increment(ref _packHits);
                    Interlocked.Exchange(ref _unmatchedRun, 0);
                    Volatile.Write(ref _lastUnmatched, null);
                    break;
                case ReadOutcome.Unmatched:
                    if (string.Equals(text, Volatile.Read(ref _lastUnmatched), System.StringComparison.Ordinal)) break;
                    Volatile.Write(ref _lastUnmatched, text);
                    Interlocked.Increment(ref _dialogueReads);
                    Interlocked.Increment(ref _unmatchedRun);
                    break;
            }
        }

        public SessionEvidenceSnapshot Snapshot() => new(
            Volatile.Read(ref _dialogueReads), Volatile.Read(ref _matchedReads), Volatile.Read(ref _blackFrames),
            Volatile.Read(ref _packHits), Volatile.Read(ref _unmatchedRun),
            Interlocked.Read(ref _inFrontMs) / 1000.0, Interlocked.Read(ref _blackRunMs) / 1000.0,
            Volatile.Read(ref _sawPicture) != 0, Interlocked.Read(ref _pictureRunMs) / 1000.0, Volatile.Read(ref _textReads));

        /// <summary>
        /// Only a speaker with a sentence under it counts as a line the pack should have: menus, maps and item panels
        /// are pack text too (a hit there proves the game is in English), and labels or HUD numbers prove nothing. A
        /// miss the resolver answered from its memo was counted when it was first read.
        /// </summary>
        public static ReadOutcome Classify(string text, string speaker, in SubtitleResolution resolution, bool gameUi)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2) return ReadOutcome.NoText;
            if (resolution.HasContent) return ReadOutcome.Matched;
            if (resolution.Source == ResolutionSource.NotDialogue) return ReadOutcome.PackHit;
            if (gameUi || resolution.Source is not (ResolutionSource.Matcher or ResolutionSource.MatcherHeaderSeparated))
                return ReadOutcome.Ignored;
            return !string.IsNullOrWhiteSpace(speaker) && SubtitleResolver.ReadsAsSpeech(text)
                ? ReadOutcome.Unmatched : ReadOutcome.Ignored;
        }
    }

    /// <summary>Whether a captured frame is black: what exclusive full screen (or a blocked capture) hands back.</summary>
    public static class FrameBlackness
    {
        /// <summary>The grid of pixels looked at; a frame with any picture leaves at the first lit one.</summary>
        public const int SampleRows = 8, SampleColumns = 32;
        /// <summary>A channel above this is picture. A blocked capture is exactly 0; the margin is for dithering.</summary>
        public const byte BlackLevel = 8;

        public static unsafe bool IsBlack(Mat frame)
        {
            if (frame == null || frame.Empty()) return true;
            int channels = frame.Channels();
            if (frame.Depth() != MatType.CV_8U) return false;   // not a screen capture: say nothing
            int rows = frame.Rows, cols = frame.Cols;
            int colour = channels < 3 ? channels : 3;   // BGRA: alpha is not picture
            long step = frame.Step();
            byte* data = frame.DataPointer;
            int rowStride = rows / SampleRows > 0 ? rows / SampleRows : 1;
            int colStride = cols / SampleColumns > 0 ? cols / SampleColumns : 1;
            for (int y = rowStride / 2; y < rows; y += rowStride)
            {
                byte* row = data + y * step;
                for (int x = colStride / 2; x < cols; x += colStride)
                {
                    byte* pixel = row + (long)x * channels;
                    for (int c = 0; c < colour; c++)
                        if (pixel[c] > BlackLevel) return false;
                }
            }
            return true;
        }
    }
}
