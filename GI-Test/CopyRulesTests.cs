using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>House rules for the interface copy, checked on the string dictionaries themselves.</summary>
    [TestClass]
    public class CopyRulesTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static IEnumerable<(string File, string Key, string Value)> Strings(string pattern)
        {
            string dir = Path.Combine(RenderTestKit.RepoRoot(), "GI-Subtitles", "Resources");
            foreach (string file in Directory.GetFiles(dir, pattern).OrderBy(f => f))
                foreach (var el in XDocument.Load(file).Root.Elements())
                    yield return (Path.GetFileName(file), (string)el.Attribute(X + "Key"), el.Value);
        }

        [TestMethod]
        public void NoEmDashesInAnyLanguage()
        {
            var hits = Strings("Strings.*.xaml").Where(s => s.Value.Contains('—')).Select(s => $"{s.File}: {s.Key}").ToList();
            Assert.AreEqual(0, hits.Count, "Use a comma or a full stop instead of an em dash:\n" + string.Join("\n", hits));
        }

        [TestMethod]
        [DataRow("Strings.en-US.xaml")]
        [DataRow("Strings.pl-PL.xaml")]
        public void NoOcrJargonInTheOfferedLanguages(string file)
        {
            var hits = Strings(file).Where(s => Regex.IsMatch(s.Value, @"\bOCR\b")).Select(s => s.Key).ToList();
            Assert.AreEqual(0, hits.Count, "Say what it does (reads the dialogue), not OCR: " + string.Join(", ", hits));
        }

        /// <summary>The 2.6 screens' keys: Wygląd napisów (look_) and Start / status bar (start_).</summary>
        private static readonly string[] W26Prefixes = { "look_", "start_", "Char_PlaysIn_", "bar_", "onb_How_", "state_" };

        /// <summary>Sample subtitles on Wygląd napisów are game lines in the subtitle language, not interface copy.</summary>
        private static bool IsSampleSubtitle(string key) => key.StartsWith("look_Sample_") || key == "look_Qt_Preview_Line";

        [TestMethod]
        [DataRow("Strings.en-US.xaml")]
        [DataRow("Strings.pl-PL.xaml")]
        public void ShellAndSetupCopyDoesNotShout(string file)
        {
            var hits = Strings(file)
                .Where(s => s.Key.StartsWith("shell_") || s.Key.StartsWith("onb_") || (W26Prefixes.Any(s.Key.StartsWith) && !IsSampleSubtitle(s.Key)))
                .Where(s => s.Value.Contains('!'))
                .Select(s => s.Key).ToList();
            Assert.AreEqual(0, hits.Count, "No exclamation marks in the shell and setup copy: " + string.Join(", ", hits));
        }

        [TestMethod]
        public void W26Keys_ExistInAllFourLocales()
        {
            static HashSet<string> Keys(string file) => Strings(file).Select(s => s.Key).Where(k => W26Prefixes.Any(k.StartsWith)).ToHashSet();
            var en = Keys("Strings.en-US.xaml");
            Assert.IsTrue(en.Count > 30, "the 2.6 keys are there");
            foreach (string other in new[] { "Strings.pl-PL.xaml", "Strings.ja-JP.xaml", "Strings.zh-CN.xaml" })
            {
                var keys = Keys(other);
                CollectionAssert.AreEquivalent(en.OrderBy(k => k).ToList(), keys.OrderBy(k => k).ToList(),
                    $"{other}: missing {string.Join(", ", en.Except(keys))}; extra {string.Join(", ", keys.Except(en))}");
            }
            // Japanese and Chinese are translated, not English copies (sample subtitles and names aside).
            var english = Strings("Strings.en-US.xaml").Where(s => W26Prefixes.Any(s.Key.StartsWith)).ToDictionary(s => s.Key, s => s.Value);
            foreach (string file in new[] { "Strings.ja-JP.xaml", "Strings.zh-CN.xaml" })
            {
                var copied = Strings(file).Where(s => W26Prefixes.Any(s.Key.StartsWith) && !IsSampleSubtitle(s.Key))
                    .Where(s => english.TryGetValue(s.Key, out var e) && e == s.Value && Regex.IsMatch(e, "[a-z]{4,}"))
                    .Select(s => s.Key).ToList();
                Assert.AreEqual(0, copied.Count, $"{file} copies English for: {string.Join(", ", copied)}");
            }
        }

        [TestMethod]
        public void CharacterScreens_NeverMentionAI()
        {
            // Every locale's character-screen strings (Char_*) and the 2.6 screens (look_, start_); the licence and
            // privacy texts are not screens anyone reads while playing.
            var hits = Strings("Strings.*.xaml")
                .Where(s => (s.Key.StartsWith("Char_") || W26Prefixes.Any(s.Key.StartsWith)) && !s.Key.Contains("Eula") && !s.Key.Contains("Legal"))
                .Where(s => Regex.IsMatch(s.Value, @"\bAI\b") || s.Value.IndexOf("sztuczn", System.StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(s => $"{s.File}: {s.Key}").ToList();
            // And no AI in what the character views and the 2.6 screens write themselves (attribute and element text,
            // comments aside).
            string views = Path.Combine(RenderTestKit.RepoRoot(), "GI-Subtitles", "Views");
            foreach (string view in new[] { "CharacterOverviewView.xaml", "CharactersPage.xaml", "QuickTranslatePopup.xaml", "SettingsWindow.xaml" })
            {
                string path = Path.Combine(views, view);
                if (!File.Exists(path)) continue;
                string xaml = Regex.Replace(File.ReadAllText(path), "<!--.*?-->", "", RegexOptions.Singleline);
                foreach (Match m in Regex.Matches(xaml, "(?:Text|Content|ToolTip|Header)=\"([^\"{][^\"]*)\"|>([^<>{}]+)<"))
                {
                    string shown = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (Regex.IsMatch(shown, @"\bAI\b")) hits.Add($"{view}: {shown.Trim()}");
                }
            }
            Assert.AreEqual(0, hits.Count, "The character screens say nothing about AI:\n" + string.Join("\n", hits));
        }

        [TestMethod]
        public void EnglishAndPolishShellKeysMatch()
        {
            static HashSet<string> Keys(string file) => Strings(file).Select(s => s.Key)
                .Where(k => k.StartsWith("shell_") || k.StartsWith("onb_")).ToHashSet();
            var en = Keys("Strings.en-US.xaml");
            var pl = Keys("Strings.pl-PL.xaml");
            CollectionAssert.AreEquivalent(en.OrderBy(k => k).ToList(), pl.OrderBy(k => k).ToList(),
                "Missing in pl-PL: " + string.Join(", ", en.Except(pl)) + "; missing in en-US: " + string.Join(", ", pl.Except(en)));
        }
    }
}
