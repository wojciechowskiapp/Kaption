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

        /// <summary>The build card's keys (2.5.1): Start's Skróty tile, the Characters header, Start › Zaawansowane.</summary>
        private static Dictionary<string, string> BuildCardCopy(string file) =>
            Strings(file).Where(s => s.Key.StartsWith("card_")).ToDictionary(s => s.Key, s => s.Value);

        private static readonly string[] Locales = { "Strings.en-US.xaml", "Strings.pl-PL.xaml", "Strings.ja-JP.xaml", "Strings.zh-CN.xaml" };

        /// <summary>U+2014, written as a number so this test does not carry the character itself.</summary>
        private const char EmDash = (char)0x2014;

        [TestMethod]
        public void BuildCardKeys_ExistInAllFourLocales_AndAreTranslated()
        {
            var en = BuildCardCopy("Strings.en-US.xaml");
            Assert.IsTrue(en.Count >= 10, "the build card's keys are there");
            foreach (string other in Locales.Skip(1))
            {
                var copy = BuildCardCopy(other);
                CollectionAssert.AreEquivalent(en.Keys.OrderBy(k => k).ToList(), copy.Keys.OrderBy(k => k).ToList(),
                    $"{other}: missing {string.Join(", ", en.Keys.Except(copy.Keys))}; extra {string.Join(", ", copy.Keys.Except(en.Keys))}");
                var english = copy.Where(kv => kv.Value == en[kv.Key] && Regex.IsMatch(kv.Value, "[a-z]{4,}")).Select(kv => kv.Key).ToList();
                Assert.AreEqual(0, english.Count, $"{other} copies English for: {string.Join(", ", english)}");
                StringAssert.Contains(copy["card_Memory_Format"], "{0}", $"{other}: the remembered UIDs go in the format");
            }
            // The owner's words for the Polish line, and the entry's name.
            var pl = BuildCardCopy("Strings.pl-PL.xaml");
            Assert.AreEqual("Nie udało się odczytać UID z ekranu gry. Wpisz go na stronie.", pl["card_Uid_NotRead"]);
            Assert.AreEqual("Karta buildu", pl["card_Title"]);
            Assert.AreEqual("Zapomnij UID", pl["card_Forget"]);
        }

        /// <summary>
        /// 2.5.1 polish: a line of its own while the text reader is still loading, and where Star Rail and ZZZ show the UID
        /// (their menu, Esc) both in the entry's tooltip and in the line after a read that found none.
        /// </summary>
        [TestMethod]
        public void BuildCardCopy_SaysTheReaderIsLoading_AndWhereStarRailAndZzzShowTheUid()
        {
            var pl = BuildCardCopy("Strings.pl-PL.xaml");
            var en = BuildCardCopy("Strings.en-US.xaml");
            Assert.AreEqual("Czytnik tekstu jeszcze się ładuje. Spróbuj za chwilę.", pl["card_Reader_Loading"]);
            Assert.AreEqual("The text reader is still loading. Try again in a moment.", en["card_Reader_Loading"]);
            Assert.AreEqual("W Star Rail i ZZZ otwórz menu gry (Esc), tam widać UID.", pl["card_Uid_MenuHint"]);
            Assert.AreEqual("In Star Rail and ZZZ open the game menu (Esc), the UID is shown there.", en["card_Uid_MenuHint"]);
            foreach (string file in Locales)
            {
                var copy = BuildCardCopy(file);
                StringAssert.Contains(copy["card_Entry_Tip"], copy["card_Uid_MenuHint"], $"{file}: the tooltip says where the menus show the UID");
                StringAssert.Contains(copy["card_Uid_MenuHint"], "Esc", $"{file}: names the key");
                Assert.AreNotEqual(copy["card_Uid_NotRead"], copy["card_Reader_Loading"], $"{file}: loading is not the same as not found");
            }
        }

        [TestMethod]
        public void BuildCardCopy_HasNoEmDashNoShoutingAndNoAI()
        {
            var hits = new List<string>();
            foreach (string file in Locales)
            foreach (var (key, value) in BuildCardCopy(file))
            {
                if (value.Contains(EmDash)) hits.Add($"{file}: {key} has an em dash");
                if (value.Contains('!')) hits.Add($"{file}: {key} has an exclamation mark");
                if (Regex.IsMatch(value, @"\bAI\b") || value.IndexOf("sztuczn", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    hits.Add($"{file}: {key} mentions AI");
                if (Regex.IsMatch(value, @"\bOCR\b")) hits.Add($"{file}: {key} says OCR");
            }
            // And none in the feature's own code and its documentation either.
            string root = RenderTestKit.RepoRoot();
            var sources = Directory.GetFiles(Path.Combine(root, "GI-Subtitles", "Services", "Cards"), "*.cs")
                .Append(Path.Combine(root, "GI-Subtitles", "Views", "SettingsWindow.BuildCard.cs"))
                .Append(Path.Combine(root, "docs", "CONFIG.md"));
            foreach (string path in sources)
                if (File.ReadAllText(path).Contains(EmDash)) hits.Add($"{Path.GetFileName(path)} has an em dash");
            Assert.AreEqual(0, hits.Count, string.Join("\n", hits));
        }

        /// <summary>The dialogs the eight start gates open (MainWindow.TryGate*), the words a stuck player reads.</summary>
        private static readonly string[] GateDialogKeys =
        {
            "Dialog_StillStarting_Title", "Dialog_StillStarting_Body", "Dialog_SignInOpenFailed_Title", "Dialog_SignInOpenFailed_Body",
            "Dialog_StillDownloading_Title", "Dialog_StillDownloading_Body",
            "Dash_EngineFailed_Runtime_Title", "Dash_EngineFailed_Runtime_Body", "Dialog_EngineFailed_Title", "Dialog_EngineFailed_Body",
            "Dialog_EngineLoading_Title", "Dialog_EngineLoading_Body",
            "Dialog_PackComing_Title", "Dialog_PackComing_Body_Format", "Dash_Status_TranslationDataMissing", "Dialog_MatcherMissing_Body",
            "Dialog_MatcherLoading_Body",
            "Dialog_RegionMissing_Title", "Dialog_RegionMissing_Body", "Dialog_RegionMissing_Details",
            "Dialog_GameNotFound_Title", "Dialog_GameNotFound_Body", "Dialog_GameNotFound_Details",
            "Dialog_GameNotFound_Primary", "Dialog_GameNotFound_Secondary",
            "Dialog_OverlayInRegion_Title", "Dialog_OverlayInRegion_Body", "Dialog_OverlayInRegion_Details",
            "Dialog_OverlayInRegion_Primary", "Dialog_OverlayInRegion_Secondary",
        };

        /// <summary>One name per thing: the text reader, the dialogue area, subtitles. No index, capture, OCR or removed UI.</summary>
        [TestMethod]
        [DataRow("Strings.en-US.xaml", @"\b(?:OCR|index|capture|region|badge|translator|wizard|recognition|dialogue reader)\b", @"\breader\b", @"\btext reader\b")]
        [DataRow("Strings.pl-PL.xaml", @"\b(?:OCR|indeks\w*|przechwytyw\w*|rozpoznawani\w*|plakietk\w*|tłumacz|kreator\w*|czytnik\w* dialogów)\b|""", @"\bczytnik\w*", @"\bczytnik\w* tekstu\b")]
        public void GateDialogs_UseOneVocabulary(string file, string banned, string readerWord, string readerName)
        {
            var copy = Strings(file).ToDictionary(s => s.Key, s => s.Value);
            var hits = new List<string>();
            foreach (string key in GateDialogKeys)
            {
                Assert.IsTrue(copy.ContainsKey(key), $"{file}: {key} is there");
                // Guide links name their page ("how-to/region"); that is an address, not a word.
                string text = Regex.Replace(copy[key], @"\S+\.\S+/\S*", "");
                foreach (Match m in Regex.Matches(text, banned, RegexOptions.IgnoreCase)) hits.Add($"{key}: \"{m.Value}\"");
                if (Regex.Matches(text, readerWord, RegexOptions.IgnoreCase).Count != Regex.Matches(text, readerName, RegexOptions.IgnoreCase).Count)
                    hits.Add($"{key}: the reader is called something other than the text reader");
            }
            Assert.AreEqual(0, hits.Count, $"{file}:\n" + string.Join("\n", hits));
        }

        /// <summary>Gate dialogs quote controls by the label they carry today, so a renamed button breaks this, not the advice.</summary>
        [TestMethod]
        [DataRow("Strings.en-US.xaml")]
        [DataRow("Strings.pl-PL.xaml")]
        public void GateDialogs_NameControlsThatExist(string file)
        {
            var copy = Strings(file).ToDictionary(s => s.Key, s => s.Value);
            string Label(string key) => copy[key].Replace("_", "");
            var expect = new (string Dialog, string[] Controls)[]
            {
                ("Dialog_EngineFailed_Body", new[] { "shell_Nav_Home", "shell_Action_Retry", "shell_Action_RunOnCpu" }),
                ("Dialog_MatcherMissing_Body", new[] { "shell_Nav_Game" }),
                ("Dialog_RegionMissing_Details", new[] { "shell_Nav_Game", "shell_Region_Detect", "shell_Region_Fix" }),
                ("Dialog_GameNotFound_Body", new[] { "shell_Action_Start" }),
                ("Dialog_GameNotFound_Details", new[] { "Dialog_GameNotFound_Primary" }),
                ("Dialog_OverlayInRegion_Details", new[] { "shell_Nav_Look", "look_Vertical", "look_MaxHeight", "shell_Advanced" }),
            };
            var hits = (from e in expect from c in e.Controls
                        where copy[e.Dialog].IndexOf(Label(c), System.StringComparison.OrdinalIgnoreCase) < 0
                        select $"{e.Dialog} does not name {c} (\"{Label(c)}\")").ToList();
            Assert.AreEqual(0, hits.Count, $"{file}:\n" + string.Join("\n", hits));
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
