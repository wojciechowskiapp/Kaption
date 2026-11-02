using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using GI_Subtitles.Services.QuickTranslate;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Segmenter cost on a large dictionary. 60k synthetic entries plus 300 skill descriptions that
    /// share the "Normal Attack" header and first sentence, the shape that made Genshin panels fail.
    /// Loose budgets so slow CI hosts do not flake; the numbers are printed for the log.
    /// </summary>
    [TestClass]
    public class QuickTranslateSegmenterPerfTests
    {
        private const int CorpusSize = 60_000;
        private const int Descriptions = 300;
        private static OptimizedMatcher _matcher;
        private static string[] _titles;
        private static string[] _descriptions;
        private static string[] _plain;

        [ClassInitialize]
        public static void Build(TestContext _)
        {
            var rand = new Random(4242);
            string[] vocab = Enumerable.Range(0, 400).Select(i => Word(rand, 3 + i % 7)).Distinct().ToArray();
            string Sentence(int words) =>
                string.Join(" ", Enumerable.Range(0, words).Select(j => vocab[rand.Next(vocab.Length)]));

            var dict = new Dictionary<string, string>(CorpusSize + 2 * Descriptions + 8, StringComparer.Ordinal);
            _plain = new string[CorpusSize];
            for (int i = 0; i < CorpusSize; i++)
            {
                string key = Sentence(6 + rand.Next(25));
                if (dict.ContainsKey(key)) { i--; continue; }
                dict[key] = "PL-" + i;
                _plain[i] = key;
            }

            dict["Normal Attack"] = "Atak podstawowy";
            dict["Charged Attack"] = "Atak naładowany";
            dict["Plunging Attack"] = "Atak spadający";
            _titles = new string[Descriptions];
            _descriptions = new string[Descriptions];
            for (int k = 0; k < Descriptions; k++)
            {
                _titles[k] = "Art: " + Sentence(2);
                _descriptions[k] =
                    "Normal Attack\nPerforms up to 5 rapid strikes.\nCharged Attack\n" +
                    $"Consumes Stamina to {Sentence(9 + rand.Next(8))}.\n" +
                    "Plunging Attack\nPlunges from mid-air to strike the ground.";
                dict[_titles[k]] = "TYTUL-" + k;
                dict[_descriptions[k]] = "OPIS-" + k;
            }

            var sw = Stopwatch.StartNew();
            _matcher = new OptimizedMatcher(dict, "EN");
            Console.WriteLine($"matcher built: {dict.Count:N0} entries in {sw.ElapsedMilliseconds} ms");
        }

        [ClassCleanup]
        public static void Cleanup() => _matcher?.Dispose();

        [TestMethod]
        public void SkillPanel_SharedHeaders_ResolvesInBudget()
        {
            int k = 137;
            var lines = new List<string> { _titles[k], "Lv. 10" };
            lines.AddRange(Wrap(_descriptions[k], 58));
            lines.Add("CD: 12s");
            lines.AddRange(Wrap(_plain[777], 58));

            int probes = 0;
            var probe = QuickTranslateSegmenter.ProbeWith(_matcher);
            Func<string, MatchProbe> counted = t => { probes++; return probe(t); };

            var sw = Stopwatch.StartNew();
            var segs = QuickTranslateSegmenter.Segment(lines, counted);
            sw.Stop();
            Console.WriteLine($"panel: {lines.Count} lines, {probes} probes, {sw.ElapsedMilliseconds} ms");

            var matched = segs.Where(s => s.IsMatched).Select(s => s.Translation).ToList();
            CollectionAssert.AreEqual(new[] { "TYTUL-137", "OPIS-137", "PL-777" }, matched, Dump(segs));
            Assert.AreEqual(2, segs.Count(s => !s.IsMatched), Dump(segs));
            Assert.IsTrue(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");
            Assert.IsTrue(probes <= 3 * lines.Count, $"{probes} probes for {lines.Count} lines");
        }

        [TestMethod]
        public void NothingMatches_FortyLines_InBudget()
        {
            var rand = new Random(99);
            var lines = Enumerable.Range(0, 40)
                .Select(i => $"zq{i} " + new string(Enumerable.Range(0, 30 + i).Select(j => (char)('a' + rand.Next(26))).ToArray()))
                .ToList();

            var sw = Stopwatch.StartNew();
            var segs = QuickTranslateSegmenter.Segment(lines, QuickTranslateSegmenter.ProbeWith(_matcher));
            sw.Stop();
            Console.WriteLine($"junk: {lines.Count} lines, {sw.ElapsedMilliseconds} ms");

            Assert.AreEqual(1, segs.Count, Dump(segs));
            Assert.IsFalse(segs[0].IsMatched);
            Assert.IsTrue(sw.ElapsedMilliseconds < 1500, $"{sw.ElapsedMilliseconds} ms");
        }

        [TestMethod]
        public void TwoHundredLines_StaysUnderTheSegmenterBudget()
        {
            var lines = new List<string>();
            for (int k = 0; k < 40; k++) lines.AddRange(Wrap(_descriptions[k * 7], 58).Take(5));

            var sw = Stopwatch.StartNew();
            var segs = QuickTranslateSegmenter.Segment(lines, QuickTranslateSegmenter.ProbeWith(_matcher));
            sw.Stop();
            Console.WriteLine($"long: {lines.Count} lines → {segs.Count} segments, {sw.ElapsedMilliseconds} ms");

            Assert.IsTrue(sw.Elapsed <= QuickTranslateSegmenter.Budget + TimeSpan.FromMilliseconds(500), $"{sw.ElapsedMilliseconds} ms");
            Assert.IsTrue(segs.Count(s => s.IsMatched) >= 30, $"only {segs.Count(s => s.IsMatched)} matched: {Dump(segs)}");
        }

        private static IEnumerable<string> Wrap(string text, int width)
        {
            foreach (string paragraph in text.Split('\n'))
            {
                var sb = new StringBuilder();
                foreach (string w in paragraph.Split(' '))
                {
                    if (sb.Length > 0 && sb.Length + 1 + w.Length > width)
                    {
                        yield return sb.ToString();
                        sb.Clear();
                    }
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(w);
                }
                if (sb.Length > 0) yield return sb.ToString();
            }
        }

        private static string Word(Random r, int len) =>
            new string(Enumerable.Range(0, len).Select(i => "bcdfghklmnprstvz"[r.Next(16)].ToString() + "aeiou"[r.Next(5)])
                .SelectMany(s => s).Take(len).ToArray());

        private static string Dump(IReadOnlyList<QuickTranslateSegment> segs) =>
            string.Join(" | ", segs.Select(s => $"[{(s.IsMatched ? "M" : "U")}] {s.Source.Replace("\n", "⏎")} => {s.Translation}"));
    }
}
