using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Copy rules for the character page, the Characters tab and the Ctrl+Q popup (W8): no em dash in their strings
    /// or code fallbacks (a comma or a full stop reads better), and no empty "W skrócie".
    /// </summary>
    [TestClass]
    public class CharacterCopyTests
    {
        // The string keys these screens own, in every language file.
        private static readonly string[] OwnedPrefixes = { "Char_", "QuickTranslate_", "Tab_Home", "Dash_Footer_" };

        // Their code: fallbacks and any other literal a user can see.
        private static readonly string[] OwnedCode =
        {
            "Views/CharacterOverviewView.xaml.cs", "Views/CharacterOverviewViewModel.cs", "Views/CharacterOverviewConverters.cs",
            "Views/CharacterVisuals.cs", "Views/CharactersPage.xaml.cs", "Views/QuickTranslatePopup.xaml.cs",
            "Views/QuickTranslateContext.cs", "Views/HorizontalOverflow.cs", "Views/SettingsWindow.Characters.cs",
            "Views/CharacterOverviewView.xaml", "Views/CharactersPage.xaml", "Views/QuickTranslatePopup.xaml", "Themes/Chips.xaml",
        };

        private static string AppDir()
        {
            string root = RenderTestKit.RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            return Path.Combine(root, "GI-Subtitles");
        }

        [TestMethod]
        public void OwnedStrings_HaveNoEmDash()
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var offenders = new List<string>();
            foreach (string file in Directory.GetFiles(Path.Combine(AppDir(), "Resources"), "Strings.*.xaml"))
            {
                foreach (var e in XDocument.Load(file).Root.Elements())
                {
                    string key = (string)e.Attribute(x + "Key") ?? "";
                    if (OwnedPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)) && e.Value.Contains('—'))
                        offenders.Add($"{Path.GetFileName(file)}: {key} = {e.Value}");
                }
            }
            Assert.AreEqual(0, offenders.Count, "use a comma or a full stop:\n" + string.Join("\n", offenders));
        }

        [TestMethod]
        public void OwnedCode_HasNoEmDashInWhatUsersRead()
        {
            var literal = new Regex("\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"", RegexOptions.Compiled);
            var offenders = new List<string>();
            foreach (string rel in OwnedCode)
            {
                string path = Path.Combine(AppDir(), rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) continue;
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    string code = rel.EndsWith(".xaml", StringComparison.Ordinal) ? Regex.Replace(line, "<!--.*?(-->|$)", "") : StripComment(line);
                    if (code.TrimStart().StartsWith("<!--", StringComparison.Ordinal) || code.TrimStart().StartsWith("*", StringComparison.Ordinal)) continue;
                    foreach (Match m in literal.Matches(code))
                        if (m.Value.Contains('—')) offenders.Add($"{rel}:{i + 1}: {m.Value}");
                }
            }
            Assert.AreEqual(0, offenders.Count, "use a comma or a full stop:\n" + string.Join("\n", offenders));
        }

        // A // comment outside a string literal is not shown to anyone.
        private static string StripComment(string line)
        {
            bool inString = false;
            for (int i = 0; i < line.Length - 1; i++)
            {
                if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) inString = !inString;
                if (!inString && line[i] == '/' && line[i + 1] == '/') return line.Substring(0, i);
            }
            return line;
        }

        [TestMethod]
        public void NothingToRead_NoGlanceHeading_FactsStayUnderTheHero()
        {
            // Bennett without AI data: one fact tile, no summary, no picture.
            var model = new CharacterOverviewModel
            {
                Name = "Bennett", Game = "genshin", ElementKey = "Pyro", Rarity = 4,
                Skills = new[] { new OverviewSkill { Kind = "ultimate", Name = "Fantastic Voyage", EnglishName = "Fantastic Voyage", Marker = "(Q)" } },
            };
            var vm = new CharacterOverviewViewModel(model);
            Assert.IsTrue(vm.HasGlance, "the key facts still show");
            Assert.AreEqual(1, vm.Facts.Count);
            Assert.IsFalse(vm.ShowGlanceHeading);
            Assert.IsFalse(vm.Sections.Any(s => s.Key == "Glance"), "no tab for a section without a heading");

            // With an AI summary the heading and its tab come back.
            model.Guide = new[] { new OverviewGuideSection { Heading = "W skrócie", Body = "Bennett leczy i daje ATK." } };
            var withSummary = new CharacterOverviewViewModel(model);
            Assert.IsTrue(withSummary.ShowGlanceHeading);
            Assert.AreEqual("Glance", withSummary.Sections[0].Key);
        }
    }
}
