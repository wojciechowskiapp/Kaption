using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using GI_Subtitles.Services.Capture;
using GI_Subtitles.Services.Detection;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using PaddleOCRSharp;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Cards
{
    /// <summary>Where on the game screen a UID was read.</summary>
    public enum UidCorner { None, BottomRight, BottomLeft }

    /// <summary>How a read ended.</summary>
    public enum UidReadOutcome
    {
        /// <summary>A UID was read.</summary>
        Read,
        /// <summary>Both corners were read and neither held a UID.</summary>
        NotFound,
        /// <summary>The time budget ran out first.</summary>
        TimedOut,
        /// <summary>The game's window is not on screen: not running, minimised, or another game is selected.</summary>
        NoGameWindow,
        /// <summary>The text reader is not ready (still loading, or it failed to start).</summary>
        NoReader,
    }

    /// <summary>One read: the UID (null when there is none), how it ended, the corner it came from and how long it took.</summary>
    public readonly record struct UidReadResult(string Uid, UidReadOutcome Outcome, UidCorner Corner, TimeSpan Elapsed);

    /// <summary>
    /// Reads the player's UID off the game's own screen for the build card. Genshin prints "UID 7xxxxxxxx" in the
    /// bottom-right corner the whole time; Honkai: Star Rail and Zenless Zone Zero show it in their menus. One small
    /// region at a time goes through the app's existing capture and text reader: the bottom-right 24 % x 7 % of the
    /// game's client area first, then the same strip on the left. Only a text block that is a UID as a whole counts, at
    /// the dialogue path's confidence floor, and the read stops after <see cref="Budget"/> (<see cref="TranslatingBudget"/>
    /// while Kaption translates). The UID itself is never logged: it identifies a player.
    /// </summary>
    public static class UidReader
    {
        public const double RegionWidthFraction = 0.24;
        public const double RegionHeightFraction = 0.07;

        /// <summary>
        /// The dialogue path's recognition floor (<c>rec_score_thresh</c> in SettingsWindow.LoadEngine). The engine
        /// already drops blocks under its own floor; this keeps the rule for a reader built with a lower one.
        /// </summary>
        public const float MinScore = 0.5f;

        /// <summary>How long the whole read may take, from the click to the browser.</summary>
        public static readonly TimeSpan Budget = TimeSpan.FromSeconds(1.5);

        /// <summary>
        /// The budget while Kaption translates. The text reader runs one read at a time (PaddleOCREngine's gate), and the
        /// dialogue pass that was under way when the click brought Settings to the front keeps it until that pass ends:
        /// up to a second or two on the processor. The capture loop starts no new pass while Kaption's own window is in
        /// front, so the read only has to outwait that one; pausing the loop would change nothing.
        /// </summary>
        public static readonly TimeSpan TranslatingBudget = TimeSpan.FromSeconds(3);

        /// <summary>The budget for one read: <see cref="TranslatingBudget"/> while translating, else <see cref="Budget"/>.</summary>
        public static TimeSpan BudgetFor(bool translating) => translating ? TranslatingBudget : Budget;

        /// <summary>
        /// True for the games that show the UID only in their menus, Honkai: Star Rail and Zenless Zone Zero: a read finds
        /// it while the game's menu (Esc) is open, and the entry's line says so.
        /// </summary>
        public static bool ShowsUidOnlyInMenus(string gameId) => BuildCardLink.CardGame(gameId) is "starrail" or "zzz";

        // A whole block: "UID 735366201", "UID: 735366201", "UID：735366201", or the digits alone when the detector cut
        // the label off. The label may come back as the reader's usual misreads of it: "U1D" and "UlD" (a one or a small
        // L for the I) and "VID" (a V for the U). The digits stay strict: 9 or 10 ASCII digits with no leading zero, which
        // the site would refuse anyway. Text is NFKC-folded first, so full-width digits and letters read as ASCII.
        private static readonly Regex UidBlock = new Regex(@"^(?:[UV][I1l]D\s*[:：]?\s*)?([1-9][0-9]{8,9})$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>The regions to read, in order: the bottom-right corner, then the bottom-left one.</summary>
        public static IReadOnlyList<(UidCorner Corner, Rectangle Area)> Regions(Rectangle client)
        {
            int w = Math.Max(1, (int)Math.Round(client.Width * RegionWidthFraction));
            int h = Math.Max(1, (int)Math.Round(client.Height * RegionHeightFraction));
            int y = client.Bottom - h;
            return new[]
            {
                (UidCorner.BottomRight, new Rectangle(client.Right - w, y, w, h)),
                (UidCorner.BottomLeft, new Rectangle(client.Left, y, w, h)),
            };
        }

        /// <summary>The UID a text block is as a whole, or null.</summary>
        public static string ParseText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = UidBlock.Match(text.Normalize(NormalizationForm.FormKC).Trim());
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>The first block, in reading order, that is a UID and was read above <paramref name="minScore"/>.</summary>
        public static string Parse(IEnumerable<TextBlock> blocks, float minScore = MinScore)
        {
            if (blocks == null) return null;
            foreach (var block in blocks)
            {
                if (block == null || !(block.Score > minScore)) continue;
                string uid = ParseText(block.Text);
                if (uid != null) return uid;
            }
            return null;
        }

        /// <summary>
        /// Reads the corners of <paramref name="client"/> (the game's client area in screen pixels) in order until one
        /// holds a UID or the budget runs out.
        /// </summary>
        /// <param name="capture">Returns a new bitmap of a screen rectangle (the caller disposes it), or null.</param>
        /// <param name="recognize">The text reader; it should stop when the token is cancelled.</param>
        /// <param name="exclude">
        /// Screen rectangles blacked out before reading: Kaption's own windows over the game (Settings, the subtitle panel,
        /// the Ctrl+Q popup).
        /// </param>
        public static UidReadResult Read(Rectangle client, Func<Rectangle, Bitmap> capture,
            Func<Mat, CancellationToken, OCRResult> recognize, IReadOnlyList<Rectangle> exclude = null,
            TimeSpan? budget = null, CancellationToken cancel = default)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            if (recognize == null) throw new ArgumentNullException(nameof(recognize));
            var clock = Stopwatch.StartNew();
            if (client.Width <= 0 || client.Height <= 0)
                return new UidReadResult(null, UidReadOutcome.NoGameWindow, UidCorner.None, clock.Elapsed);

            TimeSpan limit = budget ?? Budget;
            bool timedOut = false;
            foreach (var (corner, area) in Regions(client))
            {
                TimeSpan left = limit - clock.Elapsed;
                if (left <= TimeSpan.Zero || cancel.IsCancellationRequested)
                {
                    timedOut = true;
                    break;
                }
                Bitmap shot = null;
                Mat frame = null;
                try
                {
                    shot = capture(area);
                    if (shot == null) continue;
                    Mask(shot, area, exclude);
                    frame = BitmapConverter.ToMat(shot);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    deadline.CancelAfter(left);
                    OCRResult text = recognize(frame, deadline.Token);
                    string uid = Parse(text?.TextBlocks);
                    if (uid != null) return new UidReadResult(uid, UidReadOutcome.Read, corner, clock.Elapsed);
                }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    break;
                }
                catch (Exception ex)
                {
                    // A capture or a read that failed in one corner: the other one still gets its turn.
                    Logger.Log.Warn($"UID read: the {corner} region failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    frame?.Dispose();
                    shot?.Dispose();
                }
            }
            return new UidReadResult(null, timedOut ? UidReadOutcome.TimedOut : UidReadOutcome.NotFound, UidCorner.None, clock.Elapsed);
        }

        /// <summary>
        /// The app's path: finds the selected game's window by its process (never by a window title, which a browser tab
        /// can share), captures the corners with GDI and reads them with the shared engine. Call off the UI thread.
        /// </summary>
        /// <param name="budget">How long the read may take (<see cref="BudgetFor"/>); <see cref="Budget"/> when null.</param>
        public static UidReadResult ReadFromGame(string gameId, PaddleOCREngine engine, IReadOnlyList<Rectangle> exclude = null,
            TimeSpan? budget = null, CancellationToken cancel = default)
        {
            var clock = Stopwatch.StartNew();
            if (engine == null) return new UidReadResult(null, UidReadOutcome.NoReader, UidCorner.None, clock.Elapsed);

            var profile = GameRegionProfile.Get(gameId);
            GameWindowDetector.WindowInfo? window = null;
            try
            {
                window = GameWindowDetector.FindGameWindow(new GameRegionProfile
                {
                    GameId = profile.GameId,
                    DisplayName = profile.DisplayName,
                    ProcessNames = profile.ProcessNames ?? Array.Empty<string>(),
                    // No title fallback; a generic executable still needs its titles to tell the game apart.
                    WindowTitles = profile.RequiresWindowTitle ? profile.WindowTitles ?? Array.Empty<string>() : Array.Empty<string>(),
                    RequiresWindowTitle = profile.RequiresWindowTitle,
                });
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"UID read: looking for the game window failed: {ex.Message}");
            }
            if (window is not GameWindowDetector.WindowInfo w)
                return new UidReadResult(null, UidReadOutcome.NoGameWindow, UidCorner.None, clock.Elapsed);

            TimeSpan left = (budget ?? Budget) - clock.Elapsed;
            if (left <= TimeSpan.Zero) return new UidReadResult(null, UidReadOutcome.TimedOut, UidCorner.None, clock.Elapsed);
            using var gdi = new GdiScreenCapture();
            var result = Read(new Rectangle(w.GameX, w.GameY, w.GameW, w.GameH),
                area => gdi.CaptureRegion(area.X, area.Y, area.Width, area.Height),
                (frame, token) => engine.DetectTextFromMat(frame, token),
                exclude, left, cancel);
            return result with { Elapsed = clock.Elapsed };
        }

        /// <summary>Blacks out the parts of <paramref name="area"/> (screen pixels) that an excluded rectangle covers.</summary>
        internal static void Mask(Bitmap shot, Rectangle area, IReadOnlyList<Rectangle> exclude)
        {
            if (shot == null || exclude == null || exclude.Count == 0) return;
            Graphics g = null;
            try
            {
                foreach (var e in exclude)
                {
                    var hit = Rectangle.Intersect(area, e);
                    if (hit.Width <= 0 || hit.Height <= 0) continue;
                    g ??= Graphics.FromImage(shot);
                    g.FillRectangle(Brushes.Black, hit.X - area.X, hit.Y - area.Y, hit.Width, hit.Height);
                }
            }
            finally
            {
                g?.Dispose();
            }
        }
    }
}
