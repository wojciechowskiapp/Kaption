using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.OCR;
using GI_Subtitles.Services.Stamina;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using PaddleOCRSharp;

namespace GI_Test
{
    /// <summary>
    /// The stamina reader on real game frames, which are not in the repository (footage is for internal testing only).
    /// Set KAPTION_STAMINA_FOOTAGE to a folder with <c>truth.json</c>: a list of
    /// <c>{ "file", "game", "value" (a number, null for "nothing to read", absent for "just report"), "max" (an old
    /// version's cap), "client": [x, y, w, h] (the game picture inside a letterboxed frame) }</c>. Every frame goes
    /// through the shipping path: the manifest's spec, the embedded icon, the real recognizer on the processor.
    /// </summary>
    [TestClass]
    public class StaminaFootageTests
    {
        private sealed class Case
        {
            public string File { get; set; }
            public string Game { get; set; }
            // Undefined when the row has no "value" (just report), Null for "nothing to read", else the number.
            public JsonElement Value { get; set; }
            public int? Max { get; set; }
            public int[] Client { get; set; }
        }

        [TestMethod]
        [TestCategory("Diagnostics")]
        public void RealFrames_ReadTheNumberOrNothing()
        {
            string root = Environment.GetEnvironmentVariable("KAPTION_STAMINA_FOOTAGE");
            if (string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "truth.json")))
                Assert.Inconclusive("Set KAPTION_STAMINA_FOOTAGE to a folder with truth.json and the frames it lists.");

            var cases = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(Path.Combine(root, "truth.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            using PaddleOCREngine engine = StaminaTestKit.ProcessorEngine();
            var report = new StringBuilder();
            var wrong = new List<string>();
            var locate = new List<double>();
            var read = new List<double>();
            int reads = 0, nothing = 0;

            foreach (var group in cases.GroupBy(c => (c.Game, c.Max)))
            {
                GameManifest game = GameRegistry.Current.FindByWireId(group.Key.Game);
                Assert.IsNotNull(game?.Desktop?.StaminaScreen, $"{group.Key.Game} has no staminaScreen");
                var stamina = new GameStamina
                {
                    Name = game.Stamina.Name, Max = group.Key.Max ?? game.Stamina.Max, RegenSeconds = game.Stamina.RegenSeconds, Hold = game.Stamina.Hold,
                };
                foreach (Case c in group)
                {
                    // A fresh reader per frame: frames are unrelated, and the "same picture" shortcut must not carry over.
                    using var reader = new StaminaScreenReader(stamina, game.Desktop.StaminaScreen, StaminaLocator.ForSpec(game.Desktop.StaminaScreen));
                    using Mat frame = Cv2.ImRead(Path.Combine(root, c.File));
                    Assert.IsFalse(frame.Empty(), $"cannot read {c.File}");
                    var client = c.Client is { Length: 4 } r ? new System.Drawing.Rectangle(r[0], r[1], r[2], r[3])
                        : new System.Drawing.Rectangle(0, 0, frame.Width, frame.Height);
                    System.Drawing.Rectangle area = StaminaLocator.SearchArea(client, game.Desktop.StaminaScreen);
                    using var strip = new Mat(frame, new Rect(area.X, area.Y, area.Width, area.Height));

                    double readMs = 0;
                    var clock = Stopwatch.StartNew();
                    StaminaSample sample = reader.Read(strip, client.Size, (Mat line, out string text, out float score) =>
                    {
                        var t = Stopwatch.StartNew();
                        bool ok = engine.TryRecognizeLine(line, out text, out score);
                        readMs = t.Elapsed.TotalMilliseconds;
                        return ok;
                    });
                    double total = clock.Elapsed.TotalMilliseconds;
                    locate.Add(total - readMs);
                    if (readMs > 0) read.Add(readMs);

                    string got = sample.Outcome == StaminaSampleOutcome.Read ? sample.Value.ToString() : "-";
                    string want = Wanted(c.Value);
                    bool ok = want == "?" || want == got;
                    if (sample.Outcome == StaminaSampleOutcome.Read) reads++; else nothing++;
                    if (!ok) wrong.Add($"{c.File}: want {want}, got {got} ({sample.Outcome}, text \"{sample.Text}\")");
                    report.AppendLine($"{(ok ? "ok " : "BAD")} {c.Game,-8} {c.File,-34} want {want,4} got {got,4}  {sample.Outcome,-10} icon {sample.IconScore:0.000} "
                        + $"text \"{sample.Text}\" {sample.TextScore:0.00}  locate {total - readMs:0.0} ms read {readMs:0.0} ms");
                }
            }

            report.AppendLine($"frames {cases.Count}, read {reads}, nothing {nothing}, wrong {wrong.Count}");
            report.AppendLine($"locate ms: median {Median(locate):0.0}, worst {locate.Max():0.0}; read ms (processor): median {Median(read):0.0}, worst {(read.Count > 0 ? read.Max() : 0):0.0}");
            Console.WriteLine(report);
            File.WriteAllText(Path.Combine(root, "report.txt"), report.ToString());
            Assert.AreEqual(0, wrong.Count, string.Join(Environment.NewLine, wrong));
        }

        /// <summary>"?" for a row without a value (reported, not judged), "-" for null (nothing to read), else the number.</summary>
        internal static string Wanted(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Undefined => "?",
            JsonValueKind.Null => "-",
            _ => value.GetInt32().ToString(),
        };

        [TestMethod]
        public void TruthRows_TellAnExpectedNothingFromNoExpectation()
        {
            var rows = JsonSerializer.Deserialize<List<Case>>(
                "[{\"file\":\"a\",\"game\":\"genshin\"},{\"file\":\"b\",\"game\":\"genshin\",\"value\":null},{\"file\":\"c\",\"game\":\"genshin\",\"value\":121}]",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            CollectionAssert.AreEqual(new[] { "?", "-", "121" }, rows.Select(r => Wanted(r.Value)).ToArray());
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            return sorted[sorted.Count / 2];
        }
    }

    /// <summary>What the stamina tests share: the app's recommended recognizer on the processor.</summary>
    internal static class StaminaTestKit
    {
        public static PaddleOCREngine ProcessorEngine()
        {
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
                rec_score_thresh = 0.5f,
            };
            return new PaddleOCREngine(profile.CreateModelConfig(Path.Combine(assemblyRoot, "inference")), parameters);
        }
    }
}
