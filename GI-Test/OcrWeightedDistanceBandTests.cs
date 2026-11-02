using System;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The weighted distance computes only the diagonal band the threshold allows.
    /// A full-matrix reference must agree exactly whenever the true distance is within
    /// the threshold, and both must report "over" otherwise.
    /// </summary>
    [TestClass]
    public class OcrWeightedDistanceBandTests
    {
        private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789l1o0";

        [TestMethod]
        public void BandedDistance_MatchesFullMatrix_WithinThreshold()
        {
            var rng = new Random(20261102);
            float[] thresholds = { 0f, 0.5f, 1f, 2.5f, 5f, 12.5f, 40f, 1000f, float.MaxValue };
            int checkedWithin = 0;

            for (int n = 0; n < 20000; n++)
            {
                string a = RandomText(rng, rng.Next(0, 70));
                string b = rng.Next(3) == 0 ? Mutate(rng, a) : RandomText(rng, rng.Next(0, 70));
                float reference = FullMatrix(a, b);

                foreach (float t in thresholds)
                {
                    float banded = OcrWeightedDistance.Calculate(a.AsSpan(), b.AsSpan(), t);
                    if (reference <= t)
                    {
                        Assert.AreEqual(reference, banded, 1e-4f, $"'{a}' vs '{b}' at threshold {t}");
                        checkedWithin++;
                    }
                    else
                    {
                        Assert.IsTrue(banded > t, $"'{a}' vs '{b}': {banded} should exceed threshold {t} (true {reference})");
                    }
                }
            }

            Assert.IsTrue(checkedWithin > 50000, $"only {checkedWithin} in-threshold comparisons");
        }

        [TestMethod]
        public void BandedDistance_ReadsOcrConfusionsCheaply()
        {
            Assert.AreEqual(0.1f, OcrWeightedDistance.Calculate("he11o".AsSpan(), "hel1o".AsSpan(), 5f), 1e-5f);
            Assert.AreEqual(0.2f, OcrWeightedDistance.Calculate("he11o".AsSpan(), "hello".AsSpan(), 0.5f), 1e-5f);
            Assert.IsTrue(OcrWeightedDistance.Calculate("abcdef".AsSpan(), "uvwxyz".AsSpan(), 2f) > 2f);
        }

        private static string RandomText(Random rng, int length)
        {
            var c = new char[length];
            for (int i = 0; i < length; i++) c[i] = Alphabet[rng.Next(Alphabet.Length)];
            return new string(c);
        }

        // OCR-like noise: a few substitutions, insertions and deletions.
        private static string Mutate(Random rng, string s)
        {
            var chars = new System.Collections.Generic.List<char>(s);
            int edits = rng.Next(0, 6);
            for (int e = 0; e < edits; e++)
            {
                int op = rng.Next(3);
                if (op == 0 && chars.Count > 0) chars[rng.Next(chars.Count)] = Alphabet[rng.Next(Alphabet.Length)];
                else if (op == 1) chars.Insert(rng.Next(chars.Count + 1), Alphabet[rng.Next(Alphabet.Length)]);
                else if (chars.Count > 0) chars.RemoveAt(rng.Next(chars.Count));
            }
            return new string(chars.ToArray());
        }

        // Plain full-matrix DP with the production substitution table, no pruning.
        private static float FullMatrix(string a, string b)
        {
            var d = new float[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    float sub = d[i - 1, j - 1] + SubstitutionCost(a[i - 1], b[j - 1]);
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1f, d[i, j - 1] + 1f), sub);
                }
            }
            return d[a.Length, b.Length];
        }

        // Single-character distance with no slack is the substitution cost itself.
        private static float SubstitutionCost(char x, char y) =>
            x == y ? 0f : OcrWeightedDistance.Calculate(x.ToString().AsSpan(), y.ToString().AsSpan(), 2f);
    }
}
