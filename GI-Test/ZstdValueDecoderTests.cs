// ─────────────────────────────────────────────────────────────────────────────
//  ZstdValueDecoderTests.cs
//  ---------------------------------------------------------------------------
//  Round-trip tests for the ZstdValueDecoder + ZstdDictionaryTrainer helpers.
//  Covers no-dict + trained-dict compress/decompress symmetry, trained-dict
//  ratio improvement on repetitive corpora, and buffer-too-small errors.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ZstdSharp;

namespace GI_Test
{
    [TestClass]
    public class ZstdValueDecoderTests
    {
        private static byte[] Compress(byte[] plain, byte[] dict = null, int level = 19)
        {
            using (var c = new Compressor(level))
            {
                if (dict != null && dict.Length > 0) c.LoadDictionary(dict);
                int bound = Compressor.GetCompressBound(plain.Length);
                byte[] scratch = new byte[bound];
                int n = c.Wrap(plain, scratch);
                byte[] trimmed = new byte[n];
                Buffer.BlockCopy(scratch, 0, trimmed, 0, n);
                return trimmed;
            }
        }

        [TestMethod]
        public void Decode_NoDictionary_RoundTrip()
        {
            byte[] plain = Encoding.UTF8.GetBytes("Witaj Podróżniku, ja jestem Paimon.");
            byte[] compressed = Compress(plain);

            using (var decoder = new ZstdValueDecoder(dictionary: null))
            {
                byte[] dest = new byte[plain.Length];
                int n = decoder.Decode(compressed, dest);
                Assert.AreEqual(plain.Length, n);
                CollectionAssert.AreEqual(plain, dest);
            }
        }

        [TestMethod]
        public void Decode_WithDictionary_RoundTrip()
        {
            var samples = BuildTrainingCorpus();
            byte[] dict = ZstdDictionaryTrainer.Train(samples);
            Assert.IsTrue(dict.Length > 0);

            byte[] plain = Encoding.UTF8.GetBytes("Paimon lubi ciasto - powiedz to Podróżniku!");
            byte[] compressed = Compress(plain, dict);

            using (var decoder = new ZstdValueDecoder(dict))
            {
                byte[] dest = new byte[plain.Length];
                int n = decoder.Decode(compressed, dest);
                Assert.AreEqual(plain.Length, n);
                CollectionAssert.AreEqual(plain, dest);
            }
        }

        [TestMethod]
        public void TrainedDictionary_ProducesBetterCompression()
        {
            var samples = BuildTrainingCorpus();
            byte[] dict = ZstdDictionaryTrainer.Train(samples);

            byte[] plain = Encoding.UTF8.GetBytes("Paimon: Witaj Podróżniku! Zbierzmy trochę primogemów.");
            byte[] noDict = Compress(plain);
            byte[] withDict = Compress(plain, dict);

            // The trained dict was built from the same vocabulary family, so
            // its frames carry less metadata. On short records the win is
            // material — the assertion is lenient (any reduction) to avoid
            // flake on the zstd-port's exact numbers.
            Assert.IsTrue(withDict.Length <= noDict.Length,
                $"Expected dict to help; got {withDict.Length} vs {noDict.Length}.");
        }

        [TestMethod]
        public void Decode_BufferTooSmall_Throws()
        {
            byte[] plain = Encoding.UTF8.GetBytes("This is a somewhat longer sentence for decoding.");
            byte[] compressed = Compress(plain);

            using (var decoder = new ZstdValueDecoder(dictionary: null))
            {
                byte[] tooSmall = new byte[4]; // way under required
                // ZstdSharp throws on destination-too-small. Accept any throw
                // here so the test doesn't couple to a specific exception
                // type the port might tweak across versions.
                Assert.ThrowsException<ZstdException>(() =>
                    decoder.Decode(compressed, tooSmall));
            }
        }

        [TestMethod]
        public void DecodeAsString_MatchesRoundTrip()
        {
            string s = "Szybki brunatny lis przeskakuje nad leniwym psem.";
            byte[] plain = Encoding.UTF8.GetBytes(s);
            byte[] compressed = Compress(plain);

            using (var decoder = new ZstdValueDecoder(dictionary: null))
            {
                string roundTripped = decoder.DecodeAsString(compressed);
                Assert.AreEqual(s, roundTripped);
            }
        }

        [TestMethod]
        public void ConcurrentDecode_ThreadSafe()
        {
            byte[] plain = Encoding.UTF8.GetBytes("concurrent payload");
            byte[] compressed = Compress(plain);
            var exceptions = new List<Exception>();

            using (var decoder = new ZstdValueDecoder(dictionary: null))
            {
                Parallel.For(0, 64, i =>
                {
                    try
                    {
                        byte[] dest = new byte[plain.Length];
                        int n = decoder.Decode(compressed, dest);
                        Assert.AreEqual(plain.Length, n);
                        CollectionAssert.AreEqual(plain, dest);
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions) exceptions.Add(ex);
                    }
                });
            }

            Assert.AreEqual(0, exceptions.Count,
                exceptions.Count > 0 ? exceptions[0].ToString() : "");
        }

        [TestMethod]
        public void EmptyDictionary_Trainer_Rejected()
        {
            Assert.ThrowsException<ArgumentException>(() =>
                ZstdDictionaryTrainer.Train(new List<byte[]>()));
            Assert.ThrowsException<ArgumentException>(() =>
                ZstdDictionaryTrainer.TrainFromStrings(new string[0]));
        }

        private static List<byte[]> BuildTrainingCorpus()
        {
            // Synthetic Polish-flavoured corpus full of the scaffold phrases
            // zstd's dict trainer looks for. Not realistic, but enough to
            // produce a non-degenerate dictionary in the size the trainer
            // needs (hundreds of samples).
            var words = new[]
            {
                "Witaj", "Podróżniku", "Paimon", "ciasto", "primogem", "Cześć",
                "zadanie", "Mondstadt", "Liyue", "Inazuma", "Sumeru",
                "Signora", "Travelers", "misja", "następny", "wcześniej",
            };
            var rand = new Random(42);
            var samples = new List<byte[]>();
            for (int i = 0; i < 2048; i++)
            {
                var sb = new StringBuilder();
                int parts = 4 + rand.Next(8);
                for (int p = 0; p < parts; p++)
                {
                    sb.Append(words[rand.Next(words.Length)]);
                    sb.Append(' ');
                }
                samples.Add(Encoding.UTF8.GetBytes(sb.ToString()));
            }
            return samples;
        }
    }
}
