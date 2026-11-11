using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Services.Cards;
using GI_Subtitles.Services.OCR;
using GI_Subtitles.Services.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using PaddleOCRSharp;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace GI_Test
{
    /// <summary>
    /// The build card's UID reader: synthetic game frames (a scene, HUD text, "UID 735366201" in a corner in a game-like
    /// face, Kaption's subtitle panel over it) read by the real PP-OCRv6 recognizer on the processor, plus the strict block
    /// rule and the label's misreads, the region order, the time budget (and the longer one while translating, against a
    /// reader another pass holds) and the mask over Kaption's own windows, with fakes.
    /// </summary>
    [TestClass]
    public class UidReaderTests
    {
        private static PaddleOCREngine _engine;
        private static PrivateFontCollection _fonts;

        [ClassInitialize]
        public static void LoadReader(TestContext _)
        {
            // The app's own engine settings for the recommended model, on the processor so the result does not depend on
            // a graphics card (or on the Kaption that may be running next to the tests).
            string assemblyRoot = Path.GetDirectoryName(typeof(OCRModelConfig).Assembly.Location);
            OcrModelProfile profile = OcrModelProfiles.Resolve(OcrModelProfiles.RecommendedId, "EN");
            var parameters = new OCRParameter
            {
                use_gpu = false,
                cpu_math_library_num_threads = 3,
                max_side_len = 960,
                det_db_thresh = profile.DetectionThreshold,
                det_db_box_thresh = profile.DetectionBoxThreshold,
                det_db_unclip_ratio = profile.DetectionUnclipRatio,
                rec_img_h = profile.RecognitionImageHeight,
                rec_score_thresh = UidReader.MinScore,
            };
            _engine = new PaddleOCREngine(profile.CreateModelConfig(Path.Combine(assemblyRoot, "inference")), parameters);
            _engine.WarmUp(TimeSpan.FromSeconds(60));
        }

        [ClassCleanup]
        public static void Unload()
        {
            _engine?.Dispose();
            _fonts?.Dispose();
        }

        // ── synthetic frames ──────────────────────────────────────────────

        /// <summary>
        /// A game-like face: Source Sans 3 Semibold, which ships with the app (a rounded, heavy sans like the HUD text of
        /// the three games), else Segoe UI Semibold.
        /// </summary>
        private static Font HudFont(float px)
        {
            try
            {
                if (_fonts == null)
                {
                    string ttf = Path.Combine(RenderTestKit.RepoRoot(), "GI-Subtitles", "Assets", "Fonts", "SourceSans3-Semibold.ttf");
                    var fonts = new PrivateFontCollection();
                    fonts.AddFontFile(ttf);
                    _fonts = fonts;
                }
                var family = _fonts.Families[0];
                var style = family.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
                return new Font(family, px, style, GraphicsUnit.Pixel);
            }
            catch
            {
                return new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel);
            }
        }

        /// <summary>
        /// A "screen" with the game's client area at <paramref name="client"/>: a sky-to-grass scene with soft shapes, a
        /// minimap and a few HUD numbers, and the given texts in the bottom corners, white with a dark rim as the games
        /// draw them over the scene. Sizes follow the client height (Genshin's UID line is about 1.6 % of it).
        /// </summary>
        private static Bitmap Screen(Size screen, Rectangle client, string bottomRight = null, string bottomLeft = null,
            IEnumerable<string> otherHud = null, int seed = 7)
        {
            var bmp = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.FromArgb(24, 26, 34)); // the desktop around the game window

            using (var scene = new LinearGradientBrush(client, Color.FromArgb(112, 160, 212), Color.FromArgb(70, 104, 60), 90f))
                g.FillRectangle(scene, client);
            var rnd = new Random(seed);
            g.SetClip(client);
            for (int i = 0; i < 180; i++)
            {
                int w = rnd.Next(20, 260), h = rnd.Next(12, 150);
                using var blob = new SolidBrush(Color.FromArgb(rnd.Next(40, 150), rnd.Next(20, 230), rnd.Next(40, 230), rnd.Next(30, 210)));
                g.FillEllipse(blob, client.X + rnd.Next(-40, client.Width), client.Y + rnd.Next(-20, client.Height), w, h);
            }
            // A bright patch of sky right behind the bottom-right corner: the hard case for white text.
            using (var glare = new SolidBrush(Color.FromArgb(120, 236, 240, 246)))
                g.FillEllipse(glare, client.Right - client.Width / 5, client.Bottom - client.Height / 9, client.Width / 4, client.Height / 6);
            // Minimap and HUD numbers away from the corners.
            using (var ring = new Pen(Color.FromArgb(200, 240, 230, 200), 3))
                g.DrawEllipse(ring, client.X + client.Width * 0.03f, client.Y + client.Height * 0.04f, client.Height * 0.16f, client.Height * 0.16f);
            float px = client.Height * 0.016f;
            foreach (var (text, index) in (otherHud ?? new[] { "Lv. 90", "12345", "Paimon" }).Select((t, i) => (t, i)))
                DrawHud(g, text, client.X + client.Width * 0.45f + index * client.Width * 0.06f, client.Y + client.Height * 0.88f, px, alignRight: false);
            float y = client.Bottom - client.Height * 0.032f;
            if (bottomRight != null) DrawHud(g, bottomRight, client.Right - client.Width * 0.012f, y, px, alignRight: true);
            if (bottomLeft != null) DrawHud(g, bottomLeft, client.X + client.Width * 0.012f, y, px, alignRight: false);
            g.ResetClip();
            return bmp;
        }

        private static void DrawHud(Graphics g, string text, float x, float y, float px, bool alignRight)
        {
            using var font = HudFont(px);
            SizeF size = g.MeasureString(text, font);
            float left = alignRight ? x - size.Width : x;
            using var rim = new SolidBrush(Color.FromArgb(150, 10, 12, 20));
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0) g.DrawString(text, font, rim, left + dx, y + dy);
            using var white = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
            g.DrawString(text, font, white, left, y);
        }

        /// <summary>
        /// Kaption's subtitle panel as the overlay draws it (SubtitleOverlayDefaults): a black panel at 176 of 255 with
        /// rounded corners over <paramref name="panel"/> (screen pixels) and a 22 px white line with a dark rim, centred.
        /// </summary>
        private static void DrawSubtitlePanel(Graphics g, Rectangle panel, string line)
        {
            const int r = 10;
            using (var path = new GraphicsPath())
            {
                path.AddArc(panel.X, panel.Y, 2 * r, 2 * r, 180, 90);
                path.AddArc(panel.Right - 2 * r, panel.Y, 2 * r, 2 * r, 270, 90);
                path.AddArc(panel.Right - 2 * r, panel.Bottom - 2 * r, 2 * r, 2 * r, 0, 90);
                path.AddArc(panel.X, panel.Bottom - 2 * r, 2 * r, 2 * r, 90, 90);
                path.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb(SubtitleOverlayDefaults.BackgroundOpacity, 0, 0, 0));
                g.FillPath(fill, path);
            }
            float px = SubtitleOverlayDefaults.FontSize;
            using var font = HudFont(px);
            SizeF size = g.MeasureString(line, font);
            DrawHud(g, line, panel.X + (panel.Width - size.Width) / 2, panel.Y + (panel.Height - size.Height) / 2, px, alignRight: false);
        }

        /// <summary>The capture the reader asks for: a copy of a screen rectangle, as GDI would return it; counts calls.</summary>
        private static Func<Rectangle, Bitmap> CaptureFrom(Bitmap screen, List<Rectangle> asked) => area =>
        {
            asked?.Add(area);
            var clipped = Rectangle.Intersect(area, new Rectangle(0, 0, screen.Width, screen.Height));
            var shot = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(shot);
            g.Clear(Color.Black);
            g.DrawImage(screen, new Rectangle(clipped.X - area.X, clipped.Y - area.Y, clipped.Width, clipped.Height), clipped, GraphicsUnit.Pixel);
            return shot;
        };

        private static OCRResult Recognize(Mat frame, CancellationToken token) => _engine.DetectTextFromMat(frame, token);

        /// <summary>The engine, printing what it read (text and confidence) to the test output.</summary>
        private static OCRResult RecognizeAndShow(Mat frame, CancellationToken token)
        {
            OCRResult result = Recognize(frame, token);
            foreach (var block in result?.TextBlocks ?? new List<TextBlock>())
                Console.WriteLine($"  read \"{block.Text}\" at {block.Score:0.000}");
            return result;
        }

        /// <summary>Keeps a synthetic frame next to the review renders, to see what the reader was given.</summary>
        private static void Keep(Bitmap screen, string name)
        {
            string path = Path.Combine(RenderTestKit.ReviewDir, "uid", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // Best effort, outside the UI lock: the folder is per minute, so another test process may write this file now.
            try
            {
                screen.Save(path, ImageFormat.Png);
                Console.WriteLine(path);
            }
            catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.ExternalException)
            {
                Console.WriteLine($"{path} not written: {ex.Message}");
            }
        }

        // A generous budget for the real reader: a slow CI machine must not turn a read into a timeout. The 1.5 s
        // budget itself is covered with a fake below; the timings print to the test output.
        private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(20);

        // ── the real reader on synthetic frames ───────────────────────────

        [TestMethod]
        public void ReadsTheUid_FromTheBottomRightCorner_OfAGenshinLikeFrame()
        {
            var client = new Rectangle(160, 90, 1920, 1080); // a game window that does not start at the screen's origin
            using var screen = Screen(new Size(2240, 1260), client, bottomRight: "UID 735366201");
            Keep(screen, "uid-bottom-right.png");
            var asked = new List<Rectangle>();

            var result = UidReader.Read(client, CaptureFrom(screen, asked), RecognizeAndShow, budget: TestBudget);

            Console.WriteLine($"positive: {result.Outcome} {result.Corner} in {result.Elapsed.TotalMilliseconds:0} ms");
            Assert.AreEqual("735366201", result.Uid);
            Assert.AreEqual(UidReadOutcome.Read, result.Outcome);
            Assert.AreEqual(UidCorner.BottomRight, result.Corner);
            Assert.AreEqual(1, asked.Count, "the bottom-right corner held the UID, so the left one was never captured");
            Assert.AreEqual(UidReader.Regions(client)[0].Area, asked[0]);
        }

        [TestMethod]
        public void ReturnsNull_ForAFrameWithoutAUid()
        {
            var client = new Rectangle(0, 0, 1920, 1080);
            // HUD numbers in both corners too: a level, a count, a clock. None of them is a UID.
            using var screen = Screen(new Size(1920, 1080), client, bottomRight: "Lv. 90  12:45", bottomLeft: "54321 Primogems");
            Keep(screen, "uid-none.png");
            var asked = new List<Rectangle>();

            var result = UidReader.Read(client, CaptureFrom(screen, asked), RecognizeAndShow, budget: TestBudget);

            Console.WriteLine($"negative: {result.Outcome} in {result.Elapsed.TotalMilliseconds:0} ms");
            Assert.IsNull(result.Uid);
            Assert.AreEqual(UidReadOutcome.NotFound, result.Outcome);
            Assert.AreEqual(2, asked.Count, "both corners were read");
        }

        [TestMethod]
        public void FallsBackToTheBottomLeftCorner_WhereTheMenusShowIt()
        {
            var client = new Rectangle(0, 0, 2560, 1440);
            using var screen = Screen(new Size(2560, 1440), client, bottomLeft: "UID: 809162009", seed: 11);
            Keep(screen, "uid-bottom-left.png");

            var result = UidReader.Read(client, CaptureFrom(screen, null), RecognizeAndShow, budget: TestBudget);

            Console.WriteLine($"bottom-left: {result.Outcome} {result.Corner} in {result.Elapsed.TotalMilliseconds:0} ms");
            Assert.AreEqual("809162009", result.Uid);
            Assert.AreEqual(UidCorner.BottomLeft, result.Corner);
        }

        [TestMethod]
        public void DoesNotRead_WhatKaptionsOwnWindowCovers()
        {
            var client = new Rectangle(0, 0, 1920, 1080);
            using var screen = Screen(new Size(1920, 1080), client, bottomRight: "UID 735366201");
            Assert.AreEqual("735366201", UidReader.Read(client, CaptureFrom(screen, null), Recognize, budget: TestBudget).Uid,
                "uncovered, the corner reads");
            // The Settings window over the game's bottom-right corner: masked out, so nothing there is read (here the
            // game's own UID; in the app, Kaption's own text).
            var window = new Rectangle(1300, 600, 900, 600);

            var result = UidReader.Read(client, CaptureFrom(screen, null), Recognize, exclude: new[] { window }, budget: TestBudget);

            Assert.IsNull(result.Uid);
            Assert.AreEqual(UidReadOutcome.NotFound, result.Outcome);
        }

        [TestMethod]
        public void ReadsTheUid_AboveTheSubtitlePanel_WhenThePanelOverTheCornerIsMasked()
        {
            // The subtitle panel low over the game's bottom-right corner (dragged down there, or a long line in a small
            // window) with the UID just above it, both inside the strip the reader takes. Settings hands the panel's
            // rectangle to the read (MainWindow.OverlayScreenAreas): the subtitle is blacked out, the UID is read alone.
            var client = new Rectangle(0, 0, 1920, 1080);
            var panel = Rectangle.FromLTRB(980, 1032, 1880, 1080);
            using var screen = Screen(new Size(1920, 1080), client, seed: 5);
            using (var g = Graphics.FromImage(screen))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                DrawHud(g, "UID 618285856", client.Right - client.Width * 0.012f, client.Bottom - client.Height * 0.066f,
                    client.Height * 0.016f, alignRight: true);
                DrawSubtitlePanel(g, panel, "Chodźmy najpierw do Gildii Poszukiwaczy Przygód, potem coś zjemy.");
            }
            Keep(screen, "uid-above-subtitle-panel.png");
            var corner = UidReader.Regions(client)[0].Area;
            Assert.IsTrue(corner.IntersectsWith(panel) && corner.Top < panel.Top, "the panel covers the corner's lower part");

            var seen = new List<string>();
            OCRResult Reading(Mat frame, CancellationToken token)
            {
                OCRResult read = RecognizeAndShow(frame, token);
                seen.AddRange((read?.TextBlocks ?? new List<TextBlock>()).Select(b => b.Text));
                return read;
            }

            Console.WriteLine("unmasked:");
            UidReader.Read(client, CaptureFrom(screen, null), Reading, budget: TestBudget);
            Assert.IsTrue(seen.Any(t => UidReader.ParseText(t) == null), "unmasked, the reader also gets the subtitle in the corner");

            seen.Clear();
            Console.WriteLine("the panel masked:");
            var result = UidReader.Read(client, CaptureFrom(screen, null), Reading, exclude: new[] { panel }, budget: TestBudget);

            Console.WriteLine($"masked: {result.Outcome} {result.Corner} in {result.Elapsed.TotalMilliseconds:0} ms");
            Assert.AreEqual("618285856", result.Uid);
            Assert.AreEqual(UidReadOutcome.Read, result.Outcome);
            Assert.AreEqual(UidCorner.BottomRight, result.Corner);
            Assert.IsTrue(seen.Count > 0 && seen.All(t => UidReader.ParseText(t) == "618285856"),
                "masked, nothing of the subtitle is left: the UID is the only text read");
        }

        // ── the rules, with fakes ─────────────────────────────────────────

        private static TextBlock Block(string text, float score = 0.95f) => new TextBlock { Text = text, Score = score };

        [TestMethod]
        [DataRow("UID 735366201", "735366201")]
        [DataRow("UID: 735366201", "735366201")]
        [DataRow("UID：735366201", "735366201")]
        [DataRow("UID735366201", "735366201")]
        [DataRow("735366201", "735366201")]
        [DataRow("  UID 1300025292 ", "1300025292")]
        [DataRow("ＵＩＤ １８１２３４５６７８", "1812345678")]
        // The reader's usual misreads of the label: a one or a small L for the I, a V for the U.
        [DataRow("U1D 735366201", "735366201")]
        [DataRow("UlD 735366201", "735366201")]
        [DataRow("VID 735366201", "735366201")]
        [DataRow("V1D: 809162009", "809162009")]
        [DataRow("VlD：1300025292", "1300025292")]
        [DataRow("U1D618285856", "618285856")]
        [DataRow("UlD :618285856", "618285856")]
        [DataRow("ＶＩＤ ６１８２８５８５６", "618285856")]
        public void AWholeBlockThatIsAUid_IsRead(string text, string uid)
        {
            Assert.AreEqual(uid, UidReader.ParseText(text));
            Assert.AreEqual(uid, UidReader.Parse(new[] { Block(text) }));
        }

        [TestMethod]
        [DataRow("UID 73536620")]
        [DataRow("UID 73536620123")]
        [DataRow("UID 035366201")]
        [DataRow("UID 735 366 201")]
        [DataRow("UID 735366201.")]
        [DataRow("Lv. 90 735366201")]
        [DataRow("uid 735366201")]
        [DataRow("u1d 735366201")]
        [DataRow("UD 735366201")]
        [DataRow("UIID 735366201")]
        [DataRow("WID 735366201")]
        [DataRow("VLD 735366201")]
        [DataRow("U|D 735366201")]
        [DataRow("U1 D 735366201")]
        [DataRow("UID UID 735366201")]
        // The digits stay strict whatever the label: 9 or 10, no leading zero, nothing after them.
        [DataRow("U1D 035366201")]
        [DataRow("VID 73536620")]
        [DataRow("UlD 73536620123")]
        [DataRow("VID 735 366 201")]
        [DataRow("U1D 735366201.")]
        [DataRow("UID ٧٣٥٣٦٦٢٠١")]
        [DataRow("")]
        [DataRow((string)null)]
        public void AnythingElse_IsNotAUid(string text)
        {
            Assert.IsNull(UidReader.ParseText(text));
        }

        [TestMethod]
        public void OnlyBlocksReadAboveTheDialogueFloor_Count()
        {
            Assert.AreEqual(0.5f, UidReader.MinScore, "the dialogue path's rec_score_thresh");
            Assert.IsNull(UidReader.Parse(new[] { Block("UID 735366201", 0.5f) }), "at the floor is not above it");
            Assert.IsNull(UidReader.Parse(new[] { Block("UID 735366201", 0.31f) }));
            Assert.IsNull(UidReader.Parse(new[] { Block("UID 735366201", float.NaN) }));
            Assert.AreEqual("735366201", UidReader.Parse(new[] { Block("UID 735366201", 0.51f) }));
            Assert.AreEqual("618285856", UidReader.Parse(new[] { Block("Lv. 90"), Block("UID 618285856"), Block("UID 735366201") }),
                "the first UID in reading order");
            Assert.IsNull(UidReader.Parse(null));
        }

        [TestMethod]
        public void Regions_AreTheBottomCorners_24By7PercentOfTheClientArea()
        {
            var regions = UidReader.Regions(new Rectangle(100, 50, 1920, 1080));
            Assert.AreEqual(2, regions.Count);
            Assert.AreEqual((UidCorner.BottomRight, new Rectangle(1559, 1054, 461, 76)), regions[0], "bottom-right first");
            Assert.AreEqual((UidCorner.BottomLeft, new Rectangle(100, 1054, 461, 76)), regions[1]);
            Assert.AreEqual(TimeSpan.FromSeconds(1.5), UidReader.Budget);
        }

        [TestMethod]
        public void StopsWhenTheBudgetRunsOut()
        {
            var client = new Rectangle(0, 0, 1920, 1080);
            using var screen = new Bitmap(1920, 1080, PixelFormat.Format32bppArgb);
            var asked = new List<Rectangle>();
            // A reader that never answers on its own: it waits until the reader's deadline cancels it.
            OCRResult Hang(Mat frame, CancellationToken token)
            {
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                token.ThrowIfCancellationRequested();
                return new OCRResult();
            }
            var clock = Stopwatch.StartNew();

            var result = UidReader.Read(client, CaptureFrom(screen, asked), Hang, budget: TimeSpan.FromMilliseconds(300));

            Assert.AreEqual(UidReadOutcome.TimedOut, result.Outcome);
            Assert.IsNull(result.Uid);
            Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(3), $"stopped after {clock.ElapsedMilliseconds} ms");
            Assert.AreEqual(1, asked.Count, "no second corner once the time is up");
        }

        [TestMethod]
        public void WhileTranslating_TheReadOutwaitsTheDialoguePassThatHoldsTheReader()
        {
            Assert.AreEqual(TimeSpan.FromSeconds(1.5), UidReader.BudgetFor(translating: false), "idle, the budget stays 1.5 s");
            Assert.AreEqual(UidReader.Budget, UidReader.BudgetFor(translating: false));
            Assert.AreEqual(TimeSpan.FromSeconds(3), UidReader.BudgetFor(translating: true));
            Assert.AreEqual(UidReader.TranslatingBudget, UidReader.BudgetFor(translating: true));

            var client = new Rectangle(0, 0, 1920, 1080);
            using var screen = new Bitmap(1920, 1080, PixelFormat.Format32bppArgb);
            using var gate = new SemaphoreSlim(1, 1);
            // The engine as PaddleOCREngine runs it: one read at a time behind a gate, waited for with the caller's token.
            OCRResult Engine(Mat frame, CancellationToken token)
            {
                gate.Wait(token);
                try
                {
                    Thread.Sleep(60); // one corner's read
                    return new OCRResult { TextBlocks = new List<TextBlock> { Block("UID 618285856") } };
                }
                finally
                {
                    gate.Release();
                }
            }
            // The dialogue pass that was under way when the click came: it holds the reader for 2.2 s (a slow processor).
            // The capture loop starts no new pass while Settings is in front, so this one pass is all the read waits for.
            var hold = TimeSpan.FromMilliseconds(2200);
            Task HoldTheReader()
            {
                gate.Wait();
                return Task.Run(async () =>
                {
                    await Task.Delay(hold);
                    gate.Release();
                });
            }

            var pass = HoldTheReader();
            var translating = UidReader.Read(client, CaptureFrom(screen, null), Engine, budget: UidReader.BudgetFor(translating: true));
            pass.Wait();
            Console.WriteLine($"translating: {translating.Outcome} in {translating.Elapsed.TotalMilliseconds:0} ms");
            Assert.AreEqual(UidReadOutcome.Read, translating.Outcome, "the raised budget outlasts the pass");
            Assert.AreEqual("618285856", translating.Uid);
            Assert.IsTrue(translating.Elapsed >= hold - TimeSpan.FromMilliseconds(100), "the read waited for the pass");
            Assert.IsTrue(translating.Elapsed < UidReader.TranslatingBudget, $"and ended inside the budget ({translating.Elapsed.TotalMilliseconds:0} ms)");

            // The same pass against the idle budget: the read gives up at 1.5 s, which is why translating raises it.
            pass = HoldTheReader();
            var idle = UidReader.Read(client, CaptureFrom(screen, null), Engine, budget: UidReader.BudgetFor(translating: false));
            pass.Wait();
            Console.WriteLine($"idle budget: {idle.Outcome} in {idle.Elapsed.TotalMilliseconds:0} ms");
            Assert.AreEqual(UidReadOutcome.TimedOut, idle.Outcome);
            Assert.IsNull(idle.Uid);
            Assert.IsTrue(idle.Elapsed < hold, $"it stopped at its budget, before the pass ended ({idle.Elapsed.TotalMilliseconds:0} ms)");
        }

        [TestMethod]
        [DataRow("Genshin", false)]
        [DataRow("StarRail", true)]
        [DataRow("ZZZ", true)]
        [DataRow("Zenless", true)]
        [DataRow("WutheringWaves", false)]
        [DataRow((string)null, false)]
        public void StarRailAndZzz_ShowTheUidOnlyInTheirMenus(string game, bool menuOnly)
        {
            Assert.AreEqual(menuOnly, UidReader.ShowsUidOnlyInMenus(game));
        }

        [TestMethod]
        public void AFailedCorner_StillLetsTheOtherOneRead()
        {
            var client = new Rectangle(0, 0, 1920, 1080);
            using var screen = new Bitmap(1920, 1080, PixelFormat.Format32bppArgb);
            int calls = 0;
            OCRResult Flaky(Mat frame, CancellationToken token)
            {
                if (calls++ == 0) throw new InvalidOperationException("reader busy");
                return new OCRResult { TextBlocks = new List<TextBlock> { Block("UID 735366201") } };
            }

            var result = UidReader.Read(client, CaptureFrom(screen, null), Flaky);

            Assert.AreEqual("735366201", result.Uid);
            Assert.AreEqual(UidCorner.BottomLeft, result.Corner);
        }

        [TestMethod]
        public void NoWindowOrNoReader_IsNoUid()
        {
            var empty = UidReader.Read(Rectangle.Empty, _ => throw new AssertFailedException("nothing to capture"), (m, t) => null);
            Assert.AreEqual(UidReadOutcome.NoGameWindow, empty.Outcome);
            var noReader = UidReader.ReadFromGame("Genshin", engine: null);
            Assert.AreEqual(UidReadOutcome.NoReader, noReader.Outcome);
            Assert.IsNull(noReader.Uid);
        }
    }
}
