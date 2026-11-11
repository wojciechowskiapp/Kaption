using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Data;
using log4net.Appender;
using log4net.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>The desktop E2E suite's mode (Kaption.DesktopE2E): off in a normal run, and app.log follows the data root.</summary>
    [TestClass]
    public class E2EModeTests
    {
        private const string Live = @"C:\Users\p\AppData\Roaming\Kaption";

        [TestMethod]
        public void Decide_UnsetIsOff_BlankIsRefused()
        {
            Assert.AreEqual((null, null), E2EMode.Decide(null, Live));
            var (dir, refusal) = E2EMode.Decide("  ", Live);
            Assert.IsNull(dir);
            Assert.IsNotNull(refusal, "a variable set to blanks is refused, never read as off");
        }

        [TestMethod]
        public void Decide_TakesAnAbsoluteFolderOutsideTheProfile()
        {
            Assert.AreEqual((@"C:\e2e\data", (string)null), E2EMode.Decide(@" C:\e2e\.\data ", Live));
            Assert.AreEqual(@"C:\Users\p\AppData\Roaming\Kaption-e2e", E2EMode.Decide(Live + "-e2e", Live).DataDir,
                "a sibling whose name starts like the profile is outside it");
        }

        [TestMethod]
        public void Decide_RefusesARelativeFolderOrTheRealProfile()
        {
            foreach (string value in new[] { @"relative\data", @"\rooted-without-drive", Live, Live + @"\", Live.ToUpperInvariant(),
                                             Live + @"\e2e", Live + @"\..\Kaption\x", Live + ":e2e", Live + "::$INDEX_ALLOCATION",
                                             @"C:\e2e\notes.txt:stream" })
            {
                var (dir, refusal) = E2EMode.Decide(value, Live);
                Assert.IsNull(dir, value);
                Assert.IsNotNull(refusal, value);
            }
        }

        [TestMethod]
        public void Decide_AProfileAtADriveRootCoversEverythingUnderIt()
        {
            string drive = Path.GetPathRoot(Path.GetTempPath());
            Assert.IsNotNull(E2EMode.Decide(Path.Combine(drive, "kaption-e2e-" + Guid.NewGuid().ToString("N")), drive).Refusal);
        }

        [TestMethod]
        public void Decide_SeesThroughJunctionsOnEitherPath()
        {
            string root = Path.Combine(Path.GetTempPath(), "kaption-tests", "e2e-links-" + Guid.NewGuid().ToString("N"));
            string roaming = Path.Combine(root, "Roaming"), live = Path.Combine(roaming, "Kaption");
            string alias = Path.Combine(root, "alias"), aliasedLive = Path.Combine(root, "aliased-live");
            Directory.CreateDirectory(live);
            try
            {
                Junction(alias, roaming);
                Junction(aliasedLive, live);
                // A parent of the candidate is a link into the profile's parent: the leaf itself is no link.
                Assert.IsNotNull(E2EMode.Decide(Path.Combine(alias, "Kaption", "e2e"), live).Refusal, "aliased parent");
                Assert.IsNotNull(E2EMode.Decide(Path.Combine(alias, "Kaption"), live).Refusal, "aliased profile");
                // The profile itself is reached through a link, the candidate through the real folder.
                Assert.IsNotNull(E2EMode.Decide(Path.Combine(live, "e2e"), Path.Combine(alias, "Kaption")).Refusal, "redirected profile");
                Assert.IsNotNull(E2EMode.Decide(aliasedLive, live).Refusal, "a link to the profile");
                string outside = Path.Combine(alias, "elsewhere");
                Assert.AreEqual(outside, E2EMode.Decide(outside, live).DataDir, "through a link, but outside the profile");
            }
            finally
            {
                // Links first and without recursion, so nothing is deleted through them.
                foreach (string link in new[] { alias, aliasedLive })
                    if (Directory.Exists(link)) Directory.Delete(link);
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public void Decide_RefusesEveryOtherSpellingOfTheProfile()
        {
            string root = Path.Combine(Path.GetTempPath(), "kaption-tests", "e2e-spell-" + Guid.NewGuid().ToString("N"));
            string live = Path.Combine(root, "Roaming", "Kaption");
            Directory.CreateDirectory(live);
            try
            {
                string unc = @"\\localhost\" + live[0] + "$" + live[2..];
                foreach (string value in new[] { @"\\?\" + live, @"\\?\" + live + @"\e2e", @"\\.\" + live + @"\e2e",
                                                 unc + @"\e2e", live[..2] + "relative", @"\\?\" + root + @"\elsewhere" })
                    Assert.IsNotNull(E2EMode.Decide(value, live).Refusal, value);

                string shortLive = ShortName(live);
                if (!shortLive.Equals(live, StringComparison.OrdinalIgnoreCase))
                    Assert.IsNotNull(E2EMode.Decide(shortLive + @"\e2e", live).Refusal, "8.3 short name " + shortLive);
                Assert.AreEqual(Path.Combine(root, "elsewhere"), E2EMode.Decide(Path.Combine(root, "elsewhere"), live).DataDir);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public void Decide_RefusesASecondColon()
        {
            string root = Path.Combine(Path.GetTempPath(), "kaption-tests", "e2e-colon-" + Guid.NewGuid().ToString("N"));
            string live = Path.Combine(root, "Roaming", "Kaption"), file = Path.Combine(root, "notes.txt");
            Directory.CreateDirectory(live);
            File.WriteAllText(file, "");
            try
            {
                // With the profile there: a stream on the folder, under which no file could ever be created.
                foreach (string value in new[] { live + ":e2e", live + "::$INDEX_ALLOCATION", live + ":$I30:$INDEX_ALLOCATION",
                                                 file + ":e2e", file + "::$DATA", Path.Combine(root, "else:where", "data") })
                    Assert.IsNotNull(E2EMode.Decide(value, live).Refusal, value);

                // With no profile yet: creating "Kaption::$INDEX_ALLOCATION" creates the profile folder itself.
                Directory.Delete(live);
                foreach (string value in new[] { live + "::$INDEX_ALLOCATION", live + ":$I30:$INDEX_ALLOCATION", live + ":e2e" })
                    Assert.IsNotNull(E2EMode.Decide(value, live).Refusal, value + " (no profile folder)");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public void Decide_SeesThroughASubstDrive()
        {
            string root = Path.Combine(Path.GetTempPath(), "kaption-tests", "e2e-subst-" + Guid.NewGuid().ToString("N"));
            string live = Path.Combine(root, "Roaming", "Kaption");
            Directory.CreateDirectory(live);
            try
            {
                string noDrive = SubstDrive.RunOnHeldDrive(root, drive =>
                {
                    Assert.IsNotNull(E2EMode.Decide(drive + @"\Roaming\Kaption\e2e", live).Refusal, "the profile through a subst drive");
                    Assert.IsNotNull(E2EMode.Decide(Path.Combine(live, "e2e"), drive + @"\Roaming\Kaption").Refusal,
                        "the profile itself on a subst drive");
                    Assert.AreEqual(drive + @"\elsewhere", E2EMode.Decide(drive + @"\elsewhere", live).DataDir);
                }, SubstDrive.Real);
                // An account that cannot map a drive at all (some service accounts) has nothing to check here.
                if (noDrive != null) Assert.Inconclusive("No drive letter could be mapped to a folder here: " + noDrive);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public void SubstDrive_TakesOnlyAFreeLetter_AndRemovesOnlyItsOwnMapping()
        {
            const string Mine = @"C:\tests\mine", Theirs = @"\??\C:\tests\theirs";
            var devices = new FakeDosDevices();

            // A letter in use is neither offered nor taken, and stays as it was.
            devices.Put("Q:", Theirs);
            CollectionAssert.DoesNotContain(SubstDrive.FreeLetters(devices).ToList(), 'Q');
            Assert.IsNull(SubstDrive.TryCreate('Q', Mine, out string whyNot, devices));
            Assert.AreEqual("the letter is in use", whyNot);
            CollectionAssert.AreEqual(new[] { Theirs }, devices.Stack("Q:"));

            // Two runs saw the letter free and the other one defined it last: it is theirs, and ours comes off again.
            devices.AfterNextDefine = name => devices.Put(name, Theirs);
            Assert.IsNull(SubstDrive.TryCreate('R', Mine, out whyNot, devices));
            StringAssert.Contains(whyNot, "another process");
            CollectionAssert.AreEqual(new[] { Theirs }, devices.Stack("R:"));

            // A mapping that lands on ours later: removing ours leaves it in place.
            SubstDrive drive = SubstDrive.TryCreate('S', Mine, out _, devices);
            Assert.IsTrue(drive.IsOurs);
            devices.Put("S:", Theirs);
            Assert.IsFalse(drive.IsOurs);
            drive.Dispose();
            CollectionAssert.AreEqual(new[] { Theirs }, devices.Stack("S:"));

            // The other run got in between our look at the letter and our mapping, so ours landed on top of theirs.
            // They would now see our folder: the letter is theirs, and ours comes off at once.
            devices.BeforeNextDefine = name => devices.Put(name, Theirs);
            Assert.IsNull(SubstDrive.TryCreate('U', Mine, out whyNot, devices));
            StringAssert.Contains(whyNot, "another process");
            CollectionAssert.AreEqual(new[] { Theirs }, devices.Stack("U:"));
            Assert.AreEqual(0, devices.Locked.Count, "a refused or finished letter keeps no lock");

            // A letter another test run is using is left alone before anything is looked at or mapped.
            devices.Locked.Add("T:");
            Assert.IsNull(SubstDrive.TryCreate('T', Mine, out whyNot, devices));
            StringAssert.Contains(whyNot, "another test run");
            Assert.AreEqual(0, devices.Stack("T:").Length);
            Assert.IsTrue(devices.Locked.Contains("T:"), "the other run's lock is not released for it");

            // An account that may not map drives: the reason comes back and the checks never run.
            var denied = new FakeDosDevices { DefineError = "DefineDosDevice: Access is denied." };
            StringAssert.Contains(SubstDrive.RunOnHeldDrive(Mine, _ => Assert.Fail("ran without a drive"), denied), "Access is denied.");
            // The same with every letter in use.
            var full = new FakeDosDevices();
            for (char letter = 'A'; letter <= 'Z'; letter++) full.Put(letter + ":", Theirs);
            Assert.AreEqual("no letter is free", SubstDrive.RunOnHeldDrive(Mine, _ => Assert.Fail("ran without a drive"), full));
        }

        [TestMethod]
        public void SubstDrive_OnlyALostLetterExcusesAFailure()
        {
            const string Mine = @"C:\tests\mine", Theirs = @"\??\C:\tests\theirs";

            // Another run maps over the letter while the checks run and they fail because of it: the next letter decides.
            var devices = new FakeDosDevices();
            var tried = new List<string>();
            Assert.IsNull(SubstDrive.RunOnHeldDrive(Mine, drive =>
            {
                tried.Add(drive);
                Assert.IsTrue(devices.Locked.SetEquals([drive]), "the letter's lock, and only it, is held while the checks run");
                if (tried.Count > 1) return;
                devices.Put(drive, Theirs);
                Assert.Fail("the checks saw the other run's folder");
            }, devices));
            Assert.AreEqual(2, tried.Count);
            Assert.AreEqual(0, devices.Locked.Count, "both letters' locks are free again");
            CollectionAssert.AreEqual(new[] { Theirs }, devices.Stack(tried[0]), "the lost letter keeps the other mapping, and only it");
            Assert.AreEqual(0, devices.Stack(tried[1]).Length, "the letter that counted is free again");

            // The same, but the other mapping is gone again before the failure is handled (a finally runs after the
            // exception filter has looked): the checks still stopped half way, so that letter must not count.
            devices = new FakeDosDevices();
            tried.Clear();
            Assert.IsNull(SubstDrive.RunOnHeldDrive(Mine, drive =>
            {
                tried.Add(drive);
                if (tried.Count > 1) return;
                devices.Put(drive, Theirs);
                try
                {
                    Assert.Fail("the checks saw the other run's folder");
                }
                finally
                {
                    devices.Remove(drive, Theirs);
                }
            }, devices));
            Assert.AreEqual(2, tried.Count, "a letter that was lost and came back does not count");

            // The checks pass, but the letter is not ours at the end: no result from that letter either.
            devices = new FakeDosDevices();
            tried.Clear();
            Assert.IsNull(SubstDrive.RunOnHeldDrive(Mine, drive =>
            {
                tried.Add(drive);
                if (tried.Count == 1) devices.Put(drive, Theirs);
            }, devices));
            Assert.AreEqual(2, tried.Count, "a pass on a letter lost by the end does not count");

            // On a letter held from start to end a failure is the test's result.
            Assert.ThrowsExactly<AssertFailedException>(
                () => SubstDrive.RunOnHeldDrive(Mine, _ => Assert.Fail("a real failure"), new FakeDosDevices()));
        }

        [TestMethod]
        public void SubstDrive_RealMappingsStackAndComeOffByExactMatch()
        {
            // What the in-memory table below assumes of DefineDosDevice, checked on the real one. A process that takes
            // no letter lock can map or remove a letter at the same moment, so one letter where everything held is the
            // proof and a letter that went wrong is followed by the next; only "wrong on every letter" is a failure.
            string root = Path.Combine(Path.GetTempPath(), "kaption-tests", "e2e-subst-" + Guid.NewGuid().ToString("N"));
            string mine = Path.Combine(root, "mine"), theirs = Path.Combine(root, "theirs");
            Directory.CreateDirectory(mine);
            Directory.CreateDirectory(theirs);
            string theirTarget = SubstDrive.TargetOf(theirs);
            IDosDevices real = SubstDrive.Real;
            bool Top(string name, string target) =>
                string.Equals(real.Targets(name).FirstOrDefault(), target, StringComparison.OrdinalIgnoreCase);
            var unmapped = new List<string>();
            var wrong = new List<string>();
            try
            {
                foreach (char letter in SubstDrive.FreeLetters(real))
                {
                    using SubstDrive drive = SubstDrive.TryCreate(letter, mine, out string whyNot, real);
                    if (drive == null)
                    {
                        unmapped.Add($"{letter}: {whyNot}");
                        continue;
                    }
                    try
                    {
                        // Both must be listed: that is how a mapping underneath a new one is noticed.
                        bool stacks = real.Define(drive.Name, theirTarget) == null && Top(drive.Name, theirTarget) &&
                                      real.Targets(drive.Name).Count == 2;
                        drive.Dispose();
                        bool oursCameOffFromUnderneath = Top(drive.Name, theirTarget);
                        real.Remove(drive.Name, theirTarget);
                        bool bothGone = !Top(drive.Name, theirTarget) && !drive.IsOurs;
                        if (stacks && oursCameOffFromUnderneath && bothGone) return;
                        wrong.Add($"{letter}: a second mapping lands on top = {stacks}, it stays when ours is removed = " +
                                  $"{oursCameOffFromUnderneath}, both gone at the end = {bothGone}");
                    }
                    finally
                    {
                        real.Remove(drive.Name, theirTarget);
                    }
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
            if (wrong.Count == 0)
                Assert.Inconclusive("No drive letter could be mapped to a folder here: " +
                                    (unmapped.Count == 0 ? "no letter is free" : string.Join("; ", unmapped)));
            Assert.Fail("DefineDosDevice did not behave as SubstDrive expects on any letter: " + string.Join("; ", wrong));
        }

        [TestMethod]
        public void SubstDrive_ALetterBelongsToOneTestRunAtATime()
        {
            // The real lock, on a name no drive test uses, so nothing else on the machine can be holding it.
            string name = "lock-test-" + Guid.NewGuid().ToString("N") + ":";
            using var taken = new ManualResetEventSlim();
            using var done = new ManualResetEventSlim();
            string holderWhyNot = null;
            // A mutex belongs to a thread, so "another run" is another thread here.
            var holder = new Thread(() =>
            {
                using IDisposable held = SubstDrive.Real.TryLock(name, out holderWhyNot);
                taken.Set();
                done.Wait();
            });
            holder.Start();
            taken.Wait();
            try
            {
                if (holderWhyNot != null) Assert.Inconclusive("The letter lock cannot be taken here: " + holderWhyNot);
                Assert.IsNull(SubstDrive.Real.TryLock(name, out string whyNot), "a second run must not get the letter");
                StringAssert.Contains(whyNot, "another test run");
            }
            finally
            {
                done.Set();
                holder.Join();
            }
            using IDisposable again = SubstDrive.Real.TryLock(name, out string after);
            Assert.IsNotNull(again, "free again once the first run is done: " + after);
        }

        /// <summary>A mapping table in memory, with what another process can do to it.</summary>
        private sealed class FakeDosDevices : IDosDevices
        {
            private readonly Dictionary<string, List<string>> _stacks = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>What Define answers instead of mapping anything: an account that may not map drives.</summary>
            public string DefineError { get; set; }

            /// <summary>Runs once, right before the next Define maps anything: another process a moment earlier.</summary>
            public Action<string> BeforeNextDefine { get; set; }

            /// <summary>Runs once, right after the next Define: another process defining the same letter a moment later.</summary>
            public Action<string> AfterNextDefine { get; set; }

            /// <summary>The letters a test run holds the lock of right now.</summary>
            public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);

            public IDisposable TryLock(string name, out string whyNot)
            {
                whyNot = Locked.Add(name) ? null : "another test run is using the letter";
                return whyNot == null ? new Unlock(() => Locked.Remove(name)) : null;
            }

            private sealed class Unlock(Action unlock) : IDisposable
            {
                public void Dispose() => unlock();
            }

            /// <summary>The letter's mappings, newest first.</summary>
            public string[] Stack(string name) => _stacks.TryGetValue(name, out var stack) ? [.. stack] : [];

            /// <summary>Another process's mapping, on top of whatever the letter has.</summary>
            public void Put(string name, string target)
            {
                if (!_stacks.TryGetValue(name, out var stack)) _stacks[name] = stack = [];
                stack.Insert(0, target);
            }

            public string Define(string name, string target)
            {
                if (DefineError != null) return DefineError;
                Action<string> before = BeforeNextDefine;
                BeforeNextDefine = null;
                before?.Invoke(name);
                Put(name, target);
                Action<string> after = AfterNextDefine;
                AfterNextDefine = null;
                after?.Invoke(name);
                return null;
            }

            public bool Remove(string name, string target)
            {
                if (!_stacks.TryGetValue(name, out var stack) || !stack.Remove(target)) return false;
                if (stack.Count == 0) _stacks.Remove(name);
                return true;
            }

            public IReadOnlyList<string> Targets(string name) => Stack(name);

            public bool Opens(string name) => true;
        }

        private static string ShortName(string path)
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c for %I in (\"{path}\") do @echo %~sI")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            };
            using var process = System.Diagnostics.Process.Start(start);
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return output.Length > 0 ? output : path;
        }

        private static void Junction(string link, string target)
        {
            // Junctions need no privilege (a symbolic link does), so the test runs on any account.
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var process = System.Diagnostics.Process.Start(start);
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, $"mklink /J failed: {process.StandardError.ReadToEnd()}");
        }

        [TestMethod]
        public void NormalRun_IsNotInTheMode_AndUsesTheRoamingProfile()
        {
            Assert.IsNull(Environment.GetEnvironmentVariable(E2EMode.DataDirVariable), "GI-Test must not run in the E2E mode.");
            Assert.IsFalse(E2EMode.IsActive);
            Assert.AreEqual(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kaption"),
                GameDataPaths.AppRoot);
        }

        [TestMethod]
        public void TestRun_KeepsItsDataOutOfTheRealProfile()
        {
            Assert.AreEqual(TestEnvironment.DataDir, GameDataPaths.Root);
            // The mode's own guard is the judge of "outside the profile", whatever links the temporary folder sits behind.
            Assert.IsNull(E2EMode.Decide(TestEnvironment.DataDir, E2EMode.LiveProfile()).Refusal);
            StringAssert.StartsWith(GameDataPaths.GameDir("Genshin"), TestEnvironment.DataDir + Path.DirectorySeparatorChar);
        }

        [TestMethod]
        public void AppConfig_LogFile_IsAppLogUnderTheDataRoot()
        {
            string root = RenderTestKit.RepoRoot() ?? throw new AssertInconclusiveException("Repository root not found.");
            var config = new XmlDocument();
            config.Load(Path.Combine(root, "GI-Subtitles", "app.config"));
            var section = (XmlElement)config.SelectSingleNode("/configuration/log4net");
            // The shipped <file> element as is, on an appender that records the path instead of opening the real log.
            foreach (XmlElement appender in section.SelectNodes("appender").Cast<XmlElement>().ToList())
            {
                if (appender.GetAttribute("name") == "LogFileAppender")
                    appender.SetAttribute("type", typeof(FilePathAppender).AssemblyQualifiedName);
                else
                    section.RemoveChild(appender);
            }
            foreach (XmlElement reference in section.SelectNodes("root/appender-ref").Cast<XmlElement>().ToList())
                if (reference.GetAttribute("ref") != "LogFileAppender") reference.ParentNode.RemoveChild(reference);

            var repository = log4net.LogManager.CreateRepository("e2e-mode-" + Guid.NewGuid().ToString("N"));
            log4net.Config.XmlConfigurator.Configure(repository, section);

            var file = repository.GetAppenders().OfType<FilePathAppender>().Single();
            Assert.AreEqual(Path.Combine(GameDataPaths.Root, "app.log"), file.File);
        }

        public sealed class FilePathAppender : AppenderSkeleton
        {
            public string File { get; set; }
            protected override void Append(LoggingEvent loggingEvent) { }
        }
    }
}
