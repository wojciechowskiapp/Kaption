using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Stamina;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using PaddleOCRSharp;
using Size = System.Drawing.Size;

namespace GI_Test
{
    /// <summary>
    /// The stamina reader on composed frames: the app's own icon for each game and a number drawn in a game-like face,
    /// in a dark pill over a busy scene, laid out as the real frames were measured (digit size, gap, where the "+"
    /// button sits). Read through the shipping path with the real recognizer on the processor. Real footage is not in
    /// the repository; <see cref="StaminaFootageTests"/> runs the same path over it when a folder is given.
    /// </summary>
    [TestClass]
    public class StaminaScreenReaderTests
    {
        private static PaddleOCREngine _engine;
        private static PrivateFontCollection _fonts;
        private static int _dumped;

        /// <summary>How a game draws its pill at 1920x1080, from the measured frames (pixels).</summary>
        private sealed record Pill(double DigitHeight, double Gap, double? RightEdge, double PlusGap, int Gray, bool Spaced);

        private static readonly Dictionary<string, Pill> Pills = new()
        {
            // Genshin: 15 px digits 10 px right of the icon, the "+" about 26 px after them; dialogs write "93 / 200".
            ["genshin"] = new Pill(15, 10, null, 26, 236, false),
            // Star Rail: grey 16 px digits 11 px right of the icon, "180 / 240" with thin spaces, the "+" 22 px after.
            ["starrail"] = new Pill(16, 11, null, 22, 172, true),
            // Wuthering Waves: 23 px digits that end 140 px right of the icon whatever their length, the "+" 22 px after.
            ["wuwa"] = new Pill(23, 25, 140, 22, 238, false),
        };

        [ClassInitialize]
        public static void LoadReader(TestContext _) => _engine = StaminaTestKit.ProcessorEngine();

        [ClassCleanup]
        public static void Unload()
        {
            _engine?.Dispose();
            _fonts?.Dispose();
        }

        private static GameManifest Game(string id) => GameRegistry.Current.FindByWireId(id);

        private static Font HudFont(float px)
        {
            if (_fonts == null)
            {
                var fonts = new PrivateFontCollection();
                fonts.AddFontFile(Path.Combine(RenderTestKit.RepoRoot(), "GI-Subtitles", "Assets", "Fonts", "SourceSans3-Semibold.ttf"));
                _fonts = fonts;
            }
            var family = _fonts.Families[0];
            return new Font(family, px, family.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold, GraphicsUnit.Pixel);
        }

        private static Bitmap Icon(GameManifest game)
        {
            using Stream s = typeof(StaminaLocator).Assembly.GetManifestResourceStream(StaminaLocator.ResourcePrefix + game.Desktop.StaminaScreen.Icon + ".png");
            Assert.IsNotNull(s, $"no embedded icon for {game.Id}");
            using var loaded = new Bitmap(s);
            return new Bitmap(loaded);
        }

