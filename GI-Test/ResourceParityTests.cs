using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Every string key the desktop reads exists in English and Polish. en-US is always merged under the chosen
    /// language, so a key missing from pl-PL renders English in a Polish app instead of failing: only a direct
    /// comparison of the key sets catches it (the language-restart prompt shipped that way until session 58).
    /// </summary>
    [TestClass]
    public class ResourceParityTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static string Root => RenderTestKit.RepoRoot();

        private static HashSet<string> Keys(string file) =>
            XDocument.Load(Path.Combine(Root, "GI-Subtitles", "Resources", file)).Root.Elements()
                .Select(el => (string)el.Attribute(X + "Key")).Where(k => k != null).ToHashSet();

        private static IEnumerable<string> Sources(string pattern) =>
            Directory.EnumerateFiles(Path.Combine(Root, "GI-Subtitles"), pattern, SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !Regex.IsMatch(Path.GetFileName(p), @"^Strings\.[a-z]{2}-[A-Z]{2}\.xaml$"));

        /// <summary>The lookup helpers that only ever read strings (L and its per-file twins).</summary>
        private static readonly Regex StringHelperCall =
            new(@"\b(?:L|LF|FarmL|Word|LocalizedString|GetLocalizedString)\(\s*""([A-Za-z][A-Za-z0-9_.]*)""\s*[,)]");

        /// <summary>Resource lookups that also read brushes and styles, so they count only for keys a dictionary holds.</summary>
        private static readonly Regex ResourceLookup = new(@"\b(?:Try)?FindResource\(\s*""([A-Za-z][A-Za-z0-9_.]*)""\s*\)");

        private static readonly Regex XamlReference =
            new(@"\{(?:DynamicResource|StaticResource)\s+(?:ResourceKey=)?([A-Za-z][A-Za-z0-9_.]*)\s*\}");

        private static readonly Regex Literal = new(@"""([A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]*)""");

        internal static IReadOnlyList<Regex> RuntimeFamilies()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "GI-Test", "Fixtures", "runtime-string-keys.json")));
            return doc.RootElement.GetProperty("families").EnumerateArray()
                .Select(f => new Regex(f.GetProperty("pattern").GetString())).ToList();
        }

        /// <summary>Referenced key → the files that reference it.</summary>
        private static Dictionary<string, SortedSet<string>> References(HashSet<string> stringKeys, out HashSet<string> strict)
        {
            var refs = new Dictionary<string, SortedSet<string>>();
            strict = new HashSet<string>();
            void Add(string key, string file)
            {
                if (!refs.TryGetValue(key, out var files)) refs[key] = files = new SortedSet<string>();
                files.Add(Path.GetRelativePath(Root, file));
            }
            foreach (string file in Sources("*.cs"))
            {
                string code = File.ReadAllText(file);
                foreach (Match m in StringHelperCall.Matches(code)) { Add(m.Groups[1].Value, file); strict.Add(m.Groups[1].Value); }
                foreach (Match m in ResourceLookup.Matches(code))
                    if (stringKeys.Contains(m.Groups[1].Value)) Add(m.Groups[1].Value, file);
                // Keys held in arrays, fields and switch arms before they reach L().
                foreach (Match m in Literal.Matches(code))
                    if (stringKeys.Contains(m.Groups[1].Value)) Add(m.Groups[1].Value, file);
            }
            foreach (string file in Sources("*.xaml"))
                foreach (Match m in XamlReference.Matches(File.ReadAllText(file)))
                    if (stringKeys.Contains(m.Groups[1].Value)) Add(m.Groups[1].Value, file);
            return refs;
        }

        [TestMethod]
        public void EveryReferencedKey_ExistsInEnglishAndPolish()
        {
            var en = Keys("Strings.en-US.xaml");
            var pl = Keys("Strings.pl-PL.xaml");
            var refs = References(en.Union(pl).ToHashSet(), out var strict);
            Assert.IsTrue(refs.Count > 400, $"the scan finds the keys the app reads ({refs.Count})");

            var missing = refs.Where(r => !en.Contains(r.Key) || !pl.Contains(r.Key))
                .Select(r => $"{r.Key} (missing in {(en.Contains(r.Key) ? "" : "en-US ")}{(pl.Contains(r.Key) ? "" : "pl-PL")}): {string.Join(", ", r.Value)}")
                .OrderBy(s => s).ToList();
            Assert.AreEqual(0, missing.Count, "Add these keys to both dictionaries:\n" + string.Join("\n", missing));
            Assert.IsTrue(strict.Count > 300, "L() calls are found");
        }

        [TestMethod]
        public void RuntimeBuiltKeys_AreCompleteInPolish()
        {
            var en = Keys("Strings.en-US.xaml");
            var pl = Keys("Strings.pl-PL.xaml");
            var hits = new List<string>();
            foreach (var family in RuntimeFamilies())
            {
                var members = en.Where(k => family.IsMatch(k)).ToList();
                if (members.Count == 0) hits.Add($"{family}: matches no English key (drop it from the fixture if the code stopped building it)");
                hits.AddRange(members.Where(k => !pl.Contains(k)).Select(k => $"{k} ({family}) is missing in pl-PL"));
            }
            Assert.AreEqual(0, hits.Count, string.Join("\n", hits));
        }

        /// <summary>L("Prefix_" + x) or L($"Prefix_{x}"): a key no literal names, which only the fixture can cover.</summary>
        private static readonly Regex RuntimeBuiltCall =
            new(@"\b(?:L|LF|FarmL|Word|LocalizedString|GetLocalizedString)\(\s*\$?""([A-Za-z][A-Za-z0-9_]*_)(?:\{|""\s*\+)");

        [TestMethod]
        public void EveryRuntimeBuiltPrefix_IsListedInTheFixture()
        {
            // Without this, a new family would pass the Polish check silently until someone remembered the fixture.
            var en = Keys("Strings.en-US.xaml");
            var families = RuntimeFamilies();
            var unlisted = new SortedSet<string>();
            int seen = 0;
            foreach (string file in Sources("*.cs"))
                foreach (Match m in RuntimeBuiltCall.Matches(File.ReadAllText(file)))
                {
                    seen++;
                    string prefix = m.Groups[1].Value;
                    if (!en.Any(k => k.StartsWith(prefix, System.StringComparison.Ordinal) && families.Any(f => f.IsMatch(k))))
                        unlisted.Add($"{prefix} ({Path.GetRelativePath(Root, file)})");
                }
            Assert.IsTrue(seen >= 5, $"the scan finds the known runtime-built calls ({seen})");
            Assert.AreEqual(0, unlisted.Count,
                "Add a pattern for these prefixes to GI-Test/Fixtures/runtime-string-keys.json:\n" + string.Join("\n", unlisted));
        }

        [TestMethod]
        public void PolishHasNoKeyEnglishLacks()
        {
            var extra = Keys("Strings.pl-PL.xaml").Except(Keys("Strings.en-US.xaml")).OrderBy(k => k).ToList();
            Assert.AreEqual(0, extra.Count, "English is the base every language falls back to: " + string.Join(", ", extra));
        }

        [TestMethod]
        public void TheScan_SeesTheLookupShapesItClaims()
        {
            // Guards the regexes: one known key per lookup shape.
            var en = Keys("Strings.en-US.xaml");
            var refs = References(en, out var strict);
            Assert.IsTrue(strict.Contains("Dialog_LangRestart_Title"), "L(\"...\", fallback)");
            Assert.IsTrue(strict.Contains("Dialog_GameNotFound_Title"), "LocalizedString(\"...\", fallback)");
            Assert.IsTrue(refs.ContainsKey("shell_Nav_Home"), "{DynamicResource ...} in XAML");
            Assert.IsTrue(refs.ContainsKey("onb_Rail_Welcome"), "a key held in an array before it reaches L()");
        }
    }
}
