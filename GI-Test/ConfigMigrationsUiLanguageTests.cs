// ConfigMigrations v5 (2026-11-09): only Polish and English dictionaries ship, so a saved zh-CN or ja-JP UILang is
// rewritten to en-US once. The app's Config is static and lives in the test run's temporary profile
// (TestEnvironment); each test starts at migration level 4 and restores what it found.

using System.Linq;
using AppConfig = GI_Subtitles.Core.Config.Config;
using AppLogger = GI_Subtitles.Common.Logger;
using GI_Subtitles.Core.Config;
using log4net.Appender;
using log4net.Repository.Hierarchy;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    [DoNotParallelize]
    public class ConfigMigrationsUiLanguageTests
    {
        private const string LangKey = "UILang";
        private const string VersionKey = "ConfigMigrationVersion";
        private const string V5LogPrefix = "ConfigMigrations v5:";

        private bool _hadLang;
        private string _lang;
        private bool _hadVersion;
        private int _version;
        private MemoryAppender _log;
        private bool _repoWasConfigured;

        private static Hierarchy Repo => (Hierarchy)log4net.LogManager.GetRepository(typeof(AppLogger).Assembly);

        [TestInitialize]
        public void SetUp()
        {
            _hadLang = AppConfig.Has(LangKey);
            _lang = AppConfig.Get<string>(LangKey, null);
            _hadVersion = AppConfig.Has(VersionKey);
            _version = AppConfig.Get<int>(VersionKey, 0);
            AppConfig.Set(VersionKey, 4);

            // On CI the test host has no log4net section, and an unconfigured repository drops every record.
            _repoWasConfigured = Repo.Configured;
            Repo.Configured = true;
            _log = new MemoryAppender();
            _log.ActivateOptions();
            Repo.Root.AddAppender(_log);
        }

        [TestCleanup]
        public void TearDown()
        {
            Repo.Root.RemoveAppender(_log);
            Repo.Configured = _repoWasConfigured;
            if (_hadLang) AppConfig.Set(LangKey, _lang); else AppConfig.Remove(LangKey);
            if (_hadVersion) AppConfig.Set(VersionKey, _version); else AppConfig.Remove(VersionKey);
        }

        private string[] V5Lines() =>
            _log.GetEvents().Select(e => e.RenderedMessage).Where(m => m.StartsWith(V5LogPrefix)).ToArray();

        [TestMethod]
        [DataRow("zh-CN")]
        [DataRow("ja-JP")]
        public void RemovedLanguage_BecomesEnglish_Once(string saved)
        {
            AppConfig.Set(LangKey, saved);

            ConfigMigrations.RunAll();

            Assert.AreEqual("en-US", AppConfig.Get<string>(LangKey, null));
            Assert.AreEqual(5, AppConfig.Get<int>(VersionKey, 0));
            CollectionAssert.AreEqual(
                new[] { $"ConfigMigrations v5: UILang {saved} -> en-US (only Polish and English ship)." },
                V5Lines());
        }

        [TestMethod]
        public void Polish_IsLeftAlone()
        {
            AppConfig.Set(LangKey, "pl-PL");

            ConfigMigrations.RunAll();

            Assert.AreEqual("pl-PL", AppConfig.Get<string>(LangKey, null));
            Assert.AreEqual(5, AppConfig.Get<int>(VersionKey, 0));
            Assert.AreEqual(0, V5Lines().Length);
        }

        [TestMethod]
        public void AbsentKey_StaysAbsent()
        {
            AppConfig.Remove(LangKey);

            ConfigMigrations.RunAll();

            Assert.IsFalse(AppConfig.Has(LangKey), "first launch must still pick the language from Windows");
            Assert.AreEqual(5, AppConfig.Get<int>(VersionKey, 0));
            Assert.AreEqual(0, V5Lines().Length);
        }

        [TestMethod]
        public void SecondRun_IsANoOp()
        {
            AppConfig.Set(LangKey, "zh-CN");
            ConfigMigrations.RunAll();
            Assert.AreEqual("en-US", AppConfig.Get<string>(LangKey, null));

            // The version gate: at level 5 nothing runs again, even on a value v5 would rewrite.
            AppConfig.Set(LangKey, "ja-JP");
            ConfigMigrations.RunAll();
            Assert.AreEqual("ja-JP", AppConfig.Get<string>(LangKey, null));
            Assert.AreEqual(1, V5Lines().Length);

            // The case itself is idempotent: re-running v5 on its own output changes nothing.
            AppConfig.Set(LangKey, "en-US");
            AppConfig.Set(VersionKey, 4);
            ConfigMigrations.RunAll();
            Assert.AreEqual("en-US", AppConfig.Get<string>(LangKey, null));
            Assert.AreEqual(5, AppConfig.Get<int>(VersionKey, 0));
            Assert.AreEqual(1, V5Lines().Length);
        }
    }
}
