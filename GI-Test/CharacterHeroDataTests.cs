using System;
using System.Collections.Generic;
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
    /// <summary>
    /// The visual layer of the characters section (plan, Phase D): splash and skill icons, the AI ambience
    /// image and AI team comps. Every field is optional; the VPS adds `ai.image` / `ai.teams` later.
    /// </summary>
    [TestClass]
    public class CharacterHeroDataTests
    {
        private const string Ai = @"""ai"": {
            ""model"": ""gpt-6.1-terra"",
            ""image"": { ""url"": ""https://files.example/content/characters/starrail/img/1001.jpg"", ""w"": 1024, ""h"": 576,
                         ""alt"": { ""en"": ""Frost over a quiet station"", ""pl"": ""Szron nad cichą stacją"" } },
            ""teams"": [
              { ""name"": { ""en"": ""Freeze"", ""pl"": ""Zamrożenie"" }, ""source"": ""ai"", ""members"": [""1001"", ""1003""],
                ""roles"": { ""1001"": { ""en"": ""Shield"", ""pl"": ""Tarcza"" }, ""1003"": ""Damage"" },
                ""why"": { ""en"": ""Freeze keeps enemies busy."", ""pl"": ""Zamrożenie trzyma wrogów w miejscu."" } },
              { ""name"": ""Hypercarry"", ""source"": ""Official"", ""members"": [""1001"", ""1002"", ""9999""] },
              { ""name"": { ""en"": ""Meta"" }, ""source"": ""meta-tool"", ""members"": [""1002"", ""1003""] },
              { ""name"": { ""en"": ""Broken"" }, ""source"": ""ai"", ""members"": ""1001"" },
              { ""name"": { ""en"": ""Alone"" }, ""source"": ""ai"", ""members"": [""9999""] }
            ] }";

        internal static string Json(bool ai = true) => CharacterCatalogTests.MinimalJson
            .Replace(@"""schema"": 1, ""game"": ""starrail"",",
                @"""schema"": 1, ""game"": ""starrail"",
                  ""icon_base"": { ""character"": ""https://img.example/avatar/"", ""splash"": ""https://img.example/large/"",
                                   ""skill"": ""https://img.example/skill/"" },
                  ""icon_ext"": { ""character"": "".png"", ""splash"": "".png"", ""skill"": "".png"" },")
            .Replace(@"""id"": ""1001"", ""name"": ""March 7th"",",
                @"""id"": ""1001"", ""name"": ""March 7th"", ""icon"": ""1001"", ""splash"": ""1001"",")
            .Replace(@"{ ""kind"": ""basic"", ""name"": ""Frigid Cold Arrow"",",
                @"{ ""kind"": ""basic"", ""name"": ""Frigid Cold Arrow"", ""icon"": ""SkillIcon_1001_Normal"",")
            .Replace(@"""sub_stats"": [""SpeedDelta""], ""weapons"": [""23004""], ""teammates"": [] } },",
                @"""sub_stats"": [""SpeedDelta""], ""weapons"": [""23004""], ""teammates"": [] }" + (ai ? ", " + Ai : "") + @" },")
            .Replace(@"{ ""id"": ""1002"", ""name"": ""Dan Heng"",",
                @"{ ""id"": ""1003"", ""name"": ""Himeko"", ""icon"": ""1003"", ""rarity"": 5, ""element"": ""Fire"", ""skills"": [] },
                  { ""id"": ""1002"", ""name"": ""Dan Heng"", ""icon"": ""1002"",");

        private static CharacterCatalog Catalog(string json) =>
            CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "starrail");

        private sealed class DictLookup : ITranslationLookup
        {
            private readonly Dictionary<string, string> _map;
            public DictLookup(Dictionary<string, string> map) => _map = map;
            public bool TryTranslateExact(string english, out string translated) => _map.TryGetValue(english ?? "", out translated);
        }

        [TestMethod]
        public void Catalog_ReadsSplashAndSkillIcons()
        {
            var c = Catalog(Json());
            var march = c.Find("1001");
            Assert.AreEqual("1001", march.Splash);
            Assert.AreEqual("SkillIcon_1001_Normal", march.Skills[0].Icon);
            Assert.AreEqual("https://img.example/large/1001.png", c.IconUrl(IconKind.Splash, march.Splash));
            Assert.AreEqual("https://img.example/skill/SkillIcon_1001_Normal.png", c.IconUrl(IconKind.Skill, march.Skills[0].Icon));
            Assert.AreEqual("", c.Find("1002").Splash, "absent splash stays empty");
        }

        [TestMethod]
        public void Catalog_NoSplashBase_NoSplashUrl()
        {
            var c = Catalog(Json().Replace(@"""splash"": ""https://img.example/large/"",", ""));
            Assert.IsNull(c.IconUrl(IconKind.Splash, "1001"), "a kind without a base is not available; never guess a URL");
        }

        [TestMethod]
        public void Catalog_ReadsAiImageAndTeams_Tolerantly()
        {
            var ai = Catalog(Json()).Find("1001").Ai;
            Assert.IsNotNull(ai);
            Assert.AreEqual("https://files.example/content/characters/starrail/img/1001.jpg", ai.Image.Url);
            Assert.AreEqual(1024, ai.Image.Width);
            Assert.AreEqual(576, ai.Image.Height);
            Assert.AreEqual("Szron nad cichą stacją", ai.Image.Alt.Pick());

            // "members" as a string and a team without members are dropped; the rest keep data order.
            Assert.AreEqual(4, ai.Teams.Count);
            var freeze = ai.Teams[0];
            Assert.AreEqual("Zamrożenie", freeze.Name.Pick());
            Assert.AreEqual("ai", freeze.Source);
            CollectionAssert.AreEqual(new[] { "1001", "1003" }, freeze.Members.ToArray());
            Assert.AreEqual("Tarcza", freeze.Roles["1001"].Pick());
            Assert.AreEqual("Damage", freeze.Roles["1003"].Pick(), "a plain string is accepted");
            Assert.AreEqual("Zamrożenie trzyma wrogów w miejscu.", freeze.Why.Pick());
            Assert.AreEqual("official", ai.Teams[1].Source, "sources compare lower-case");
            Assert.AreEqual("Hypercarry", ai.Teams[1].Name.Pick());
            Assert.AreEqual("meta-tool", ai.Teams[2].Source, "unknown sources are kept");
        }

        [TestMethod]
        public void Catalog_RejectsNonHttpsAmbienceImage()
        {
            var ai = Catalog(Json().Replace("https://files.example/content", "http://files.example/content")).Find("1001").Ai;
            Assert.IsNull(ai.Image);
            Assert.AreEqual(4, ai.Teams.Count, "a bad image does not cost the teams");
        }

        [TestMethod]
        public void Catalog_WithoutTheVisualFields_LoadsAsBefore()
        {
            var c = Catalog(Json(ai: false));
            Assert.IsNull(c.Find("1001").Ai);
            Assert.AreEqual(3, c.Characters.Count);
        }

        [TestMethod]
        public void Builder_SkillsCarryTheEnglishNameAndIcon()
        {
            var c = Catalog(Json());
            var m = CharacterOverviewBuilder.Build(c.Find("1001"), c,
                new DictLookup(new Dictionary<string, string> { ["Frigid Cold Arrow"] = "Mroźna strzała" }));
            Assert.AreEqual("Mroźna strzała", m.Skills[0].Name);
            Assert.AreEqual("Frigid Cold Arrow", m.Skills[0].EnglishName);
            Assert.AreEqual("https://img.example/skill/SkillIcon_1001_Normal.png", m.Skills[0].IconUrl);
            Assert.AreEqual("https://img.example/large/1001.png", m.SplashUrl);
            Assert.IsNull(m.Skills[0].IconPath, "paths come from the cache, never the builder");
        }

        [TestMethod]
        public void Builder_Ambience_PolishAltFirst()
        {
            var c = Catalog(Json());
            var m = CharacterOverviewBuilder.Build(c.Find("1001"), c, null);
            Assert.AreEqual("https://files.example/content/characters/starrail/img/1001.jpg", m.AmbienceUrl);
            Assert.AreEqual("Szron nad cichą stacją", m.AmbienceAlt);
            Assert.AreEqual(1024.0 / 576.0, m.AmbienceAspect, 1e-9);
        }

        [TestMethod]
        public void Builder_Teams_OfficialFirst_KnownMembersOnly()
        {
            var c = Catalog(Json());
            var m = CharacterOverviewBuilder.Build(c.Find("1001"), c, null);

            CollectionAssert.AreEqual(new[] { "official", "ai", "meta-tool" }, m.Teams.Select(t => t.Source).ToArray());
            var official = m.Teams[0];
            CollectionAssert.AreEqual(new[] { "March 7th", "Dan Heng" }, official.Members.Select(x => x.Name).ToArray(),
                "an id missing from the roster is dropped");
            Assert.AreEqual("https://img.example/avatar/1002.png", official.Members[1].IconUrl);
            var freeze = m.Teams[1];
            Assert.AreEqual("Zamrożenie", freeze.Name);
            Assert.AreEqual("Tarcza", freeze.Members[0].Role);
            Assert.AreEqual("Himeko", freeze.Members[1].Name, "character names stay English");
            Assert.AreEqual("Zamrożenie trzyma wrogów w miejscu.", freeze.Why);
        }

        [TestMethod]
        public void Builder_Leveling_UsesTheEnglishSkillNames()
        {
            var json = Json().Replace(@"""role"": { ""t"": ""Shields allies"", ""p"": [] },",
                @"""role"": { ""t"": ""Shields allies"", ""p"": [] }, ""leveling"": [0],");
            var c = Catalog(json);
            var m = CharacterOverviewBuilder.Build(c.Find("1001"), c,
                new DictLookup(new Dictionary<string, string> { ["Frigid Cold Arrow"] = "Mroźna strzała" }));
            Assert.AreEqual("Frigid Cold Arrow", m.Highlights.Single(h => h.LabelKey == "Char_Tip_Leveling").Text,
                "the game shows English skill names, so references use them");
            CollectionAssert.AreEqual(new[] { "Frigid Cold Arrow" }, m.Leveling.ToArray());
            Assert.IsFalse(m.LevelingIsAi);
        }

        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        private sealed class Handler : HttpMessageHandler
        {
            public readonly List<string> Urls = new List<string>();
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            {
                lock (Urls) Urls.Add(r.RequestUri.AbsoluteUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Png) { Headers = { { "Content-Type", "image/png" } } },
                });
            }
        }

        [TestMethod]
        public async Task Icons_CoverSkillsSplashAmbienceAndTeams()
        {
            string dir = Path.Combine(Path.GetTempPath(), "kaption-icons-" + Guid.NewGuid().ToString("N"));
            try
            {
                var h = new Handler();
                var cache = new CharacterImageCache(dir, new HttpClient(h));
                var c = Catalog(Json());
                var m = CharacterOverviewBuilder.Build(c.Find("1001"), c, null);

                Assert.IsTrue(await CharacterIcons.FetchMissingAsync(m, cache));
                Assert.IsTrue(File.Exists(m.Skills[0].IconPath));
                Assert.IsTrue(File.Exists(m.SplashPath));
                Assert.IsTrue(File.Exists(m.AmbiencePath));
                Assert.IsTrue(m.Teams.SelectMany(t => t.Members).All(x => File.Exists(x.IconPath)));

                var again = CharacterOverviewBuilder.Build(c.Find("1001"), c, null);
                CharacterIcons.ApplyCached(again, cache);
                Assert.AreEqual(m.SplashPath, again.SplashPath);
                Assert.AreEqual(m.Skills[0].IconPath, again.Skills[0].IconPath);
                int calls = h.Urls.Count;
                Assert.IsFalse(await CharacterIcons.FetchMissingAsync(again, cache), "everything is on disk");
                Assert.AreEqual(calls, h.Urls.Count);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [TestMethod]
        public async Task ImageCache_LargeCapOnlyWhereAsked()
        {
            string dir = Path.Combine(Path.GetTempPath(), "kaption-icons-" + Guid.NewGuid().ToString("N"));
            try
            {
                var big = new byte[CharacterImageCache.MaxBytes + 1024];
                Array.Copy(Png, big, Png.Length);
                var http = new HttpClient(new FixedHandler(big));
                var cache = new CharacterImageCache(dir, http);
                Assert.IsNull(await cache.GetAsync("https://img.example/icon/a.png"), "icons keep the 2 MB cap");
                Assert.IsNotNull(await cache.GetAsync("https://img.example/large/a.png", CharacterImageCache.LargeMaxBytes));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private sealed class FixedHandler : HttpMessageHandler
        {
            private readonly byte[] _body;
            public FixedHandler(byte[] body) => _body = body;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_body) { Headers = { { "Content-Type", "image/png" } } },
                });
        }
    }
}
