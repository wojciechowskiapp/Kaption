using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// A rotation's origin (session 54 round 5): "guide" (transcribed from a page; also when absent) or "model" (composed from
    /// the members' kits, headed "Suggested rotation"), and a guide rotation's source folded into its heading ("Rotacja · wg
    /// KQM ↗"). Loading, the host table, the heading and its tooltip per origin and language, the strings.
    /// </summary>
    [TestClass]
    public class CharacterRotationOriginTests
    {
        private const string Varka = CharacterRotationTests.Varka, Durin = CharacterRotationTests.Durin,
            Prune = CharacterRotationTests.Prune, Chiori = CharacterRotationTests.Chiori;

        /// <summary>Chiori's Geo team with one rotation composed from the kits (the same turns as the guide sample), no source.</summary>
        internal const string GeoModelRotation = """
            [
              { "origin": "model", "covers": true,
                "steps": [ { "member": "10000038", "actions": [ { "a": "E" } ] },
                           { "member": "10000094", "actions": [ { "a": "E" }, { "a": "Q" } ] },
                           { "member": "10000103", "actions": [ { "a": "E" }, { "a": "N", "n": 2 }, { "a": "Q" } ] },
                           { "member": "10000057", "actions": [ { "a": "Q" }, { "a": "E" }, { "a": "combo" } ] } ],
                "combo": { "10000057": "N2 > CA > N2 > CA > Arataki Kesagiri Final Slash" } }
            ]
            """;

        /// <summary>The same rotation adapted from a guide's rotation for a similar team: suggested, with that guide as its source.</summary>
        internal static readonly string GeoAdaptedRotation = GeoModelRotation.Replace(
            "\"origin\": \"model\",", "\"origin\": \"model\", \"adapted\": true, \"source\": \"https://keqingmains.com/q/chiori-quickguide/\",");

        /// <summary>Varka's team from a guide (no origin written, as every bundle before round 5), Chiori's suggested.</summary>
        internal static CharacterCatalog Mixed() =>
            CharacterRotationTests.Load(CharacterRotationTests.GenshinJson(CharacterRotationTests.VarkaRotations, GeoModelRotation));

        /// <summary>As <see cref="Mixed"/>, Chiori's rotation adapted from a guide.</summary>
        internal static CharacterCatalog Adapted() =>
            CharacterRotationTests.Load(CharacterRotationTests.GenshinJson(CharacterRotationTests.VarkaRotations, GeoAdaptedRotation));

        private static IReadOnlyList<TeamRotation> Loaded(string rotations) =>
            CharacterRotationTests.Genshin(rotations).Find(Varka).Ai.Teams.Single().Rotations;

        private static string One(string originJson, string member = Durin) =>
            "{ " + (originJson == null ? "" : "\"origin\": " + originJson + ", ") +
            "\"steps\": [ { \"member\": \"" + member + "\", \"actions\": [ { \"a\": \"E\" } ] } ] }";

        [TestMethod]
        public void Load_TheOrigin_AbsentOrNullIsAGuide()
        {
            foreach (var (json, expected) in new[]
                     {
                         ((string)null, RotationNotation.OriginGuide),
                         ("null", RotationNotation.OriginGuide),
                         ("\"guide\"", RotationNotation.OriginGuide),
                         ("\"model\"", RotationNotation.OriginModel),
                         ("\" model \"", RotationNotation.OriginModel),
                     })
                Assert.AreEqual(expected, Loaded("[ " + One(json) + " ]").Single().Origin, json ?? "absent");

            // Every bundle before round 5 carries no origin: all from a guide, as they were.
            var old = Loaded(CharacterRotationTests.VarkaRotations);
            Assert.AreEqual(2, old.Count);
            Assert.IsTrue(old.All(r => r.Origin == RotationNotation.OriginGuide));
            Assert.AreEqual(RotationNotation.OriginModel, Mixed().Find(Chiori).Ai.Teams.Single().Rotations.Single().Origin);
        }

        [TestMethod]
        public void Load_Adapted_AFlag_AndASuggestedRotationKeepsItsSource()
        {
            var adapted = Adapted().Find(Chiori).Ai.Teams.Single().Rotations.Single();
            Assert.AreEqual((RotationNotation.OriginModel, true, "https://keqingmains.com/q/chiori-quickguide/"), (adapted.Origin, adapted.Adapted, adapted.Source));
            Assert.IsNull(Mixed().Find(Chiori).Ai.Teams.Single().Rotations.Single().Adapted, "absent says nothing");

            foreach (var (json, expected) in new[] { ("false", (bool?)false), ("null", null), ("true", true) })
                Assert.AreEqual(expected, Loaded("[ { \"origin\": \"model\", \"adapted\": " + json + ", \"steps\": [ { \"member\": \"" + Durin +
                                                 "\", \"actions\": [ { \"a\": \"E\" } ] } ] } ]").Single().Adapted, json);

            // The URL rule is the guide's: a bad source on a suggested rotation is no source, the rotation stays.
            var bad = Loaded("[ { \"origin\": \"model\", \"adapted\": true, \"source\": \"http://keqingmains.com/q/\", \"steps\": [ { \"member\": \"" + Durin +
                             "\", \"actions\": [ { \"a\": \"E\" } ] } ] } ]").Single();
            Assert.AreEqual("", bad.Source);

            string good = "{ \"variant\": \"Prune\", \"steps\": [ { \"member\": \"" + Prune + "\", \"actions\": [ { \"a\": \"Q\" } ] } ] }";
            foreach (string value in new[] { "\"yes\"", "\"true\"", "1", "{}" })
            {
                var kept = Loaded("[ { \"origin\": \"model\", \"adapted\": " + value + ", \"steps\": [ { \"member\": \"" + Durin +
                                  "\", \"actions\": [ { \"a\": \"E\" } ] } ] }, " + good + " ]");
                Assert.AreEqual("Prune", kept.Single().Variant, value + ": that rotation alone is skipped");
            }
        }

        [TestMethod]
        public void Load_AnyOtherOrigin_SkipsThatRotationAlone()
        {
            string good = "{ \"origin\": \"model\", \"variant\": \"Prune\", \"steps\": [ { \"member\": \"" + Prune +
                          "\", \"actions\": [ { \"a\": \"Q\" } ] } ] }";
            foreach (string bad in new[] { "\"Model\"", "\"GUIDE\"", "\"ai\"", "\"page\"", "\"\"", "\" \"", "1", "true", "{}", "[]", "[ \"model\" ]" })
            {
                var kept = Loaded("[ " + One(bad) + ", " + good + " ]");
                Assert.AreEqual(1, kept.Count, bad);
                Assert.AreEqual("Prune", kept[0].Variant, bad);
                Assert.AreEqual(RotationNotation.OriginModel, kept[0].Origin, bad);
                Assert.AreEqual(1, CharacterRotationTests.Genshin("[ " + One(bad) + ", " + good + " ]").Find(Varka).Ai.Teams.Count, bad + ": the team stays");
            }
        }

        [TestMethod]
        public void Builder_AndViewModel_MarkASuggestedRotation()
        {
            var catalog = Mixed();
            var varka = CharacterOverviewBuilder.Build(catalog.Find(Varka), catalog, null);
            var chiori = CharacterOverviewBuilder.Build(catalog.Find(Chiori), catalog, null);
            Assert.IsTrue(varka.Teams.Single().Rotations.All(r => !r.IsSuggested), "a guide's rotations");
            var suggested = chiori.Teams.Single().Rotations.Single();
            Assert.IsTrue(suggested.IsSuggested);
            Assert.AreEqual(4, suggested.Steps.Count, "the same chips otherwise");

            Assert.IsTrue(new CharacterOverviewViewModel(chiori).Teams.Single().Rotations.Single().IsSuggested);
            Assert.IsTrue(new CharacterOverviewViewModel(varka).Teams.Single().Rotations.All(r => !r.IsSuggested));

            // Origin changes the heading only: the strip's text is the guide sample's.
            var guides = CharacterRotationTests.Genshin();
            string guideText = new CharacterOverviewViewModel(CharacterOverviewBuilder.Build(guides.Find(Chiori), guides, null)).Teams.Single().Rotations[0].Text;
            Assert.AreEqual(guideText, new CharacterOverviewViewModel(chiori).Teams.Single().Rotations.Single().Text);
        }

        // ── source ──────────────────────────────────────────────────────────

        private static string WithSource(string sourceJson) =>
            "{ \"source\": " + sourceJson + ", \"steps\": [ { \"member\": \"" + Durin + "\", \"actions\": [ { \"a\": \"E\" } ] } ] }";

        [TestMethod]
        public void Load_TheSource_AnHttpsUrlOrNone_TheRotationLoadsEitherWay()
        {
            string atCap = "https://keqingmains.com/" + new string('a', RotationSource.MaxLength - "https://keqingmains.com/".Length);
            foreach (var (json, expected) in new[]
                     {
                         ("\"https://keqingmains.com/q/varka-quickguide/\"", "https://keqingmains.com/q/varka-quickguide/"),
                         ("\"" + atCap + "\"", atCap),
                         ("\"http://keqingmains.com/varka/\"", ""),
                         ("\"ftp://keqingmains.com/varka/\"", ""),
                         ("\"javascript:alert(1)\"", ""),
                         ("\"/q/varka-quickguide/\"", ""),
                         ("\"keqingmains.com/varka/\"", ""),
                         ("\"https://user:pass@keqingmains.com/varka/\"", ""),
                         ("\"https://user@keqingmains.com/varka/\"", ""),
                         ("\"" + atCap + "a\"", ""),
                         ("\"\"", ""),
                         ("null", ""),
                         ("42", ""),
                         ("{ \"url\": \"https://keqingmains.com/\" }", ""),
                         ("[ \"https://keqingmains.com/\" ]", ""),
                     })
            {
                var r = Loaded("[ " + WithSource(json) + " ]");
                Assert.AreEqual(1, r.Count, json + ": the rotation still loads");
                Assert.AreEqual(expected, r[0].Source, json);
            }
            Assert.AreEqual("", Loaded("[ " + One(null) + " ]").Single().Source, "absent");
        }

        [TestMethod]
        public void SiteName_TheSharedHostTable()
        {
            foreach (var (url, name) in new[]
                     {
                         ("https://keqingmains.com/q/varka-quickguide/", "KQM"),
                         ("https://www.keqingmains.com/varka/", "KQM"),
                         ("https://old.keqingmains.com/varka/", "KQM"),
                         ("https://game8.co/games/Genshin-Impact/archives/1", "Game8"),
                         ("https://gamewith.net/genshin-impact/article/show/1", "GameWith"),
                         ("https://mobalytics.gg/genshin-impact/builds/varka", "Mobalytics"),
                         ("https://www.reddit.com/r/Genshin_Impact/comments/x/", "Reddit"),
                         ("https://old.reddit.com/r/Genshin_Impact/comments/x/", "Reddit"),
                         ("https://www.hoyolab.com/article/1", "HoYoLAB"),
                         ("https://www.prydwen.gg/star-rail/characters/acheron", "Prydwen"),
                         ("https://www.icy-veins.com/genshin-impact/varka-build", "Icy Veins"),
                         ("https://genshin.gg/characters/varka/", "genshin.gg"),
                         ("https://genshin-builds.com/characters/varka", "Genshin Builds"),
                         ("https://KEQINGMAINS.com/varka/", "KQM"),
                         ("https://example.org/varka", "example.org"),
                         ("https://www.example.org/varka", "example.org"),
                         ("https://m.gamewith.net/genshin-impact/", "m.gamewith.net"),
                         ("https://keqingmains.com.evil.example/", "keqingmains.com.evil.example"),
                         ("http://keqingmains.com/", ""),
                         ("", ""),
                         (null, ""),
                     })
                Assert.AreEqual(name, RotationSource.SiteName(url), url ?? "null");

            Assert.AreEqual("www.keqingmains.com", RotationSource.Host("https://www.keqingmains.com/varka/"), "the tooltip is the host");
        }

        /// <summary>The strip's words as the view reads them from Strings.{culture}.xaml.</summary>
        internal static RotationWords WordsOf(string culture)
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var doc = XDocument.Load(Path.Combine(RenderTestKit.RepoRoot(), "GI-Subtitles", "Resources", $"Strings.{culture}.xaml"));
            string W(string key) => doc.Root.Elements().Single(e => (string)e.Attribute(x + "Key") == key).Value;
            return new RotationWords(W("Char_Rotation_Hold"), W("Char_Rotation_Optional"), W("Char_Rotation_Either"))
            {
                Guide = W("Char_Rotation"), Suggested = W("Char_Rotation_Suggested"), Per = W("Char_Rotation_Per"),
                TipGuideFrom = W("Char_Rotation_TipGuideFrom"), TipGuide = W("Char_Rotation_TipGuide"), TipSuggested = W("Char_Rotation_TipSuggested"),
                TipSuggestedFrom = W("Char_Rotation_TipSuggestedFrom"),
            };
        }

        [TestMethod]
        public void Heading_PerOriginAndLanguage_WithAndWithoutASource()
        {
            var catalog = Mixed();
            var varka = CharacterOverviewBuilder.Build(catalog.Find(Varka), catalog, null);
            var chiori = CharacterOverviewBuilder.Build(catalog.Find(Chiori), catalog, null);
            var adaptedCatalog = Adapted();
            var adapted = CharacterOverviewBuilder.Build(adaptedCatalog.Find(Chiori), adaptedCatalog, null);
            Assert.AreEqual("https://keqingmains.com/q/chiori-quickguide/", adapted.Teams.Single().Rotations.Single().SourceUrl);

            foreach (var (culture, expected) in new[]
                     {
                         ("en-US", new[]
                         {
                             "Rotation", "per KQM", "From the KQM guide. Check it in play.",
                             "Rotation", "", "From a guide. Check it in play.",
                             "Suggested rotation", "", "Suggested from the team's kits. Check it in play.",
                             "Suggested rotation", "per KQM", "Suggested from the KQM guide's rotation for a similar team. Check it in play.",
                         }),
                         ("pl-PL", new[]
                         {
                             "Rotacja", "wg KQM", "Rotacja z poradnika KQM. Sprawdź ją w grze.",
                             "Rotacja", "", "Rotacja z poradnika. Sprawdź ją w grze.",
                             "Proponowana rotacja", "", "Propozycja na podstawie umiejętności drużyny. Sprawdź ją w grze.",
                             "Proponowana rotacja", "wg KQM", "Propozycja na podstawie rotacji z poradnika KQM dla podobnej drużyny. Sprawdź ją w grze.",
                         }),
                     })
            {
                var words = WordsOf(culture);
                var vm = new CharacterOverviewViewModel(varka);
                vm.SetRotationWords(words);
                var rows = vm.Teams.Single().Rotations.ToList();
                var suggestedVm = new CharacterOverviewViewModel(chiori);
                suggestedVm.SetRotationWords(words);
                rows.Add(suggestedVm.Teams.Single().Rotations.Single());
                var adaptedVm = new CharacterOverviewViewModel(adapted);
                adaptedVm.SetRotationWords(words);
                rows.Add(adaptedVm.Teams.Single().Rotations.Single());

                CollectionAssert.AreEqual(expected, rows.SelectMany(r => new[] { r.Heading, r.SourceLink, r.HeadingTip }).ToArray(), culture);
                CollectionAssert.AreEqual(new[] { " \u00B7 ", "", "", " \u00B7 " }, rows.Select(r => r.SourceSeparator).ToArray(), culture);
                CollectionAssert.AreEqual(new[] { " \u2197", "", "", " \u2197" }, rows.Select(r => r.SourceGlyph).ToArray(), culture);
                Assert.AreEqual(("https://keqingmains.com/q/varka-quickguide/", "KQM", "keqingmains.com"),
                    (rows[0].SourceUrl, rows[0].SourceName, rows[0].SourceHost));
                Assert.IsFalse(rows[1].HasSource || rows[2].HasSource, culture);
                Assert.IsTrue(rows[3].IsSuggested && rows[3].HasSource, culture);
                Assert.IsFalse(rows.Any(r => r.Text.Contains("keqingmains")), "the URL never appears as text");
            }

            // Rows built without a view read English, as the en-US strings.
            var plain = new CharacterOverviewViewModel(varka).Teams.Single().Rotations[0];
            Assert.AreEqual(("Rotation", "per KQM", "From the KQM guide. Check it in play."), (plain.Heading, plain.SourceLink, plain.HeadingTip));
            Assert.AreEqual(RotationWords.English, WordsOf("en-US"), "the English defaults are the en-US strings");
        }

        [TestMethod]
        public void Heading_AnUnknownSite_ShowsItsHost_ABadTranslationFallsBackToEnglish()
        {
            string rotation = "[ { \"source\": \"https://www.example.org/varka-rotation\", \"steps\": [ { \"member\": \"" + Durin +
                              "\", \"actions\": [ { \"a\": \"E\" } ] } ] } ]";
            var catalog = CharacterRotationTests.Genshin(rotation);
            var model = CharacterOverviewBuilder.Build(catalog.Find(Varka), catalog, null);
            var vm = new CharacterOverviewViewModel(model);
            vm.SetRotationWords(WordsOf("pl-PL"));
            var row = vm.Teams.Single().Rotations.Single();
            Assert.AreEqual(("wg example.org", "Rotacja z poradnika example.org. Sprawdź ją w grze.", "www.example.org"),
                (row.SourceLink, row.HeadingTip, row.SourceHost));

            var broken = new CharacterOverviewViewModel(model);
            broken.SetRotationWords(RotationWords.English with { Per = "wg", TipGuideFrom = "z {1} poradnika" });
            row = broken.Teams.Single().Rotations.Single();
            Assert.AreEqual(("per example.org", "From the example.org guide. Check it in play."), (row.SourceLink, row.HeadingTip));
        }

        [TestMethod]
        public void Strings_TheHeadingsLinkAndTooltips_PerLanguage()
        {
            var en = WordsOf("en-US");
            var pl = WordsOf("pl-PL");
            CollectionAssert.AreEqual(new[]
            {
                "Rotation", "Suggested rotation", "per {0}", "From the {0} guide. Check it in play.", "From a guide. Check it in play.",
                "Suggested from the team's kits. Check it in play.", "Suggested from the {0} guide's rotation for a similar team. Check it in play.",
            }, new[] { en.Guide, en.Suggested, en.Per, en.TipGuideFrom, en.TipGuide, en.TipSuggested, en.TipSuggestedFrom });
            CollectionAssert.AreEqual(new[]
            {
                "Rotacja", "Proponowana rotacja", "wg {0}", "Rotacja z poradnika {0}. Sprawdź ją w grze.", "Rotacja z poradnika. Sprawdź ją w grze.",
                "Propozycja na podstawie umiejętności drużyny. Sprawdź ją w grze.",
                "Propozycja na podstawie rotacji z poradnika {0} dla podobnej drużyny. Sprawdź ją w grze.",
            }, new[] { pl.Guide, pl.Suggested, pl.Per, pl.TipGuideFrom, pl.TipGuide, pl.TipSuggested, pl.TipSuggestedFrom });
        }
    }
}