        /// <summary>
        /// A game picture of <paramref name="client"/> size: a scene of soft shapes, and (when <paramref name="text"/>
        /// is given) the stamina pill in the top band at <paramref name="xFraction"/> of the width, drawn
        /// <paramref name="iconScale"/> times the template's size (Genshin's dialogs draw it at 0.86 of the map's).
        /// </summary>
        internal static Bitmap Frame(Size client, GameManifest game, string text, double xFraction = 0.78, double iconScale = 1.0,
            int seed = 3, string iconOf = null, bool plus = true, double tracking = 0)
        {
            double ui = StaminaLocator.InterfaceScale(client);
            Pill pill = Pills[game.Id];
            var bmp = new Bitmap(client.Width, client.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var all = new Rectangle(0, 0, client.Width, client.Height);
            using (var scene = new LinearGradientBrush(all, Color.FromArgb(96, 150, 204), Color.FromArgb(196, 172, 88), 70f))
                g.FillRectangle(scene, all);
            var rnd = new Random(seed);
            for (int i = 0; i < 220; i++)
            {
                using var blob = new SolidBrush(Color.FromArgb(rnd.Next(60, 200), rnd.Next(20, 250), rnd.Next(40, 250), rnd.Next(30, 250)));
                g.FillEllipse(blob, rnd.Next(-60, client.Width), rnd.Next(-30, client.Height), rnd.Next(20, 300), rnd.Next(12, 170));
            }
            // HUD text elsewhere in the band, so the reader is not looking at the only bright thing there.
            using (Font hud = HudFont((float)(22 * ui)))
            {
                g.DrawString("Lv. 90", hud, Brushes.White, (float)(client.Width * 0.50), (float)(client.Height * 0.05));
                g.DrawString("04:38", hud, Brushes.White, (float)(client.Width * 0.60), (float)(client.Height * 0.02));
            }
            if (text == null) return bmp;

            using Bitmap icon = Icon(iconOf == null ? game : Game(iconOf));
            float iconW = (float)(icon.Width * iconScale * ui), iconH = (float)(icon.Height * iconScale * ui);
            float x = (float)(client.Width * xFraction), y = (float)(client.Height * 0.058);
            Color pillColor = icon.GetPixel(icon.Width - 1, icon.Height / 2);
            float pillH = iconH * 1.25f;
            using (var back = new SolidBrush(pillColor))
                g.FillRectangle(back, x - iconH * 0.3f, y - (pillH - iconH) / 2, (float)(iconW + 330 * ui), pillH);
            g.DrawImage(icon, x, y, iconW, iconH);

            using Font font = HudFont((float)(pill.DigitHeight * ui / 0.66));
            using var ink = new SolidBrush(Color.FromArgb(pill.Gray, pill.Gray, pill.Gray));
            string shown = pill.Spaced ? text.Replace("/", " / ") : text;
            using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            // Drawn glyph by glyph so a test can pull the digits together until they touch (tracking, in 1080p pixels).
            float[] advances = shown.Select(ch => ch == ' ' ? font.Size * 0.2f : g.MeasureString(ch.ToString(), font, int.MaxValue, format).Width + (float)(tracking * ui)).ToArray();
            float width = advances.Sum() - (float)(tracking * ui);
            float textX = pill.RightEdge is double end ? x + iconW + (float)(end * ui) - width : x + iconW + (float)(pill.Gap * ui);
            float textY = y + iconH / 2 - font.GetHeight(g) / 2;
            float penX = textX;
            for (int i = 0; i < shown.Length; i++)
            {
                if (shown[i] != ' ') g.DrawString(shown[i].ToString(), font, ink, penX, textY, format);
                penX += advances[i];
            }
            if (plus)
            {
                // The "+" button after the number: as bright as the digits, one wide gap away.
                float side = (float)(pill.DigitHeight * ui), px = textX + width + (float)(pill.PlusGap * ui), py = y + iconH / 2 - side / 2;
                using var pen = new Pen(ink, Math.Max(2f, side / 6));
                g.DrawLine(pen, px, py + side / 2, px + side, py + side / 2);
                g.DrawLine(pen, px + side / 2, py, px + side / 2, py + side);
            }
            return bmp;
        }

        internal static StaminaSample Read(GameManifest game, Bitmap frame, StaminaScreenReader reader = null, StaminaLineReader line = null)
        {
            StaminaScreenReader own = reader ?? StaminaScreenReader.ForGame(game);
            try
            {
                var client = new Rectangle(0, 0, frame.Width, frame.Height);
                Rectangle area = StaminaLocator.SearchArea(client, game.Desktop.StaminaScreen);
                using Mat mat = BitmapConverter.ToMat(frame);
                using var strip = new Mat(mat, new Rect(area.X, area.Y, area.Width, area.Height));
                StaminaSample sample = own.Read(strip, client.Size, line ?? ((Mat l, out string text, out float score) => _engine.TryRecognizeLine(l, out text, out score)));
                // KAPTION_STAMINA_DUMP=<folder> keeps every strip the tests composed, to look at what the reader was given.
                string dump = Environment.GetEnvironmentVariable("KAPTION_STAMINA_DUMP");
                if (!string.IsNullOrWhiteSpace(dump))
                {
                    Directory.CreateDirectory(dump);
                    Cv2.ImWrite(Path.Combine(dump, $"{game.Id}-{frame.Width}x{frame.Height}-{sample.Outcome}-{sample.Value}-{System.Threading.Interlocked.Increment(ref _dumped):000}.png"), strip);
                }
                return sample;
            }
            finally
            {
                if (reader == null) own.Dispose();
            }
        }

        [TestMethod]
        public void MeasuredGames_HaveASpecAndAnIcon_AndZzzIsOff()
        {
            foreach (string id in new[] { "genshin", "starrail", "wuwa" })
            {
                GameManifest game = Game(id);
                Assert.IsTrue(StaminaCardModel.Supports(game), id);
                using StaminaScreenReader reader = StaminaScreenReader.ForGame(game);
                Assert.IsNotNull(reader, $"{id}: the spec names an icon the build does not carry");
                GameStaminaScreen spec = game.Desktop.StaminaScreen;
                Assert.IsTrue(spec.Confirmations >= 2 && spec.RefillConfirmations >= spec.Confirmations, id);
                Assert.IsTrue(spec.SampleSeconds >= 1 && spec.Scales.Length > 0 && spec.MinIconScore > 0.5 && spec.MinTextScore > 0.5, id);
            }
            // Zenless Zone Zero is not measured: no spec, so no reader and no card.
            Assert.IsNull(Game("zzz").Desktop.StaminaScreen);
            Assert.IsNull(StaminaScreenReader.ForGame(Game("zzz")));
            Assert.IsFalse(StaminaCardModel.Supports(Game("zzz")));
            Assert.IsNotNull(Game("zzz").Stamina, "the pool's numbers are still there for the day it is measured");
        }

        [TestMethod]
        public void ReadsTheNumber_AtEveryWindowSize_ForEveryMeasuredGame()
        {
            var sizes = new[] { new Size(1920, 1080), new Size(2560, 1440), new Size(3840, 2160), new Size(1600, 900), new Size(1920, 1200), new Size(3440, 1440) };
            var failures = new List<string>();
            int reads = 0;
            foreach (string id in new[] { "genshin", "starrail", "wuwa" })
            {
                GameManifest game = Game(id);
                int max = game.Stamina.Max;
                int[] values = { 0, 7, 63, 121, max - 1, max };
                double[] iconScales = id == "genshin" ? new[] { 1.0, 0.86 } : new[] { 1.0 };
                int n = 0;
                foreach (Size size in sizes)
                foreach (double iconScale in iconScales)
                {
                    int value = values[n % values.Length];
                    double x = 0.56 + 0.07 * (n % 5);
                    using Bitmap frame = Frame(size, game, $"{value}/{max}", x, iconScale, seed: 11 + n);
                    StaminaSample sample = Read(game, frame);
                    reads++;
                    if (sample.Outcome != StaminaSampleOutcome.Read || sample.Value != value)
                        failures.Add($"{id} {size.Width}x{size.Height} icon x{iconScale}: want {value}, got {sample.Outcome} {sample.Value} (icon {sample.IconScore:0.00}, text \"{sample.Text}\" {sample.TextScore:0.00})");
                    n++;
                }
            }
            Assert.AreEqual(0, failures.Count, $"{failures.Count} of {reads} wrong:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }

        [TestMethod]
        public void AWindowJustOffTheReferenceSize_IsReadInItsOwnPixels()
        {
            // Within 2 % of 1080 high the strip is searched unscaled; what is found must then not be scaled back, or the
            // number's cut starts in the wrong place (a pill at x 1500 would be looked for 28 px to the left).
            foreach (Size size in new[] { new Size(1920, 1060), new Size(1890, 1080), new Size(1950, 1100) })
            foreach (string id in new[] { "genshin", "starrail", "wuwa" })
            {
                GameManifest game = Game(id);
                using Bitmap frame = Frame(size, game, $"87/{game.Stamina.Max}", xFraction: 0.82, seed: 31);
                StaminaSample sample = Read(game, frame);
                Assert.AreEqual((StaminaSampleOutcome.Read, 87), (sample.Outcome, sample.Value), $"{id} {size.Width}x{size.Height}: \"{sample.Text}\"");

                using StaminaLocator locator = StaminaLocator.ForSpec(game.Desktop.StaminaScreen);
                var client = new Rectangle(0, 0, size.Width, size.Height);
                Rectangle area = StaminaLocator.SearchArea(client, game.Desktop.StaminaScreen);
                using Mat mat = BitmapConverter.ToMat(frame);
                using var strip = new Mat(mat, new Rect(area.X, area.Y, area.Width, area.Height));
                var icons = locator.FindIcons(strip, size);
                Assert.IsTrue(icons.Count > 0, id);
                int drawnAt = (int)(size.Width * 0.82) - area.X;
                Assert.IsTrue(Math.Abs(icons[0].Icon.X - drawnAt) <= 3, $"{id} {size.Width}x{size.Height}: icon found at {icons[0].Icon.X}, drawn at {drawnAt}");
            }
        }

        [TestMethod]
        public void ReadsAnOverflow_AndStopsBeforeThePlusButton()
        {
            // Genshin holds up to 2000 through refills: "232/200" is a value, not a misread.
            GameManifest genshin = Game("genshin");
            using (Bitmap frame = Frame(new Size(1920, 1080), genshin, "232/200"))
            {
                StaminaSample sample = Read(genshin, frame);
                Assert.AreEqual(StaminaSampleOutcome.Read, sample.Outcome, sample.Text);
                Assert.AreEqual(232, sample.Value);
                // The cut ends at the gap before "+": the recognizer never sees the button.
                Assert.AreEqual("232/200", sample.Text?.Replace(" ", ""));
            }
            foreach (string id in new[] { "starrail", "wuwa" })
            {
                GameManifest game = Game(id);
                using Bitmap frame = Frame(new Size(2560, 1440), game, $"45/{game.Stamina.Max}");
                StaminaSample sample = Read(game, frame);
                Assert.AreEqual(StaminaSampleOutcome.Read, sample.Outcome, $"{id}: {sample.Text}");
                Assert.AreEqual($"45/{game.Stamina.Max}", sample.Text?.Replace(" ", ""), id);
            }
        }

        [TestMethod]
        public void DigitsThatTouch_AreReadWhole_NeverAsASmallerNumber()
        {
            // Tight kerning joins neighbouring digits into one blob. The cut must keep such a blob: dropping the joined
            // "12" of "120/240" would leave "0/240", a valid-looking and wrong value.
            var wrong = new List<string>();
            int read = 0, total = 0;
            foreach (string id in new[] { "genshin", "starrail", "wuwa" })
            {
                GameManifest game = Game(id);
                foreach (double tracking in new[] { -1.0, -2.0, -3.0 })
                foreach (int value in new[] { 120, 111, 47 })
                {
                    using Bitmap frame = Frame(new Size(1920, 1080), game, $"{value}/{game.Stamina.Max}", tracking: tracking, seed: 21);
                    StaminaSample sample = Read(game, frame);
                    total++;
                    if (sample.Outcome == StaminaSampleOutcome.Read && sample.Value == value) read++;
                    else if (sample.Outcome == StaminaSampleOutcome.Read) wrong.Add($"{id} tracking {tracking}: {value} read as {sample.Value} (\"{sample.Text}\")");
                }
            }
            Assert.AreEqual(0, wrong.Count, string.Join(Environment.NewLine, wrong));
            // Most still read; the tightest ones may be refused, which is the safe answer.
            Assert.IsTrue(read >= total * 2 / 3, $"only {read} of {total} read");
            Console.WriteLine($"touching digits: {read} of {total} read, none wrong");
        }

        [TestMethod]
        public void NothingToRead_GivesNothing()
        {
            foreach (string id in new[] { "genshin", "starrail", "wuwa" })
            {
                GameManifest game = Game(id);
                // No pill at all, at three sizes and three scenes.
                foreach (Size size in new[] { new Size(1920, 1080), new Size(3840, 2160), new Size(1600, 900) })
                for (int seed = 0; seed < 3; seed++)
                {
                    using Bitmap frame = Frame(size, game, null, seed: 40 + seed);
                    Assert.AreEqual(StaminaSampleOutcome.NoIcon, Read(game, frame).Outcome, $"{id} {size} seed {seed}");
                }
                // Another game's icon is not this game's.
                string other = id == "genshin" ? "wuwa" : "genshin";
                using (Bitmap frame = Frame(new Size(1920, 1080), game, $"50/{game.Stamina.Max}", iconOf: other))
                    Assert.AreEqual(StaminaSampleOutcome.NoIcon, Read(game, frame).Outcome, $"{id}: {other}'s icon");
                // The right icon beside a plain count (Genshin's Fragile Resin pill looks like this): no slash, no value.
                using (Bitmap frame = Frame(new Size(1920, 1080), game, "9", plus: false))
                    Assert.AreNotEqual(StaminaSampleOutcome.Read, Read(game, frame).Outcome, $"{id}: a bare count");
                // A cap that is not the game's: an old version, another counter, a misread.
                using (Bitmap frame = Frame(new Size(1920, 1080), game, $"121/{game.Stamina.Max - 40}"))
                {
                    StaminaSample sample = Read(game, frame);
                    Assert.AreEqual(StaminaSampleOutcome.Refused, sample.Outcome, $"{id}: wrong cap, text \"{sample.Text}\"");
                }
            }
        }

        [TestMethod]
        public void AnUnchangedNumber_IsNotReadTwice_AndABusyReaderIsSkipped()
        {
            GameManifest game = Game("genshin");
            using StaminaScreenReader reader = StaminaScreenReader.ForGame(game);
            int calls = 0;
            bool free = true;
            StaminaLineReader line = (Mat l, out string text, out float score) =>
            {
                calls++;
                text = null;
                score = 0;
                return free && _engine.TryRecognizeLine(l, out text, out score);
            };
            using Bitmap first = Frame(new Size(1920, 1080), game, "121/200", seed: 5);
            using Bitmap sameNumberOtherScene = Frame(new Size(1920, 1080), game, "121/200", seed: 6);
            using Bitmap next = Frame(new Size(1920, 1080), game, "122/200", seed: 5);

            // The reader is busy with dialogue: the look is skipped, nothing is remembered.
            free = false;
            Assert.AreEqual(StaminaSampleOutcome.ReaderBusy, Read(game, first, reader, line).Outcome);
            free = true;
            StaminaSample a = Read(game, first, reader, line);
            Assert.AreEqual((StaminaSampleOutcome.Read, 121, false), (a.Outcome, a.Value, a.Reused));
            int afterFirst = calls;

            // The scene behind the pill moved, the pill did not: the same pixels, so no second recognition.
            StaminaSample b = Read(game, sameNumberOtherScene, reader, line);
            Assert.AreEqual((StaminaSampleOutcome.Read, 121, true), (b.Outcome, b.Value, b.Reused));
            Assert.AreEqual(afterFirst, calls, "an unchanged number costs no recognition");

            StaminaSample c = Read(game, next, reader, line);
            Assert.AreEqual((StaminaSampleOutcome.Read, 122, false), (c.Outcome, c.Value, c.Reused));
            Assert.AreEqual(afterFirst + 1, calls);
        }

        [TestMethod]
        public void SampleSaver_KeepsThePill_LimitsTheFolder_AndRationsMisses()
        {
            GameManifest game = Game("wuwa");
            string dir = Path.Combine(Path.GetTempPath(), "kaption-stamina-saver-" + Guid.NewGuid().ToString("N"));
            try
            {
                var saver = new StaminaSampleSaver(dir, 3, false);
                DateTimeOffset t = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
                var client = new Size(1920, 1080);
                Rectangle area = StaminaLocator.SearchArea(new Rectangle(0, 0, client.Width, client.Height), game.Desktop.StaminaScreen);
                for (int i = 0; i < 5; i++)
                {
                    using Bitmap frame = Frame(client, game, $"{100 + i}/240");
                    using Mat mat = BitmapConverter.ToMat(frame);
                    using var strip = new Mat(mat, new Rect(area.X, area.Y, area.Width, area.Height));
                    StaminaSample sample = Read(game, frame);
                    string file = saver.Save(strip, sample, t.AddSeconds(i * 3));
                    Assert.IsNotNull(file);
                    StringAssert.EndsWith(file, $"-read-{100 + i}.png");
                    using Mat saved = Cv2.ImRead(file);
                    Assert.IsTrue(saved.Width < strip.Width / 2 && saved.Height <= strip.Height, "the pill, not the whole band");
                }
                Assert.AreEqual(3, Directory.GetFiles(dir, "*.png").Length, "the oldest pictures go first");
                Assert.IsTrue(Directory.GetFiles(dir).All(f => !f.EndsWith("-read-100.png") && !f.EndsWith("-read-101.png")));

                // A look that found no icon keeps the strip, at most once a minute.
                using Bitmap empty = Frame(client, game, null);
                using Mat emptyMat = BitmapConverter.ToMat(empty);
                using var emptyStrip = new Mat(emptyMat, new Rect(area.X, area.Y, area.Width, area.Height));
                var miss = new StaminaSample(StaminaSampleOutcome.NoIcon, 0, 0, 0, default, null, false);
                Assert.IsNotNull(saver.Save(emptyStrip, miss, t.AddMinutes(5)));
                Assert.IsNull(saver.Save(emptyStrip, miss, t.AddMinutes(5).AddSeconds(30)));
                Assert.IsNotNull(saver.Save(emptyStrip, miss, t.AddMinutes(6).AddSeconds(1)));
                Assert.IsNull(saver.Save(emptyStrip, new StaminaSample(StaminaSampleOutcome.ReaderBusy, 0, 0.9, 0, default, null, false), t.AddMinutes(9)));

                // The night the clocks go back (Poland, 25 October 2026, 01:00 UTC) local time repeats an hour. The names
                // are in UTC, so the pictures taken after the change are the ones kept, not the ones deleted.
                string night = dir + "-dst";
                try
                {
                    var across = new StaminaSampleSaver(night, 2, false);
                    var before = new DateTimeOffset(2026, 10, 25, 0, 40, 0, TimeSpan.Zero);
                    string[] written = new[] { 0, 10, 30, 40 }.Select(m =>
                    {
                        using Bitmap frame = Frame(client, game, $"{60 + m}/240");
                        using Mat mat = BitmapConverter.ToMat(frame);
                        using var strip = new Mat(mat, new Rect(area.X, area.Y, area.Width, area.Height));
                        return across.Save(strip, Read(game, frame), before.AddMinutes(m));
                    }).ToArray();
                    Assert.IsTrue(written.All(f => f != null));
                    StringAssert.Contains(Path.GetFileName(written[0]), "20261025-004000-000Z-");
                    CollectionAssert.AreEquivalent(new[] { Path.GetFileName(written[2]), Path.GetFileName(written[3]) },
                        Directory.GetFiles(night, "*.png").Select(Path.GetFileName).ToArray(), "the two newest, taken after the clocks went back");
                }
                finally
                {
                    if (Directory.Exists(night)) Directory.Delete(night, true);
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        [TestCategory("Performance")]
        public void ALook_IsCheap_AtFourK()
        {
            GameManifest game = Game("genshin");
            using StaminaScreenReader reader = StaminaScreenReader.ForGame(game);
            using Bitmap frame = Frame(new Size(3840, 2160), game, null, seed: 9);
            var client = new Rectangle(0, 0, frame.Width, frame.Height);
            Rectangle area = StaminaLocator.SearchArea(client, game.Desktop.StaminaScreen);
            using Mat mat = BitmapConverter.ToMat(frame);
            using var strip = new Mat(mat, new Rect(area.X, area.Y, area.Width, area.Height));
            StaminaLineReader never = (Mat l, out string text, out float score) => throw new AssertFailedException("nothing to read in this frame");
            reader.Read(strip, client.Size, never); // first call pays OpenCV's own warm-up
            var times = new List<double>();
            for (int i = 0; i < 15; i++)
            {
                var clock = Stopwatch.StartNew();
                Assert.AreEqual(StaminaSampleOutcome.NoIcon, reader.Read(strip, client.Size, never).Outcome);
                times.Add(clock.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Console.WriteLine($"look at 3840x2160 with no icon: median {times[times.Count / 2]:0.0} ms, worst {times[^1]:0.0} ms ({area.Width}x{area.Height} strip)");
            // Generous: other agents share this machine. The look runs off the UI thread once every few seconds.
            Assert.IsTrue(times[times.Count / 2] < 250, $"median {times[times.Count / 2]:0.0} ms");
        }
    }
}
