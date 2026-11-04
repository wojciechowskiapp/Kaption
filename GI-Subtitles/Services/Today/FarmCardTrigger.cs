using System;
using System.Collections.Generic;
using System.Windows;

namespace GI_Subtitles.Services.Today
{
    /// <summary>
    /// When the card shows by itself: once per server day, the first time Genshin has been in front for
    /// <see cref="SettleTime"/> (past the loading screen). Pure; the watcher in MainWindow.FarmCard.cs feeds it.
    /// </summary>
    public sealed class FarmCardTrigger
    {
        public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(8);

        private DateTime? _frontSinceUtc;

        /// <summary>
        /// Whether the watcher should look for the game at all: the card is on, Genshin is the selected game and
        /// today's card has not shown yet. Otherwise nothing can change until the next reset, so no process probe.
        /// </summary>
        public static bool ShouldWatch(bool enabled, bool genshinSelected, string lastShownDay, string todayKey) =>
            enabled && genshinSelected && !string.Equals(lastShownDay, todayKey, StringComparison.Ordinal);

        /// <summary>Forgets when the game came to the front, so a skipped stretch never counts as settle time.</summary>
        public void Reset() => _frontSinceUtc = null;

        /// <param name="lastShownDay">FarmCardLastShownDay; <paramref name="todayKey"/> is ServerDay.Key for the region.</param>
        public bool Tick(bool enabled, bool genshinInFront, string lastShownDay, string todayKey, DateTime nowUtc)
        {
            if (!genshinInFront)
            {
                _frontSinceUtc = null;
                return false;
            }
            _frontSinceUtc ??= nowUtc;
            if (!enabled || string.Equals(lastShownDay, todayKey, StringComparison.Ordinal)) return false;
            return nowUtc - _frontSinceUtc.Value >= SettleTime;
        }
    }

    /// <summary>
    /// Where the card goes, in physical pixels: the top-right corner of the game's monitor, never over the dialogue
    /// region: top-right, top-left, right a quarter down, right just above each avoided box, bottom-right. The first
    /// spot clear of every rectangle in <c>avoid</c> wins, else the top-right corner.
    /// </summary>
    public static class FarmCardPlacement
    {
        public const double Margin = 24;

        public static Point Compute(Rect workArea, Size card, IReadOnlyList<Rect> avoid)
        {
            double right = workArea.Right - card.Width - Margin, left = workArea.Left + Margin;
            double top = workArea.Top + Margin, bottom = workArea.Bottom - card.Height - Margin;
            var spots = new List<Point> { new(right, top), new(left, top), new(right, Math.Max(top, workArea.Top + workArea.Height * 0.25)) };
            foreach (var r in avoid ?? Array.Empty<Rect>())
                if (!r.IsEmpty) spots.Add(new Point(right, Math.Max(workArea.Top, r.Top - card.Height - Margin)));
            spots.Add(new Point(right, bottom));
            foreach (var p in spots)
            {
                var box = new Rect(Clamp(p.X, workArea.Left, workArea.Right - card.Width), Clamp(p.Y, workArea.Top, workArea.Bottom - card.Height), card.Width, card.Height);
                if (!Overlaps(box, avoid)) return box.TopLeft;
            }
            return new Point(Clamp(right, workArea.Left, workArea.Right - card.Width), Clamp(top, workArea.Top, workArea.Bottom - card.Height));
        }

        private static bool Overlaps(Rect box, IReadOnlyList<Rect> avoid)
        {
            foreach (var r in avoid ?? Array.Empty<Rect>())
                if (!r.IsEmpty && box.IntersectsWith(r) && Rect.Intersect(box, r) is { Width: > 0.5, Height: > 0.5 }) return true;
            return false;
        }

        private static double Clamp(double v, double min, double max) => max < min ? min : Math.Min(Math.Max(v, min), max);
    }
}
