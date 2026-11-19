using System;
using System.Collections.Generic;
using System.Drawing;

namespace GI_Subtitles.Services.Capture
{
    /// <summary>What a read check found out about the subtitle overlay and the frames the app reads.</summary>
    public enum OverlayReadState
    {
        /// <summary>Nothing was proven either way: treat the overlay as readable.</summary>
        Unknown,
        /// <summary>The reader still sees the overlay.</summary>
        InFrame,
        /// <summary>The reader does not see the overlay, and the overlay was on screen while it did not.</summary>
        OutOfFrame,
    }

    public enum StripSight
    {
        /// <summary>The reader had no frame to give.</summary>
        NoFrame,
        Seen,
        NotSeen,
    }

    /// <summary>
    /// One look at the strip's place through the reader, and which picture it was: a number that only grows, and only
    /// when the reader that made this look took a new picture of the screen.
    /// </summary>
    public readonly record struct StripLook(StripSight Sight, long Frame);

    /// <summary>What the check asks of the window that owns the overlay and the reader.</summary>
    public interface IOverlayReadHost
    {
        long NowMs { get; }
        void ShowStrip(bool shown);
        /// <summary>
        /// Keeps the overlay out of the reader's frames (true), in a way that leaves it in a plain screen copy, or lets
        /// it into every screen read again (false). False: Windows refused.
        /// </summary>
        bool SetHidden(bool hidden);
        /// <summary>The strip's place in screen pixels now; null while the overlay window is not on screen.</summary>
        Rectangle? StripArea();
        StripLook ReadThroughReader(Rectangle area);
        /// <summary>The same place through a plain screen copy, which shows what is drawn on the screen.</summary>
        StripSight ReadThroughGdi(Rectangle area);
        /// <summary>Lets a composition pass.</summary>
        void Wait();
    }

    public sealed class OverlayReadLimits
    {
        /// <summary>How long one switch may take to show in the reader's frames.</summary>
        public int StepMs { get; init; } = 1000;
        public int TotalMs { get; init; } = 3000;
        /// <summary>New frames in a row that must miss the strip.</summary>
        public int CleanFrames { get; init; } = 3;
        /// <summary>How often a moved or hidden window may start the check over.</summary>
        public int MaxRestarts { get; init; } = 2;
    }

    public sealed record OverlayReadResult(
        OverlayReadState State,
        string Reason,
        int Restarts,
        long ElapsedMs,
        long SwitchFrame,
        IReadOnlyList<long> VerdictFrames);

    /// <summary>
    /// Proves by reading, never by what an API answered, that the subtitle overlay is not in the frames the app reads:
    /// a small strip on the overlay is first seen through the reader (so the place, the monitor and the colours are
    /// right), then the overlay is taken out of the reader's frames and three new frames in a row must miss the strip,
    /// each of them while a plain screen copy shows the strip, still there. Anything short of that is not "out of the
    /// frame": a strip that both miss may be faded, covered or not drawn, and "missed" would then prove nothing.
    /// </summary>
    public static class OverlayReadCheck
    {
        public static OverlayReadResult Run(IOverlayReadHost host, OverlayReadLimits limits = null)
        {
            ArgumentNullException.ThrowIfNull(host);
            var run = new Attempt(host, limits ?? new OverlayReadLimits());
            try
            {
                for (int restarts = 0; ; restarts++)
                {
                    Verdict verdict = run.Once();
                    if (verdict.StartOver && restarts < run.Limits.MaxRestarts) continue;
                    if (verdict.StartOver)
                        verdict = Verdict.Of(OverlayReadState.Unknown, "the overlay window kept moving or was not on screen");
                    return new OverlayReadResult(verdict.State, verdict.Reason, restarts,
                        host.NowMs - run.StartedMs, verdict.SwitchFrame, verdict.Frames ?? Array.Empty<long>());
                }
            }
            finally
            {
                host.ShowStrip(false);
            }
        }

        private readonly record struct Verdict(bool StartOver, OverlayReadState State, string Reason, long SwitchFrame, long[] Frames)
        {
            public static Verdict Again => new(true, OverlayReadState.Unknown, null, 0, null);
            public static Verdict Of(OverlayReadState state, string reason) => new(false, state, reason, 0, null);
            public static Verdict Out(long switchFrame, List<long> frames) =>
                new(false, OverlayReadState.OutOfFrame, "the reader missed the strip while a plain screen copy showed it", switchFrame, frames.ToArray());
        }

        private enum Waited { Reached, StillTheOtherWay, NoNewFrame, Moved, OutOfTime, NotOnScreen }

        private sealed class Attempt
        {
            private readonly IOverlayReadHost _host;
            public OverlayReadLimits Limits { get; }
            public long StartedMs { get; }

            public Attempt(IOverlayReadHost host, OverlayReadLimits limits)
            {
                _host = host;
                Limits = limits;
                StartedMs = host.NowMs;
            }

