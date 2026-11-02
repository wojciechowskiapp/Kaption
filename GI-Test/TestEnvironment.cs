using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The test run's own settings folder. Config is static and saves on every Set, so without this a test (or a
    /// window built with its code-behind) would read and rewrite a real Kaption profile, even one in use.
    /// </summary>
    [TestClass]
    public class TestEnvironment
    {
        public static string ConfigDir { get; } = Path.Combine(Path.GetTempPath(), "kaption-tests", "config-" + Environment.ProcessId);

        [AssemblyInitialize]
        public static void UseATemporaryProfile(TestContext _)
        {
            if (Directory.Exists(ConfigDir)) Directory.Delete(ConfigDir, recursive: true);
            Directory.CreateDirectory(ConfigDir);
            Environment.SetEnvironmentVariable("KAPTION_CONFIG_DIR", ConfigDir);
            // Renders capture a frame, not a timeline: the shell's micro-motion stays off unless a test turns it on.
            GI_Subtitles.Views.Shell.Motion.Override = false;
        }
    }
}
