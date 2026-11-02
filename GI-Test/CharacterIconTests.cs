using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Services.Characters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class CharacterIconTests
    {
        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        private static string Json => CharacterCatalogTests.MinimalJson
            .Replace(@"""schema"": 1, ""game"": ""starrail"",",
                @"""schema"": 1, ""game"": ""starrail"",
                  ""icon_base"": { ""character"": ""https://img.example/avatar/"", ""weapon"": ""https://img.example/lc/"", ""set"": ""https://img.example/relic/"" },
                  ""icon_ext"": { ""character"": "".png"", ""weapon"": "".png"", ""set"": "".png"" },")
            .Replace(@"""id"": ""1001"", ""name"": ""March 7th"",", @"""id"": ""1001"", ""name"": ""March 7th"", ""icon"": ""1001"",")
            .Replace(@"""id"": ""1002"", ""name"": ""Dan Heng"",", @"""id"": ""1002"", ""name"": ""Dan Heng"", ""icon"": ""1002"",")
            .Replace(@"""teammates"": []", @"""teammates"": [""1002""]")
            .Replace(@"""name"": ""Hunter of Glacial Forest"",", @"""name"": ""Hunter of Glacial Forest"", ""icon"": ""71003"",")
            .Replace(@"""name"": ""In the Name of the World"",", @"""name"": ""In the Name of the World"", ""icon"": ""23004"",");

        private static CharacterCatalog Catalog(string json = null) =>
            CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json ?? Json)), "starrail");

        [TestMethod]
        public void Catalog_BuildsIconUrlsPerKind()
        {
            var c = Catalog();
            Assert.AreEqual("https://img.example/avatar/1001.png", c.IconUrl(IconKind.Character, c.Find("1001").Icon));
            Assert.AreEqual("https://img.example/relic/71003.png", c.IconUrl(IconKind.Set, c.Sets["103"].Icon));
            Assert.AreEqual("https://img.example/lc/23004.png", c.IconUrl(IconKind.Weapon, c.Weapons["23004"].Icon));
            Assert.IsNull(c.IconUrl(IconKind.Character, null));
            Assert.IsNull(c.IconUrl(IconKind.Character, ""), "no icon field, no URL");
        }

        [TestMethod]
        public void Catalog_WithoutIconBase_HasNoUrls()
        {
            var c = Catalog(Json.Replace(@"""icon_base""", @"""unused""").Replace(@"""icon_ext""", @"""unused2"""));
            Assert.IsNull(c.IconUrl(IconKind.Character, "1001"));
        }

        [TestMethod]
        public void Catalog_RejectsUnsafeIconNames()
        {
            var c = Catalog();
            Assert.IsNull(c.IconUrl(IconKind.Character, "../../evil"));
            Assert.IsNull(c.IconUrl(IconKind.Character, "a b"));
            Assert.IsNull(c.IconUrl(IconKind.Character, "x?y=1"));
            Assert.IsNull(c.IconUrl(IconKind.Character, "x#y"));
        }

        [TestMethod]
        public void Catalog_AllowsAmpersandInIconNames()
        {
            // A real ZZZ set: "SuitBranch&BladeSong". '&' is legal in a URL path and in a file name.
            var c = Catalog();
            Assert.AreEqual("https://img.example/relic/SuitBranch&BladeSong.png", c.IconUrl(IconKind.Set, "SuitBranch&BladeSong"));
        }

        [TestMethod]
        public void Builder_CarriesIconUrls()
        {
            var c = Catalog();
            var m = CharacterOverviewBuilder.Build(c.Find("1001"), c, null);
            Assert.AreEqual("https://img.example/avatar/1001.png", m.IconUrl);
            Assert.AreEqual("https://img.example/relic/71003.png", m.SetGroups[0].Options[0].IconUrl);
            Assert.AreEqual("https://img.example/lc/23004.png", m.Weapons[0].IconUrl);
            Assert.IsNull(m.IconPath, "paths come from the cache, never the builder");
        }

        private sealed class Handler : HttpMessageHandler
        {
            public int Calls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            {
                Interlocked.Increment(ref Calls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Png) { Headers = { { "Content-Type", "image/png" } } },
                });
            }
        }

        [TestMethod]
        public async Task Icons_FetchMissing_ThenApplyCachedFillsEveryPath()
        {
            string dir = Path.Combine(Path.GetTempPath(), "kaption-icons-" + Guid.NewGuid().ToString("N"));
            try
            {
                var h = new Handler();
                var cache = new CharacterImageCache(dir, new HttpClient(h));
                var c = Catalog();
                var m = CharacterOverviewBuilder.Build(c.Find("1001"), c, null);

                CharacterIcons.ApplyCached(m, cache);
                Assert.IsNull(m.IconPath);

                Assert.IsTrue(await CharacterIcons.FetchMissingAsync(m, cache));
                Assert.AreEqual(4, h.Calls);
                Assert.IsTrue(File.Exists(m.IconPath));
                Assert.IsTrue(File.Exists(m.SetGroups[0].Options[0].IconPath));
                Assert.IsTrue(File.Exists(m.Weapons[0].IconPath));
                Assert.IsTrue(File.Exists(m.TeammateCards[0].IconPath));

                var again = CharacterOverviewBuilder.Build(c.Find("1001"), c, null);
                CharacterIcons.ApplyCached(again, cache);
                Assert.AreEqual(m.IconPath, again.IconPath);
                Assert.IsFalse(await CharacterIcons.FetchMissingAsync(again, cache), "nothing new to fetch");
                Assert.AreEqual(4, h.Calls);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [TestMethod]
        public async Task Icons_NullInputs_AreHarmless()
        {
            CharacterIcons.ApplyCached(null, null);
            Assert.IsFalse(await CharacterIcons.FetchMissingAsync(null, null));
        }
    }
}
