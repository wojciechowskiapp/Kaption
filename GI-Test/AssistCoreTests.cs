using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Capture;
using GI_Subtitles.Services.Games;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using Size = System.Drawing.Size;

namespace GI_Test
{
    /// <summary>A frame in memory standing in for the capture backend: what the tick "already holds".</summary>
    internal sealed class MemoryCapture : IScreenCapture
    {
        public readonly int Width, Height;
        public readonly byte[] Pixels;
        public bool Holds = true;
        public FrameCopy Answer = FrameCopy.Copied;
        public int Copies, Rectangles;

        public MemoryCapture(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new byte[width * height * 4];
        }

        public bool IsAvailable => true;
        public bool HoldsFrame => Holds;
        public Bitmap CaptureRegion(int x, int y, int width, int height) => throw new NotSupportedException();
        public Bitmap CaptureRegionInto(int x, int y, int width, int height, Bitmap destination) => throw new NotSupportedException("the watch never asks for a new frame");

        public FrameCopy TryCopyFromLastFrame(ReadOnlySpan<Rectangle> regions, Span<byte> destination)
        {
            Copies++;
            if (Answer != FrameCopy.Copied) return Answer;
            int at = 0;
            foreach (Rectangle r in regions)
            {
                if (r.X < 0 || r.Y < 0 || r.Right > Width || r.Bottom > Height) return FrameCopy.OutsideOutput;
                Rectangles++;
                for (int y = 0; y < r.Height; y++)
                {
                    Pixels.AsSpan(((r.Y + y) * Width + r.X) * 4, r.Width * 4).CopyTo(destination.Slice(at));
                    at += r.Width * 4;
                }
            }
            return FrameCopy.Copied;
        }

        /// <summary>Paints a BGR picture (tight rows) at a place, scaled to a size.</summary>
        public void Paint(byte[] bgr, int w, int h, Rectangle at)
        {
            for (int y = 0; y < at.Height; y++)
            {
                for (int x = 0; x < at.Width; x++)
                {
                    int sx = Math.Min(w - 1, x * w / at.Width), sy = Math.Min(h - 1, y * h / at.Height);
                    int to = ((at.Y + y) * Width + at.X + x) * 4, from = (sy * w + sx) * 3;
                    Pixels[to] = bgr[from];
                    Pixels[to + 1] = bgr[from + 1];
                    Pixels[to + 2] = bgr[from + 2];
                    Pixels[to + 3] = 255;
                }
            }
        }

