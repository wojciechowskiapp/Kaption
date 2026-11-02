using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Character text reaches the pack exactly, even though the catalog stores it plain: TextMap keys keep the
    /// game's colour tags and a literal "\n" (Bennett's constellations were showing in English).
    /// </summary>
    [TestClass]
    public class CharacterTranslationLookupTests
    {
        private const string Bennett = @"{
  ""schema"": 1, ""game"": ""genshin"", ""game_version"": ""7.1.0"",
  ""characters"": [
    { ""id"": ""10000032"", ""name"": ""Bennett"", ""rarity"": 4, ""element"": ""Pyro"", ""weapon_type"": ""Sword"",
      ""skills"": [
        { ""kind"": ""skill"", ""name"": ""Passion Overload"", ""desc"": { ""t"": ""Bennett puts all his fire and passion for adventuring into his sword."", ""p"": [] } },
        { ""kind"": ""constellation"", ""name"": ""Grand Expectation"",
          ""desc"": { ""t"": ""Fantastic Voyage's ATK increase no longer has an HP restriction, and gains an additional 20% of Bennett's Base ATK."", ""p"": [] } },
        { ""kind"": ""constellation"", ""name"": ""Impasse Conqueror"",
          ""desc"": { ""t"": ""When Bennett's HP falls below 70%, his Energy Recharge is increased by 30%."", ""p"": [] } },
        { ""kind"": ""constellation"", ""name"": ""Unstoppable Fervor"",
          ""desc"": { ""t"": ""Increases the Level of Passion Overload by 3.\nMaximum upgrade level is 15."", ""p"": [] } }
      ] }
  ]
}";

        // Real TextMap shapes: colour tags around skill names, a literal backslash-n between lines.
        private static OptimizedMatcher Pack() => new OptimizedMatcher(new Dictionary<string, string>
        {
            ["Bennett"] = "Bennett",
            ["Passion Overload"] = "Przeładowanie Pasji",
            ["Bennett puts all his fire and passion for adventuring into his sword."] =
                "Bennett wkłada w miecz cały swój zapał do przygód.",
            ["Grand Expectation"] = "Wielkie oczekiwanie",
            ["<color=#FFD780FF>Fantastic Voyage</color>'s ATK increase no longer has an HP restriction, and gains an additional 20% of Bennett's Base ATK."] =
                "Zwiększenie ATK <color=#FFD780FF>Fantastic Voyage</color> nie ma już ograniczenia związanego z HP i zyskuje dodatkowe 20% Bazowego ATK Bennetta.",
            ["Impasse Conqueror"] = "Zdobywca Impasu",
            ["Unstoppable Fervor"] = "Niepowstrzymany zapał",
            ["Increases the Level of <color=#FFD780FF>Passion Overload</color> by 3.\\nMaximum upgrade level is 15."] =
                "Zwiększa Poziom <color=#FFD780FF>Passion Overload</color> o 3.\\nMaksymalny poziom ulepszeń to 15.",
        }, "EN");

        private static CharacterOverviewModel Build(OptimizedMatcher matcher)
        {
            var catalog = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Bennett)), "genshin");
            return CharacterOverviewBuilder.Build(catalog.Find("10000032"), catalog, new MatcherTranslationLookup(() => matcher));
        }

        [TestMethod]
        public void Bennett_ConstellationDescriptions_ArePolish()
        {
            var ranks = Build(Pack()).Skills.Where(s => s.Kind == "constellation").ToList();

            Assert.AreEqual("Grand Expectation", ranks[0].EnglishName, "the name the game shows stays the reference");
            Assert.AreEqual("Wielkie oczekiwanie", ranks[0].Name);
            Assert.AreEqual("Zwiększenie ATK Fantastic Voyage nie ma już ograniczenia związanego z HP i zyskuje dodatkowe 20% Bazowego ATK Bennetta.",
                ranks[0].Description, "a key with colour tags still matches; the tags never reach the screen (no ultimate here, so no marker)");
            Assert.IsFalse(ranks[0].DescriptionInEnglish);

            Assert.AreEqual("Zwiększa Poziom Passion Overload (E) o 3.\nMaksymalny poziom ulepszeń to 15.", ranks[2].Description,
                "line breaks match the pack's literal \\n; the skill named in it gets its marker");
            Assert.IsFalse(ranks[2].DescriptionInEnglish);
        }

        [TestMethod]
        public void MissingPolish_StaysEnglish_AndIsMarked()
        {
            var ranks = Build(Pack()).Skills.Where(s => s.Kind == "constellation").ToList();
            Assert.AreEqual("When Bennett's HP falls below 70%, his Energy Recharge is increased by 30%.", ranks[1].Description);
            Assert.IsTrue(ranks[1].DescriptionInEnglish);
        }

        [TestMethod]
        public void PlainKeys_StillMatchExactly()
        {
            var skill = Build(Pack()).Skills.First(s => s.Kind == "skill");
            Assert.AreEqual("Przeładowanie Pasji", skill.Name);
            Assert.AreEqual("Bennett wkłada w miecz cały swój zapał do przygód.", skill.Description);
        }

        [TestMethod]
        public void DeviceWording_AndPlaceholderMark_Resolve()
        {
            // Chiori's passive: the catalog drops {LAYOUT_PC#Press} (leaving two spaces); the pack keeps it and the '#'.
            var matcher = new OptimizedMatcher(new Dictionary<string, string>
            {
                ["#If you {LAYOUT_MOBILE#Tap}{LAYOUT_PC#Press}{LAYOUT_PS#Press} the Elemental Skill, you will trigger the <color=#FFD780FF>Tapestry</color> effect."] =
                    "#Jeśli {LAYOUT_MOBILE#dotkniesz}{LAYOUT_PC#naciśniesz}{LAYOUT_PS#naciśniesz} Umiejętność Żywiołu, aktywujesz efekt <color=#FFD780FF>Tapestry</color>.",
            }, "EN");
            var lookup = new MatcherTranslationLookup(() => matcher);

            Assert.IsTrue(lookup.TryTranslateExact("If you  the Elemental Skill, you will trigger the Tapestry effect.", out string pl));
            Assert.AreEqual("Jeśli naciśniesz Umiejętność Żywiołu, aktywujesz efekt <color=#FFD780FF>Tapestry</color>.", pl,
                "PC wording, no '#', no device tags");
            Assert.IsTrue(lookup.TryTranslateExact("If you Press the Elemental Skill, you will trigger the Tapestry effect.", out _),
                "a catalog that keeps the PC wording finds it too");
        }

        [TestMethod]
        public void FlavourTextInItalics_StillMatches()
        {
            // The catalog keeps <i> around flavour text while dropping the colour tags.
            var matcher = new OptimizedMatcher(new Dictionary<string, string>
            {
                ["Summons blooming ice, dealing <color=#99FFFFFF>AoE Cryo DMG</color>.\\n\\n<i>\"Clouds shroud the moonlit night.\"</i>"] =
                    "Przywołuje kwitnący lód, zadając <color=#99FFFFFF>obszarowe obrażenia Cryo</color>.\\n\\n<i>„Chmury spowijają księżycową noc.”</i>",
            }, "EN");
            var lookup = new MatcherTranslationLookup(() => matcher);

            Assert.IsTrue(lookup.TryTranslateExact("Summons blooming ice, dealing AoE Cryo DMG.\n\n<i>\"Clouds shroud the moonlit night.\"</i>", out string pl));
            StringAssert.StartsWith(pl, "Przywołuje kwitnący lód");
            StringAssert.Contains(pl, ".\n\n");
        }

        [TestMethod]
        public void PunctuationVariants_NeverAnswer()
        {
            // The markup-free index compares whole strings, so "Grand Expectation?" is not "Grand Expectation".
            var lookup = new MatcherTranslationLookup(() => Pack());
            Assert.IsFalse(lookup.TryTranslateExact("Grand Expectation?", out _));
            Assert.IsFalse(lookup.TryTranslateExact("Fantastic Voyage's ATK increase", out _));
        }
    }
}
