using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// The character's own page on kaption.one (owner, 2.5.0): the site's slug rule ported from
    /// tools/landing-characters/transform.cjs, Polish or English by the interface, the bundle's slug first, and
    /// every character of the current bundles proven against the site's sitemap.
    /// </summary>
    [TestClass]
    public class CharacterPageLinkTests
    {
        // ── the port, with the site's own cases (tools/test-build-landing-characters.cjs) ──

        [TestMethod]
        public void Slugify_FoldsToLowercaseAscii_AsTheSiteDoes()
        {
            Assert.AreEqual("topaz-and-numby", CharacterPageLink.Slugify("Topaz & Numby"));
            Assert.AreEqual("dan-heng-imbibitor-lunae", CharacterPageLink.Slugify("Dan Heng • Imbibitor Lunae"));
            Assert.AreEqual("silver-wolf-lv-999", CharacterPageLink.Slugify("Silver Wolf LV.999"));
            Assert.AreEqual("traveler-hydro", CharacterPageLink.Slugify("Traveler (Hydro)"));
            Assert.AreEqual("soldier-0-anby", CharacterPageLink.Slugify("Soldier 0 - Anby"));
            Assert.AreEqual("lucja-zolc-aesir", CharacterPageLink.Slugify("Łucja Żółć Æsir"));
            Assert.AreEqual("", CharacterPageLink.Slugify(""));
            Assert.AreEqual("", CharacterPageLink.Slugify(null));
        }

        [TestMethod]
        public void AssignSlugs_TheLowestIdKeepsThePlainName_WhateverTheInputOrder()
        {
            var chars = new[]
            {
                new CharacterRecord { Id = "1224", Name = "March 7th", WeaponType = "The Hunt", Element = "Imaginary" },
                new CharacterRecord { Id = "1001", Name = "March 7th", WeaponType = "Preservation", Element = "Ice" },
                new CharacterRecord { Id = "1900", Name = "March 7th", WeaponType = "The Hunt", Element = "Fire" },
                new CharacterRecord { Id = "42", Name = "???" },
            };
            var a = CharacterPageLink.AssignSlugs(chars);
            var b = CharacterPageLink.AssignSlugs(chars.Reverse());
            CollectionAssert.AreEquivalent(a.OrderBy(p => p.Key).ToList(), b.OrderBy(p => p.Key).ToList());
            Assert.AreEqual("march-7th", a["1001"]);
            Assert.AreEqual("march-7th-the-hunt", a["1224"]);
            Assert.AreEqual("march-7th-fire", a["1900"]);
            Assert.AreEqual("character-42", a["42"]);
        }

        [TestMethod]
        public void Url_PolishInterface_OpensThePolishPage_EveryOtherTheEnglishOne()
        {
            Assert.AreEqual("https://kaption.one/pl/postacie/genshin-impact/chiori/", CharacterPageLink.PageUrl("genshin-impact", "chiori", "pl-PL"));
            Assert.AreEqual("https://kaption.one/pl/postacie/genshin-impact/chiori/", CharacterPageLink.PageUrl("genshin-impact", "chiori", "pl"));
            foreach (string other in new[] { "en-US", "en", "ja-JP", "zh-CN", "", null })
                Assert.AreEqual("https://kaption.one/characters/genshin-impact/chiori/", CharacterPageLink.PageUrl("genshin-impact", "chiori", other),
                    $"the site has Polish and English pages only ({other ?? "null"})");

            Assert.AreEqual("genshin-impact", CharacterPageLink.GameSlug("genshin"));
            Assert.AreEqual("honkai-star-rail", CharacterPageLink.GameSlug("starrail"));
            Assert.AreEqual("honkai-star-rail", CharacterPageLink.GameSlug("StarRail"), "profile ids too");
            Assert.AreEqual("zenless-zone-zero", CharacterPageLink.GameSlug("zzz"));
            Assert.AreEqual("", CharacterPageLink.GameSlug("wutheringwaves"), "no pages, no link");
        }

        [TestMethod]
        public void TheBundlesSlugOrPageUrl_WinsOverThePort_InvalidOnesAreIgnored()
        {
            string json = CharacterCatalogTests.MinimalJson
                .Replace(@"""id"": ""1001"", ""name"": ""March 7th"",", @"""id"": ""1001"", ""name"": ""March 7th"", ""slug"": ""march-7th-preservation"",")
                .Replace(@"""id"": ""1002"", ""name"": ""Dan Heng"",",
                    @"""id"": ""1002"", ""name"": ""Dan Heng"", ""page_url"": ""https://kaption.one/characters/honkai-star-rail/dan-heng-canonical/"",");
            var c = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "starrail");

            Assert.AreEqual("https://kaption.one/pl/postacie/honkai-star-rail/march-7th-preservation/", CharacterPageLink.For(c.Find("1001"), c, "pl-PL"));
            Assert.AreEqual("https://kaption.one/pl/postacie/honkai-star-rail/dan-heng-canonical/", CharacterPageLink.For(c.Find("1002"), c, "pl-PL"),
                "a page URL from the bundle gives the slug; the language still follows the interface");
            Assert.AreEqual("https://kaption.one/characters/honkai-star-rail/dan-heng-canonical/", CharacterPageLink.For(c.Find("1002"), c, "en-US"));

            string bad = CharacterCatalogTests.MinimalJson
                .Replace(@"""id"": ""1001"", ""name"": ""March 7th"",",
                    @"""id"": ""1001"", ""name"": ""March 7th"", ""slug"": ""March 7th!"", ""page_url"": ""https://evil.example/x/"",");
            var cb = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(bad)), "starrail");
            Assert.AreEqual("", cb.Find("1001").Slug);
            Assert.AreEqual("", cb.Find("1001").PageUrl, "only https pages on kaption.one");
            Assert.AreEqual("https://kaption.one/pl/postacie/honkai-star-rail/march-7th/", CharacterPageLink.For(cb.Find("1001"), cb, "pl-PL"), "the port");
        }

        [TestMethod]
        public void TheHubSearch_IsTheLastResort()
        {
            Assert.AreEqual("https://kaption.one/pl/postacie/genshin-impact/?q=Traveler%20%28Hydro%29",
                CharacterPageLink.HubUrl("genshin-impact", "Traveler (Hydro)", "pl-PL"));
            Assert.AreEqual("https://kaption.one/characters/zenless-zone-zero/?q=Ellen", CharacterPageLink.HubUrl("zenless-zone-zero", "Ellen", "en-US"));
            Assert.AreEqual("", CharacterPageLink.HubUrl("", "Ellen", "en-US"));
            // A record always yields a slug (its name, else "character-<id>"), so the hub is only for a game without pages.
            var record = new CharacterRecord { Id = "7", Name = "" };
            var catalog = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(CharacterCatalogTests.MinimalJson)), "starrail");
            StringAssert.StartsWith(CharacterPageLink.For(record, catalog, "pl-PL"), "https://kaption.one/pl/postacie/honkai-star-rail/");
        }

        // ── the golden check: every character of the three current bundles has its page on the site ──

        [TestMethod]
        public void EveryCharacterInTheBundles_MapsToAPageInTheSitemap()
        {
            string root = RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            var site = new HashSet<string>(File.ReadAllLines(Path.Combine(root, "GI-Test", "Fixtures", "site-character-urls.txt"))
                .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal)), StringComparer.Ordinal);
            Assert.AreEqual(576, site.Count, "the fixture is the sitemap's character section");

            var misses = new List<string>();
            int checkedCount = 0;
            foreach (string game in new[] { "genshin", "starrail", "zzz" })
            {
                var catalog = CharacterOverviewRenderTests.LoadCatalog(root, game);
                if (catalog == null) Assert.Inconclusive($"No tools/out/{game}/characters-*.json found.");
                foreach (var record in catalog.Characters)
                {
                    foreach (string lang in new[] { "pl-PL", "en-US" })
                    {
                        string url = CharacterPageLink.For(record, catalog, lang);
                        checkedCount++;
                        if (!site.Contains(url) || url.Contains("?q=")) misses.Add($"{game} {record.Id} {record.Name} ({lang}): {url}");
                    }
                }
            }
            foreach (string m in misses) Console.WriteLine("miss: " + m);
            Console.WriteLine($"{checkedCount} URLs checked, {misses.Count} misses");
            Assert.AreEqual(568, checkedCount, "284 characters in the three bundles, two languages each");
            Assert.AreEqual(0, misses.Count, string.Join("\n", misses));
        }

        // ── the view: one filled button at the top, a quiet line at the end ──

        [TestMethod]
        public void TheTopButton_OpensThePageInTheInterfaceLanguage_TheEndKeepsOneQuietLine()
        {
            RunSta(() =>
            {
                foreach (var (lang, label, url) in new[]
                {
                    ("pl-PL", "Pełny przewodnik na kaption.one", "https://kaption.one/pl/postacie/genshin-impact/chiori/"),
                    ("en-US", "Full guide on kaption.one", "https://kaption.one/characters/genshin-impact/chiori/"),
                })
                {
                    var model = new CharacterOverviewModel
                    {
                        Name = "Chiori", EnglishName = "Chiori", Game = "genshin", ElementKey = "Geo", Element = "Geo", Rarity = 5,
                        PageUrlPl = "https://kaption.one/pl/postacie/genshin-impact/chiori/",
                        PageUrlEn = "https://kaption.one/characters/genshin-impact/chiori/",
                        Links = new[] { new OverviewLink { Site = "game8", Label = "Game8", Url = "https://game8.co/games/Genshin-Impact/archives/1", IconKey = "game8", Monogram = "8" } },
                    };
                    var view = new CharacterOverviewView(model);
                    var host = new Border { Child = view };
                    host.Resources.MergedDictionaries.Add(Dictionary("Themes/Palette.Light.xaml"));
                    host.Resources.MergedDictionaries.Add(Dictionary("Resources/CharacterPaletteLight.xaml"));
                    host.Resources.MergedDictionaries.Add(Dictionary("Resources/CharacterLayoutPage.xaml"));
                    foreach (var d in Strings(lang)) host.Resources.MergedDictionaries.Add(d);
                    Layout(host, 900);
                    Layout(host, 900); // the lower sections join one frame later

                    var top = (Button)view.FindName("SitePageButton");
                    Assert.AreEqual(Visibility.Visible, top.Visibility);
                    Assert.AreEqual(url, top.Tag, lang);
                    Assert.AreEqual(url, top.ToolTip, "the tooltip shows the real URL");
                    Assert.AreEqual(label, AutomationProperties.GetName(top));

                    var more = (Button)view.FindName("MoreOnSiteLink");
                    Assert.AreEqual(url, more.Tag, "the quiet line opens the same page");
                    var links = (FrameworkElement)view.FindName("Section_Links");
                    Assert.IsFalse(Descendants<Button>(links).Any(b => b.Style == view.TryFindResource("KBrandButton")),
                        "no second big button at the end");
                }
            });
        }
    }
}
