using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using GI_Subtitles.Services.Community;
using GI_Subtitles.Services.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Velopack;
using AppConfig = GI_Subtitles.Core.Config.Config;

namespace GI_Test
{
    /// <summary>
    /// Config.json is the player's settings. Every save must leave the old file or the new one on disk, whole: a save cut
    /// by a crash, a forced stop (the updater ends what is left of the app) or a power loss must never truncate it.
    /// </summary>
    [TestClass]
    [DoNotParallelize] // the settings file is one per test process
    public class ConfigSaveTests
    {
        private const string BlobKey = "ConfigSaveTests.Blob";

        /// <summary>
        /// A file rewritten in place is empty, then partial, while it is written; anything that reads it then (or a stop
        /// that lands then) gets a broken document. A reader polling the file through a run of saves must only ever
        /// see whole ones.
        /// </summary>
        [TestMethod]
        public void Save_NeverExposesAPartialFile()
        {
            string file = AppConfig.SettingsFilePath;
            string blob = new string('x', 256 * 1024);
            int reads = 0, broken = 0;
            string firstBroken = null;
            bool stop = false;
            var reader = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    string text;
                    try
                    {
                        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var sr = new StreamReader(stream);
                        text = sr.ReadToEnd();
                    }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    reads++;
                    bool whole;
                    try { whole = JsonNode.Parse(text) is JsonObject; }
                    catch (JsonException) { whole = false; }
                    if (!whole)
                    {
                        broken++;
                        firstBroken ??= $"{text.Length} chars";
                    }
                }
            }) { IsBackground = true };
            reader.Start();
            try
            {
                for (int i = 0; i < 40; i++) AppConfig.Set(BlobKey, blob + i);
            }
            finally
            {
                Volatile.Write(ref stop, true);
                reader.Join();
                AppConfig.Remove(BlobKey);
            }
            Assert.IsTrue(reads > 0, "The reader never got the file open.");
            Assert.AreEqual(0, broken, $"{broken} of {reads} reads saw a partial Config.json (first: {firstBroken}).");
        }

        [TestMethod]
        public void Save_LeavesNoTempFiles_AndTheNewestValue()
        {
            const string key = "ConfigSaveTests.Value";
            try
            {
                for (int i = 0; i <= 5; i++) AppConfig.Set(key, i);
                string file = AppConfig.SettingsFilePath;
                var saved = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                Assert.AreEqual(5, saved[key]!.GetValue<int>());
                string[] leftovers = Directory.GetFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file) + ".*.tmp");
                Assert.AreEqual(0, leftovers.Length, string.Join(", ", leftovers));
            }
            finally { AppConfig.Remove(key); }
        }

        /// <summary>
        /// A process stopped inside File.Replace leaves ReplaceFileW's own "~RF" temp file (seen in the update lab: 11 of 40
        /// forced stops); the next save deletes it. A save that never got swapped in leaves "Config.json.&lt;id&gt;.tmp", which
        /// may be the only copy of the settings: the next save moves it aside under a name no cleanup matches, and never
        /// deletes it.
        /// </summary>
        [TestMethod]
        public void Save_DeletesReplaceTemps_AndKeepsUnfinishedSavesAside()
        {
            using var dir = new ScratchFolder();
            string target = dir.Path("Config.json");
            File.WriteAllText(target, "{}");
            File.WriteAllText(target + "~RFa827003.TMP", "");
            File.WriteAllText(target + ".1a2b3c4d.tmp", "{\"Unfinished\":1}");
            File.WriteAllText(dir.Path("Other.json~RFa827004.TMP"), "");
            AtomicFile.WriteAllText(target, "{\"a\":1}");
            string[] names = Directory.GetFiles(dir.Root).Select(System.IO.Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.AreEqual(3, names.Length, string.Join(", ", names));
            Assert.AreEqual("Config.json", names[0]);
            StringAssert.StartsWith(names[1], "Config.json.unrestored-");
            Assert.AreEqual("{\"Unfinished\":1}", File.ReadAllText(dir.Path(names[1])));
            Assert.AreEqual("Other.json~RFa827004.TMP", names[2]);
        }

        /// <summary>
        /// Two failures in a row: a save whose swap failed left the only copy of the settings in its temp file, and at the
        /// next start something holds that file, so it cannot be put back. The saves that follow must never delete it:
        /// while it is held it stays, and once free it goes aside under a name no cleanup matches.
        /// </summary>
        [TestMethod]
        public void UnfinishedSave_ThatCannotBeRestored_IsNeverDeleted()
        {
            using var dir = new ScratchFolder();
            string target = dir.Path("Config.json");
            string onlyCopy = target + ".1a2b3c4d.tmp";
            File.WriteAllText(onlyCopy, "{\"Kept\":1}");
            using (new FileStream(onlyCopy, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.IsFalse(AtomicFile.RecoverInterruptedSave(target), "a held file cannot be restored");
                AtomicFile.WriteAllText(target, "{}");
            }
            AtomicFile.WriteAllText(target, "{\"After\":1}");
            string[] kept = Directory.GetFiles(dir.Root).Where(f => File.ReadAllText(f) == "{\"Kept\":1}").ToArray();
            Assert.AreEqual(1, kept.Length, "the unfinished save survives: " + string.Join(", ", Directory.GetFiles(dir.Root).Select(System.IO.Path.GetFileName)));
            StringAssert.StartsWith(System.IO.Path.GetFileName(kept[0]), "Config.json.unrestored-");
            Assert.AreEqual(0, Directory.GetFiles(dir.Root, "Config.json.*.tmp").Length, "no temp file is left");
        }

        /// <summary>
        /// The same two failures, but the app ends before the hold does. The next start finds a Config.json (the defaults the
        /// first one saved), so nothing restores; the unfinished save must still never be deleted once it is free.
        /// </summary>
        [TestMethod]
        public void UnfinishedSave_ThatCannotBeRestored_SurvivesARestart()
        {
            using var dir = new ScratchFolder();
            string target = dir.Path("Config.json");
            string onlyCopy = target + ".1a2b3c4d.tmp";
            File.WriteAllText(onlyCopy, "{\"Kept\":2}");
            using (new FileStream(onlyCopy, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.AreEqual(AtomicFile.RestoreResult.Unreadable, AtomicFile.RestoreInterruptedSave(target));
                AtomicFile.WriteAllText(target, "{}");
            }
            // The next process: nothing in memory says the file is precious; the rule alone keeps it.
            AtomicFile.WriteAllText(target, "{\"After\":1}");
            string[] kept = Directory.GetFiles(dir.Root).Where(f => File.ReadAllText(f) == "{\"Kept\":2}").ToArray();
            Assert.AreEqual(1, kept.Length, "the unfinished save survives the restart: " + string.Join(", ", Directory.GetFiles(dir.Root).Select(System.IO.Path.GetFileName)));
            StringAssert.StartsWith(System.IO.Path.GetFileName(kept[0]), "Config.json.unrestored-");
        }

        /// <summary>A partial Config.json that something holds cannot be moved: what was read is kept as .bad instead.</summary>
        [TestMethod]
        public void ReadSettings_KeepsACopy_WhenTheBrokenFileCannotBeMoved()
        {
            using var dir = new ScratchFolder();
            string target = dir.Path("Config.json");
            File.WriteAllText(target, "{\"Game\": \"WuWa\", \"Volu");
            var into = new Dictionary<string, JsonNode>();
            using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.AreEqual(AppConfig.LoadOutcome.Broken, AppConfig.ReadSettings(target, true, into, new object()));
            Assert.AreEqual("{\"Game\": \"WuWa\", \"Volu", File.ReadAllText(target + ".bad"));
            AtomicFile.WriteAllText(target, "{}");
            Assert.AreEqual("{\"Game\": \"WuWa\", \"Volu", File.ReadAllText(target + ".bad"), "a later save leaves the copy");
        }

        /// <summary>
        /// The load gives a held file a few short tries; one released in that time is restored. One still held after them
        /// is reported (settings start from defaults, the caller saves them) and kept, as the test above shows.
        /// </summary>
        [TestMethod]
        public void ReadSettings_RetriesABriefHold_ThenStartsFromDefaultsAndKeepsTheFile()
        {
            using var dir = new ScratchFolder();
            string target = dir.Path("Config.json");
            string onlyCopy = target + ".1a2b3c4d.tmp";
            File.WriteAllText(onlyCopy, "{\"Game\":\"ZZZ\"}");
            var into = new Dictionary<string, JsonNode>();
            var brief = new FileStream(onlyCopy, FileMode.Open, FileAccess.Read, FileShare.None);
            using (var release = new Timer(_ => brief.Dispose(), null, 50, Timeout.Infinite))
                Assert.AreEqual(AppConfig.LoadOutcome.Loaded, AppConfig.ReadSettings(target, true, into, new object()), "a brief hold is waited out");
            Assert.AreEqual("ZZZ", into["Game"]!.GetValue<string>());

            File.Delete(target);
            File.WriteAllText(onlyCopy, "{\"Game\":\"WuWa\"}");
            into.Clear();
            using (new FileStream(onlyCopy, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.AreEqual(AppConfig.LoadOutcome.Unrestored, AppConfig.ReadSettings(target, true, into, new object()));
            Assert.AreEqual(0, into.Count, "settings start from defaults");
            Assert.IsTrue(File.Exists(onlyCopy), "the unfinished save is kept");
        }

        /// <summary>A scanner that blocks the rename but not a read: the unfinished save is copied into place.</summary>
        [TestMethod]
        public void ReadSettings_RestoresByCopy_WhenOnlyTheRenameIsBlocked()
        {
            using var dir = new ScratchFolder();
            string target = dir.Path("Config.json");
            File.WriteAllText(target + ".1a2b3c4d.tmp", "{\"Game\":\"WuWa\"}");
            var into = new Dictionary<string, JsonNode>();
            using (new FileStream(target + ".1a2b3c4d.tmp", FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.AreEqual(AppConfig.LoadOutcome.Loaded, AppConfig.ReadSettings(target, true, into, new object()));
            Assert.AreEqual("WuWa", into["Game"]!.GetValue<string>());
            Assert.AreEqual("{\"Game\":\"WuWa\"}", File.ReadAllText(target));
        }

        /// <summary>A partial Config.json still holds most settings: it is kept as .bad before defaults are written.</summary>
        [TestMethod]
        public void ReadSettings_KeepsAFileThatDoesNotParse()
        {
            using var dir = new ScratchFolder();
            string target = dir.Path("Config.json");
            File.WriteAllText(target, "{\"Game\": \"ZZZ\", \"Volu");
            var into = new Dictionary<string, JsonNode>();
            Assert.AreEqual(AppConfig.LoadOutcome.SetAside, AppConfig.ReadSettings(target, true, into, new object()));
            Assert.IsFalse(File.Exists(target));
            Assert.AreEqual("{\"Game\": \"ZZZ\", \"Volu", File.ReadAllText(target + ".bad"));

            File.WriteAllText(target, "");
            Assert.AreEqual(AppConfig.LoadOutcome.Broken, AppConfig.ReadSettings(target, true, into, new object()), "an empty file holds nothing to keep");
            Assert.IsTrue(File.Exists(target));
        }

        private sealed class ScratchFolder : IDisposable
        {
            public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kaption-tests", "config-" + Guid.NewGuid().ToString("N"));
            public ScratchFolder() => Directory.CreateDirectory(Root);
            public string Path(string name) => System.IO.Path.Combine(Root, name);
            public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch (IOException) { } }
        }

        /// <summary>Saves from several threads at once (the UI and background readers both write settings) all land.</summary>
        [TestMethod]
        public void ConcurrentSaves_AllSucceed_AndTheFileHoldsEveryKey()
        {
            string[] keys = Enumerable.Range(0, 8).Select(i => "ConfigSaveTests.Thread" + i).ToArray();
            try
            {
                var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
                var threads = keys.Select(k => new Thread(() =>
                {
                    try { for (int i = 0; i < 25; i++) AppConfig.Set(k, i); }
                    catch (Exception ex) { errors.Enqueue(ex); }
                })).ToList();
                threads.ForEach(t => t.Start());
                threads.ForEach(t => t.Join());
                Assert.IsTrue(errors.IsEmpty, string.Join(Environment.NewLine, errors.Select(e => e.Message)));
                var saved = JsonNode.Parse(File.ReadAllText(AppConfig.SettingsFilePath))!.AsObject();
                foreach (string k in keys) Assert.AreEqual(24, saved[k]!.GetValue<int>(), k);
            }
            finally { foreach (string k in keys) AppConfig.Remove(k); }
        }
    }

    /// <summary>The E2E mode's never-installed app gets a Velopack locator that opens no log file.</summary>
    [TestClass]
    public class VelopackStartupTests
    {
        [TestMethod]
        public void E2EMode_GetsTheUninstalledLocator_EverythingElseVelopacksOwn()
        {
            Assert.IsInstanceOfType(VelopackStartup.LocatorFor(e2eMode: true), typeof(UninstalledVelopackLocator));
            Assert.IsNull(VelopackStartup.LocatorFor(e2eMode: false), "A shipped build must keep Velopack's own locator.");
        }

        [TestMethod]
        public void UninstalledLocator_IsNotInstalled_ForTheUpdateManager()
        {
            var locator = new UninstalledVelopackLocator();
            Assert.IsNull(locator.CurrentlyInstalledVersion);
            Assert.IsNull(locator.PackagesDir);
            Assert.AreEqual(0, locator.GetLocalPackages().Count);
            var manager = new UpdateManager("http://127.0.0.1:9/feed/", null, locator);
            Assert.IsFalse(manager.IsInstalled);
            Assert.IsNotNull(locator.Process);
        }
    }
}
