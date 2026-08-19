// ─────────────────────────────────────────────────────────────────────────────
//  LoggerLevelSwitchTests.cs
//  ---------------------------------------------------------------------------
//  Verifies the claim the whole "detailed logging" toggle rests on: the log
//  level changes at runtime, so the user never has to restart just to make
//  logging more verbose.
//
//  It also pins the invariant that makes the default configuration work — the
//  root level must never sit above the file appender's threshold, because
//  log4net evaluates the logger level FIRST. Get that backwards and debug
//  records are dropped before the appender is ever consulted, which would
//  silently empty the in-memory history that diagnostic bundles ship.
//
//  These tests mutate process-wide log4net state and the on-disk Config, so
//  each one restores what it found.
// ─────────────────────────────────────────────────────────────────────────────

using AppLogger = GI_Subtitles.Common.Logger;
using GI_Subtitles.Core.Config;
using log4net.Core;
using log4net.Repository.Hierarchy;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class LoggerLevelSwitchTests
    {
        private string _originalLogLevel;
        private log4net.Appender.RollingFileAppender _temporaryAppender;

        [TestInitialize]
        public void SetUp()
        {
            _originalLogLevel = Config.Get<string>("LogLevel", null);

            // On a developer machine log4net has already configured the real
            // appender from app.config. On a CI runner the entry assembly is
            // the test host, whose config has no log4net section, so nothing is
            // configured and there would be no appender to assert against.
            // Rather than skipping the assertions there — which would quietly
            // turn CI green without testing anything — stand up an equivalent
            // appender and tear it down afterwards.
            if (FileAppender() == null)
            {
                _temporaryAppender = new log4net.Appender.RollingFileAppender
                {
                    Name = "LogFileAppender",
                    File = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kaption-leveltest.log"),
                    AppendToFile = true,
                    Layout = new log4net.Layout.PatternLayout("%message%newline"),
                    Threshold = Level.Info,
                };
                _temporaryAppender.ActivateOptions();
                Repo.Root.AddAppender(_temporaryAppender);
            }
        }

        [TestCleanup]
        public void TearDown()
        {
            if (_temporaryAppender != null)
            {
                Repo.Root.RemoveAppender(_temporaryAppender);
                _temporaryAppender.Close();
                _temporaryAppender = null;
            }

            Config.Set("LogLevel", _originalLogLevel ?? "");
            AppLogger.ApplyConfiguredLevel();
        }

        /// <summary>
        /// log4net resolves a repository per calling assembly. Logger lives in
        /// GI-Subtitles, so its level changes land in that assembly's
        /// repository — asking for the repository from the test assembly would
        /// hand back a different, unconfigured one and every assertion here
        /// would be measuring the wrong object.
        /// </summary>
        private static Hierarchy Repo =>
            (Hierarchy)log4net.LogManager.GetRepository(typeof(AppLogger).Assembly);

        private static log4net.Appender.AppenderSkeleton FileAppender()
        {
            foreach (var appender in Repo.Root.Appenders)
            {
                if (appender is log4net.Appender.RollingFileAppender rolling &&
                    rolling.Name == "LogFileAppender")
                {
                    return rolling;
                }
            }
            return null;
        }

        /// <summary>
        /// The file appender only exists when log4net found a config file in
        /// this host. Level switching is still meaningful without it, so the
        /// appender-specific assertions opt out rather than reporting a failure
        /// that says nothing about the code under test.
        /// </summary>
        private static log4net.Appender.AppenderSkeleton RequireFileAppender()
        {
            var appender = FileAppender();
            if (appender == null)
            {
                Assert.Inconclusive("No file appender configured in this test host.");
            }
            return appender;
        }

        [TestMethod]
        public void SetDetailedLogging_On_TakesEffectWithoutRestart()
        {
            AppLogger.SetDetailedLogging(true);

            Assert.IsTrue(AppLogger.IsDetailedLoggingEnabled(),
                "Detailed logging did not report as enabled immediately after being switched on.");
            Assert.IsTrue(Repo.Root.Level <= Level.Debug,
                $"Root level is {Repo.Root.Level}; debug records would still be dropped, "
                + "so the switch did not take effect live.");
        }

        [TestMethod]
        public void SetDetailedLogging_Off_ReturnsTheFileToInfo()
        {
            AppLogger.SetDetailedLogging(true);
            AppLogger.SetDetailedLogging(false);

            Assert.IsFalse(AppLogger.IsDetailedLoggingEnabled());

            var appender = RequireFileAppender();
            Assert.AreEqual(Level.Info, appender.Threshold,
                "Turning detailed logging off should put the on-disk log back to INFO.");
        }

        [TestMethod]
        public void SetDetailedLogging_Toggling_IsIdempotent()
        {
            // Users click checkboxes more than once; the second click must not
            // leave the level somewhere different from the first.
            AppLogger.SetDetailedLogging(true);
            AppLogger.SetDetailedLogging(true);
            Assert.IsTrue(AppLogger.IsDetailedLoggingEnabled());

            AppLogger.SetDetailedLogging(false);
            AppLogger.SetDetailedLogging(false);
            Assert.IsFalse(AppLogger.IsDetailedLoggingEnabled());
        }

        [TestMethod]
        public void RootLevel_NeverSitsAboveTheFileThreshold()
        {
            // log4net checks the logger level before the appender threshold, so
            // a root above the threshold silently discards everything in between
            // — including the debug history a diagnostic bundle depends on.
            foreach (bool detailed in new[] { true, false, true })
            {
                AppLogger.SetDetailedLogging(detailed);

                var appender = RequireFileAppender();
                Assert.IsTrue(Repo.Root.Level <= appender.Threshold,
                    $"Root level {Repo.Root.Level} is above the file threshold {appender.Threshold}; " +
                    "records between the two would be dropped before reaching any appender.");
            }
        }

        [TestMethod]
        public void ApplyConfiguredLevel_UnknownValue_IsIgnored()
        {
            AppLogger.SetDetailedLogging(false);
            var before = Repo.Root.Level;

            Config.Set("LogLevel", "NOT_A_LEVEL");
            AppLogger.ApplyConfiguredLevel();

            Assert.AreEqual(before, Repo.Root.Level,
                "A typo in Config.json should leave the configured default in force, not change the level.");
        }
    }
}