            public Verdict Once()
            {
                _host.ShowStrip(true);
                if (!_host.SetHidden(false))
                    return Verdict.Of(OverlayReadState.Unknown, "Windows would not show the overlay to screen reads, so the strip cannot be checked");
                Rectangle? place = _host.StripArea();
                if (place == null) return Verdict.Again;
                Rectangle area = place.Value;
                var frames = new List<long>();

                // 1. Shown: the reader must see the strip, or nothing it misses later means anything.
                switch (Until(area, wantSeen: true, 1, onScreenToo: false, frames, out _))
                {
                    case Waited.Reached: break;
                    case Waited.Moved: return Verdict.Again;
                    case Waited.OutOfTime: return TookTooLong();
                    case Waited.NoNewFrame: return Verdict.Of(OverlayReadState.Unknown, "the reader gave no new frame");
                    case Waited.StillTheOtherWay:
                        // Which of the two it is tells whoever reads the log where to look.
                        return Verdict.Of(OverlayReadState.Unknown, _host.ReadThroughGdi(area) == StripSight.Seen
                            ? "the reader never saw the strip although it is on the screen: the overlay is still kept out of the reader's frames"
                            : "the reader never saw the strip and a plain screen copy does not show it either: it is not drawn there, or something covers it");
                    default: return Verdict.Of(OverlayReadState.Unknown, "the reader never saw the strip");
                }

                // 2. Out, and still there: new frames in a row must miss the strip, each while a plain screen copy shows
                // it. When that copy misses it too, the overlay is hidden from every capture, faded, covered or not
                // drawn, and frames without the strip prove nothing about the reader.
                _host.SetHidden(true);
                switch (Until(area, wantSeen: false, Limits.CleanFrames, onScreenToo: true, frames, out long switchFrame))
                {
                    case Waited.Reached: break;
                    case Waited.Moved: return Verdict.Again;
                    case Waited.OutOfTime: return TookTooLong();
                    case Waited.NoNewFrame: return Verdict.Of(OverlayReadState.Unknown, "too few new frames after the overlay was taken out");
                    case Waited.NotOnScreen:
                        return Verdict.Of(OverlayReadState.Unknown,
                            "no proof the strip was on screen while the reader missed it: a plain screen copy does not show it either");
                    default: return Verdict.Of(OverlayReadState.InFrame, "the reader still sees the strip");
                }
                return _host.StripArea() == area ? Verdict.Out(switchFrame, frames) : Verdict.Again;
            }

            private static Verdict TookTooLong() => Verdict.Of(OverlayReadState.Unknown, "the check took too long");

            /// <summary>
            /// Reads the strip's place until <paramref name="count"/> new frames in a row show it (or miss it). Only
            /// frames taken after the switch count: the first read is thrown away, because the frame it gets may have
            /// been composed before the switch, and a frame already seen is never counted twice. A new picture that
            /// could not be read starts the row again: nobody knows what it showed. With
            /// <paramref name="onScreenToo"/> a frame counts only while a plain screen copy shows the strip.
            /// </summary>
            private Waited Until(Rectangle area, bool wantSeen, int count, bool onScreenToo, List<long> frames, out long switchFrame)
            {
                frames.Clear();
                switchFrame = _host.ReadThroughReader(area).Frame;
                long last = switchFrame;
                // The newest frame that was looked at showed the other thing than the one waited for.
                bool otherWay = false;
                long stepStarted = _host.NowMs;
                while (true)
                {
                    _host.Wait();
                    long now = _host.NowMs;
                    if (now - StartedMs > Limits.TotalMs) return Waited.OutOfTime;
                    // Out of time for this switch. New frames that all agreed, but too few of them, prove as little as none.
                    if (now - stepStarted > Limits.StepMs)
                        return otherWay ? Waited.StillTheOtherWay : Waited.NoNewFrame;
                    if (_host.StripArea() != area) return Waited.Moved;

                    StripLook look = _host.ReadThroughReader(area);
                    if (look.Frame <= last) continue;
                    last = look.Frame;
                    if (look.Sight == StripSight.NoFrame)
                    {
                        frames.Clear();
                        continue;
                    }
                    otherWay = (look.Sight == StripSight.Seen) != wantSeen;
                    if (otherWay)
                    {
                        frames.Clear();
                        continue;
                    }
                    if (onScreenToo && _host.ReadThroughGdi(area) != StripSight.Seen) return Waited.NotOnScreen;
                    frames.Add(look.Frame);
                    if (frames.Count >= count) return Waited.Reached;
                }
            }
        }
    }

    /// <summary>
    /// The strip the overlay draws for the read check: 16x4 device pixels, the left half magenta and the right half
    /// cyan. Two colours side by side, so a patch of the game's picture is not taken for it.
    /// </summary>
    public static class StripCode
    {
        public const int Width = 16, Height = 4;
        /// <summary>How far a channel may be off: layered windows blend exactly, colour filters do not.</summary>
        private const int Tolerance = 48;

        /// <summary>True when tight BGRA rows of the strip's size show magenta on the left and cyan on the right.</summary>
        public static bool Matches(ReadOnlySpan<byte> bgra, int width, int height)
        {
            if (width != Width || height != Height || bgra.Length < Width * Height * 4) return false;
            // Inner pixels only: the outermost ones may be blended with what is beside the strip.
            for (int y = 1; y < Height - 1; y++)
            for (int x = 2; x < Width / 2 - 2; x++)
            {
                if (!Near(bgra, x, y, 255, 0, 255)) return false;
                if (!Near(bgra, x + Width / 2, y, 255, 255, 0)) return false;
            }
            return true;
        }

        private static bool Near(ReadOnlySpan<byte> bgra, int x, int y, int b, int g, int r)
        {
            int i = (y * Width + x) * 4;
            return Math.Abs(bgra[i] - b) <= Tolerance && Math.Abs(bgra[i + 1] - g) <= Tolerance && Math.Abs(bgra[i + 2] - r) <= Tolerance;
        }
    }
}
