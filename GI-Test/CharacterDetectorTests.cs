using System.IO;
using System.Text;
using GI_Subtitles.Services.Characters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class CharacterDetectorTests
    {
        private const string Json = @"{ ""schema"": 1, ""game"": ""zzz"", ""characters"": [
  { ""id"": ""1011"", ""name"": ""Anby"", ""skills"": [
      { ""kind"": ""basic"", ""name"": ""Basic Attack: Turbo Volt"" },
      { ""kind"": ""skill"", ""name"": ""Special Attack: Fan Kick"" },
      { ""kind"": ""core"", ""name"": ""Shared Passive Title"" } ] },
  { ""id"": ""1021"", ""name"": ""Nekomata"", ""skills"": [
      { ""kind"": ""basic"", ""name"": ""Basic Attack: Kitty Slash"" },
      { ""kind"": ""skill"", ""name"": ""Frigid Cold Arrow"" },
      { ""kind"": ""core"", ""name"": ""Shared Passive Title"" } ] }
] }";

        private static CharacterDetector Detector() =>
            new CharacterDetector(CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Json)), "zzz"));

        [TestMethod]
        public void SkillTitle_Detects()
        {
            Assert.AreEqual("1011", Detector().Detect(new[] { "Lv. 12", "Basic Attack: Turbo Volt", "Press to attack" })?.Id);
        }

        [TestMethod]
        public void SharedTitle_Ignored()
        {
            Assert.IsNull(Detector().Detect(new[] { "Shared Passive Title", "Some description" }));
        }

        [TestMethod]
        public void TwoCharactersTie_Null()
        {
            Assert.IsNull(Detector().Detect(new[] { "Basic Attack: Turbo Volt", "Basic Attack: Kitty Slash" }));
        }

        [TestMethod]
        public void NameOnly_NotEnough()
        {
            Assert.IsNull(Detector().Detect(new[] { "Anby" }));
        }

        [TestMethod]
        public void NamePlusTitle_Detects()
        {
            Assert.AreEqual("1021", Detector().Detect(new[] { "Nekomata", "Kitty Slash" })?.Id);
        }

        [TestMethod]
        public void PrefixStripped_EitherSide()
        {
            Assert.AreEqual("1011", Detector().Detect(new[] { "Turbo Volt" })?.Id);
            Assert.AreEqual("1021", Detector().Detect(new[] { "Skill: Frigid Cold Arrow" })?.Id);
        }

        [TestMethod]
        public void OcrNoise_StillDetects()
        {
            Assert.AreEqual("1021", Detector().Detect(new[] { "Frigid Cold Arr0w" })?.Id);
        }

        [TestMethod]
        public void UnrelatedText_Null()
        {
            Assert.IsNull(Detector().Detect(new[] { "Paimon is hungry", "Lv. 90" }));
        }

        [TestMethod]
        public void SearchFold_IgnoresCaseAndPolishDiacritics()
        {
            Assert.AreEqual("lowca z lodowego lasu", GI_Subtitles.Views.SettingsWindow.FoldForSearch("  Łowca z Lodowego Lasu "));
            Assert.AreEqual("zolw zrebie", GI_Subtitles.Views.SettingsWindow.FoldForSearch("Żółw Źrebię"));
            Assert.AreEqual("", GI_Subtitles.Views.SettingsWindow.FoldForSearch(null));
        }

        [TestMethod]
        public void CharactersPage_RetranslatesOnlyWhenANewPackIsReady()
        {
            object english = null, pack = new object(), newer = new object();
            Assert.IsTrue(GI_Subtitles.Views.SettingsWindow.NeedsRetranslation(english, pack, currentReady: true));
            Assert.IsFalse(GI_Subtitles.Views.SettingsWindow.NeedsRetranslation(english, pack, currentReady: false));
            Assert.IsFalse(GI_Subtitles.Views.SettingsWindow.NeedsRetranslation(pack, pack, currentReady: true));
            Assert.IsTrue(GI_Subtitles.Views.SettingsWindow.NeedsRetranslation(pack, newer, currentReady: true));
            Assert.IsFalse(GI_Subtitles.Views.SettingsWindow.NeedsRetranslation(pack, null, currentReady: false));
        }

        [TestMethod]
        public void EmptyInputs_Null()
        {
            Assert.IsNull(Detector().Detect(null));
            Assert.IsNull(new CharacterDetector(CharacterCatalog.Empty).Detect(new[] { "Turbo Volt" }));
            Assert.IsNull(new CharacterDetector(null).Detect(new[] { "Turbo Volt" }));
        }
    }
}