        /// <summary>A busy scene: soft noise, so nothing in it is flat and nothing is a template.</summary>
        public void Scene(int seed)
        {
            var random = new Random(seed);
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int at = (y * Width + x) * 4;
                    int wave = (int)(60 + 40 * Math.Sin(x / 37.0) + 30 * Math.Cos(y / 23.0));
                    Pixels[at] = (byte)(wave + random.Next(12));
                    Pixels[at + 1] = (byte)(wave / 2 + random.Next(12));
                    Pixels[at + 2] = (byte)(90 + random.Next(12));
                    Pixels[at + 3] = 255;
                }
            }
        }

        public void Dispose() { }
    }

    internal sealed class FakeAssistHost : IAssistHost
    {
        public MemoryCapture Frame;
        /// <summary>True: every tick holds a new frame. False: the frame stays the one it was.</summary>
        public bool NewFrame = true;
        private long _serial;
        public string Game = "TestGame";
        public Rectangle? Client;
        public List<Rectangle> Own = new List<Rectangle>();
        public AssistLineReader Line;

        public IScreenCapture Capture => Frame;
        public long FrameSerial => NewFrame ? ++_serial : _serial;
        public string SelectedGame => Game;
        public AssistLineReader LineReader => Line;
        public IReadOnlyList<Rectangle> OwnWindowAreas() => Own;

        public bool TryGetGameClient(out Rectangle client)
        {
            client = Client ?? Rectangle.Empty;
            return Client != null;
        }

        public bool Post(Action onUiThread)
        {
            onUiThread();
            return true;
        }
    }

    internal sealed class FakeReader : IAssistReader
    {
        public string Id { get; set; } = "fake";
        public string LogName => "Fake reader";
        public string Screen { get; set; }
        public int PeriodSeconds { get; set; } = 3;
        public bool BlacksOutOwnWindows { get; set; }
        public Func<Rectangle, bool, Rectangle> Where = (client, _) => new Rectangle(client.Left, client.Top, 64, 16);
        public Func<AssistPixels, AssistLineReader, AssistReading> OnLook;
        public int Looks;
        public bool Disposed;

        public Rectangle Area(Rectangle client, bool fixedPlacesAllowed) => Where(client, fixedPlacesAllowed);

        public AssistReading Look(AssistPixels pixels, Size client, DateTimeOffset capturedAt, AssistLineReader read)
        {
            Looks++;
            return OnLook != null ? OnLook(pixels, read) : new AssistReading("testgame", Id, AssistOutcome.Read, AssistRefusal.None, 7, "7", capturedAt);
        }

        public void Dispose() => Disposed = true;
    }

    internal static class AssistTestKit
    {
        public static readonly GameAssistCanvas Canvas = new GameAssistCanvas { Width = 1920, Height = 1080, Scale = GameAssistCanvas.ScaleMin };

        /// <summary>A picture of coloured blocks, tight BGR rows: unlike any smooth scene.</summary>
        public static byte[] Blocks(int w, int h, int seed)
        {
            var random = new Random(seed);
            var bgr = new byte[w * h * 3];
            var colours = new byte[64 * 3];
            random.NextBytes(colours);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int cell = (y / 6 % 8) * 8 + x / 6 % 8;
                    Array.Copy(colours, cell * 3, bgr, (y * w + x) * 3, 3);
                }
            }
            return bgr;
        }

        public static GameManifest Game(GameAssist assist) => new GameManifest
        {
            Schema = 1, Id = "testgame", GameId = "TestGame", Name = "Test Game", Status = GameManifest.StatusLive,
            Desktop = new GameDesktop { Assist = assist },
        };
    }

    [TestClass]
    public class AssistGeometryTests
    {
        [TestMethod]
        public void APlace_IsMeasuredFromAnEdge_AndScalesWithTheInterface()
        {
            var box = new GameAssistBox { Edge = "topRight", X = -300, Y = 40, W = 40, H = 32 };
            // 16:9 at the canvas size, with the window somewhere on a second monitor.
            Assert.IsTrue(AssistGeometry.TryPlace(box, AssistTestKit.Canvas, new Rectangle(2000, 100, 1920, 1080), 0, out Rectangle a));
            Assert.AreEqual(new Rectangle(2000 + 1920 - 300, 140, 40, 32), a);
            // 4K: everything twice as large, still from the right edge.
            Assert.IsTrue(AssistGeometry.TryPlace(box, AssistTestKit.Canvas, new Rectangle(0, 0, 3840, 2160), 0, out Rectangle b));
            Assert.AreEqual(new Rectangle(3840 - 600, 80, 80, 64), b);
            // 21:9: the interface keeps its 1440-high size and the piece stays at the window's own right edge,
            // outside the centred 16:9 band (measured on Wuthering Waves).
            Assert.IsTrue(AssistGeometry.TryPlace(box, AssistTestKit.Canvas, new Rectangle(0, 0, 3440, 1440), 0, out Rectangle c));
            Assert.AreEqual(new Rectangle(3440 - 400, 53, 53, 43), c);
            // Slack grows the box on every side, in canvas pixels.
            Assert.IsTrue(AssistGeometry.TryPlace(box, AssistTestKit.Canvas, new Rectangle(0, 0, 1920, 1080), 2, out Rectangle d));
            Assert.AreEqual(new Rectangle(1920 - 302, 38, 44, 36), d);
        }

        [TestMethod]
        public void APlace_IsRefused_RatherThanGuessed()
        {
            var box = new GameAssistBox { Edge = "bottomRight", X = -200, Y = -50, W = 180, H = 30 };
            var client = new Rectangle(0, 0, 1920, 1080);
            Assert.IsTrue(AssistGeometry.TryPlace(box, AssistTestKit.Canvas, client, 0, out _));
            // A scale rule this build does not know, an edge it does not know, no canvas: no place.
            Assert.IsFalse(AssistGeometry.TryPlace(box, new GameAssistCanvas { Width = 1920, Height = 1080, Scale = "diagonal" }, client, 0, out _));
            Assert.IsFalse(AssistGeometry.TryPlace(new GameAssistBox { Edge = "middle", W = 10, H = 10 }, AssistTestKit.Canvas, client, 0, out _));
            Assert.IsFalse(AssistGeometry.TryPlace(box, null, client, 0, out _));
            // A box that would leave the window.
            Assert.IsFalse(AssistGeometry.TryPlace(new GameAssistBox { Edge = "topLeft", X = -5, Y = 0, W = 20, H = 20 }, AssistTestKit.Canvas, client, 0, out _));

            Assert.IsTrue(AssistGeometry.FitsCanvas(box, AssistTestKit.Canvas));
            Assert.IsFalse(AssistGeometry.FitsCanvas(new GameAssistBox { Edge = "bottomRight", X = -100, Y = -20, W = 180, H = 30 }, AssistTestKit.Canvas));
        }

        [TestMethod]
        public void OnlyAMeasuredWindowShape_GetsFixedPlaces()
        {
            var measured = new GameAssistChecked { Version = "1.0", Aspects = new[] { "16:9", "21:9" }, Source = "test" };
            Assert.IsTrue(AssistGeometry.IsChecked(measured, new Size(1920, 1080)));
            Assert.IsTrue(AssistGeometry.IsChecked(measured, new Size(1366, 768)));
            Assert.IsTrue(AssistGeometry.IsChecked(measured, new Size(3440, 1440)), "what players call 21:9");
            Assert.IsTrue(AssistGeometry.IsChecked(measured, new Size(2560, 1080)));
            Assert.IsFalse(AssistGeometry.IsChecked(measured, new Size(1920, 1200)), "16:10 was not measured");
            Assert.IsFalse(AssistGeometry.IsChecked(measured, new Size(1280, 1024)));
            Assert.IsFalse(AssistGeometry.IsChecked(null, new Size(1920, 1080)));
            Assert.IsFalse(AssistGeometry.IsChecked(new GameAssistChecked { Aspects = new[] { "wide", "16:0" } }, new Size(1920, 1080)));
        }
    }

    [TestClass]
    public class ScreenProbeTests
    {
        private const int W = 40, H = 32;

        /// <summary>The patch the watch would copy for the template drawn <paramref name="scale"/> times larger, shifted by a few pixels.</summary>
        private static (byte[] bgra, int w, int h) Patch(byte[] template, double scale, int shiftX, int shiftY, int sceneSeed = 1)
        {
            int pw = (int)Math.Round((W + ScreenProbe.Slack * 2) * scale), ph = (int)Math.Round((H + ScreenProbe.Slack * 2) * scale);
            var frame = new MemoryCapture(pw, ph);
            frame.Scene(sceneSeed);
            if (template != null)
            {
                frame.Paint(template, W, H, new Rectangle((int)Math.Round((ScreenProbe.Slack + shiftX) * scale), (int)Math.Round((ScreenProbe.Slack + shiftY) * scale),
                    (int)Math.Round(W * scale), (int)Math.Round(H * scale)));
            }
            return (frame.Pixels, pw, ph);
        }

        [TestMethod]
        public void TheTemplateAtItsPlace_Matches_AtEveryInterfaceSize_AndAFewPixelsOff()
        {
            byte[] blocks = AssistTestKit.Blocks(W, H, 4);
            AssistTemplate template = AssistTemplate.FromBgr("blocks", blocks, W, H);
            Assert.IsNotNull(template);
            var probe = new ScreenProbe();
            foreach (double scale in new[] { 1.0, 0.75, 1.3333, 2.0 })
            {
                foreach ((int dx, int dy) in new[] { (0, 0), (2, -2), (-2, 1) })
                {
                    var (bgra, w, h) = Patch(blocks, scale, dx, dy);
                    double score = probe.Match(template, bgra, w, h);
                    Assert.IsTrue(score >= 0.9, $"scale {scale}, shift {dx},{dy}: {score:0.000}");
                }
            }
        }

        [TestMethod]
        public void AnythingElseAtThePlace_DoesNotMatch()
        {
            AssistTemplate template = AssistTemplate.FromBgr("blocks", AssistTestKit.Blocks(W, H, 4), W, H);
            var probe = new ScreenProbe();
            for (int seed = 1; seed <= 20; seed++)
            {
                var (scene, w, h) = Patch(null, 1, 0, 0, seed);
                Assert.IsTrue(probe.Match(template, scene, w, h) < 0.5, $"a plain scene, seed {seed}");
                var (other, ow, oh) = Patch(AssistTestKit.Blocks(W, H, 100 + seed), 1, 0, 0, seed);
                Assert.IsTrue(probe.Match(template, other, ow, oh) < 0.7, $"another picture of the same kind, seed {seed}");
            }
            // A black screen (a fade, a loading screen) is no match, not a weak one.
            Assert.AreEqual(0, probe.Match(template, new byte[(W + 4) * (H + 4) * 4], W + 4, H + 4));
            // A patch far too small for the picture, or shorter than it says.
            Assert.AreEqual(0, probe.Match(template, new byte[10 * 10 * 4], 10, 10));
            Assert.AreEqual(0, probe.Match(template, new byte[16], W + 4, H + 4));
        }

        [TestMethod]
        public void ATemplate_MustBeSmallAndNotFlat()
        {
            Assert.IsNull(AssistTemplate.FromBgr("flat", new byte[W * H * 3], W, H));
            Assert.IsNull(AssistTemplate.FromBgr("large", AssistTestKit.Blocks(65, 20, 1), 65, 20));
            Assert.IsNull(AssistTemplate.FromBgr("short", new byte[10], W, H));
            Assert.IsNull(AssistTemplate.Load("genshin", "no-such-template"));
        }

        [TestMethod]
        public void AProbe_AllocatesNothing_AfterTheFirst()
        {
            AssistTemplate template = AssistTemplate.FromBgr("blocks", AssistTestKit.Blocks(W, H, 4), W, H);
            var probe = new ScreenProbe();
            var (bgra, w, h) = Patch(AssistTestKit.Blocks(W, H, 4), 1.3333, 1, 1);
            probe.Match(template, bgra, w, h);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++) probe.Match(template, bgra, w, h);
            Assert.AreEqual(0, GC.GetAllocatedBytesForCurrentThread() - before, "bytes allocated by 200 probes");
        }

        /// <summary>
        /// The probe's answer worked out the plain way, for a patch already at canvas size: every shift the slack
        /// allows, every pixel of every plane, one multiply at a time. What the probe has to agree with, and the
        /// yardstick its speed is held to.
        /// </summary>
        private sealed class PlainSum
        {
            private readonly double[] _planes; // the picture, each colour plane with its mean taken off
            private readonly double _norm;
            private readonly int _w, _h;

            public PlainSum(byte[] bgr, int w, int h)
            {
                _w = w;
                _h = h;
                int n = w * h;
                _planes = new double[n * 3];
                double squares = 0;
                for (int c = 0; c < 3; c++)
                {
                    double mean = 0;
                    for (int i = 0; i < n; i++) mean += bgr[i * 3 + c];
                    mean /= n;
                    for (int i = 0; i < n; i++)
                    {
                        double v = bgr[i * 3 + c] - mean;
                        _planes[c * n + i] = v;
                        squares += v * v;
                    }
                }
                _norm = Math.Sqrt(squares);
            }

            public double Match(byte[] bgra, int width, int height)
            {
                int n = _w * _h;
                double best = 0;
                for (int dy = 0; dy + _h <= height; dy++)
                {
                    for (int dx = 0; dx + _w <= width; dx++)
                    {
                        double dot = 0, spread = 0;
                        for (int c = 0; c < 3; c++)
                        {
                            double sum = 0, squares = 0;
                            for (int y = 0; y < _h; y++)
                            {
                                int at = ((dy + y) * width + dx) * 4 + c, of = c * n + y * _w;
                                for (int x = 0; x < _w; x++)
                                {
                                    double v = bgra[at + x * 4];
                                    sum += v;
                                    squares += v * v;
                                    dot += v * _planes[of + x];
                                }
                            }
                            spread += squares - sum * sum / n;
                        }
                        if (spread <= 0) continue;
                        best = Math.Max(best, dot / (Math.Sqrt(spread) * _norm));
                    }
                }
                return Math.Min(1, best);
            }
        }

        [TestMethod]
        public void AProbe_GivesTheAnswerOfThePlainSum()
        {
            byte[] blocks = AssistTestKit.Blocks(W, H, 4);
            AssistTemplate template = AssistTemplate.FromBgr("blocks", blocks, W, H);
            var plain = new PlainSum(blocks, W, H);
            var probe = new ScreenProbe();
            void Agree(byte[] bgra, int w, int h, string what) =>
                Assert.AreEqual(plain.Match(bgra, w, h), probe.Match(template, bgra, w, h), 1e-4, what);

            foreach ((int dx, int dy) in new[] { (0, 0), (2, -2), (-2, 1) })
            {
                var (shown, w, h) = Patch(blocks, 1, dx, dy);
                Agree(shown, w, h, $"the picture, shift {dx},{dy}");
            }
            for (int seed = 1; seed <= 20; seed++)
            {
                var (scene, w, h) = Patch(null, 1, 0, 0, seed);
                Agree(scene, w, h, $"a plain scene, seed {seed}");
                var (other, ow, oh) = Patch(AssistTestKit.Blocks(W, H, 100 + seed), 1, 0, 0, seed);
                Agree(other, ow, oh, $"another picture of the same kind, seed {seed}");
            }
        }

        /// <summary>The promise for the build that ships: ten anchors in under a millisecond (measured 0.51 ms).</summary>
        private const double TenAnchorsTargetMs = 1.0;

        /// <summary>
        /// The same promise for any build on any machine, as a share of the plain sum timed in the same moments. A
        /// probe costs 0.32 of it (Debug and Release alike; less on a busy core), and where the target was set the
        /// plain sum takes 1.61 ms, so 1 ms is 0.62 of it.
        /// </summary>
        private const double MaxShareOfPlainSum = 0.62;

        /// <summary>Whether the app's code under test is the optimised build, the only one a time in milliseconds is promised for.</summary>
        private static bool AppCodeIsOptimised =>
            (Attribute.GetCustomAttribute(typeof(ScreenProbe).Assembly, typeof(DebuggableAttribute)) as DebuggableAttribute)?.IsJITOptimizerDisabled != true;

        [TestMethod]
        [TestCategory("Performance")]
        public void TenAnchors_AreProbedInUnderAMillisecond()
        {
            const int anchors = 10, side = 48;
            var probe = new ScreenProbe();
            var templates = new AssistTemplate[anchors];
            var plain = new PlainSum[anchors];
            var patches = new (byte[] bgra, int w, int h)[anchors];
            for (int i = 0; i < anchors; i++)
            {
                byte[] picture = AssistTestKit.Blocks(side, side, 10 + i);
                templates[i] = AssistTemplate.FromBgr("t" + i, picture, side, side);
                plain[i] = new PlainSum(picture, side, side);
                var frame = new MemoryCapture(side + ScreenProbe.Slack * 2, side + ScreenProbe.Slack * 2);
                frame.Scene(i); // the usual case: the screen is not showing, so every shift is tried and none matches
                patches[i] = (frame.Pixels, frame.Width, frame.Height);
            }
            var runs = new double[2000];
            var shares = new double[runs.Length];
            for (int warm = 0; warm < 50; warm++)
            {
                probe.Match(templates[0], patches[0].bgra, patches[0].w, patches[0].h);
                plain[0].Match(patches[0].bgra, patches[0].w, patches[0].h);
            }
            for (int r = 0; r < runs.Length; r++)
            {
                long started = Stopwatch.GetTimestamp();
                for (int i = 0; i < anchors; i++) probe.Match(templates[i], patches[i].bgra, patches[i].w, patches[i].h);
                long probed = Stopwatch.GetTimestamp();
                // The yardstick, right after: one anchor the plain way (they all cost the same), so a slow or busy
                // machine slows both halves of the pair.
                int one = r % anchors;
                plain[one].Match(patches[one].bgra, patches[one].w, patches[one].h);
                long summed = Stopwatch.GetTimestamp();
                runs[r] = (probed - started) * 1000.0 / Stopwatch.Frequency;
                shares[r] = (probed - started) / (double)((summed - probed) * anchors);
            }
            Array.Sort(runs);
            Array.Sort(shares);
            double p50 = runs[runs.Length / 2], p99 = runs[runs.Length * 99 / 100], share = shares[shares.Length / 2];
            bool optimised = AppCodeIsOptimised;
            Console.WriteLine($"probe of {anchors} anchors of {side}x{side}: p50 {p50:0.000} ms, p99 {p99:0.000} ms; " +
                $"{share:0.000} of the plain sum (p50 {p50 / share:0.000} ms); {(optimised ? "optimised" : "unoptimised")} build");
            Assert.IsTrue(share < MaxShareOfPlainSum, $"a probe costs {share:0.000} of the plain sum");
            // Milliseconds are held to the target only on the optimised build. Unoptimised, the same loop took 2.3 ms on
            // the PC it was written on and 2.8 to 3.4 ms on CI's cloud runner: a bound there measures the machine.
            if (optimised) Assert.IsTrue(p50 < TenAnchorsTargetMs, $"p50 {p50:0.000} ms");
        }

        [TestMethod]
        public void AScreen_Counts_AfterTwoProbesAgree()
        {
            var presence = new ScreenPresence(2);
            Assert.IsFalse(presence.HasObservation);
            Assert.IsFalse(presence.Observe(0, true), "one glimpse is not a screen");
            Assert.IsTrue(presence.Observe(0, true));
            Assert.IsTrue(presence.Observe(0, false), "one miss (a fade) does not close it");
            Assert.IsFalse(presence.Observe(0, false));
            Assert.IsFalse(presence.IsPresent(1));

            // A frame that did not change counts the last probe again.
            presence.Observe(1, true);
            presence.Repeat(1);
            Assert.IsTrue(presence.IsPresent(1));
            presence.Repeat(0);
            Assert.IsFalse(presence.IsPresent(0));
            new ScreenPresence(1).Repeat(0); // nothing seen yet: nothing to repeat
            Assert.IsFalse(new ScreenPresence(1).HasObservation);
            Assert.IsFalse(presence.IsPresent(5), "an index that is not a screen");
        }
    }

    [TestClass]
    public class AssistWatchTests
    {
        private sealed class Rig
        {
            public long Now = 100_000;
            public readonly FakeAssistHost Host = new FakeAssistHost();
            public readonly HashSet<string> On = new HashSet<string>();
            public readonly Dictionary<string, FakeReader> Readers = new Dictionary<string, FakeReader>();
            public readonly Dictionary<string, AssistTemplate> Templates = new Dictionary<string, AssistTemplate>();
            public readonly List<AssistReading> Read = new List<AssistReading>();
            public readonly List<IThreadPoolWorkItem> Held = new List<IThreadPoolWorkItem>();
            public bool HoldLooks;
            public GameManifest Game;
            public AssistWatch Watch;

            public Rig(GameAssist assist, params string[] on)
            {
                Game = AssistTestKit.Game(assist);
                foreach (string id in on) On.Add(id);
                Host.Frame = new MemoryCapture(2200, 1300);
                Host.Frame.Scene(1);
                Host.Client = new Rectangle(100, 50, 1920, 1080);
                Watch = new AssistWatch(Host, (game, id, spec) => Readers.TryGetValue(id, out FakeReader r) ? r : null, On.Contains, null,
                    name => name == "TestGame" ? Game : null, () => new[] { Game },
                    clock: () => Now, background: work => work(),
                    queue: item => { if (HoldLooks) Held.Add(item); else item.Execute(); },
                    loadTemplate: (game, name) => Templates.TryGetValue(name, out AssistTemplate t) ? t : null);
                Watch.Read += Read.Add;
            }

            /// <summary>Ticks at 100 ms, as the capture loop does, for a while.</summary>
            public void Run(int ms)
            {
                for (int t = 0; t < ms; t += 100)
                {
                    Now += 100;
                    Watch.OnCaptureTick();
                }
            }
        }

        private static GameAssist SearchOnly(params string[] ids) => new GameAssist
        {
            Checked = new GameAssistChecked { Version = "1.0", Aspects = new[] { "16:9" }, Source = "test" },
            Readers = ids.ToDictionary(id => id, _ => new GameAssistReader { Kind = GameAssistReader.KindCount, Locate = GameAssistReader.LocateSearch }),
        };

        private const int TW = 40, TH = 32;

        /// <summary>One screen ("map", an anchor at the top right) and one reader with a box on it.</summary>
        private static Rig WithScreen(out Rectangle anchorAt, bool paint = true)
        {
            var assist = new GameAssist
            {
                Canvas = AssistTestKit.Canvas,
                Checked = new GameAssistChecked { Version = "1.0", Aspects = new[] { "16:9" }, Source = "test" },
                Screens = new Dictionary<string, GameAssistScreen>
                {
                    ["map"] = new GameAssistScreen { Anchors = new[] { new GameAssistAnchor { Template = "pin", Edge = "topRight", X = -300, Y = 40, W = TW, H = TH, Threshold = 0.85 } } },
                },
                Readers = new Dictionary<string, GameAssistReader>
                {
                    ["count"] = new GameAssistReader { Kind = GameAssistReader.KindCount, Locate = GameAssistReader.LocateBox, Screen = "map", Box = new GameAssistBox { Edge = "bottomLeft", X = 20, Y = -60, W = 200, H = 40 } },
                },
            };
            var rig = new Rig(assist, "count");
            byte[] blocks = AssistTestKit.Blocks(TW, TH, 4);
            rig.Templates["pin"] = AssistTemplate.FromBgr("pin", blocks, TW, TH);
            rig.Readers["count"] = new FakeReader
            {
                Id = "count", Screen = "map", PeriodSeconds = 1,
                Where = (client, fixedPlaces) => fixedPlaces && AssistGeometry.TryPlace(assist.Readers["count"].Box, assist.Canvas, client, 0, out Rectangle r) ? r : Rectangle.Empty,
            };
            anchorAt = new Rectangle(100 + 1920 - 300, 50 + 40, TW, TH);
            if (paint) rig.Host.Frame.Paint(blocks, TW, TH, anchorAt);
            return rig;
        }

        [TestMethod]
        public void NothingSwitchedOn_CostsNothing()
        {
            var rig = new Rig(SearchOnly("a"));
            rig.Readers["a"] = new FakeReader { Id = "a" };
            Assert.IsFalse(rig.Watch.IsOn);
            rig.Run(10_000);
            Assert.AreEqual(0, rig.Host.Frame.Copies);
            Assert.AreEqual(0, rig.Readers["a"].Looks);
        }

        [TestMethod]
        public void AGameWithoutTheBlock_OrWithoutAReaderInThisBuild_ReadsNothing()
        {
            var none = new Rig(null, "a");
            none.Run(10_000);
            Assert.AreEqual(0, none.Host.Frame.Copies);

            // The manifest lists a reader this build has no code for: it stays off, nothing is copied.
            var unknown = new Rig(SearchOnly("a"), "a");
            Assert.IsTrue(unknown.Watch.IsOn);
            unknown.Run(10_000);
            Assert.AreEqual(0, unknown.Host.Frame.Copies);
        }

        [TestMethod]
        public void AReader_LooksOnceAPeriod_AtItsOwnPixels_AndItsReadingArrives()
        {
            var rig = new Rig(SearchOnly("a"), "a");
            byte[] seen = null;
            rig.Readers["a"] = new FakeReader
            {
                Id = "a", PeriodSeconds = 3,
                Where = (client, _) => new Rectangle(client.Left + 10, client.Top + 20, 30, 5),
                OnLook = (pixels, _) =>
                {
                    seen = pixels.Span.ToArray();
                    Assert.AreEqual((30, 5), (pixels.Width, pixels.Height));
                    return new AssistReading("testgame", "a", AssistOutcome.Read, AssistRefusal.None, 42, "42", DateTimeOffset.UtcNow);
                },
            };
            rig.Run(10_000);
            // The first tick prepares the game; then a look at once and one every three seconds.
            Assert.AreEqual(4, rig.Readers["a"].Looks);
            Assert.AreEqual(4, rig.Host.Frame.Copies);
            Assert.AreEqual(4, rig.Read.Count);
            Assert.AreEqual(42, rig.Read[0].Value);
            // The pixels are the frame's, at the reader's place.
            var expected = new byte[30 * 5 * 4];
            for (int y = 0; y < 5; y++) rig.Host.Frame.Pixels.AsSpan(((70 + y) * 2200 + 110) * 4, 120).CopyTo(expected.AsSpan(y * 120));
            CollectionAssert.AreEqual(expected, seen);
        }

        [TestMethod]
        public void ABusyTextEngine_ARefusal_AndNothingShown_GiveNoReading()
        {
            foreach (AssistOutcome outcome in new[] { AssistOutcome.Busy, AssistOutcome.Refused, AssistOutcome.NotShown })
            {
                var rig = new Rig(SearchOnly("a"), "a");
                bool engineAsked = false;
                rig.Host.Line = (Mat line, out string text, out float score) => { engineAsked = true; text = null; score = 0; return false; };
                rig.Readers["a"] = new FakeReader
                {
                    Id = "a",
                    OnLook = (pixels, read) =>
                    {
                        if (outcome == AssistOutcome.Busy)
                        {
                            using Mat line = pixels.AsMat();
                            Assert.IsFalse(read(line, out _, out _), "a busy engine is skipped, not waited for");
                        }
                        return AssistReading.Nothing("testgame", "a", outcome, DateTimeOffset.UtcNow, outcome == AssistOutcome.Refused ? AssistRefusal.WrongCap : AssistRefusal.None);
                    },
                };
                rig.Run(7_000);
                Assert.IsTrue(rig.Readers["a"].Looks >= 2, outcome.ToString());
                Assert.AreEqual(0, rig.Read.Count, outcome.ToString());
                Assert.AreEqual(outcome == AssistOutcome.Busy, engineAsked);
            }
        }

        [TestMethod]
        public void AGameOnAnotherMonitor_ANoFrame_AndNoWindow_GoQuiet()
        {
            var rig = new Rig(SearchOnly("a"), "a");
            rig.Readers["a"] = new FakeReader { Id = "a" };
            rig.Host.Frame.Answer = FrameCopy.OutsideOutput;
            rig.Run(120_000);
            Assert.AreEqual(0, rig.Readers["a"].Looks);
            Assert.IsTrue(rig.Host.Frame.Copies <= 3, $"asked {rig.Host.Frame.Copies} times in two minutes");

            rig.Host.Frame.Answer = FrameCopy.NoFrame;
            int before = rig.Host.Frame.Copies;
            rig.Run(10_000);
            Assert.AreEqual(0, rig.Readers["a"].Looks);
            Assert.IsTrue(rig.Host.Frame.Copies - before <= 11, "no frame is asked again once a second at most");

            rig.Host.Frame.Answer = FrameCopy.Copied;
            rig.Host.Client = null; // the game window is gone
            before = rig.Host.Frame.Copies;
            rig.Run(60_000);
            Assert.AreEqual(before, rig.Host.Frame.Copies);

            rig.Host.Client = new Rectangle(100, 50, 1920, 1080);
            rig.Run(5_000);
            Assert.IsTrue(rig.Readers["a"].Looks > 0, "and it reads again when the window is back");
        }

        [TestMethod]
        public void TheFallbackBackend_GetsOneRectangleASecond()
        {
            var rig = new Rig(SearchOnly("a", "b"), "a", "b");
            rig.Readers["a"] = new FakeReader { Id = "a", PeriodSeconds = 1 };
            rig.Readers["b"] = new FakeReader { Id = "b", PeriodSeconds = 1 };
            rig.Host.Frame.Holds = false;
            rig.Run(10_500);
            Assert.IsTrue(rig.Host.Frame.Rectangles <= 11, $"{rig.Host.Frame.Rectangles} rectangles in ten seconds");
            Assert.IsTrue(rig.Readers["a"].Looks >= 4 && rig.Readers["b"].Looks >= 4, "each reader gets its turn");

            // The same two readers on a held frame: both every second, in one copy.
            var held = new Rig(SearchOnly("a", "b"), "a", "b");
            held.Readers["a"] = new FakeReader { Id = "a", PeriodSeconds = 1 };
            held.Readers["b"] = new FakeReader { Id = "b", PeriodSeconds = 1 };
            held.Run(10_500);
            Assert.AreEqual(held.Readers["a"].Looks, held.Host.Frame.Copies);
            Assert.IsTrue(held.Readers["a"].Looks >= 10 && held.Readers["b"].Looks >= 10);
        }

        [TestMethod]
        public void KaptionsOwnWindows_AreBlackedOut_OrTheLookIsSkipped()
        {
            var rig = new Rig(SearchOnly("black", "skip"), "black", "skip");
            int blackPixels = -1;
            rig.Readers["black"] = new FakeReader
            {
                Id = "black", BlacksOutOwnWindows = true,
                Where = (client, _) => new Rectangle(client.Left, client.Top, 100, 20),
                OnLook = (pixels, _) =>
                {
                    ReadOnlySpan<byte> p = pixels.Span;
                    blackPixels = 0;
                    for (int i = 0; i < p.Length; i += 4)
                    {
                        if (p[i] == 0 && p[i + 1] == 0 && p[i + 2] == 0 && p[i + 3] == 255) blackPixels++;
                    }
                    return AssistReading.Nothing("testgame", "black", AssistOutcome.NotShown, DateTimeOffset.UtcNow);
                },
            };
            rig.Readers["skip"] = new FakeReader { Id = "skip", Where = (client, _) => new Rectangle(client.Left, client.Top + 100, 100, 20) };
            // The subtitle panel covers the left 40 pixels of the first area and touches the second.
            rig.Host.Own.Add(new Rectangle(0, 0, 140, 200));
            rig.Run(4_000);
            Assert.AreEqual(40 * 20, blackPixels);
            Assert.AreEqual(0, rig.Readers["skip"].Looks);

            rig.Host.Own.Clear();
            rig.Run(4_000);
            Assert.AreEqual(0, blackPixels);
            Assert.IsTrue(rig.Readers["skip"].Looks > 0);
        }

        [TestMethod]
        public void AReaderOnAScreen_LooksOnlyWhileThatScreenShows()
        {
            Rig rig = WithScreen(out Rectangle anchorAt);
            rig.Run(900);
            Assert.AreEqual(0, rig.Readers["count"].Looks, "one glimpse is not a screen");
            rig.Run(3_000);
            int looks = rig.Readers["count"].Looks;
            Assert.IsTrue(looks >= 2, $"{looks} looks once the screen counted");

            // The screen closes. The look that first misses the anchor does not hand that frame to the reader, although
            // the screen still counts until a second probe agrees: a number on the menu that replaced it is not its number.
            rig.Host.Frame.Scene(2);
            rig.Run(7_000);
            Assert.AreEqual(looks, rig.Readers["count"].Looks);
            // No declared screen on the frame: the cost is the probe, one small rectangle a second.
            int before = rig.Host.Frame.Rectangles;
            rig.Run(10_000);
            Assert.IsTrue(rig.Host.Frame.Rectangles - before <= 10, $"{rig.Host.Frame.Rectangles - before} rectangles in ten seconds");
        }

        [TestMethod]
        public void AnUnmeasuredWindowShape_ACoveredAnchor_AndAMissingTemplate_GiveNoFixedPlace()
        {
            // 16:10 was not measured: the anchor is there, the screen is not told.
            Rig wide = WithScreen(out _, paint: false);
            wide.Host.Frame = new MemoryCapture(2200, 1400);
            wide.Host.Client = new Rectangle(100, 50, 1920, 1200);
            wide.Host.Frame.Paint(AssistTestKit.Blocks(TW, TH, 4), TW, TH, new Rectangle(100 + 1920 - 300, 90, TW, TH));
            wide.Run(6_000);
            Assert.AreEqual(0, wide.Readers["count"].Looks);
            Assert.AreEqual(0, wide.Host.Frame.Copies, "nothing is even copied");

            // One of Kaption's own windows over the anchor: no glimpse, so no screen.
            Rig covered = WithScreen(out Rectangle anchorAt);
            covered.Host.Own.Add(new Rectangle(anchorAt.X + 10, anchorAt.Y, 300, 300));
            covered.Run(6_000);
            Assert.AreEqual(0, covered.Readers["count"].Looks);

            // The build carries no such template: the reader stays off.
            Rig missing = WithScreen(out _);
            missing.Templates.Clear();
            missing.Run(6_000);
            Assert.AreEqual(0, missing.Host.Frame.Copies);
            Assert.IsTrue(missing.Readers["count"].Disposed);
        }

        [TestMethod]
        public void OnTheFallbackBackend_FixedScreensAreNotTold()
        {
            Rig rig = WithScreen(out Rectangle anchorAt);
            // A second anchor, also on the frame.
            var second = new GameAssistAnchor { Template = "pin2", Edge = "topLeft", X = 30, Y = 30, W = TW, H = TH, Threshold = 0.85 };
            GameAssistScreen map = rig.Game.Desktop.Assist.Screens["map"];
            map.Anchors = new[] { map.Anchors[0], second };
            byte[] blocks = AssistTestKit.Blocks(TW, TH, 11);
            rig.Templates["pin2"] = AssistTemplate.FromBgr("pin2", blocks, TW, TH);
            rig.Host.Frame.Paint(blocks, TW, TH, new Rectangle(130, 80, TW, TH));

            rig.Host.Frame.Holds = false; // one screen copy per rectangle, one rectangle a second
            rig.Run(8_000);
            Assert.AreEqual(0, rig.Host.Frame.Rectangles, "two copies on one tick would stall it; the screen is refused instead");
            Assert.AreEqual(0, rig.Readers["count"].Looks);

            // One anchor is no better there: its copy and the reader's would be a second apart, and the reader could
            // get a menu that replaced the one the anchor vouched for.
            Rig single = WithScreen(out _);
            single.Host.Frame.Holds = false;
            single.Run(8_000);
            Assert.AreEqual(0, single.Host.Frame.Rectangles);
            Assert.AreEqual(0, single.Readers["count"].Looks);

            // The same screen on a held frame is told, and its reader looks.
            rig.Host.Frame.Holds = true;
            rig.Run(5_000);
            Assert.IsTrue(rig.Readers["count"].Looks >= 2);
        }

        [TestMethod]
        public void AFrameThatDidNotChange_IsNotProbedAgain()
        {
            Rig rig = WithScreen(out _);
            rig.Readers["count"].PeriodSeconds = 60;
            rig.Run(300); // prepared, and the first probe
            Assert.AreEqual(1, rig.Host.Frame.Copies);
            rig.Host.NewFrame = false;
            rig.Run(5_000);
            // The screen counts (the unchanged frame repeats the first glimpse) and its reader looked once; no anchor
            // was copied again.
            Assert.AreEqual(1, rig.Readers["count"].Looks);
            Assert.AreEqual(2, rig.Host.Frame.Copies);
            Assert.AreEqual(2, rig.Host.Frame.Rectangles);
        }

        [TestMethod]
        public void ALookThatWaitedTooLong_IsDropped_AndOnlyOneRunsAtATime()
        {
            var rig = new Rig(SearchOnly("a"), "a");
            rig.Readers["a"] = new FakeReader { Id = "a", PeriodSeconds = 3 };
            rig.HoldLooks = true;
            rig.Run(20_000);
            Assert.AreEqual(1, rig.Held.Count, "no second look while one is in flight");
            Assert.AreEqual(1, rig.Host.Frame.Copies);
            rig.Held[0].Execute(); // long after its picture
            Assert.AreEqual(0, rig.Readers["a"].Looks);
            rig.HoldLooks = false;
            rig.Run(4_000);
            Assert.IsTrue(rig.Readers["a"].Looks >= 1);
        }

        [TestMethod]
        public void SwitchingAReader_RebuildsTheGame_AndAnotherGameIsPreparedAgain()
        {
            var rig = new Rig(SearchOnly("a", "b"), "a");
            rig.Readers["a"] = new FakeReader { Id = "a" };
            rig.Readers["b"] = new FakeReader { Id = "b" };
            rig.Run(4_000);
            Assert.IsTrue(rig.Readers["a"].Looks > 0);
            Assert.AreEqual(0, rig.Readers["b"].Looks);

            rig.On.Add("b");
            rig.Watch.SettingsChanged();
            rig.Run(4_000);
            Assert.IsTrue(rig.Readers["a"].Disposed, "the old readers were retired");
            Assert.IsTrue(rig.Readers["b"].Looks > 0);

            rig.On.Clear();
            rig.Watch.SettingsChanged();
            int copies = rig.Host.Frame.Copies;
            rig.Run(10_000);
            Assert.IsFalse(rig.Watch.IsOn);
            Assert.AreEqual(copies, rig.Host.Frame.Copies);

            // A game the registry does not know.
            rig.On.Add("a");
            rig.Watch.SettingsChanged();
            rig.Host.Game = "Elsewhere";
            copies = rig.Host.Frame.Copies;
            rig.Run(10_000);
            Assert.AreEqual(copies, rig.Host.Frame.Copies);
        }

        [TestMethod]
        public void AReaderThatThrows_DoesNotReachTheTick()
        {
            var rig = new Rig(SearchOnly("a"), "a");
            rig.Readers["a"] = new FakeReader { Id = "a", OnLook = (_, __) => throw new InvalidOperationException("broken reader") };
            rig.Run(10_000);
            Assert.IsTrue(rig.Readers["a"].Looks >= 3, "and the next look still comes");
            Assert.AreEqual(0, rig.Read.Count);
        }
    }

    [TestClass]
    public class AssistStoreTests
    {
        private static string NewDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "kaption-tests", "assist-" + Guid.NewGuid().ToString("N"))).FullName;

        [TestMethod]
        public void Readings_AreKeptPerGameAndAccount_AndSurviveARestart()
        {
            string dir = NewDir();
            Func<string, string> file = game => Path.Combine(dir, game, AssistStore.FileName);
            var at = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
            var store = new AssistStore(file, TimeSpan.Zero);
            var changed = new List<string>();
            store.Changed += changed.Add;
            store.Record("Genshin", null, "stamina", "121/200", "", at);
            store.Record("Genshin", null, "stamina", "63/200", "", at.AddHours(1));
            store.Record("Genshin", "a-1234567890", "stamina", "200/200", "", at.AddMinutes(5));
            store.Record("WuWa", null, "stamina", "240/240", "", at);
            CollectionAssert.AreEqual(new[] { "Genshin", "Genshin", "Genshin", "WuWa" }, changed);

            var again = new AssistStore(file, TimeSpan.Zero);
            var genshin = again.Readings("Genshin");
            Assert.AreEqual(2, genshin.Count, "one per account: two accounts on one PC do not mix");
            Assert.AreEqual(new AssistStoredReading(AssistStore.LocalAccount, "stamina", "63/200", "", at.AddHours(1)), genshin[0]);
            Assert.AreEqual("a-1234567890", genshin[1].Account);
            Assert.AreEqual(1, again.Readings("WuWa").Count);
            Assert.AreEqual(0, again.Readings("StarRail").Count);
            Assert.AreEqual(0, Directory.GetFiles(Path.Combine(dir, "Genshin"), "*.tmp").Length, "no half-written file is left");
        }

        [TestMethod]
        public void Delete_ForgetsEverythingReadForThatGameOnly()
        {
            string dir = NewDir();
            Func<string, string> file = game => Path.Combine(dir, game, AssistStore.FileName);
            var store = new AssistStore(file, TimeSpan.Zero);
            store.Record("Genshin", null, "stamina", "121/200", "", DateTimeOffset.UtcNow);
            store.Record("WuWa", null, "stamina", "240/240", "", DateTimeOffset.UtcNow);
            string deleted = null;
            store.Deleted += game => deleted = game;
            store.DeleteGame("Genshin");
            Assert.AreEqual("Genshin", deleted);
            Assert.AreEqual(0, store.Readings("Genshin").Count);
            Assert.AreEqual(0, store.Health("Genshin").Count);
            Assert.IsFalse(File.Exists(file("Genshin")));
            Assert.AreEqual(1, new AssistStore(file, TimeSpan.Zero).Readings("WuWa").Count);
        }

        [TestMethod]
        public void AWrite_WaitsForMore_AndFlushWritesNow()
        {
            string dir = NewDir();
            Func<string, string> file = game => Path.Combine(dir, game, AssistStore.FileName);
            var store = new AssistStore(file, TimeSpan.FromMinutes(5));
            store.Record("Genshin", null, "stamina", "121/200", "", DateTimeOffset.UtcNow);
            Assert.IsFalse(File.Exists(file("Genshin")), "debounced");
            Assert.AreEqual(1, store.Readings("Genshin").Count, "but already known");
            store.Flush();
            Assert.IsTrue(File.Exists(file("Genshin")));
        }

        [TestMethod]
        public void ABrokenFile_ReadsAsEmpty_AndIsReplaced()
        {
            string dir = NewDir();
            Func<string, string> file = game => Path.Combine(dir, game, AssistStore.FileName);
            Directory.CreateDirectory(Path.Combine(dir, "Genshin"));
            foreach (string broken in new[] { "{ \"version\": 1, \"accounts\": { \"local\": { \"stamina\": { \"val", "null", "{ \"version\": 9 }", "{ \"version\": 1, \"accounts\": { \"local\": null } }" })
            {
                File.WriteAllText(file("Genshin"), broken);
                var store = new AssistStore(file, TimeSpan.Zero);
                Assert.AreEqual(0, store.Readings("Genshin").Count, broken);
                store.Record("Genshin", null, "stamina", "1/200", "", DateTimeOffset.UtcNow);
                Assert.AreEqual(1, new AssistStore(file, TimeSpan.Zero).Readings("Genshin").Count, broken);
            }
        }

        [TestMethod]
        public void AFileWithEmptyEntries_IsReadWithoutThem()
        {
            string dir = NewDir();
            Func<string, string> file = game => Path.Combine(dir, game, AssistStore.FileName);
            Directory.CreateDirectory(Path.Combine(dir, "Genshin"));
            File.WriteAllText(file("Genshin"), "{ \"version\": 1, \"accounts\": { \"local\": { \"stamina\": null, \"party\": { \"screen\": \"\" }, "
                + "\"map\": { \"value\": \"3\", \"seenUtc\": \"2026-11-10T12:00:00+00:00\" } } }, \"health\": { \"stamina\": null, \"map\": { \"sessionsSinceSeen\": 2 } } }");
            var store = new AssistStore(file, TimeSpan.Zero);
            Assert.AreEqual("map", store.Readings("Genshin").Single().ReaderId, "an entry without a value is dropped");
            Assert.AreEqual(new AssistReaderHealth("map", null, 2), store.Health("Genshin").Single());
            store.SessionStarted("Genshin", new[] { "stamina", "map" });
            store.MarkSeen("Genshin", "stamina", DateTimeOffset.UtcNow);
            Assert.AreEqual(2, store.Health("Genshin").Count);
        }

        [TestMethod]
        public void AWriteThatFails_IsTriedAgain_AndADeleteThatFailsSaysSo()
        {
            string dir = NewDir();
            Func<string, string> file = game => Path.Combine(dir, game, AssistStore.FileName);
            var at = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
            var store = new AssistStore(file, TimeSpan.Zero);
            store.Record("Genshin", null, "stamina", "121/200", "", at);

            // Something holds the file (a backup tool, an editor): the new reading cannot be written now...
            using (new FileStream(file("Genshin"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                store.Record("Genshin", null, "stamina", "63/200", "", at.AddHours(1));
                store.Flush();
            }
            // ...and is written by the next flush instead of being lost at the next restart.
            store.Flush();
            Assert.AreEqual("63/200", new AssistStore(file, TimeSpan.Zero).Readings("Genshin").Single().Value);

            // Delete while the file is held: it says so, shows nothing, and the old readings do not come back.
            bool gone;
            using (new FileStream(file("Genshin"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                gone = store.DeleteGame("Genshin");
            }
            Assert.IsFalse(gone);
            Assert.AreEqual(0, store.Readings("Genshin").Count);
            store.Flush();
            Assert.AreEqual(0, new AssistStore(file, TimeSpan.Zero).Readings("Genshin").Count, "an empty file took the old one's place");
            Assert.IsTrue(store.DeleteGame("Genshin"));
            Assert.IsFalse(File.Exists(file("Genshin")));

            // The same for the sample pictures.
            string pictures = Path.Combine(dir, "pictures");
            Directory.CreateDirectory(pictures);
            string picture = Path.Combine(pictures, "a.png");
            File.WriteAllBytes(picture, new byte[] { 1 });
            using (new FileStream(picture, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.IsFalse(AssistSampleSaver.DeleteFolder(pictures));
            }
            Assert.IsTrue(AssistSampleSaver.DeleteFolder(pictures));
        }

        [TestMethod]
        public void AFileThatCannotBeReadAtStart_IsMergedLater_NeverReplaced()
        {
            string dir = NewDir();
            Func<string, string> file = game => Path.Combine(dir, game, AssistStore.FileName);
            var at = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
            var before = new AssistStore(file, TimeSpan.Zero);
            before.Record("Genshin", null, "stamina", "121/200", "", at);
            before.Record("Genshin", "a-1234567890", "stamina", "200/200", "", at);
            before.SessionStarted("Genshin", new[] { "party" });

            // The app starts while a backup tool holds the file.
            var store = new AssistStore(file, TimeSpan.Zero);
            using (new FileStream(file("Genshin"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.AreEqual(0, store.Readings("Genshin").Count);
                store.SessionStarted("Genshin", new[] { "stamina", "party" });
                store.Record("Genshin", null, "stamina", "63/200", "", at.AddHours(1));
                store.Flush();
            }
            // Released: the next write reads it first and lays this run's changes over it.
            store.Flush();
            var after = new AssistStore(file, TimeSpan.Zero);
            var readings = after.Readings("Genshin");
            Assert.AreEqual(2, readings.Count, "the other account's reading survived");
            Assert.AreEqual("63/200", readings.Single(r => r.Account == AssistStore.LocalAccount).Value);
            Assert.AreEqual("200/200", readings.Single(r => r.Account == "a-1234567890").Value);
            Assert.AreEqual(2, after.Health("Genshin").Single(h => h.ReaderId == "party").SessionsSinceSeen, "sessions add up");
            Assert.AreEqual(2, store.Readings("Genshin").Count, "and this run sees the whole of it too");
        }

        [TestMethod]
        public void Health_CountsSessionsSinceAReaderLastRead()
        {
            string dir = NewDir();
            Func<string, string> file = game => Path.Combine(dir, game, AssistStore.FileName);
            var at = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
            for (int session = 0; session < 3; session++)
            {
                var run = new AssistStore(file, TimeSpan.Zero);
                run.SessionStarted("Genshin", new[] { "stamina" });
                run.SessionStarted("Genshin", new[] { "stamina" }); // once per run of the app
            }
            var store = new AssistStore(file, TimeSpan.Zero);
            Assert.AreEqual(new AssistReaderHealth("stamina", null, 3), store.Health("Genshin").Single());

            store.MarkSeen("Genshin", "stamina", at);
            Assert.AreEqual(new AssistReaderHealth("stamina", at, 0), new AssistStore(file, TimeSpan.Zero).Health("Genshin").Single());
            // While it keeps reading, "last read" moves on disk only every ten minutes.
            store.MarkSeen("Genshin", "stamina", at.AddMinutes(3));
            Assert.AreEqual(at, new AssistStore(file, TimeSpan.Zero).Health("Genshin").Single().LastSeenUtc);
            store.MarkSeen("Genshin", "stamina", at.AddMinutes(11));
            Assert.AreEqual(at.AddMinutes(11), new AssistStore(file, TimeSpan.Zero).Health("Genshin").Single().LastSeenUtc);
        }

        [TestMethod]
        public void AnAccountTag_IsStableOnThisPc_AndSaysNothingAboutTheUid()
        {
            string dir = NewDir();
            Func<string, string> file = game => Path.Combine(dir, game, AssistStore.FileName);
            var store = new AssistStore(file, TimeSpan.Zero);
            string tag = store.AccountTag("Genshin", "712345678");
            Assert.AreEqual(tag, new AssistStore(file, TimeSpan.Zero).AccountTag("Genshin", " 712345678 "));
            Assert.AreNotEqual(tag, store.AccountTag("Genshin", "712345679"));
            Assert.IsFalse(tag.Contains("7123"), tag);
            Assert.IsFalse(File.ReadAllText(file("Genshin")).Contains("712345678"), "the UID is not written down");
            Assert.AreEqual(AssistStore.LocalAccount, store.AccountTag("Genshin", " "));
            // Another PC (another salt) gives another tag for the same UID.
            Assert.AreNotEqual(AssistStore.TagFor("712345678", "salt-a"), AssistStore.TagFor("712345678", "salt-b"));
        }
    }

    [TestClass]
    public class AssistSampleSaverTests
    {
        [TestMethod]
        public void TheFolder_IsCappedByCountAndByBytes_OldestOutFirst()
        {
            string dir = Path.Combine(Path.GetTempPath(), "kaption-tests", "assist-samples-" + Guid.NewGuid().ToString("N"));
            using var picture = new Mat(40, 120, MatType.CV_8UC3);
            Cv2.Randu(picture, new Scalar(0, 0, 0), new Scalar(255, 255, 255)); // noise does not compress: about 14 KB a file
            var at = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

            var byCount = new AssistSampleSaver(dir, 5, long.MaxValue);
            for (int i = 0; i < 12; i++) Assert.IsNotNull(byCount.Save(picture, "near-map-pin-80", at.AddSeconds(i)));
            string[] kept = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            Assert.AreEqual(5, kept.Length);
            StringAssert.StartsWith(kept[0], "20261110-120007-000Z-near-map-pin-80");

            long one = new FileInfo(Directory.GetFiles(dir)[0]).Length;
            var byBytes = new AssistSampleSaver(dir, 100, one * 3 + 10);
            byBytes.Save(picture, "read-121", at.AddMinutes(1));
            Assert.AreEqual(3, Directory.GetFiles(dir).Length);
            // A picture heavier than the folder may be is not kept, and nothing else is thrown out for it.
            Assert.IsNull(new AssistSampleSaver(dir, 100, one - 1).Save(picture, "read-122", at.AddMinutes(2)));
            Assert.AreEqual(3, Directory.GetFiles(dir).Length);

            Assert.IsNull(byCount.Save(new Mat(), "empty", at));
            AssistSampleSaver.DeleteFolder(dir);
            Assert.IsFalse(Directory.Exists(dir));
            AssistSampleSaver.DeleteFolder(dir); // not there: not an error
        }
    }
}
