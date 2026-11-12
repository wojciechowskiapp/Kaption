using System;
using System.Drawing;
using OpenCvSharp;

namespace GI_Subtitles.Services.Assist
{
    /// <summary>Reads one cut-out text line; false when the text engine is busy (the look is skipped, never queued).</summary>
    public delegate bool AssistLineReader(Mat line, out string text, out float score);

    public enum AssistOutcome
    {
        /// <summary>What the reader looks for is not on the screen: the usual case.</summary>
        NotShown,
        /// <summary>The text engine was busy with dialogue; nothing was read and nothing is concluded.</summary>
        Busy,
        /// <summary>Something was there and the reader would not vouch for it.</summary>
        Refused,
        Read,
    }

    /// <summary>Why a reader refused. A reader never guesses: each of these is "no reading".</summary>
    public enum AssistRefusal
    {
        None,
        /// <summary>The anchor is there but what should be beside it is not (a look-alike, a screen fading in).</summary>
        NothingBeside,
        /// <summary>The text is not what this reader reads, or the read was not confident.</summary>
        NotTheShape,
        /// <summary>A number outside its range.</summary>
        OutOfRange,
        /// <summary>A cap that is not the manifest's.</summary>
        WrongCap,
        /// <summary>A name outside the vocabulary.</summary>
        UnknownName,
        /// <summary>Two reads that disagree.</summary>
        Disagrees,
    }

    /// <summary>
    /// One look of one reader: a typed value, or why there is none. <see cref="At"/> is the moment of the picture,
    /// not of the read.
    /// </summary>
    public readonly record struct AssistReading(string GameId, string ReaderId, AssistOutcome Outcome, AssistRefusal Refusal, int Value,
        string Text, DateTimeOffset At)
    {
        public static AssistReading Nothing(string gameId, string readerId, AssistOutcome outcome, DateTimeOffset at,
            AssistRefusal refusal = AssistRefusal.None) => new AssistReading(gameId, readerId, outcome, refusal, 0, null, at);
    }

    /// <summary>The pixels of one copied rectangle: tight BGRA rows, valid only during the call that receives them.</summary>
    public readonly struct AssistPixels
    {
        public AssistPixels(IntPtr data, int width, int height)
        {
            Data = data;
            Width = width;
            Height = height;
        }

        public IntPtr Data { get; }
        public int Width { get; }
        public int Height { get; }
        public int Stride => Width * 4;

        public unsafe ReadOnlySpan<byte> Span => new ReadOnlySpan<byte>((void*)Data, Width * Height * 4);

        /// <summary>The same pixels as an OpenCV picture (BGRA); no copy, so it lives no longer than they do.</summary>
        public Mat AsMat() => Mat.FromPixelData(Height, Width, MatType.CV_8UC4, Data, Stride);
    }

    /// <summary>
    /// One thing the app may read off the game screen for a player who switched it on. The watch decides when a
    /// reader looks and hands it the pixels of its own area; the reader says what it saw or refuses. It never
    /// captures, never waits for the text engine and never returns a guess.
    /// </summary>
    public interface IAssistReader : IDisposable
    {
        /// <summary>The manifest's reader id ("stamina").</summary>
        string Id { get; }

        /// <summary>The name the cost line in the log uses.</summary>
        string LogName { get; }

        /// <summary>The screen it needs (an id of the manifest's screens), or null when it finds its own anchor.</summary>
        string Screen { get; }

        /// <summary>Seconds between looks.</summary>
        int PeriodSeconds { get; }

        /// <summary>
        /// True: Kaption's own windows inside the area are blacked out and the look goes on. False: an area one of
        /// them touches is skipped for that look.
        /// </summary>
        bool BlacksOutOwnWindows { get; }

        /// <summary>The rectangle to copy for a look, in screen pixels; empty when this window has none.</summary>
        Rectangle Area(Rectangle client, bool fixedPlacesAllowed);

        /// <summary>Off the UI thread, one look at a time. <paramref name="read"/> is null while there is no text engine.</summary>
        AssistReading Look(AssistPixels pixels, System.Drawing.Size client, DateTimeOffset capturedAt, AssistLineReader read);
    }
}
