using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using GI_Subtitles.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Covers the streaming reader that replaced Newtonsoft's JsonTextReader in
    /// the dialogue-graph loaders. These loaders parse an 18 MB file that the app
    /// writes itself, so a parse regression here silently disables chain
    /// prediction rather than failing loudly.
    /// </summary>
    [TestClass]
    public class Utf8StreamJsonReaderTests
    {
        private static Stream Utf8(string json, bool withBom = false)
        {
            var ms = new MemoryStream();
            if (withBom)
            {
                byte[] bom = Encoding.UTF8.GetPreamble();
                ms.Write(bom, 0, bom.Length);
            }
            byte[] body = Encoding.UTF8.GetBytes(json);
            ms.Write(body, 0, body.Length);
            ms.Position = 0;
            return ms;
        }

        /// <summary>Feeds at most <c>chunk</c> bytes per Read so token boundaries
        /// land inside the buffer refill path.</summary>
        private sealed class ChokedStream : Stream
        {
            private readonly Stream _inner;
            private readonly int _chunk;
            public ChokedStream(Stream inner, int chunk) { _inner = inner; _chunk = chunk; }
            public override int Read(byte[] buffer, int offset, int count) =>
                _inner.Read(buffer, offset, Math.Min(count, _chunk));
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }

        private static List<string> ReadPropertyNames(Stream s)
        {
            var names = new List<string>();
            using (var r = new Utf8StreamJsonReader(s))
            {
                while (r.Read())
                {
                    if (r.TokenType == JsonTokenType.PropertyName)
                        names.Add(r.GetString());
                }
            }
            return names;
        }

        [TestMethod]
        public void SkipsUtf8Bom()
        {
            // DialogGraphDownloader.SaveCompactJson writes a BOM, so every
            // plaintext graph file on disk starts with EF BB BF. Utf8JsonReader
            // rejects one as an invalid start of value.
            var names = ReadPropertyNames(Utf8("{\"a\":1,\"b\":2}", withBom: true));
            CollectionAssert.AreEqual(new[] { "a", "b" }, names);
        }

        [TestMethod]
        public void ParsesWithoutBom()
        {
            var names = ReadPropertyNames(Utf8("{\"a\":1,\"b\":2}"));
            CollectionAssert.AreEqual(new[] { "a", "b" }, names);
        }

        [TestMethod]
        public void SkipsBomWhenItSpansABufferRefill()
        {
            var names = ReadPropertyNames(
                new ChokedStream(Utf8("{\"a\":1}", withBom: true), 1));
            CollectionAssert.AreEqual(new[] { "a" }, names);
        }

        [TestMethod]
        public void QuotedHashAboveInt64MaxRoundTripsExactly()
        {
            // HSR TextMap hashes are xxhash64. Roughly half exceed long.MaxValue,
            // and the builders emit them quoted to preserve the full range.
            const ulong expected = 13056811057485265789UL;
            using (var r = new Utf8StreamJsonReader(Utf8("{\"h\":\"13056811057485265789\"}")))
            {
                Assert.IsTrue(r.Read());
                Assert.IsTrue(r.Read());
                Assert.IsTrue(r.Read());
                Assert.AreEqual(expected, r.GetUInt64());
            }
        }

        [TestMethod]
        public void UnquotedUInt64MaxRoundTripsExactly()
        {
            using (var r = new Utf8StreamJsonReader(Utf8("{\"h\":18446744073709551615}")))
            {
                Assert.IsTrue(r.Read());
                Assert.IsTrue(r.Read());
                Assert.IsTrue(r.Read());
                Assert.AreEqual(ulong.MaxValue, r.GetUInt64());
            }
        }

        [TestMethod]
        public void NumericValueReadsAsStringLikeTheOldReader()
        {
            // RoleId arrives as either a JSON string or a number; the Newtonsoft
            // path used jr.Value?.ToString() and callers still expect a string.
            using (var r = new Utf8StreamJsonReader(Utf8("{\"ri\":1005}")))
            {
                Assert.IsTrue(r.Read());
                Assert.IsTrue(r.Read());
                Assert.IsTrue(r.Read());
                Assert.AreEqual("1005", r.GetString());
            }
        }

        [TestMethod]
        public void SkipTraversesSubtreeLargerThanTheBuffer()
        {
            var sb = new StringBuilder("{\"skipme\":[");
            for (int i = 0; i < 40000; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(i);
            }
            sb.Append("],\"after\":7}");

            using (var r = new Utf8StreamJsonReader(Utf8(sb.ToString())))
            {
                Assert.IsTrue(r.Read());
                Assert.IsTrue(r.Read());
                Assert.AreEqual("skipme", r.GetString());
                r.Read();
                r.Skip();
                Assert.IsTrue(r.Read());
                Assert.AreEqual("after", r.GetString());
            }
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(7)]
        [DataRow(64)]
        [DataRow(65536)]
        public void ProducesIdenticalTokensRegardlessOfChunkSize(int chunk)
        {
            const string json = "{\"1\":{\"h\":\"18446744073709551615\",\"n\":[1,2,3],\"rt\":\"NPC\"}," +
                                "\"2\":{\"h\":42,\"rt\":\"PLAYER\",\"pl\":\"Zażółć gęślą jaźń\"}}";
            var expected = ReadPropertyNames(Utf8(json));
            var actual = ReadPropertyNames(new ChokedStream(Utf8(json), chunk));
            CollectionAssert.AreEqual(expected, actual, $"chunk size {chunk} changed the token stream");
        }

        [TestMethod]
        public void PreservesNonAsciiValues()
        {
            using (var r = new Utf8StreamJsonReader(Utf8("{\"pl\":\"Zażółć gęślą jaźń\"}")))
            {
                Assert.IsTrue(r.Read());
                Assert.IsTrue(r.Read());
                Assert.IsTrue(r.Read());
                Assert.AreEqual("Zażółć gęślą jaźń", r.GetString());
            }
        }
    }
}
