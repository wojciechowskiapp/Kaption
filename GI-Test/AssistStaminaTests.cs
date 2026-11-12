using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using AppConfig = GI_Subtitles.Core.Config.Config;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Capture;
using GI_Subtitles.Services.Data;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Stamina;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using PaddleOCRSharp;
using Size = System.Drawing.Size;
using Rect = OpenCvSharp.Rect;

namespace GI_Test
{
    /// <summary>
    /// The stamina reader as the screen watch's first reader: the same frames, the same manifest spec, the embedded
    /// icon and the real recognizer on the processor, now through the watch's copy and its one look at a time.
    /// </summary>
    [TestClass]
    public class AssistStaminaTests
    {
        private static PaddleOCREngine _engine;

        [ClassInitialize]
        public static void LoadReader(TestContext _) => _engine = StaminaTestKit.ProcessorEngine();

        [ClassCleanup]
        public static void Unload()
        {
            _engine?.Dispose();
            _engine = null;
        }

        private static bool ReadLine(Mat line, out string text, out float score) => _engine.TryRecognizeLine(line, out text, out score);

        private static GameManifest Game(string id) => GameRegistry.Current.FindByWireId(id);

        /// <summary>A composed game picture as the frame a capture backend holds.</summary>
        private static MemoryCapture Held(Bitmap frame)
        {
            var held = new MemoryCapture(frame.Width, frame.Height);
            BitmapData bits = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < frame.Height; y++)
                    Marshal.Copy(bits.Scan0 + y * bits.Stride, held.Pixels, y * frame.Width * 4, frame.Width * 4);
            }
            finally
            {
                frame.UnlockBits(bits);
            }
            return held;
        }

        [TestMethod]
        public void EveryMeasuredStaminaScreen_IsAReaderOfTheWatch()
        {
            int measured = 0;
            foreach (GameManifest game in GameRegistry.Current.Listed)
            {
                GameAssistReader spec = null;
                game.Desktop?.Assist?.Readers?.TryGetValue(AssistSettings.StaminaReader, out spec);
                if (game.Desktop?.StaminaScreen == null)
                {
                    Assert.IsNull(spec, $"{game.Id} has no measured screen, so nothing to list");
                    Assert.IsNull(AssistReaders.Create(game, AssistSettings.StaminaReader,
                        new GameAssistReader { Kind = "fraction", Locate = "search", Spec = "staminaScreen", Cap = "stamina.max" }), game.Id);
                    continue;
                }
                measured++;
                Assert.IsNotNull(spec, $"{game.Id}: a measured stamina screen the watch does not list would never be read");
                using IAssistReader reader = AssistReaders.Create(game, AssistSettings.StaminaReader, spec);
                Assert.IsInstanceOfType(reader, typeof(StaminaAssistReader), game.Id);
                Assert.AreEqual(game.Desktop.StaminaScreen.SampleSeconds, reader.PeriodSeconds);
                Assert.IsNull(reader.Screen, "it finds its own anchor");
                Assert.IsTrue(reader.BlacksOutOwnWindows);
                var client = new Rectangle(40, 30, 1920, 1080);
                Assert.AreEqual(StaminaLocator.SearchArea(client, game.Desktop.StaminaScreen), reader.Area(client, false), "the same strip as before, whatever the window shape");
                // A kind, a way of locating or a spec this build does not know is not approximated.
                Assert.IsNull(AssistReaders.Create(game, "x", new GameAssistReader { Kind = "count", Locate = "search", Spec = "staminaScreen", Cap = "stamina.max" }));
                Assert.IsNull(AssistReaders.Create(game, "x", new GameAssistReader { Kind = "fraction", Locate = "box", Screen = "map", Cap = "stamina.max" }));
            }
            Assert.AreEqual(3, measured);
        }

        [TestMethod]
        public void TheStaminaReader_ThroughTheWatch_ReadsTheNumber_OrNothing()
        {
            foreach ((string id, string text, int value, Size size) in new[]
                     {
                         ("genshin", "121/200", 121, new Size(1920, 1080)),
                         ("starrail", "180/300", 180, new Size(2560, 1440)),
                         ("wuwa", "63/240", 63, new Size(3440, 1440)),
                     })
            {
                GameManifest game = Game(id);
                using Bitmap with = StaminaScreenReaderTests.Frame(size, game, text);
                using Bitmap without = StaminaScreenReaderTests.Frame(size, game, null);
                long now = 50_000;
                var host = new FakeAssistHost { Game = game.GameId, Frame = Held(with), Client = new Rectangle(0, 0, size.Width, size.Height), Line = ReadLine };
                var read = new List<AssistReading>();
                var watch = new AssistWatch(host, AssistReaders.Create, readerId => readerId == AssistSettings.StaminaReader, null,
                    name => GameRegistry.Current.Find(name), () => GameRegistry.Current.Listed,
                    clock: () => now, background: work => work(), queue: item => item.Execute());
                watch.Read += read.Add;
                void Run(int ms)
                {
                    for (int t = 0; t < ms; t += 100)
                    {
                        now += 100;
                        watch.OnCaptureTick();
                    }
                }

                Run(7_000);
                Assert.IsTrue(read.Count >= 2, $"{id}: {read.Count} readings in seven seconds");
                Assert.IsTrue(read.All(r => r.Outcome == AssistOutcome.Read && r.Value == value && r.GameId == id && r.ReaderId == "stamina"), $"{id}: {string.Join(", ", read.Select(r => r.Value))}");
                Assert.AreEqual(1, host.Frame.Rectangles / host.Frame.Copies, "one strip a look");

                // The screen closes: looks go on, readings stop.
                read.Clear();
                host.Frame = Held(without);
                Run(7_000);
                Assert.AreEqual(0, read.Count, id);
                Assert.IsTrue(host.Frame.Copies >= 2, id);

                // The text engine is busy with dialogue: nothing is read and nothing waits.
                host.Frame = Held(with);
                host.Line = (Mat line, out string t, out float s) => { t = null; s = 0; return false; };
                Run(7_000);
                Assert.AreEqual(0, read.Count, id);
                // No engine yet (it is still loading): the same.
                host.Line = null;
                Run(7_000);
                Assert.AreEqual(0, read.Count, id);
            }
        }

        [TestMethod]
        public void AKnownPlace_SkipsTheSearch_AndTheSearchStaysTheFallback()
        {
            GameManifest game = Game("genshin");
            StaminaLineReader line = (Mat l, out string text, out float score) => _engine.TryRecognizeLine(l, out text, out score);
            foreach (Size size in new[] { new Size(1920, 1080), new Size(2560, 1440), new Size(3840, 2160) })
            {
                using StaminaScreenReader reader = StaminaScreenReader.ForGame(game);
                using Bitmap first = StaminaScreenReaderTests.Frame(size, game, "121/200", seed: 5);
                using Bitmap spent = StaminaScreenReaderTests.Frame(size, game, "81/200", seed: 6);
                using Bitmap elsewhere = StaminaScreenReaderTests.Frame(size, game, "80/200", xFraction: 0.66, seed: 7);
                using Bitmap closed = StaminaScreenReaderTests.Frame(size, game, null, seed: 8);

                StaminaSample a = StaminaScreenReaderTests.Read(game, first, reader, line);
                Assert.AreEqual((StaminaSampleOutcome.Read, 121, false), (a.Outcome, a.Value, a.KnownPlace), $"{size}: the first read searches");
                StaminaSample b = StaminaScreenReaderTests.Read(game, spent, reader, line);
                Assert.AreEqual((StaminaSampleOutcome.Read, 81), (b.Outcome, b.Value), size.ToString());
                Console.WriteLine($"{size.Width}x{size.Height}: second read at a known place: {b.KnownPlace} (icon {b.IconScore:0.000})");
                if (size.Width == 1920) Assert.IsTrue(b.KnownPlace, "at the reference size the remembered place answers");
                // Another screen draws the pill somewhere else: the remembered place misses and the search finds it.
                StaminaSample c = StaminaScreenReaderTests.Read(game, elsewhere, reader, line);
                Assert.AreEqual((StaminaSampleOutcome.Read, 80, false), (c.Outcome, c.Value, c.KnownPlace), size.ToString());
                Assert.AreEqual(StaminaSampleOutcome.NoIcon, StaminaScreenReaderTests.Read(game, closed, reader, line).Outcome, size.ToString());
                // The known place shows something that is not the number (another cap): it is read once, refused, and
                // the search that follows arrives at the same cut without asking the text engine again.
                using Bitmap otherCap = StaminaScreenReaderTests.Frame(size, game, "81/300", seed: 9);
                int asked = 0;
                StaminaSample refused = StaminaScreenReaderTests.Read(game, otherCap, reader, (Mat l, out string text, out float score) =>
                {
                    asked++;
                    return _engine.TryRecognizeLine(l, out text, out score);
                });
                Assert.AreEqual(StaminaSampleOutcome.Refused, refused.Outcome, size.ToString());
                Assert.IsTrue(asked <= 1 || !b.KnownPlace, $"{size}: the refused cut was recognised {asked} times");
                // Both places are remembered now.
                Assert.IsTrue(StaminaScreenReaderTests.Read(game, first, reader, line).Value == 121 && StaminaScreenReaderTests.Read(game, elsewhere, reader, line).Value == 80);
            }
        }

        [TestMethod]
        [TestCategory("Performance")]
        public void AKnownPlace_CostsAFractionOfTheSearch()
        {
            GameManifest game = Game("genshin");
            StaminaLineReader line = (Mat l, out string text, out float score) => _engine.TryRecognizeLine(l, out text, out score);
            using StaminaScreenReader reader = StaminaScreenReader.ForGame(game);
            using Bitmap frame = StaminaScreenReaderTests.Frame(new Size(1920, 1080), game, "121/200");
            var clock = Stopwatch.StartNew();
            StaminaScreenReaderTests.Read(game, frame, reader, line);
            double search = clock.Elapsed.TotalMilliseconds;
            var known = new List<double>();
            for (int i = 0; i < 20; i++)
            {
                clock.Restart();
                StaminaSample s = StaminaScreenReaderTests.Read(game, frame, reader, line);
                known.Add(clock.Elapsed.TotalMilliseconds);
                Assert.IsTrue(s.KnownPlace && s.Reused);
            }
            known.Sort();
            Console.WriteLine($"first look (search + read): {search:0.0} ms; a look at a known place with an unchanged number: median {known[10]:0.00} ms (the test's own frame-to-picture conversion included)");
        }

        [TestMethod]
        public void AnAcceptedReading_IsWrittenWithWhatBelongsToIt_AndDeleteForgetsItAll()
        {
            GameManifest game = Game("starrail");
            var at = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
            var reading = new StaminaReading(180, at, 120);
            try
            {
                StaminaSettings.SaveAccepted(game.Id, reading, true, at);
                Assert.AreEqual(reading, StaminaSettings.LoadReading(game.Id));
                Assert.IsTrue(StaminaSettings.LoadArmed(game.Id));
                Assert.AreEqual(at, StaminaSettings.LoadSeen(game.Id));
                // A one-point correction: the reading moves, what is owed stays owed.
                var corrected = new StaminaReading(181, at.AddMinutes(1), 300);
                StaminaSettings.SaveAccepted(game.Id, corrected, null, at.AddMinutes(1));
                Assert.AreEqual(corrected, StaminaSettings.LoadReading(game.Id));
                Assert.IsTrue(StaminaSettings.LoadArmed(game.Id));
                // The file on disk has all of it: one write, not three.
                string json = File.ReadAllText(Path.Combine(TestEnvironment.ConfigDir, "Config.json"));
                StringAssert.Contains(json, "Stamina.Reading.starrail");
                StringAssert.Contains(json, "Stamina.NoticeArmed.starrail");
                StringAssert.Contains(json, "Stamina.Seen.starrail");

                string dir = Path.Combine(Path.GetTempPath(), "kaption-tests", "assist-" + Guid.NewGuid().ToString("N"));
                var store = new AssistStore(g => Path.Combine(dir, g, AssistStore.FileName), TimeSpan.Zero);
                store.Record(game.GameId, null, AssistSettings.StaminaReader, "181/300", "", at);
                string pictures = StaminaSampleSaver.FolderFor(game.GameId);
                Directory.CreateDirectory(pictures);
                File.WriteAllBytes(Path.Combine(pictures, "20261110-120000-000Z-read-181.png"), new byte[] { 1 });
                string anchors = AssistSampleSaver.FolderFor(game.GameId);
                Directory.CreateDirectory(anchors);
                StringAssert.StartsWith(anchors, Path.Combine(GameDataPaths.Root, AssistSampleSaver.FolderName));

                AssistData.DeleteEverything(game, store);
                Assert.IsNull(StaminaSettings.LoadReading(game.Id));
                Assert.IsNull(StaminaSettings.LoadSeen(game.Id));
                Assert.IsFalse(StaminaSettings.LoadArmed(game.Id));
                Assert.IsFalse(AppConfig.Has("Stamina.Reading.starrail"));
                Assert.AreEqual(0, store.Readings(game.GameId).Count);
                Assert.IsFalse(Directory.Exists(pictures));
                Assert.IsFalse(Directory.Exists(anchors));
            }
            finally
            {
                StaminaSettings.Forget(game.Id);
            }
        }

        [TestMethod]
        public void TheSettingsCard_ListsEveryReaderOfTheGame_WithItsSwitchAndWhatWasRead()
        {
            var en = CultureInfo.GetCultureInfo("en-US");
            Func<string, string, string> l = (key, fallback) => fallback;
            var at = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
            GameManifest genshin = Game("genshin");

            AssistCardModel off = AssistCardModel.Build(genshin, _ => false, Array.Empty<AssistStoredReading>(), l, en);
            Assert.AreEqual(1, off.Readers.Count);
            Assert.AreEqual(("stamina", "Original Resin timer", false), (off.Readers[0].ReaderId, off.Readers[0].Title, off.Readers[0].On));
            StringAssert.Contains(off.Readers[0].Sentence, "Original Resin number beside its icon");
            StringAssert.Contains(off.Readers[0].Sentence, "121/200");
            Assert.IsNull(off.NothingToRead);
            Assert.IsFalse(off.HasReadings);

            AssistCardModel on = AssistCardModel.Build(genshin, id => id == "stamina",
                new[] { new AssistStoredReading("local", "stamina", "63/200", "", at), new AssistStoredReading("local", "gone-reader", "1", "", at) }, l, en);
            Assert.IsTrue(on.Readers[0].On);
            Assert.AreEqual(2, on.ReadLines.Count, "what a reader that left the manifest read is still listed: delete must be able to forget it");
            StringAssert.StartsWith(on.ReadLines[0], "Original Resin: 63/200, read ");
            StringAssert.StartsWith(on.ReadLines[1], "gone-reader: 1, read ");

            // Star Rail's cap is its own.
            StringAssert.Contains(AssistCardModel.Build(Game("starrail"), _ => false, null, l, en).Readers[0].Sentence, "121/300");

            // A game with nothing measured says so instead of showing an empty list.
            AssistCardModel zzz = AssistCardModel.Build(Game("zzz"), _ => true, null, l, en);
            Assert.AreEqual(0, zzz.Readers.Count);
            Assert.AreEqual("There is nothing to read in Zenless Zone Zero yet.", zzz.NothingToRead);
            Assert.IsFalse(zzz.HasReadings);
            // ...and still shows, and can delete, what an older version read there.
            AssistCardModel left = AssistCardModel.Build(Game("zzz"), _ => true, new[] { new AssistStoredReading("local", "stamina", "200/240", "", at) }, l, en);
            Assert.IsTrue(left.HasReadings);
            StringAssert.StartsWith(left.ReadLines[0], "stamina: 200/240, read ");
            Assert.IsNotNull(AssistCardModel.Build(null, _ => true, null, l, en).NothingToRead);
        }

        [TestMethod]
        public void EveryReaderInAManifest_HasItsWords_InBothLanguages()
        {
            RenderTestKit.RunSta(() =>
            {
                var keys = new List<string>
                {
                    "Assist_Title", "Assist_Hint", "Assist_None", "Assist_Read_Title", "Assist_Read_Line", "Assist_Read_Nothing", "Assist_Delete",
                    "Assist_Delete_Tip", "Assist_Delete_Failed", "Assist_Samples", "Assist_Samples_Hint",
                };
                foreach (string id in GameRegistry.Current.Listed.SelectMany(g => g.Desktop?.Assist?.Readers?.Keys ?? Enumerable.Empty<string>()).Distinct())
                {
                    keys.Add("Assist_Reader_" + id + "_Title");
                    keys.Add("Assist_Reader_" + id + "_Body");
                }
                Assert.IsTrue(keys.Contains("Assist_Reader_stamina_Body"));
                foreach (string lang in new[] { "en-US", "pl-PL" })
                {
                    var host = new FrameworkElement();
                    foreach (var d in RenderTestKit.Strings(lang)) host.Resources.MergedDictionaries.Add(d);
                    foreach (string key in keys)
                    {
                        string text = host.TryFindResource(key) as string;
                        Assert.IsFalse(string.IsNullOrWhiteSpace(text), $"{lang}: {key}");
                        // The words a player must never read here.
                        foreach (string banned in new[] { "OCR", "AI", "model" })
                            Assert.IsFalse(text.Split(' ', '.', ',', ':').Contains(banned), $"{lang}: {key} says {banned}");
                    }
                    // The card's sentences take the pool's name and its cap.
                    string body = string.Format((string)host.TryFindResource("Assist_Reader_stamina_Body"), "Original Resin", 200);
                    StringAssert.Contains(body, "Original Resin");
                    StringAssert.Contains(body, "121/200");
                }
            });
        }

        [TestMethod]
        public void TheSwitches_AreOffUntilSwitchedOn_AndTheStaminaReaderKeepsItsOwnKey()
        {
            bool before = StaminaSettings.Enabled;
            int changes = 0;
            Action count = () => changes++;
            AssistSettings.Changed += count;
            try
            {
                StaminaSettings.Enabled = false;
                Assert.IsFalse(AssistSettings.IsOn("stamina"));
                Assert.IsFalse(AssistSettings.IsOn("party"), "a reader nobody switched on");
                Assert.IsFalse(AssistSettings.IsOn(null));
                Assert.IsFalse(AssistSettings.AnyOn(GameRegistry.Current));

                changes = 0;
                AssistSettings.SetOn("stamina", true);
                Assert.IsTrue(StaminaSettings.Enabled, "the consent given on the Start card and the one given in Settings are one key");
                Assert.IsTrue(AssistSettings.AnyOn(GameRegistry.Current));
                Assert.AreEqual(1, changes);
                AssistSettings.SetOn("stamina", true);
                Assert.AreEqual(1, changes, "no change, no event");

                AssistSettings.SetOn("party", true);
                Assert.IsTrue(AppConfig.Get("Assist.Reader.party", false));
                Assert.AreEqual(2, changes);

                Assert.IsFalse(AssistSettings.SaveSamples);
                Assert.IsFalse(StaminaSettings.SaveSamples);
                AssistSettings.SaveSamples = true;
                Assert.IsTrue(StaminaSettings.SaveSamples, "the Diagnostics switch turns the stamina pictures on as well");
                Assert.AreEqual(3, changes);

                // A Config from before this switch existed: the old key counts, the checkbox shows it, and switching
                // it off really stops the pictures.
                AssistSettings.SaveSamples = false;
                AppConfig.Set(StaminaSettings.SaveSamplesKey, true);
                Assert.IsTrue(AssistSettings.SaveSamples);
                Assert.IsTrue(StaminaSettings.SaveSamples);
                AssistSettings.SaveSamples = false;
                Assert.IsFalse(StaminaSettings.SaveSamples);
                Assert.IsFalse(AppConfig.Has(StaminaSettings.SaveSamplesKey));
            }
            finally
            {
                AssistSettings.Changed -= count;
                AssistSettings.SaveSamples = false;
                AppConfig.Remove("Assist.Reader.party");
                StaminaSettings.Enabled = before;
            }
        }

        /// <summary>
        /// Recorded gameplay in which the number is never on screen (dialogue scenes), one frame a second, through the
        /// game's stamina reader: any read fails the test. What it read or refused is printed with the moment and
        /// kept as a small picture to look at. Set KAPTION_ASSIST_VIDEOS to <c>game=folder;game=folder</c>; every <c>*.mp4</c> under a folder
        /// is played. Footage is never committed, so without the variable the test is inconclusive.
        /// </summary>
        [TestMethod]
        [TestCategory("Diagnostics")]
        public void RecordedGameplayWithoutTheNumber_GivesNoReading()
        {
            string spec = Environment.GetEnvironmentVariable("KAPTION_ASSIST_VIDEOS");
            if (string.IsNullOrWhiteSpace(spec)) Assert.Inconclusive("Set KAPTION_ASSIST_VIDEOS to game=folder;game=folder with recorded gameplay.");
            string outDir = Path.Combine(Path.GetTempPath(), "kaption-render", "assist-videos");
            Directory.CreateDirectory(outDir);
            int frames = 0, reads = 0, refused = 0, videos = 0;
            var readAt = new List<string>();
            foreach (string part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = part.Split('=', 2);
                GameManifest game = pair.Length == 2 ? Game(pair[0].Trim()) : null;
                Assert.IsNotNull(game?.Desktop?.StaminaScreen, $"no measured stamina screen for \"{part}\"");
                foreach (string file in Directory.EnumerateFiles(pair[1].Trim(), "*.mp4", SearchOption.AllDirectories).OrderBy(f => f))
                {
                    using var video = new VideoCapture(file);
                    if (!video.IsOpened()) continue;
                    videos++;
                    double fps = video.Fps > 1 ? video.Fps : 30;
                    int step = Math.Max(1, (int)Math.Round(fps)), index = 0;
                    using StaminaScreenReader reader = StaminaScreenReader.ForGame(game);
                    using var frame = new Mat();
                    while (video.Read(frame) && !frame.Empty())
                    {
                        if (index++ % step != 0) continue;
                        frames++;
                        var client = new Rectangle(0, 0, frame.Width, frame.Height);
                        Rectangle area = StaminaLocator.SearchArea(client, game.Desktop.StaminaScreen);
                        using var strip = new Mat(frame, new Rect(area.X, area.Y, area.Width, area.Height));
                        StaminaSample sample = reader.Read(strip, client.Size, (Mat l, out string text, out float score) => _engine.TryRecognizeLine(l, out text, out score));
                        if (sample.Outcome != StaminaSampleOutcome.Read && sample.Outcome != StaminaSampleOutcome.Refused) continue;
                        if (sample.Outcome == StaminaSampleOutcome.Read) reads++; else refused++;
                        if (sample.Outcome == StaminaSampleOutcome.Read) readAt.Add($"{Path.GetFileName(Path.GetDirectoryName(file))} at {(index - 1) / fps:0} s: {sample.Value}");
                        string name = $"{game.Id}-{Path.GetFileName(Path.GetDirectoryName(file))}-{(index - 1) / fps:0000}s-{sample.Outcome}-{sample.Value}.png";
                        var around = new Rect(Math.Max(0, sample.Line.X - sample.Line.Height * 4), Math.Max(0, sample.Line.Y - sample.Line.Height),
                            Math.Min(strip.Width - Math.Max(0, sample.Line.X - sample.Line.Height * 4), sample.Line.Width + sample.Line.Height * 8),
                            Math.Min(strip.Height - Math.Max(0, sample.Line.Y - sample.Line.Height), sample.Line.Height * 3));
                        using (var cut = new Mat(strip, around)) Cv2.ImWrite(Path.Combine(outDir, name), cut);
                        Console.WriteLine($"{sample.Outcome,-8} {sample.Value,4}  icon {sample.IconScore:0.000}  text \"{sample.Text}\" {sample.TextScore:0.00}  {name}");
                    }
                }
            }
            Console.WriteLine($"{videos} videos, {frames} frames (one a second): {reads} read, {refused} refused. Pictures: {outDir}");
            Assert.IsTrue(videos > 0 && frames > 0, "no video with frames was found");
            Assert.AreEqual(0, reads, "read where the number is not on screen: " + string.Join("; ", readAt));
        }
    }

    /// <summary>The second look at the held frame, on the real backends: needs a desktop.</summary>
    [TestClass]
    public class AssistCaptureTests
    {
        [TestMethod]
        public void Rectangles_ArePackedBackToBack()
        {
            var regions = new[] { new Rectangle(5, 5, 10, 4), new Rectangle(0, 0, 3, 3) };
            Assert.AreEqual(10 * 4 * 4 + 3 * 3 * 4, FrameRegions.BytesFor(regions));
            Assert.ThrowsExactly<ArgumentException>(() => FrameRegions.BytesFor(new[] { new Rectangle(0, 0, 0, 5) }));
            using var gdi = new GdiScreenCapture();
            Assert.IsFalse(gdi.HoldsFrame);
            Assert.ThrowsExactly<ArgumentException>(() => gdi.TryCopyFromLastFrame(regions, new byte[16]), "a destination that is too small is a caller's bug");
        }

        private static (double p50, double p99) Time(int runs, Action act)
        {
            var ms = new double[runs];
            for (int i = 0; i < runs; i++)
            {
                long started = Stopwatch.GetTimestamp();
                act();
                ms[i] = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            }
            Array.Sort(ms);
            return (ms[runs / 2], ms[Math.Min(runs - 1, runs * 99 / 100)]);
        }

        /// <summary>Ten anchor patches (a 48x48 picture plus slack) spread over the top of the screen.</summary>
        private static Rectangle[] TenPatches()
        {
            int side = 48 + ScreenProbe.Slack * 2;
            return Enumerable.Range(0, 10).Select(i => new Rectangle(20 + i * 60, 20 + i % 3 * 60, side, side)).ToArray();
        }

        [TestMethod]
        [TestCategory("Integration")]
        [TestCategory("RequiresScreen")]
        public void Gdi_CopiesEachRectangleFromTheScreen()
        {
            if (!Environment.UserInteractive) Assert.Inconclusive("No interactive desktop.");
            using var gdi = new GdiScreenCapture();
            Rectangle[] patches = TenPatches();
            var buffer = new byte[FrameRegions.BytesFor(patches)];
            if (gdi.TryCopyFromLastFrame(patches, buffer) != FrameCopy.Copied) Assert.Inconclusive("The screen cannot be copied right now (a locked PC).");
            Assert.IsTrue(buffer.Any(b => b != 0), "pixels arrived");

            // No pixel is compared with another capture: this path copies the live screen each time, and a desktop
            // nobody owns may have moved in between. The packing is checked on a frame in memory (AssistWatchTests).
            var (p50, p99) = Time(100, () => gdi.TryCopyFromLastFrame(patches, buffer));
            var (one50, one99) = Time(200, () => gdi.TryCopyFromLastFrame(patches.AsSpan(0, 1), buffer));
            Console.WriteLine($"GDI, 10 patches of {patches[0].Width}x{patches[0].Height}: p50 {p50:0.000} ms, p99 {p99:0.000} ms; one patch (the watch's ration, once a second): p50 {one50:0.000} ms, p99 {one99:0.000} ms");
        }

        [TestMethod]
        [TestCategory("Integration")]
        [TestCategory("RequiresScreen")]
        public void Dxgi_CopiesFromTheHeldFrame_WithoutANewOne()
        {
            if (!Environment.UserInteractive) Assert.Inconclusive("No interactive desktop.");
            DxgiScreenCapture dxgi;
            try { dxgi = new DxgiScreenCapture(); }
            catch (Exception ex) { Assert.Inconclusive($"Desktop duplication is not available here: {ex.Message}"); return; }
            using (dxgi)
            {
                if (!dxgi.IsAvailable) Assert.Inconclusive("Desktop duplication is not available here.");
                Assert.IsTrue(dxgi.HoldsFrame);
                Rectangle[] patches = TenPatches();
                var buffer = new byte[FrameRegions.BytesFor(patches)];
                Assert.AreEqual(FrameCopy.NoFrame, dxgi.TryCopyFromLastFrame(patches, buffer), "before the first capture there is no frame to look at");

                Bitmap first = null;
                // A desktop where nothing moves gives its first frame late; ask for a few seconds.
                var patience = Stopwatch.StartNew();
                while (first == null && patience.ElapsedMilliseconds < 4000) first = dxgi.CaptureRegion(patches[0].X, patches[0].Y, patches[0].Width, patches[0].Height);
                if (first == null) Assert.Inconclusive("The desktop gave no frame (a locked PC).");
                using (first)
                {
                    Assert.AreEqual(FrameCopy.Copied, dxgi.TryCopyFromLastFrame(patches, buffer));
                    Color pixel = first.GetPixel(3, 2);
                    int at = (2 * patches[0].Width + 3) * 4;
                    Assert.AreEqual((pixel.B, pixel.G, pixel.R), (buffer[at], buffer[at + 1], buffer[at + 2]), "the same frame the capture returned");
                }
                // A rectangle off this output is an answer, not an exception.
                Assert.AreEqual(FrameCopy.OutsideOutput, dxgi.TryCopyFromLastFrame(new[] { new Rectangle(-50_000, 0, 10, 10) }, buffer));

                var probe = new ScreenProbe();
                AssistTemplate[] templates = Enumerable.Range(0, 10).Select(i => AssistTemplate.FromBgr("t" + i, AssistTestKit.Blocks(48, 48, 10 + i), 48, 48)).ToArray();
                var (copy50, copy99) = Time(2000, () => dxgi.TryCopyFromLastFrame(patches, buffer));
                var (all50, all99) = Time(2000, () =>
                {
                    dxgi.TryCopyFromLastFrame(patches, buffer);
                    int offset = 0;
                    for (int i = 0; i < patches.Length; i++)
                    {
                        int bytes = FrameRegions.BytesFor(patches[i]);
                        probe.Match(templates[i], buffer.AsSpan(offset, bytes), patches[i].Width, patches[i].Height);
                        offset += bytes;
                    }
                });
                Console.WriteLine($"DXGI, 10 patches of {patches[0].Width}x{patches[0].Height} from the held frame: copy p50 {copy50:0.000} ms, p99 {copy99:0.000} ms; copy and probe p50 {all50:0.000} ms, p99 {all99:0.000} ms");

                // The stamina strip of a 1920x1080 game, as the tick copies it.
                var strip = new[] { new Rectangle(864, 21, 1056, 130) };
                var stripBuffer = new byte[FrameRegions.BytesFor(strip)];
                if (dxgi.TryCopyFromLastFrame(strip, stripBuffer) == FrameCopy.Copied)
                {
                    var (s50, s99) = Time(1000, () => dxgi.TryCopyFromLastFrame(strip, stripBuffer));
                    Console.WriteLine($"DXGI, the stamina strip (1056x130) from the held frame: p50 {s50:0.000} ms, p99 {s99:0.000} ms");
                }
            }
        }
    }
}
