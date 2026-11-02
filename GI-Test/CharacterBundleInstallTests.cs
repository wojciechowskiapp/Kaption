using System;
using System.IO;
using System.Text.Json.Nodes;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Data;
using GI_Subtitles.Services.Security;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>The optional `characters` bundle section installs, and never breaks a bundle install.</summary>
    [TestClass]
    public class CharacterBundleInstallTests
    {
        private string _game;
        private string _bundlePath;
        private FileProtectionHelper _protector;

        [TestInitialize]
        public void Setup()
        {
            _game = "CharBundleTest_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Directory.CreateDirectory(GameDataPaths.GameDir(_game));
            _bundlePath = Path.Combine(Path.GetTempPath(), "char-bundle-" + Guid.NewGuid().ToString("N") + ".json");
            _protector = new FileProtectionHelper(TestProtection.Create());
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { if (File.Exists(_bundlePath)) File.Delete(_bundlePath); } catch { }
            try { Directory.Delete(GameDataPaths.GameDir(_game), true); } catch { }
        }

        private void WriteBundle(JsonNode characters)
        {
            var bundle = new JsonObject
            {
                ["bundle_version"] = 2,
                ["dialog_graph"] = new JsonObject(),
                ["hash_to_dialogs"] = new JsonObject(),
                ["npc_names"] = new JsonObject(),
                ["talk_index"] = new JsonObject(),
                ["quest_info"] = new JsonObject(),
            };
            if (characters != null) bundle["characters"] = characters;
            bundle["extension"] = new JsonObject { ["game"] = _game };
            File.WriteAllText(_bundlePath, bundle.ToJsonString());
        }

        private static JsonNode Section(string game) =>
            JsonNode.Parse(CharacterCatalogTests.MinimalJson.Replace(@"""game"": ""starrail""", $@"""game"": ""{game}"""));

        [TestMethod]
        public void BundleWithCharacters_InstallsReadableCatalog()
        {
            WriteBundle(Section(_game));

            var install = GamedataSyncService.InstallBundleFromFile(_bundlePath, _game, _protector);

            Assert.IsTrue(install.Success, install.Message);
            Assert.IsTrue(install.CharactersInstalled);
            using var s = _protector.OpenForReading(GameDataPaths.CharactersJson(_game));
            var catalog = CharacterCatalog.Load(s, _game);
            Assert.AreEqual("March 7th", catalog.Find("1001")?.Name);
        }

        [TestMethod]
        public void BundleWithoutCharacters_InstallsWithoutFile()
        {
            WriteBundle(null);

            var install = GamedataSyncService.InstallBundleFromFile(_bundlePath, _game, _protector);

            Assert.IsTrue(install.Success, install.Message);
            Assert.IsFalse(install.CharactersInstalled);
            Assert.IsFalse(_protector.FileExists(GameDataPaths.CharactersJson(_game)));
        }

        [TestMethod]
        public void MalformedCharactersSection_DoesNotFailInstall()
        {
            WriteBundle(new JsonArray(1, 2, 3));

            var install = GamedataSyncService.InstallBundleFromFile(_bundlePath, _game, _protector);

            Assert.IsTrue(install.Success, install.Message);
            Assert.IsFalse(install.CharactersInstalled);
        }
    }
}
