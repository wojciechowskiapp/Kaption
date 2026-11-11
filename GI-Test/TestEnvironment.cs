using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The test run's own settings and data folders. Config is static and saves on every Set, and a bundle install
    /// writes under the data root, so without this a test (or a window built with its code-behind) would read and
    /// rewrite a real Kaption profile, even one in use.
    /// </summary>
    [TestClass]
    public class TestEnvironment
    {
        public static string ConfigDir { get; } = Path.Combine(Path.GetTempPath(), "kaption-tests", "config-" + Environment.ProcessId);

        /// <summary>GameDataPaths.Root in this run: game folders, the gamedata manifest and the fix-this-line files.</summary>
        public static string DataDir { get; } = Path.Combine(ConfigDir, "data");

        private static IDisposable _shipped;

        [AssemblyInitialize]
        public static void UseATemporaryProfile(TestContext _)
        {
            if (Directory.Exists(ConfigDir)) Directory.Delete(ConfigDir, recursive: true);
            Directory.CreateDirectory(ConfigDir);
            Environment.SetEnvironmentVariable("KAPTION_CONFIG_DIR", ConfigDir);
            // Whatever the app keeps under its data root (game folders, the gamedata manifest) goes there as well.
            GI_Subtitles.Services.Data.GameDataPaths.TestRoot = DataDir;
            // Fix-this-line files (overrides, community cache, send queue) go to the same temporary profile.
            GI_Subtitles.Services.Community.TranslationLayerRegistry.TestRoot = DataDir;
            // Renders capture a frame, not a timeline: the shell's micro-motion stays off unless a test turns it on.
            GI_Subtitles.Views.Shell.Motion.Override = false;
            // Every shipped game, a withdrawn one included (ShippedGames): the run never disposes it.
            _shipped = ShippedGames.Use();
        }
    }
}
