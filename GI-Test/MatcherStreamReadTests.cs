using System;
using System.Collections.Generic;
using System.IO;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The matcher cache is read through a CryptoStream, where every Read call is costly.
    /// Deserialising must pull the payload in large chunks, never one primitive at a time.
    /// </summary>
    [TestClass]
    public class MatcherStreamReadTests
    {
        private sealed class ForwardOnlyCountingStream : Stream
        {
            private readonly MemoryStream _inner;
            public int ReadCalls;

            public ForwardOnlyCountingStream(byte[] data) { _inner = new MemoryStream(data, false); }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadCalls++;
                return _inner.Read(buffer, offset, count);
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private static OptimizedMatcher BuildSample(out byte[] payload)
        {
            var dict = new Dictionary<string, string>();
            for (int i = 0; i < 3000; i++)
                dict["The traveler spoke to guard number " + i + " near the northern gate."] = "Podróżnik rozmawiał ze strażnikiem " + i + ".";
            dict["Paimon"] = "Paimon";
            var matcher = new OptimizedMatcher(dict, "EN");
            using (var ms = new MemoryStream())
            {
                matcher.SerializeToStream(ms);
                payload = ms.ToArray();
            }
            return matcher;
        }

        [TestMethod]
        public void Deserialize_ForwardOnlyStream_ReadsInLargeChunks()
        {
            OptimizedMatcher source = BuildSample(out byte[] payload);
            var stream = new ForwardOnlyCountingStream(payload);

            OptimizedMatcher loaded = OptimizedMatcher.DeserializeFromStream(stream);

            Assert.AreEqual(source.EntryCount, loaded.EntryCount);
            int ceiling = payload.Length / 16384 + 16;
            Assert.IsTrue(stream.ReadCalls <= ceiling,
                $"{stream.ReadCalls} Read calls for {payload.Length:N0} bytes (ceiling {ceiling}).");
        }

        [TestMethod]
        public void Deserialize_ForwardOnlyStream_MatchesLikeTheSource()
        {
            OptimizedMatcher source = BuildSample(out byte[] payload);
            OptimizedMatcher loaded = OptimizedMatcher.DeserializeFromStream(new ForwardOnlyCountingStream(payload));

            foreach (string probe in new[]
            {
                "The traveler spoke to guard number 1234 near the northern gate.",
                "The travelor spoke to guard numbr 77 near the northern gate",
                "Paimon",
            })
            {
                string expected = source.FindClosestMatch(probe, out string expectedKey);
                string actual = loaded.FindClosestMatch(probe, out string actualKey);
                Assert.AreEqual(expected, actual, probe);
                Assert.AreEqual(expectedKey, actualKey, probe);
            }
        }
    }
}
