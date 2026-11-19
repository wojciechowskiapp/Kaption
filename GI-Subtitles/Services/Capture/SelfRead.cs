using System;
using GI_Subtitles.Services.Translation;

namespace GI_Subtitles.Services.Capture
{
    /// <summary>
    /// Did the app just read its own subtitle? With the panel over the game's text that must never happen, and the read
    /// check (<see cref="OverlayReadCheck"/>) proves it at a start; this is the watch between checks. The text just read
    /// is compared with the line on screen: the app's own line read back was 0.97 to 1.00 like the line shown, the
    /// game's English at most 0.26 like any Polish line (eight lines, .plan/research/s68/OVERLAY-OVER-TEXT.md,
    /// section 11), so 0.8 leaves room on both sides. Nothing here allocates: it runs once per read.
    /// </summary>
    public static class SelfRead
    {
        /// <summary>A line shorter than this, in letters and digits, matches too much by chance to be evidence.</summary>
        public const int MinLetters = 12;
        /// <summary>One minus the edit distance over the line's length.</summary>
        public const double MinSimilarity = 0.8;
        private const int MaxLetters = 512;

        /// <summary>
        /// True when <paramref name="read"/> is the line on screen (<paramref name="shown"/>) read back. A line that is
        /// like the read it came from (<paramref name="source"/>: a name, a line the pack left in English) is never
        /// evidence, because reading the game gives the same text; neither is a line with no such read (a prediction).
        /// </summary>
        public static bool Looks(ReadOnlySpan<char> read, ReadOnlySpan<char> shown, ReadOnlySpan<char> source)
        {
            Span<char> shownBuffer = stackalloc char[MaxLetters], readBuffer = stackalloc char[MaxLetters], sourceBuffer = stackalloc char[MaxLetters];
            ReadOnlySpan<char> line = Letters(shown, shownBuffer), text = Letters(read, readBuffer), origin = Letters(source, sourceBuffer);
            if (line.Length < MinLetters || text.Length == 0 || origin.Length == 0) return false;
            return !Holds(origin, line) && Holds(text, line);
        }

        /// <summary>
        /// Whether <paramref name="text"/> is <paramref name="line"/> or has it inside, give or take the edits a read
        /// makes. A read is often longer than the line: the speaker's name is read before it, or more text after it.
        /// </summary>
        private static bool Holds(ReadOnlySpan<char> text, ReadOnlySpan<char> line)
        {
            float allowed = (float)((1 - MinSimilarity) * line.Length);
            if (text.Length <= line.Length) return OcrWeightedDistance.Calculate(line, text, allowed) <= allowed;
            for (int start = 0; start + line.Length <= text.Length; start++)
                if (OcrWeightedDistance.Calculate(line, text.Slice(start, line.Length), allowed) <= allowed) return true;
            return false;
        }

        /// <summary>Letters and digits in lower case, as many as fit.</summary>
        private static ReadOnlySpan<char> Letters(ReadOnlySpan<char> text, Span<char> into)
        {
            int n = 0;
            foreach (char c in text)
            {
                if (!char.IsLetterOrDigit(c)) continue;
                if (n == into.Length) break;
                into[n++] = char.ToLowerInvariant(c);
            }
            return into.Slice(0, n);
        }
    }

    /// <summary>
    /// Counts own lines read in a row: one can be a coincidence, two are the app reading itself. It keeps the read the
    /// line on screen came from, because that is what tells an own line from a line the game shows in the same words.
    /// </summary>
    public sealed class SelfReadTripwire
    {
        public const int HitsInARow = 2;
        private int _inARow;
        private string _source;

        /// <summary>
        /// The subtitle tick decided what is on screen. A new line: the read that gave it is its source, and a line
        /// nothing was read for (a prediction) has none. The same line as before keeps the source it has, whatever is
        /// read after it: were the app's own line read back and matched to the same line again, taking that read as
        /// the source would make the line "like its source" and nothing would ever count. Only a line without a
        /// source takes the first read that gives it (a predicted line that the game's text then confirms).
        /// </summary>
        public void LineDecided(string read, bool readGaveTheLine, bool lineChanged)
        {
            if (lineChanged) _source = readGaveTheLine ? read : null;
            else if (readGaveTheLine && _source == null) _source = read;
        }

        /// <summary>Takes one read. True when it is the second own line in a row (or a later one, until <see cref="Reset"/>).</summary>
        public bool Observe(ReadOnlySpan<char> read, ReadOnlySpan<char> shown)
        {
            // Nothing read counts neither way: a quiet moment between two own lines does not clear the first.
            if (read.IsWhiteSpace()) return false;
            if (!SelfRead.Looks(read, shown, _source))
            {
                _inARow = 0;
                return false;
            }
            return ++_inARow >= HitsInARow;
        }

        /// <summary>A new count; the source of the line on screen stays, as the line does.</summary>
        public void Reset() => _inARow = 0;
    }
}
