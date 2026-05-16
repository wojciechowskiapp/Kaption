using System;
using System.Collections.Generic;
using System.Text;
using ZstdSharp;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// Thin wrapper over <see cref="DictBuilder.TrainFromBuffer"/>. Trains
    /// a zstd compression dictionary from a corpus of sample strings — the
    /// dictionary is the payoff: per-record compression starts already
    /// primed with the common substrings of the corpus instead of having
    /// to build its dictionary from scratch in every tiny frame header.
    ///
    /// For the matcher's Polish value pool (short dialogue lines, lots of
    /// "Paimon", "Traveler", stock phrases), a trained dict gives roughly
    /// 5-8x compression vs. 2-3x without one (see
    /// <c>.plan/research/ZSTD-LIBRARY-EVAL.md</c>).
    /// </summary>
    public static class ZstdDictionaryTrainer
    {
        /// <summary>The default dict capacity from ZstdSharp / zstd CLI.</summary>
        public const int DefaultDictCapacityBytes = 112_640;

        /// <summary>
        /// Train a zstd dictionary from a sequence of UTF-8 sample blobs.
        /// Returns the trained dictionary bytes, ready to feed into
        /// <see cref="ZstdValueDecoder"/> or <see cref="Compressor.LoadDictionary(byte[])"/>.
        /// </summary>
        /// <param name="samples">
        /// Collection of sample byte arrays. zstd recommends 5-10k samples
        /// of 256-2048 bytes each. Smaller samples or fewer samples produce
        /// a less effective dictionary; larger inputs blow training time.
        /// </param>
        /// <param name="dictCapacityBytes">
        /// Target dictionary size. 110 KB is the default; 64 KB and 32 KB
        /// are viable for smaller corpora.
        /// </param>
        public static byte[] Train(IReadOnlyList<byte[]> samples, int dictCapacityBytes = DefaultDictCapacityBytes)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (dictCapacityBytes <= 0) throw new ArgumentOutOfRangeException(nameof(dictCapacityBytes));
            if (samples.Count == 0)
                throw new ArgumentException("Need at least one sample to train a dictionary.", nameof(samples));

            // ZstdSharp's DictBuilder.TrainFromBuffer accepts IEnumerable<byte[]>.
            // We copy into a materialized list so callers can pass lazy sources.
            var materialized = new List<byte[]>(samples.Count);
            foreach (var s in samples)
            {
                if (s == null) continue;
                if (s.Length == 0) continue;
                materialized.Add(s);
            }

            if (materialized.Count == 0)
                throw new ArgumentException("All samples were null or empty.", nameof(samples));

            return DictBuilder.TrainFromBuffer(materialized, dictCapacityBytes);
        }

        /// <summary>Convenience: train from a sequence of strings (UTF-8 encoded).</summary>
        public static byte[] TrainFromStrings(IEnumerable<string> samples,
            int dictCapacityBytes = DefaultDictCapacityBytes)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            var bytes = new List<byte[]>();
            foreach (var s in samples)
            {
                if (string.IsNullOrEmpty(s)) continue;
                bytes.Add(Encoding.UTF8.GetBytes(s));
            }
            if (bytes.Count == 0)
                throw new ArgumentException("All samples were null or empty.", nameof(samples));
            return Train(bytes, dictCapacityBytes);
        }
    }
}
