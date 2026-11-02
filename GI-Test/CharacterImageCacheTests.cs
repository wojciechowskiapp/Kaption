using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Services.Characters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class CharacterImageCacheTests
    {
        // Smallest valid PNG: the cache checks the signature, not just the content type.
        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        private string _dir;

        [TestInitialize]
        public void Setup() => _dir = Path.Combine(Path.GetTempPath(), "kaption-icons-" + Guid.NewGuid().ToString("N"));

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
            public int Calls;
            public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref Calls);
                return Task.FromResult(_respond(request));
            }
        }

        private static HttpResponseMessage Image(byte[] body, string type = "image/png") =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { { "Content-Type", type } } } };

        [TestMethod]
        public async Task Downloads_Once_ThenServesFromDisk()
        {
            var h = new FakeHandler(_ => Image(Png));
            var cache = new CharacterImageCache(_dir, new HttpClient(h));

            string a = await cache.GetAsync("https://img.example/UI_AvatarIcon_Ayaka.png");
            string b = await cache.GetAsync("https://img.example/UI_AvatarIcon_Ayaka.png");

            Assert.IsNotNull(a);
            Assert.AreEqual(a, b);
            Assert.IsTrue(File.Exists(a));
            CollectionAssert.AreEqual(Png, File.ReadAllBytes(a));
            Assert.AreEqual(1, h.Calls);

            var fresh = new CharacterImageCache(_dir, new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("no network"))));
            Assert.AreEqual(a, await fresh.GetAsync("https://img.example/UI_AvatarIcon_Ayaka.png"), "a restart reuses the disk copy");
        }

        [TestMethod]
        public async Task CachedPathOrNull_NeverTouchesNetwork()
        {
            var h = new FakeHandler(_ => Image(Png));
            var cache = new CharacterImageCache(_dir, new HttpClient(h));
            Assert.IsNull(cache.CachedPathOrNull("https://img.example/a.png"));
            await cache.GetAsync("https://img.example/a.png");
            Assert.IsNotNull(cache.CachedPathOrNull("https://img.example/a.png"));
            Assert.AreEqual(1, h.Calls);
        }

        [TestMethod]
        public async Task RejectsNonImages_HttpErrors_AndOversizedBodies()
        {
            var cache = new CharacterImageCache(_dir, new HttpClient(new FakeHandler(r =>
                r.RequestUri.AbsolutePath.Contains("html") ? Image(System.Text.Encoding.UTF8.GetBytes("<html>"), "text/html") :
                r.RequestUri.AbsolutePath.Contains("404") ? new HttpResponseMessage(HttpStatusCode.NotFound) :
                r.RequestUri.AbsolutePath.Contains("fake") ? Image(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }) :
                Image(Png.Concat(new byte[CharacterImageCache.MaxBytes]).ToArray()))));

            Assert.IsNull(await cache.GetAsync("https://img.example/page.html"));
            Assert.IsNull(await cache.GetAsync("https://img.example/404.png"));
            Assert.IsNull(await cache.GetAsync("https://img.example/fake.png"));
            Assert.IsNull(await cache.GetAsync("https://img.example/huge.png"));
            Assert.AreEqual(0, Directory.Exists(_dir) ? Directory.GetFiles(_dir).Length : 0, "nothing half-written stays behind");
        }

        [TestMethod]
        public async Task FailureIsRemembered_NoRetryStorm()
        {
            var h = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
            var cache = new CharacterImageCache(_dir, new HttpClient(h));
            for (int i = 0; i < 5; i++) Assert.IsNull(await cache.GetAsync("https://img.example/down.png"));
            Assert.AreEqual(1, h.Calls);
        }

        [TestMethod]
        public async Task RejectsNonHttpsAndGarbageUrls()
        {
            var h = new FakeHandler(_ => Image(Png));
            var cache = new CharacterImageCache(_dir, new HttpClient(h));
            foreach (var bad in new[] { null, "", "not a url", "http://img.example/a.png", "file:///C:/Windows/win.ini", "https://img.example/../x.png" })
                Assert.IsNull(await cache.GetAsync(bad), bad ?? "null");
            Assert.AreEqual(0, h.Calls);
        }

        [TestMethod]
        public async Task ConcurrentRequestsForOneUrl_DownloadOnce()
        {
            var h = new FakeHandler(_ => { Thread.Sleep(50); return Image(Png); });
            var cache = new CharacterImageCache(_dir, new HttpClient(h));
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetAsync("https://img.example/same.png")));
            Assert.IsTrue(results.All(r => r != null && r == results[0]));
            Assert.AreEqual(1, h.Calls);
        }
    }
}
