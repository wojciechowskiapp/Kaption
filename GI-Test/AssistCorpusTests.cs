using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Games;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using PaddleOCRSharp;

namespace GI_Test
{
    /// <summary>
    /// The in-game helper's readers over real frames. The samples are committed (Fixtures/assist-corpus/&lt;game&gt;/*.json:
    /// where a frame comes from and what must be read on it); the frames are footage and stay on the PC, fetched by
    /// benchmark/tools/assist_frames.py into the folder KAPTION_ASSIST_FRAMES names. Every reader of the sample's
    /// game looks at the frame through the watch itself, with the real recognizer on the processor. Without samples
    /// or without the folder the test is inconclusive.
    /// </summary>
    [TestClass]
    public class AssistCorpusTests
    {
        private sealed class Sample
        {
            public string Id { get; set; }
            public string Game { get; set; }
            public string Aspect { get; set; }
            public int[] Client { get; set; }
            public Dictionary<string, JsonElement> Expect { get; set; }
        }

        [TestMethod]
        [TestCategory("Diagnostics")]
        public void EveryReader_ReadsWhatASampleExpects_AndNothingElse()
        {
            string samplesDir = Path.Combine(RenderTestKit.RepoRoot(), "GI-Test", "Fixtures", "assist-corpus");
            var samples = Directory.Exists(samplesDir)
                ? Directory.EnumerateFiles(samplesDir, "*.json", SearchOption.AllDirectories).OrderBy(f => f)
                    .Select(f => JsonSerializer.Deserialize<Sample>(File.ReadAllText(f), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }))
                    .ToList()
                : new List<Sample>();
            if (samples.Count == 0) Assert.Inconclusive("No sample is committed yet (GI-Test/Fixtures/assist-corpus/README.md).");
            string frames = Environment.GetEnvironmentVariable("KAPTION_ASSIST_FRAMES");
            if (string.IsNullOrWhiteSpace(frames) || !Directory.Exists(frames))
                Assert.Inconclusive("Set KAPTION_ASSIST_FRAMES to the folder benchmark/tools/assist_frames.py fetched the frames into.");

            using PaddleOCREngine engine = StaminaTestKit.ProcessorEngine();
            var report = new StringBuilder();
            var wrong = new List<string>();
            int looked = 0;
            foreach (Sample sample in samples)
            {
                string file = Path.Combine(frames, sample.Game, sample.Id + ".png");
                if (!File.Exists(file))
                {
                    report.AppendLine($"--  {sample.Id}: not fetched");
                    continue;
                }
                GameManifest game = GameRegistry.Current.FindByWireId(sample.Game);
                Assert.IsNotNull(game, $"{sample.Id}: no game {sample.Game}");
                using Mat picture = Cv2.ImRead(file, ImreadModes.Color);
                Assert.IsFalse(picture.Empty(), $"{sample.Id}: {file} is not a picture");
                looked++;

                Dictionary<string, List<AssistReading>> read = Look(game, picture, sample.Client, engine);
                var expect = sample.Expect ?? new Dictionary<string, JsonElement>();
                foreach (string reader in expect.Keys.Union(read.Keys).OrderBy(k => k))
                {
                    bool asText = expect.TryGetValue(reader, out JsonElement e) && e.ValueKind != JsonValueKind.Number;
                    string want = !expect.ContainsKey(reader) ? null : asText ? e.GetString() : e.GetInt32().ToString();
                    // Every reading of the run counts, not the last one: a wrong value that a later look corrected
                    // would still have reached the player.
                    string[] got = read.TryGetValue(reader, out List<AssistReading> all)
                        ? all.Select(r => asText ? r.Text : r.Value.ToString()).Distinct().ToArray()
                        : Array.Empty<string>();
                    bool ok = want == null ? got.Length == 0 : got.Length == 1 && got[0] == want;
                    string seen = got.Length == 0 ? "nothing" : string.Join(" and ", got);
                    report.AppendLine($"{(ok ? "ok" : "!!")}  {sample.Id} ({sample.Aspect})  {reader}: want {want ?? "nothing"}, got {seen}");
                    if (!ok) wrong.Add($"{sample.Id}/{reader}: want {want ?? "nothing"}, got {seen}");
                }
                if (expect.Count == 0 && read.Count == 0) report.AppendLine($"ok  {sample.Id} ({sample.Aspect})  nothing to read, nothing read");
            }
            Console.WriteLine(report.ToString());
            Console.WriteLine($"{samples.Count} samples, {looked} with a frame here, {wrong.Count} wrong.");
            if (looked == 0) Assert.Inconclusive("No frame of any sample is on this PC: run benchmark/tools/assist_frames.py fetch.");
            Assert.AreEqual(0, wrong.Count, string.Join("; ", wrong));
        }

        /// <summary>The frame as the capture's held frame, every reader of the game switched on, the watch run for a few looks.</summary>
        private static Dictionary<string, List<AssistReading>> Look(GameManifest game, Mat picture, int[] client, PaddleOCREngine engine)
        {
            var held = new MemoryCapture(picture.Width, picture.Height);
            using (Mat bgra = picture.CvtColor(ColorConversionCodes.BGR2BGRA))
            {
                int height = bgra.Height, rowBytes = bgra.Width * 4;
                for (int y = 0; y < height; y++) Marshal.Copy(bgra.Ptr(y), held.Pixels, y * rowBytes, rowBytes);
            }
            long now = 10_000;
            var host = new FakeAssistHost
            {
                Game = game.GameId,
                Frame = held,
                Client = client != null && client.Length == 4 ? new Rectangle(client[0], client[1], client[2], client[3]) : new Rectangle(0, 0, picture.Width, picture.Height),
                Line = (Mat line, out string text, out float score) => engine.TryRecognizeLine(line, out text, out score),
            };
            var read = new Dictionary<string, List<AssistReading>>();
            using var watch = new AssistWatch(host, AssistReaders.Create, _ => true, null,
                name => GameRegistry.Current.Find(name), () => GameRegistry.Current.Listed,
                clock: () => now, background: work => work(), queue: item => item.Execute());
            watch.Read += reading =>
            {
                if (!read.TryGetValue(reading.ReaderId, out List<AssistReading> all)) read[reading.ReaderId] = all = new List<AssistReading>();
                all.Add(reading);
            };
            // Long enough for a screen to count (two probes) and for the slowest reader to look twice.
            for (int t = 0; t < 12_000; t += 100)
            {
                now += 100;
                watch.OnCaptureTick();
            }
            return read;
        }
    }
}
